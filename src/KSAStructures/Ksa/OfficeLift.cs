using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// The lift: the car's motion between its stops, who is aboard, the floor buttons, and the riders
/// carried with it.
/// </summary>
internal static partial class Office
{
    // Heights of the car floor above the tower's ground, a lip above each landing.
    private const double GarageStop = -4.98;
    private const double LobbyStop = 0.22;
    private const double OfficeStop = 61.82;

    // How far under the ground the vault's floor is, or zero for none: read at load from a file beside
    // the assembly, because the pit is a terrain decal and those can only be added then.
    private static readonly double VaultDepth = ReadVaultDepth();
    private static readonly double VaultStop = -VaultDepth + 0.02;
    private static readonly double[] Stops = VaultDepth > 0.0 ? [VaultStop, GarageStop, LobbyStop, OfficeStop] : [GarageStop, LobbyStop, OfficeStop];
    private static readonly string[] StopNames = VaultDepth > 0.0 ? ["vault", "garage", "lobby", "office"] : ["garage", "lobby", "office"];
    private static double LowestStop => Stops[0];

    // Under the 1.5 m/s the engine's ground contact pushes a body out at: the floor carries a
    // rider by contact alone, so it stays on its feet and can walk about the car.
    private const double MaxSpeed = 1.3;
    private const double Acceleration = 0.8;
    private const double DwellSeconds = 4.0;

    // An express lift: it leaves and arrives at walking pace, and the shaft between is skipped in
    // one step, car and riders together. Nothing in the shaft tells one metre from the next.
    private const double CrawlMetres = 3.0;

    private static double _leftFrom = LobbyStop;
    private static readonly Dictionary<Vehicle, double> Skips = [];

    // The floor a rider asked for with a number key, read in the GUI pass and taken by the step.
    // Frames left in which the car is drawn under its rider and not at its own height. A rider's
    // move reaches the screen a frame or two after it is written, through the engine's worker, and
    // the car drawn 55 m ahead of it leaves the rider looking at an empty, sunlit shaft: a flash.
    private static int _carOnRider;

    private static int _asked = -1;
    private static bool _goingUp = true;
    private static bool _flownAboard;

    private static double _height = LobbyStop;
    private static double _target = LobbyStop;
    private static double _speed;
    private static double _dwell;
    private static bool _armed = true;
    private static double _sinceLog;
    // Each rider's height above the car floor, as of the last step, and what it was at departure.
    private static readonly Dictionary<Vehicle, double> Riders = [];
    private static readonly Dictionary<Vehicle, double> Rest = [];
    private static int _carried, _refused;
    private static double _drawn = LobbyStop, _lastStep = 1.0 / 60.0, _settle, _correction;
    private static double _sinceRideLog;

    private static void Leave(double stop)
    {
        _leftFrom = _height;
        _target = stop;
    }

    /// <summary>The rider's floor buttons, from the GUI pass: ImGui is what sees the keys.</summary>
    public static void Keys()
    {
        try
        {
            if (!_flownAboard || _height != _target || ImGui.GetIO().WantTextInput) return;

            // The garage, lobby and office keep their keys with or without a vault under them.
            int first = Stops.Length - 3;
            if (ImGui.IsKeyPressed(ImGuiKey._1, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad1, repeat: false)) _asked = first;
            else if (ImGui.IsKeyPressed(ImGuiKey._2, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad2, repeat: false)) _asked = first + 1;
            else if (ImGui.IsKeyPressed(ImGuiKey._3, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad3, repeat: false)) _asked = first + 2;
            else if (first > 0 && (ImGui.IsKeyPressed(ImGuiKey._0, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0, repeat: false))) _asked = 0;

            int here = Array.FindIndex(Stops, stop => Math.Abs(stop - _height) < 0.01);
            string hint = $"Lift at the {(here >= 0 ? StopNames[here] : "?")}  -  press {(first > 0 ? "0 vault,  " : "")}1 garage,  2 lobby,  3 office"
                          + (_asked >= 0 && _asked != here ? $"   (going to the {StopNames[_asked]})" : "");
            ImGuiViewportPtr main = ImGui.GetMainViewport();
            float2 size = ImGui.CalcTextSize(hint);
            ImGui.GetForegroundDrawList().AddText(
                new float2(main.Pos.X + ((main.Size.X - size.X) / 2f), main.Pos.Y + main.Size.Y - 120f),
                new ImColor8(255, 255, 255, 220), hint);
        }
        catch
        {
        }
    }

    /// <summary>Advance the lift by one simulated step.</summary>
    public static void Update(double dtSim)
    {
        if (_lift is null || _tower is null) return;

        // The engine builds static objects once every mod's game data is attached, which is after
        // this mod places its landmarks, so the car is looked up when the world is first stepped.
        _car ??= StaticObject.Find(LiftId);
        _carVisual ??= StaticObject.Find(LiftVisualId);
        if (_car is null) return;

        try
        {
            EnsureChair(dtSim);
            TameTheRim(KsaWorld.ControlledVehicle is { } flownNow && KsaWorld.IsAlive(flownNow) && KsaWorld.ParentBody(flownNow) is { } on
                           && ReferenceEquals(on.BodyTemplate, _earth) ? on : null);
            if (!OfficeDry.Defined && KsaWorld.ControlledVehicle is { } any && KsaWorld.ParentBody(any) is { } planet
                && ReferenceEquals(planet.BodyTemplate, _earth))
            {
                // Only what is dug below the sea: the deep pit, and the vault where there is one.
                double level = planet.MeanRadius + planet.GetTerrainHeightFromDirCcf(_tower.ForwardCcf);
                _tower.GetAxesCcf(out _, out double3 dryEast, out double3 dryNorth);
                double3 At(double east, double north) =>
                    (_tower.ForwardCcf * level) + (dryEast * (east + 3.0)) + (dryNorth * (north - LandmarkNorth));

                OfficeDry.Define(planet, At(OfficeDeep.PitEast, OfficeDeep.PitNorth), OfficeDeep.PitRadius + 2.0,
                                 level - OfficeDeep.Depth - 30.0, level - 0.2);
                if (VaultDepth > 0.0)
                {
                    OfficeDry.Define(planet, At(VaultEast, VaultNorth), VaultRadius + 2.0, level - VaultDepth - 30.0, level - 0.2);
                }
            }

            OfficeDry.Watch();

            // The car first, so every rider below is measured against the floor of the same instant.
            if (_settle > 0.0) _settle -= dtSim;
            if (_carOnRider > 0) _carOnRider--;
            bool wasMoving = _speed != 0.0;
            if (_height != _target && dtSim > 0.0)
            {
                double left = Math.Abs(_target - _height);
                double wanted = Math.Min(MaxSpeed, Math.Sqrt(2.0 * Acceleration * left) + 0.05);
                double speed = Math.Min(wanted, Math.Abs(_speed) + (Acceleration * dtSim));
                double step = speed * dtSim;

                if (step >= left) { _height = _target; _speed = 0.0; Log.Info($"office: lift arrived at {_height:F1} m"); }
                else { _speed = Math.Sign(_target - _height) * speed; _height += _speed * dtSim; }

                // Only the blind shafts are skipped, lobby to office and vault to garage: the car
                // passes every doorway at its own pace, whichever way it is going and wherever from.
                bool up = _target > _height;
                foreach ((double low, double high) in (ReadOnlySpan<(double, double)>)
                         [(LobbyStop + CrawlMetres, OfficeStop - CrawlMetres), (VaultStop + CrawlMetres, GarageStop - CrawlMetres)])
                {
                    if (high <= low || _height <= low - 0.001 || _height >= high + 0.001 || (up ? _target <= high : _target >= low)) continue;

                    double by = (up ? high : low) - _height;
                    if (Math.Abs(by) > 1.0)
                    {
                        _height += by;
                        foreach (Vehicle rider in Riders.Keys) Skips[rider] = by;
                        _carOnRider = 12;
                    }
                }
            }
            else _speed = 0.0;
            if (wasMoving && _speed == 0.0) _settle = 0.25;

            // What the engine draws and collides next frame is the car a step on, so that the floor
            // and the rider carried onto it are in the same place when that frame is drawn.
            _lastStep = dtSim > 0.0 ? dtSim : _lastStep;
            _drawn = _speed == 0.0 ? _height : Math.Clamp(_height + (_speed * _lastStep), LowestStop, OfficeStop);

            Riders.Clear();
            Span<bool> called = stackalloc bool[Stops.Length];
            _flownAboard = false;
            Celestial? earth = null;

            foreach (Vehicle v in KsaWorld.Vehicles)
            {
                if (v is not KittenEva || !KsaWorld.IsAlive(v)) continue;
                if (KsaWorld.ParentBody(v) is not { } body || !ReferenceEquals(body.BodyTemplate, _earth)) continue;

                earth = body;
                double3 local = InLiftFrame(v, body);
                if (Math.Abs(local.Y) > 40.0 || Math.Abs(local.Z) > 40.0) continue;

                double aboveFloor = local.X - _height;
                bool inCar = local.Y is > -1.3 and < 1.45 && Math.Abs(local.Z) < 1.3 && aboveFloor is > -0.6 and < 2.7;
                if (inCar)
                {
                    Riders[v] = aboveFloor;
                    _flownAboard |= ReferenceEquals(v, KsaWorld.ControlledVehicle);
                }
                else if (Math.Abs(local.Y) < 10.0 && Math.Abs(local.Z) < 10.0)
                {
                    // Somebody waiting by the shaft on a floor calls the car there.
                    for (int i = 0; i < Stops.Length; i++) called[i] |= Math.Abs(local.X - Stops[i]) < 3.0;
                }

                _sinceLog += dtSim;
                if (_sinceLog > 2.0)
                {
                    _sinceLog = 0.0;
                    Log.Debug($"office: kitten at up {local.X:F2} east {local.Y:F2} north {local.Z:F2} in the lift's "
                              + $"frame, car {_height:F2} m, {(inCar ? "aboard" : "not aboard")}");
                }
            }

            bool moving = _height != _target;
            if (!moving && dtSim > 0.0)
            {
                int here = Array.FindIndex(Stops, stop => Math.Abs(stop - _height) < 0.01);
                if (Riders.Count == 0)
                {
                    _armed = true;
                    _dwell = 0.0;
                    _asked = -1;

                    int calls = 0, calling = -1;
                    for (int i = 0; i < Stops.Length; i++)
                        if (called[i]) { calls++; calling = i; }
                    if (calls == 1 && calling != here) Leave(Stops[calling]);
                }
                else
                {
                    // A number key takes it there at once, however long the rider has stood in it.
                    // With none, a rider who has just stepped in is carried on the way the car was
                    // going after a few seconds, turning round at the top and the bottom; one who
                    // has just arrived stays put.
                    _dwell += dtSim;
                    int to = _asked >= 0 && _asked != here ? _asked : -1;
                    if (to < 0 && _armed && _dwell >= DwellSeconds && here >= 0)
                    {
                        if (here == Stops.Length - 1) _goingUp = false;
                        if (here == 0) _goingUp = true;
                        to = here + (_goingUp ? 1 : -1);
                    }

                    if (to >= 0)
                    {
                        _armed = false;
                        _asked = -1;
                        _goingUp = Stops[to] > _height;
                        Rest.Clear();
                        foreach ((Vehicle r, double above) in Riders) Rest[r] = above;
                        Leave(Stops[to]);
                        Log.Info($"office: lift leaving for the {StopNames[to]} with {Riders.Count} aboard");
                    }
                }
            }

            if (_speed != 0.0)
            {
                _sinceRideLog += dtSim;
                if (_sinceRideLog >= 0.5)
                {
                    _sinceRideLog = 0.0;
                    string riders = string.Join(", ", Riders.Select(r =>
                        $"{r.Value:F2} m above the floor, {r.Key.Situation}, bubble {r.Key.HasPhysicsBubble}"));
                    Log.Info($"office: car {_height:F2} m at {_speed:F2} m/s; riders [{riders}]; "
                             + $"carried {_carried}, refused {_refused}");
                    _carried = 0;
                    _refused = 0;
                }
            }

            // Each landmark sits on the terrain under its own point, so the car's height is taken
            // from the tower's ground rather than its own.
            double correction = 0.0;
            if (earth is not null)
                correction = earth.GetTerrainHeightFromDirCcf(_tower.ForwardCcf) - earth.GetTerrainHeightFromDirCcf(_lift.ForwardCcf);
            if (!double.IsFinite(correction)) correction = 0.0;

            _correction = correction;

            _car.Template.GroundOffset = new DistanceReference(_height + correction);
        }
        catch (Exception e)
        {
            Log.Warn($"office: lift step failed ({e.Message})");
        }
    }

    private static readonly Dictionary<Vehicle, double3> Moves = [];

    /// <summary>Sets a craft down at a place in the building's frame, for a test that cannot drive there.</summary>
    public static bool Put(Vehicle craft, double x, double y, double z)
    {
        if (!TryView(craft, x, y, z, 0.0, 0.0, out double3 offset, out _, out _)) return false;

        Moves[craft] = offset;
        return true;
    }

    // Keep a rider awake while the car moves. A kitten standing still sleeps, the engine does not
    // move a sleeping craft's launch-pad collider, and the floor would leave without it. Only from
    // BeforeWorker's window.
    private static void Carry(Vehicle v)
    {
        if (Moves.Remove(v, out double3 to))
        {
            VehicleCommand.TryChangeVelocity(v, double3.Zero, to.Transform(doubleQuat.Conjugate(v.Body2Cce)));
            return;
        }

        if (Skips.Remove(v, out double by) && _lift is not null && KsaWorld.ParentBody(v) is { } body)
        {
            _lift.GetAxesCcf(out double3 upCcf, out _, out _);
            double3 move = (upCcf.Transform(body.GetCcf2Cce()) * by).Transform(doubleQuat.Conjugate(v.Body2Cce));
            if (VehicleCommand.TryChangeVelocity(v, double3.Zero, move)) _carried++;
            else _refused++;
            return;
        }

        if (_speed == 0.0 || !Riders.ContainsKey(v) || !v.Situation.IsOnRails()) return;

        if (VehicleCommand.TryChangeVelocity(v, double3.Zero)) _carried++;
        else _refused++;
    }
}
