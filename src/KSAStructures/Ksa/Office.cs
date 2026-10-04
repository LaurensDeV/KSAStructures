using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSAStructures;

/// <summary>
/// Stands the office tower on Earth and runs its lift.
///
/// <para>A kitten walks only on what the engine calls ground, and a launch-pad landmark's static
/// object is ground where a craft's colliders are not. The engine draws and collides a static
/// object only for a landmark flagged <c>IsLaunchPad</c>, so the tower is one — and the lift car
/// is a second, whose <c>GroundOffset</c> is its height.</para>
///
/// <para><b>A craft collides with its nearest launch pad only.</b> The two landmarks stand 3 m
/// apart so the plane between them is the lift doorway: a kitten past it stands on the car alone.</para>
/// </summary>
internal static partial class Office
{
    public const string TowerLandmark = "Kessler Systems HQ";
    public const string LiftLandmark = "Kessler Systems HQ lift";
    private const string TowerId = "KSAOffice_Tower";
    private const string LiftId = "KSAOffice_LiftObject";
    private const string LiftVisualId = "KSAOffice_LiftVisual";
    private const string HarmonyId = "com.kesslersystems.ksastructures.office";

    private static Harmony? _harmony;
    private static StaticObject? _carVisual;

    // 190 m west of CCSFS LC-39A: outside the pad's 108 m footprint, inside the 275 m disc Core
    // levels around it. The lift landmark is the shaft centre, 3 m west of the tower landmark.
    private const double LatitudeDeg = 28.60829876577433;

    // Both landmarks stand 7.6 m south of the building's own east-west line, on ground nothing is
    // dug under: a static object stands at the terrain height of its landmark's point, and the
    // lift's shaft is dug. Still either side of the lift doorway's plane, at one height.
    private const double LandmarkNorth = -7.6;
    private const double LandmarkLatitudeDeg = LatitudeDeg + (LandmarkNorth / 110860.0);
    private const double TowerLongitudeDeg = -80.60607;
    private const double LiftLongitudeDeg = TowerLongitudeDeg - (3.0 / 97730.0);

    /// <summary>This mod, as the loader handed it over: what a texture path resolves against.</summary>
    public static Mod? Owner { get; set; }

    private static LandmarkReference? _tower;
    private static LandmarkReference? _lift;
    private static StaticObject? _car;
    private static CelestialTemplate? _earth;

    public static void Place()
    {
        try
        {
            if (!ModLibrary.TryGet("Earth", out CelestialTemplate? earth) || earth is null)
            {
                Log.Warn("office: no Earth template to stand the tower on");
                return;
            }

            if (earth.Locations.Exists(l => l.Id == TowerLandmark)) return;

            Dig(earth);

            _tower = Landmark(TowerLandmark, TowerId, TowerLongitudeDeg);
            _lift = Landmark(LiftLandmark, LiftId, LiftLongitudeDeg);
            // After Core's pads, so a launch from one of them still stands on its own pad where the
            // footprints overlap: the first landmark whose footprint holds a place gives its height.
            earth.Locations.Add(_tower);
            earth.Locations.Add(_lift);

            _earth = earth;
            HoldUpWheels();

            MethodInfo? target = AccessTools.Method(typeof(LocationReference), nameof(LocationReference.UpdateStaticObjectRenderData));
            MethodInfo postfix = typeof(Office).GetMethod(nameof(AfterRenderData), BindingFlags.NonPublic | BindingFlags.Static)!;
            if (target is null) Log.Warn("office: no render hook, the lift car is not drawn");
            else
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Underground.Install(_harmony);
                OfficeDry.Install(_harmony);
                ClutterZones.Install(_harmony);
                KeepOutOfSaves(_harmony);
            }
            Log.Info("office: landmarks placed");
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not place the tower ({e.Message})");
        }
    }

    private static LandmarkReference Landmark(string id, string staticObject, double longitudeDeg)
    {
        var landmark = new LandmarkReference
        {
            Id = id,
            IsLaunchPad = true,
            StaticObjectId = staticObject,
            Latitude = new RadianReference(LandmarkLatitudeDeg * Math.PI / 180.0),
            Longitude = new RadianReference(longitudeDeg * Math.PI / 180.0),
        };
        landmark.OnDataLoad(Mod.Empty);
        return landmark;
    }

    public static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        Underground.Remove();
        OfficeDry.Forget();
        _harmony = null;
    }

    // Draws the car where the riders are put, as the engine draws a landmark's own static object,
    // and the tower itself whenever the engine has declined to. The engine draws a landmark's object
    // only to a camera above the tangent plane at the landmark's point on the planet's raw height
    // map, which knows nothing of the levelling: a camera low in the tunnel is under that plane, and
    // the whole site vanished. Inside the engine's render pass: nothing here may throw.
    private static void AfterRenderData(LocationReference __instance, IViewport viewport, Celestial celestial, int frameIndex)
    {
        try
        {
            bool tower = ReferenceEquals(__instance, _tower);
            if (!tower && !ReferenceEquals(__instance, _lift)) return;

            Camera camera = viewport.GetCamera();
            doubleQuat ccf2Cce = celestial.GetCcf2Cce();
            double3 forwardCce = __instance.ForwardCcf.Transform(ccf2Cce);
            double3 origin = camera.EclToEgo(celestial.GetSurfacePositionEclFromCce(forwardCce));
            __instance.GetAxesCcf(out double3 upCcf, out double3 eastCcf, out double3 northCcf);
            double3 up = upCcf.Transform(ccf2Cce), east = eastCcf.Transform(ccf2Cce), north = northCcf.Transform(ccf2Cce);

            if (tower)
            {
                Underground.Light(viewport, origin + (east * 3.0) - (north * LandmarkNorth), up, east, north);
                if (camera.IsVisibleTo(forwardCce, celestial)) return;
            }

            StaticObject? drawn = tower ? __instance.GetStaticObject() : _carVisual;
            if (drawn is null) return;

            double car = _drawn;
            if (!tower && _carOnRider > 0 && KsaWorld.ControlledVehicle is { } rider && KsaWorld.IsAlive(rider)
                && Rest.TryGetValue(rider, out double rest))
            {
                car = Math.Clamp(InLiftFrame(rider, celestial).X - rest, LowestStop, OfficeStop);
            }

            double3 at = tower ? origin + (up * drawn.GroundOffset)
                               : origin + (up * (car + _correction)) - (north * LandmarkNorth);

            var asmb2Ego = new double4x4(up.X, up.Y, up.Z, 0.0, east.X, east.Y, east.Z, 0.0,
                                         north.X, north.Y, north.Z, 0.0, at.X, at.Y, at.Z, 1.0);
            drawn.UpdateRenderData(viewport, frameIndex, in asmb2Ego);
        }
        catch { }
    }

    /// <summary>
    /// A view from a place in the building's frame, as an offset from the craft the camera follows:
    /// up, east and north of the building's origin, looking along a compass heading and a pitch.
    /// </summary>
    public static bool TryView(Vehicle followed, double x, double y, double z, double headingDeg, double pitchDeg,
                               out double3 offset, out double3 forward, out double3 up)
    {
        offset = forward = up = default;
        if (_tower is null || _lift is null || KsaWorld.ParentBody(followed) is not { } body) return false;

        double3 at = InLiftFrame(followed, body);
        _lift.GetAxesCcf(out double3 upCcf, out double3 eastCcf, out double3 northCcf);
        doubleQuat ccf2Cce = body.GetCcf2Cce();
        up = upCcf.Transform(ccf2Cce);
        double3 east = eastCcf.Transform(ccf2Cce), north = northCcf.Transform(ccf2Cce);

        // The lift's landmark stands on the shaft's centre, six metres west of the building's origin.
        offset = (up * (x - at.X)) + (east * (y - (at.Y - 6.0))) + (north * (z - at.Z));

        double heading = headingDeg * Math.PI / 180.0, pitch = pitchDeg * Math.PI / 180.0;
        forward = (((north * Math.Cos(heading)) + (east * Math.Sin(heading))) * Math.Cos(pitch)) + (up * Math.Sin(pitch));
        return true;
    }

    // Up, east, north from the lift landmark's ground point, with the tower's ground as zero height.
    private static double3 InLiftFrame(Vehicle v, Celestial body)
    {
        double3 posCcf = v.GetPositionCce().Transform(body.GetCce2Ccf());
        double ground = body.MeanRadius + body.GetTerrainHeightFromDirCcf(_tower!.ForwardCcf);
        _lift!.GetAxesCcf(out double3 up, out double3 east, out double3 north);
        double3 d = posCcf - (_lift.ForwardCcf * ground);
        // About the shaft's centre, which stands north of the landmark by as much as the landmark was moved south.
        return new double3(double3.Dot(d, up), double3.Dot(d, east), double3.Dot(d, north) + LandmarkNorth);
    }
}
