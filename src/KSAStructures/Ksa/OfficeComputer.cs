using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// The office computer, played from the chair: the player's controls go to another craft, the main
/// view stays at the seated kitten's eyes, and the monitor shows that craft through one of the
/// engine's own camera windows.
///
/// <para>The picture is painted over the monitor in screen space, corner by corner, so it is drawn
/// with the interface and goes when the interface is hidden.</para>
/// </summary>
internal sealed class OfficeComputer : IViewPose
{
    private const double FovDeg = 50.0;

    // The seated kitten's eyes and the middle of the monitor, in the chair's own frame.
    private static readonly double3 EyeAsmb = new(0.78, 0.05, 0.0);
    private static readonly double3 LookAsmb = Vec.Unit(new double3(0.10, 0.95, 0.0));

    // The monitor's picture in the building's frame, as the viewer sees it: top left, top right,
    // bottom right, bottom left. A hair in front of the mesh that carries the still picture.
    // In the monitor's own frame, whose origin is the foot on the desk.
    private static readonly double3[] Corners =
    [
        new(0.52, -0.066, 0.36), new(0.52, -0.066, -0.36), new(0.12, -0.066, -0.36), new(0.12, -0.066, 0.36),
    ];

    private Vehicle? _chair;
    private Vehicle? _played;
    private KsaWorld.MainView _saved;
    private int _window = -1;
    private int _shown = -1;
    private bool _onTheMesh;

    public bool Playing => _played is not null;

    public IMouseDrag? Orbit => null;

    private static bool Seated(out Vehicle chair)
    {
        chair = KsaWorld.ControlledVehicle!;
        return chair is not null && KsaWorld.IsAlive(chair) && chair.Id == Office.ChairId;
    }

    /// <summary>The key and the picture, from the GUI pass.</summary>
    public void Draw()
    {
        try
        {
            bool enter = !ImGui.GetIO().WantTextInput
                         && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

            if (Playing)
            {
                if (enter) { Stop(); return; }
                Paint();
                Hint($"Playing {KsaWorld.DisplayName(_played!)} - Enter logs off");
            }
            else if (Seated(out Vehicle chair))
            {
                if (enter) Start(chair);
                else Hint("Enter: play KSA on the computer");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: {e.Message}");
        }
    }

    private static void Hint(string text)
    {
        ImGuiViewportPtr main = ImGui.GetMainViewport();
        float2 size = ImGui.CalcTextSize(text);
        var at = new float2(main.Pos.X + ((main.Size.X - size.X) / 2f), main.Pos.Y + main.Size.Y - 90f);
        ImGui.GetForegroundDrawList().AddText(at, new ImColor8(255, 255, 255, 220), text);
    }

    private void Start(Vehicle chair)
    {
        Vehicle? nearest = null;
        double best = double.MaxValue;
        double3 here = KsaWorld.PositionEcl(chair);
        foreach (Vehicle v in KsaWorld.Vehicles)
        {
            if (v is KittenEva || ReferenceEquals(v, chair) || !KsaWorld.IsAlive(v) || v.Id is Office.ChairId or Office.MonitorId) continue;

            double d = Vec.Len(KsaWorld.PositionEcl(v) - here);
            if (d < best) (best, nearest) = (d, v);
        }

        if (nearest is null) { Log.Info("office computer: there is no craft to play"); return; }
        if (!KsaWorld.TryOpenCameraWindow(out _window)) { Log.Warn("office computer: no camera window to show the game in"); return; }

        KsaWorld.TryHideViewportWindow(_window);
        KsaWorld.ResizeViewport(_window, new int2(768, 416));
        _shown = -1;
        _saved = KsaWorld.RememberMainView();
        _chair = chair;
        _played = nearest;
        Program.ControlledVehicle = nearest;
        Log.Info($"office computer: playing {KsaWorld.DisplayName(nearest)}, {best:F0} m away");
    }

    public void Stop()
    {
        if (!Playing) return;

        try
        {
            ShowOnTheMesh(live: false);
            if (_window >= 0) KsaWorld.CloseCameraWindow(_window);
            if (KsaWorld.IsAlive(_chair))
            {
                Program.ControlledVehicle = _chair;
                KsaWorld.TryHandBackMainView(_saved, _chair, out _, out _);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: could not log off cleanly ({e.Message})");
        }

        _window = -1;
        _chair = null;
        _played = null;
        Log.Info("office computer: logged off");
    }

    /// <summary>Both views, each frame: the kitten's eyes on the monitor, and the monitor's camera on the craft.</summary>
    public void Drive(double dt)
    {
        if (!Playing) return;
        if (!KsaWorld.IsAlive(_chair) || !KsaWorld.IsAlive(_played) || !KsaWorld.InFlightScene) { Stop(); return; }

        try
        {
            if (TryPose(KsaWorld.PositionEcl(_chair!), out double3 offset, out double3 forward, out double3 up, out double fov))
                KsaWorld.TryLookFromMainViewport(offset, forward, up, fov, this);

            // From the office's side of the craft, a little above it, so the tower is behind the camera.
            double3 at = KsaWorld.PositionEcl(_played!);
            double3 vertical = KsaWorld.LocalUp(_played!);
            double3 away = Vec.Unit(Vec.RejectFrom(at - KsaWorld.PositionEcl(_chair!), vertical));
            if (!Vec.IsFinite(away) || Vec.Len2(away) < 0.5) away = Vec.Unit(Vec.AnyPerpendicular(vertical));

            double distance = Math.Clamp(6.0 * KsaWorld.MeanRadius(_played!), 20.0, 250.0);
            double3 eye = at - (away * distance) + (vertical * (distance * 0.3));
            KsaWorld.TryLookFromViewport(_window, eye, Vec.Unit(at - eye), vertical, FovDeg, dt);
            ShowOnTheMesh(live: true);
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: {e.Message}");
        }
    }

    public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                        out double3 upEcl, out double fovDeg)
    {
        offsetFromFollowed = forwardEcl = upEcl = default;
        fovDeg = FovDeg;
        if (_chair is not { } chair || !KsaWorld.IsAlive(chair)) return false;

        // A separation and two directions, all in the chair's own frame: nothing here carries the
        // ecliptic's motion.
        float3 c = chair.CenterOfMassAsmbF;
        doubleQuat toEcl = chair.Body2Cce;
        offsetFromFollowed = double3.Transform(EyeAsmb - new double3(c.X, c.Y, c.Z), toEcl);
        forwardEcl = double3.Transform(LookAsmb, toEcl);
        upEcl = double3.Transform(new double3(1, 0, 0), toEcl);
        return Vec.IsFinite(offsetFromFollowed) && Vec.IsFinite(forwardEcl);
    }

    // The monitor's own textures pointed at the camera window's picture, so the monitor shows it as
    // part of the scene: in perspective, behind whatever stands in front, and with the interface
    // hidden. Pointed again whenever the engine rebuilds the picture, and back at the still on the
    // way out.
    private void ShowOnTheMesh(bool live)
    {
        try
        {
            PbrMaterialReference material = ModLibrary.Get<PbrMaterialReference>("KSAOffice_MonitorMaterial");
            if (material.DiffuseReference is not { } diffuse) return;

            var textures = Program.Instance.BindlessTextures;
            if (!live)
            {
                if (_onTheMesh)
                {
                    textures.SetTexture(diffuse.BindlessHandle, diffuse.ImageView);
                    if (material.EmissiveMap is { } still) textures.SetTexture(still.BindlessHandle, still.ImageView);
                }

                _onTheMesh = false;
                _shown = -1;
                return;
            }

            if (!KsaWorld.TryViewportImage(_window, out var view, out int generation)) return;
            if (generation == _shown && _onTheMesh) return;

            textures.SetTexture(diffuse.BindlessHandle, view);

            // The glow too, or the still desktop goes on glowing through the game.
            if (material.EmissiveMap is { } glow) textures.SetTexture(glow.BindlessHandle, view);
            _shown = generation;
            _onTheMesh = true;
        }
        catch (Exception e)
        {
            if (_onTheMesh || _shown != -2) Log.Warn($"office computer: the monitor cannot show the game itself ({e.Message}); painting it over instead");
            _onTheMesh = false;
            _shown = -2;
        }
    }

    // A static object's shader has no emissive term, so the picture on the mesh is only as bright
    // as the light falling on it. Painted over it with the interface, it is as bright as a screen
    // is. In strips, each with its own projected corners, so the picture keeps its perspective.
    private void Paint()
    {
        if (!KsaWorld.TryViewportTexture(_window, out ImTextureRef texture, out _)) return;
        if (Program.MainViewport is not { } view || view.GetCamera() is not { } camera) return;

        Vehicle? monitor = null;
        foreach (Vehicle v in KsaWorld.Vehicles)
            if (v.Id == Office.MonitorId && KsaWorld.IsAlive(v)) monitor = v;
        if (monitor is null) return;

        // Through the matrix the engine draws the monitor's own parts with, so the picture sits on
        // the mesh wherever the monitor has ended up.
        double4x4 asmb2Ego = monitor.GetMatrixAsmb2Ego(camera);

        const int Across = 8, Down = 5;
        Span<float2> grid = stackalloc float2[(Across + 1) * (Down + 1)];
        for (int j = 0; j <= Down; j++)
        {
            for (int i = 0; i <= Across; i++)
            {
                double u = (double)i / Across, v = (double)j / Down;
                double3 top = Corners[0] + ((Corners[1] - Corners[0]) * u);
                double3 bottom = Corners[3] + ((Corners[2] - Corners[3]) * u);
                double3 ecl = camera.EgoToEcl((top + ((bottom - top) * v)).Transform(asmb2Ego));

                float2 s = camera.EclToScreen(ecl, ignoreBehind: true);
                if (!float.IsFinite(s.X) || !float.IsFinite(s.Y)) return;
                grid[(j * (Across + 1)) + i] = new float2(view.Position.X + s.X, view.Position.Y + s.Y);
            }
        }

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        for (int j = 0; j < Down; j++)
        {
            for (int i = 0; i < Across; i++)
            {
                int a = (j * (Across + 1)) + i, b = a + Across + 1;
                float u0 = (float)i / Across, u1 = (float)(i + 1) / Across, v0 = (float)j / Down, v1 = (float)(j + 1) / Down;
                float2? tl = new float2(u0, v0), tr = new float2(u1, v0), br = new float2(u1, v1), bl = new float2(u0, v1);
                draw.AddImageQuad(texture, in grid[a], in grid[a + 1], in grid[b + 1], in grid[b], in tl, in tr, in br, in bl, null);
            }
        }
    }
}
