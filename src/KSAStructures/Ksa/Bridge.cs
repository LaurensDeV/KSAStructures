using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// Commands from outside the game, read from <c>Logs/bridge/KSAStructures/in</c> and answered in
/// <c>out</c>: what lets an agent in a terminal load a save, hold the view somewhere in the office,
/// change its lighting, step the world and photograph it in a game that stays running.
/// <c>tools/ksa-mcp/server.py</c> is the other end. Only a developer's install starts it.
///
/// <para><b>Files, not a socket</b>: a folder needs no port and works across the WSL boundary as it
/// is.</para>
///
/// <para>One command at a time, oldest first. A command that takes frames -- a step, a capture --
/// holds the queue until it answers, so a sequence of them is a script that runs in order.</para>
/// </summary>
internal sealed class Bridge(Settings settings)
{
    // A few times a second is quick enough for a person and costs one directory listing.
    private const double PollSeconds = 0.1;
    private double _sincePoll;

    private Func<double, double, Reply?>? _running;
    private BridgeCommand? _current;

    // What a held view gives back, and the craft it was measured from.
    private KsaWorld.MainView _saved;
    private Vehicle? _posedFrom;
    private SceneCamera? _scene;
    private (double X, double Y, double Z, double Heading, double Pitch, double Fov)? _officeView;

    private static readonly string[] ShaderIds = [DarkPass.ShaderId];

    // A folder of its own: every mod built from the same tooling has a bridge, and two reading one
    // folder each answer whichever command they reach first.
    private static string Root => Path.Combine(Log.Folder, "bridge", "KSAStructures");
    private static string Inbox => Path.Combine(Root, "in");
    private static string Outbox => Path.Combine(Root, "out");

    private readonly record struct Reply(bool Ok, string Error, Dictionary<string, object?> Data);

    private static Reply Done(Dictionary<string, object?>? data = null) => new(true, string.Empty, data ?? []);
    private static Reply Failed(string why) => new(false, why, []);

    /// <summary>Hands a held view back, so unloading does not leave the player in a pose nothing drives.</summary>
    public void Release()
    {
        if (_scene is null && _officeView is null) return;

        _scene = null;
        _officeView = null;
        KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
        _posedFrom = null;
    }

    /// <summary>Holds the view a <c>camera</c> or <c>office_view</c> command set. With the step.</summary>
    public void DriveCamera()
    {
        try
        {
            if (_officeView is { } view && _posedFrom is { } from && KsaWorld.IsAlive(from))
            {
                if (Office.TryView(from, view.X, view.Y, view.Z, view.Heading, view.Pitch,
                                   out double3 offset, out double3 forward, out double3 up))
                {
                    KsaWorld.TryLookFromMainViewport(offset, forward, up, view.Fov);
                }

                return;
            }

            _scene?.Drive();
        }
        catch
        {
            // The view is the player's to take back; a pose that cannot be held is simply not held.
        }
    }

    /// <summary>
    /// One frame: advances the command running, or takes the next. Called from the one hook KSA always
    /// calls, so it runs in the menu and with the UI hidden. Nothing here may throw.
    /// </summary>
    public void Update(double dtPlayer, double dtSim)
    {
        try
        {
            if (_running is not null && _current is not null)
            {
                if (_running(dtPlayer, dtSim) is { } reply) Answer(_current, reply);
                return;
            }

            _sincePoll += dtPlayer;
            if (_sincePoll < PollSeconds) return;
            _sincePoll = 0.0;

            if (!Directory.Exists(Inbox)) return;

            string? next = Directory.GetFiles(Inbox, "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
            if (next is null) return;

            string text = File.ReadAllText(next);
            File.Delete(next);

            if (!BridgeCommand.TryParse(text, out BridgeCommand? command, out string trouble))
            {
                Log.Warn($"bridge: refused {Path.GetFileName(next)} -- {trouble}");
                return;
            }

            Start(command!);
        }
        catch (Exception e)
        {
            if (_current is { } command) Answer(command, Failed($"threw: {e.Message}"));
            else Log.Warn($"bridge: {e.Message}");
        }
    }

    private void Start(BridgeCommand command)
    {
        _current = command;
        Log.Info($"bridge: {command.Name} ({command.Id})");

        Reply? now = command.Name switch
        {
            "status" => Status(),
            "pause" => KsaWorld.SetPaused(true) ? Done() : Failed("the world would not pause"),
            "resume" => KsaWorld.SetPaused(false) ? Done() : Failed("the world would not resume"),
            "speed" => KsaWorld.SetSimulationSpeed(command.Number("x", 1.0)) ? Done() : Failed("speed refused"),
            "set" => Set(command),
            "get" => Get(command),
            "camera" => Camera(command),
            "office_view" => OfficeView(command),
            "office_light" => OfficeLight(command),
            "office_put" => KsaWorld.ControlledVehicle is { } flown
                            && Office.Put(flown, command.Number("x", -4.3), command.Number("y", 6.5), command.Number("z", 50.0))
                                ? Done() : Failed("could not move the craft"),
            "reload_shaders" => ReloadShaders(),
            "step" => BeginStep(command),
            "capture" => BeginCapture(command),
            "load" => BeginLoad(command),
            _ => Failed($"no command '{command.Name}'"),
        };

        if (now is { } reply) Answer(command, reply);
    }

    private void Answer(BridgeCommand command, Reply reply)
    {
        _running = null;
        _current = null;

        Directory.CreateDirectory(Outbox);

        Dictionary<string, object?> body = new()
        {
            ["id"] = command.Id,
            ["cmd"] = command.Name,
            ["ok"] = reply.Ok,
            ["error"] = reply.Ok ? null : reply.Error,
            ["data"] = reply.Data,
        };

        // Written aside and moved, so the other end never reads half a reply.
        string final = Path.Combine(Outbox, command.Id + ".json");
        string partial = final + ".part";
        File.WriteAllText(partial, JsonSerializer.Serialize(body, JsonOptions));
        File.Move(partial, final, overwrite: true);

        if (!reply.Ok) Log.Warn($"bridge: {command.Name} ({command.Id}) failed -- {reply.Error}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ---- commands that answer at once -------------------------------------------------------

    private Reply Status() => Done(new()
    {
        ["build"] = Build.Version,
        ["in_flight"] = KsaWorld.InFlightScene,
        ["craft"] = KsaWorld.ControlledVehicle is { } craft ? KsaWorld.DisplayName(craft) : null,
        ["paused"] = KsaWorld.IsPaused,
        ["speed"] = KsaWorld.SimulationSpeed,
        ["camera_held"] = _scene is not null || _officeView is not null,
    });

    private Reply Set(BridgeCommand command)
    {
        string field = command.String("name");
        if (!command.TryRaw("value", out JsonElement value)) return Failed("set needs a name and a value");

        if (!BridgeCommand.TrySetField(settings, field, value, out string trouble)) return Failed(trouble);

        // The panel's tick box does this beside the write; the field alone changes nothing.
        if (string.Equals(field, nameof(Settings.VerboseLog), StringComparison.OrdinalIgnoreCase))
        {
            Log.Threshold = settings.VerboseLog ? Log.Level.Debug : Log.Level.Info;
        }

        return Done(new() { [field] = BridgeCommand.FieldText(settings, field) });
    }

    private Reply Get(BridgeCommand command)
    {
        string field = command.String("name");
        return BridgeCommand.FieldText(settings, field) is { } text
                   ? Done(new() { [field] = text })
                   : Failed($"no field '{field}' on Settings");
    }

    // The main view held "east", "north" and "up" metres from the craft being flown, looking at it
    // or at the craft named "at" ("at_up" metres above it), at "fov_deg". "release" hands it back.
    private Reply Camera(BridgeCommand command)
    {
        if (command.Flag("release", false))
        {
            if (_scene is null) return Done();

            _scene = null;
            if (_officeView is null)
            {
                KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
                _posedFrom = null;
            }

            return Done();
        }

        if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");

        string at = command.String("at");
        Vehicle? target = at.Length > 0
                              ? KsaWorld.Vehicles.FirstOrDefault(v => KsaWorld.IsAlive(v) && KsaWorld.DisplayName(v) == at)
                              : null;
        if (at.Length > 0 && target is null) return Failed($"no craft called '{at}'");

        if (_scene is null && _officeView is null)
        {
            _saved = KsaWorld.RememberMainView();
            _posedFrom = craft;
        }

        _scene = new SceneCamera
        {
            Anchor = craft,
            EastNorthUp = new double3(command.Number("east", 0.0), command.Number("north", -8.0), command.Number("up", 2.0)),
            AtCraft = target,
            AtUp = command.Number("at_up", 0.0),
            FovDeg = command.Number("fov_deg", 50.0),
        };

        return Done(new() { ["held"] = _officeView is null && _scene.Drive() });
    }

    // The main view held at a place in the office's own frame, for looking at what cannot be flown to.
    private Reply OfficeView(BridgeCommand command)
    {
        if (command.Flag("release", false))
        {
            if (_officeView is null) return Done();

            _officeView = null;
            if (_scene is null)
            {
                KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
                _posedFrom = null;
            }

            return Done();
        }

        if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");

        if (_officeView is null && _scene is null)
        {
            _saved = KsaWorld.RememberMainView();
            _posedFrom = craft;
        }

        _officeView = (command.Number("x", -3.3), command.Number("y", 0.0), command.Number("z", 0.0),
                       command.Number("heading_deg", 0.0), command.Number("pitch_deg", 0.0), command.Number("fov_deg", 70.0));
        return Done(new() { ["held"] = true });
    }

    private static Reply OfficeLight(BridgeCommand command)
    {
        Underground.Darken = command.Flag("darken", Underground.Darken);
        Underground.Shadows = command.Flag("shadows", Underground.Shadows);
        Underground.LampScale = (float)command.Number("scale", Underground.LampScale);
        Underground.Fill = (float)command.Number("fill", Underground.Fill);
        return Done();
    }

    // KSA's own reloader cannot reach a mod's shader: it maps a path by finding "Content" in it.
    // What it does is ShaderReference.DoLoad, which is internal, and the pass's pipelines are then
    // dropped so they rebuild against the new module. A file that is missing makes DoLoad destroy
    // the old module and keep nothing, so that is checked first.
    private static Reply ReloadShaders()
    {
        MethodInfo? doLoad = typeof(FileReference).GetMethod("DoLoad", BindingFlags.NonPublic | BindingFlags.Instance);
        if (doLoad is null) return Failed("FileReference.DoLoad did not resolve");

        List<string> reloaded = [];
        List<string> seen = [];
        foreach (string id in ShaderIds)
        {
            if (!ModLibrary.TryGet<ShaderReference>(id, out ShaderReference? shader) || shader is null)
            {
                return Failed($"no shader '{id}'");
            }

            if (!File.Exists(shader.ModPath)) return Failed($"'{shader.ModPath}' is not there");

            // What was compiled and whether the module changed: a reload that answers "reloaded"
            // and draws the old shader is otherwise indistinguishable from one that worked.
            FileInfo file = new(shader.ModPath);
            nint before = shader.Shader?.VkHandle ?? 0;

            try
            {
                doLoad.Invoke(shader, null);
            }
            catch (TargetInvocationException e)
            {
                return Failed($"{id} did not compile: {e.InnerException?.Message ?? e.Message}");
            }

            nint after = shader.Shader?.VkHandle ?? 0;
            string sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(shader.ModPath))).ToLowerInvariant();
            string line = $"{id}: {shader.ModPath}, {file.Length} bytes written {file.LastWriteTime:HH:mm:ss}, "
                          + $"sha1 {sha1[..12]}, module {before:X} -> {after:X}";
            seen.Add(line);
            Log.Info($"bridge: reloaded {line}");

            reloaded.Add(id);
        }

        DarkPass.Release();
        return Done(new() { ["reloaded"] = reloaded, ["modules"] = seen });
    }

    // ---- commands that take frames ----------------------------------------------------------

    // Runs the world for so many simulated seconds and stops it again, so what is photographed next
    // is at an age that was asked for rather than one that happened.
    private Reply? BeginStep(BridgeCommand command)
    {
        double seconds = command.Number("seconds", 1.0);
        if (!(seconds > 0.0)) return Failed("seconds must be positive");

        double advanced = 0.0;
        double waited = 0.0;
        KsaWorld.SetPaused(false);

        _running = (dtPlayer, dtSim) =>
        {
            waited += dtPlayer;
            advanced += Math.Max(dtSim, 0.0);

            if (advanced >= seconds)
            {
                KsaWorld.SetPaused(true);
                return Done(new() { ["advanced_s"] = Math.Round(advanced, 3) });
            }

            return waited > (seconds * 20.0) + 30.0 ? Failed($"only {advanced:F2} s passed") : null;
        };

        return null;
    }

    private Reply? BeginLoad(BridgeCommand command)
    {
        string save = command.String("save");
        if (save.Length == 0) return Failed("load needs a save");

        try
        {
            GameSaves.LoadSaveGame(save);
        }
        catch (Exception e)
        {
            return Failed($"could not load '{save}': {e.Message}");
        }

        // The craft a held camera was set from is gone with the world it was in.
        _scene = null;
        double waited = 0.0;

        _running = (dtPlayer, _) =>
        {
            waited += dtPlayer;

            // A beat past the craft appearing, for the world to settle round it.
            if (waited > 3.0 && KsaWorld.InFlight)
            {
                return Done(new() { ["craft"] = KsaWorld.DisplayName(KsaWorld.ControlledVehicle!) });
            }

            return waited > 60.0 ? Failed("no craft after 60 s") : null;
        };

        return null;
    }

    // Photographs through KSA's own capture, which names files to the second: so each one is moved
    // aside and renamed before the next is asked for, and two can never be one file. Every picture
    // gets a manifest beside it -- the time, the camera and where the craft is on screen -- so nothing
    // has to be matched up by its time afterwards.
    private Reply? BeginCapture(BridgeCommand command)
    {
        string label = command.String("label");
        if (label.Length == 0) label = "shot";
        label = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

        int frames = Math.Clamp((int)command.Number("frames", 1.0), 1, 120);
        double everySim = Math.Max(command.Number("every_s", 0.0), 0.0);
        int everyFrames = Math.Max((int)command.Number("every_frames", 1.0), 1);

        string folder = Path.Combine(Outbox, command.Id);
        Directory.CreateDirectory(folder);

        string shots = Path.Combine(KSA.Constants.DocumentsFolderPath, "exports", "screenshots");

        List<Dictionary<string, object?>> taken = [];
        int index = 0;
        bool waiting = false;
        DateTime askedAt = default;
        Dictionary<string, object?>? manifest = null;
        double sinceSim = 0.0;
        int sinceFrames = 0;
        double waited = 0.0;
        int warming = 0;

        // Paused with a spacing asked for, the world is run that far and stopped again before each
        // picture, so the ages are exactly the ones asked for however long a command takes to arrive.
        bool stepping = KsaWorld.IsPaused && everySim > 0.0;
        int settle = 0;

        _running = (dtPlayer, dtSim) =>
        {
            if (!waiting)
            {
                sinceSim += Math.Max(dtSim, 0.0);
                sinceFrames++;

                if (stepping && index > 0)
                {
                    if (sinceSim < everySim)
                    {
                        if (KsaWorld.IsPaused) KsaWorld.SetPaused(false);
                        return null;
                    }

                    if (!KsaWorld.IsPaused)
                    {
                        KsaWorld.SetPaused(true);
                        settle = 0;
                    }

                    // A frame or two for the paused world to be the one drawn.
                    if (++settle < 3) return null;
                }

                bool due = index == 0 || stepping
                           || (everySim > 0.0 ? sinceSim >= everySim : sinceFrames >= everyFrames);
                if (!due) return null;

                askedAt = DateTime.UtcNow.AddSeconds(-0.5);

                // KSA's own warm-up hides the UI for these frames before the picture, so nothing blended
                // over frames still carries a window; the manifest is written on the frame the picture
                // is actually taken, so it describes that instant.
                if (!KsaWorld.TryRequestScreenshot(flags: $"warm={WarmFrames}"))
                {
                    return Failed("KSA would not take a screenshot");
                }

                manifest = null;
                warming = WarmFrames;
                waiting = true;
                waited = 0.0;
                return null;
            }

            if (manifest is null && --warming <= 0) manifest = Manifest(label, index);

            waited += dtPlayer;
            if (waited > 10.0) return Failed($"screenshot {index} never arrived");

            string? file = Directory.Exists(shots)
                               ? new DirectoryInfo(shots).GetFiles("ksa_*.png")
                                                         .Where(f => f.LastWriteTimeUtc >= askedAt && f.Length > 0)
                                                         .OrderByDescending(f => f.LastWriteTimeUtc)
                                                         .FirstOrDefault()?.FullName
                               : null;
            if (file is null) return null;

            string name = $"{index:D2}-{label}";
            string target = Path.Combine(folder, name + ".png");

            try
            {
                File.Move(file, target, overwrite: true);
            }
            catch (IOException)
            {
                // Still being written; the next frame will find it finished.
                return null;
            }

            manifest ??= Manifest(label, index);
            manifest["file"] = target;
            File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(manifest, JsonOptions));
            taken.Add(manifest);

            index++;
            waiting = false;
            sinceSim = 0.0;
            sinceFrames = 0;

            return index >= frames ? Done(new() { ["folder"] = folder, ["frames"] = taken }) : null;
        };

        return null;
    }

    // How many frames the UI is hidden for before a capture.
    private const int WarmFrames = 24;

    // What a picture was of, recorded on the frame it was taken.
    private static Dictionary<string, object?> Manifest(string label, int index)
    {
        Dictionary<string, object?> m = new()
        {
            ["label"] = label,
            ["index"] = index,
            ["wall"] = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ["paused"] = KsaWorld.IsPaused,
            ["speed"] = KsaWorld.SimulationSpeed,
            ["fov_deg"] = Math.Round(KsaWorld.MainViewFovDeg(), 2),
        };

        if (KsaWorld.ControlledVehicle is { } craft)
        {
            m["craft"] = KsaWorld.DisplayName(craft);
            m["craft_screen"] = Screen(KsaWorld.PositionEcl(craft));
        }

        return m;
    }

    // Where a point falls in the picture, as fractions from the top left, or null when it is behind
    // the camera. The same matrix the frame was drawn with, so a crop taken off it is exact.
    private static double[]? Screen(double3 pointEcl)
    {
        if (Program.GetMainCamera() is not { } camera) return null;

        double4 clip = camera.EgoToClipDouble(pointEcl - camera.PositionEcl);
        if (!(clip.W > 1e-6)) return null;

        return [Math.Round((clip.X / clip.W * 0.5) + 0.5, 4), Math.Round((clip.Y / clip.W * 0.5) + 0.5, 4)];
    }
}
