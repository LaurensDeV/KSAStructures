using System.Reflection;
using HarmonyLib;
using KSA;

namespace KSAStructures;

/// <summary>The chair and the computer: craft of one part each, set down in the office once per world.</summary>
internal static partial class Office
{
    public const string ChairId = "Office chair";
    private const string ChairPart = "KSArmory_Prefab_OfficeChair";

    private const double OfficeFloor = 61.8;
    private const double GroundSurface = 0.0;

    public const string MonitorId = "Office computer";

    // What stands in the office as craft of its own: metres east of the tower's landmark, and the
    // height of what it stands on above the tower's ground.
    private static readonly (string Id, string Part, double East, double Height)[] Furniture =
    [
        (ChairId, ChairPart, 6.7, OfficeFloor + 0.03),
        (MonitorId, "KSArmory_Prefab_OfficeMonitor", 7.76, OfficeFloor + 0.56 + 0.01),
    ];

    private static bool _furnishing;
    private static double _sinceChairCheck = 3.0;
    private static bool _chairFailed;

    /// <summary>
    /// The furniture left out of a save. A save names each craft's part templates, and one naming a
    /// template that is not installed ends the game on load, so a save holding the chair could not be
    /// opened without this mod. They are set down again whenever they are missing. A piece somebody is
    /// flying or sitting in is the exception: taking it out would take them with it.
    /// </summary>
    public static void KeepOutOfSaves(Harmony harmony)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Universe), nameof(Universe.SerializeSystems));
            if (target is null) { Log.Warn("office: no save hook; a save holds the office's furniture"); return; }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(Office).GetMethod(nameof(AfterSerialize), BindingFlags.NonPublic | BindingFlags.Static)!));
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not hook the save ({e.GetBaseException().Message})");
        }
    }

    private static void AfterSerialize(List<CelestialSystemData> __result)
    {
        try
        {
            string? flown = KsaWorld.ControlledVehicle?.Id;
            foreach (CelestialSystemData system in __result)
            {
                system.Vehicles.RemoveAll(v => v.Id != flown && Array.Exists(Furniture, piece => piece.Id == v.Id));
            }
        }
        catch
        {
        }
    }

    // One chair per world: a save that already holds it is left alone.
    private static void EnsureChair(double dtSim)
    {
        _sinceChairCheck += dtSim;
        if (_chairFailed || _sinceChairCheck < 3.0 || _tower?.GetStaticObject() is not { } tower) return;
        _sinceChairCheck = 0.0;

        if (!KsaWorld.InFlightScene || KsaWorld.ControlledVehicle is not { } flown || !KsaWorld.IsAlive(flown)) return;
        if (KsaWorld.ParentBody(flown) is not { } body || !ReferenceEquals(body.BodyTemplate, _earth)) return;

        // One a pass: building a craft waits out the engine's worker.
        foreach ((string id, string part, double east, double height) in Furniture)
        {
            bool there = false;
            foreach (Vehicle v in KsaWorld.Vehicles) there |= v.Id == id;
            if (there) continue;

            // The launch menu's own placement stands a craft on the pad surface of the landmark it
            // is within the footprint of, so for this one call the tower's surface is whatever this
            // stands on.
            // Over the levelled ground, whatever is dug under it: the basement lies under the office.
            double least = LeastDug(body, east - 3.0, 0.0, 0.4, 0.4);
            tower.Template.SurfaceHeight = new DistanceReference(height + least);
            _furnishing = true;
            try
            {
                _chairFailed = CraftSpawner.SpawnPartParked(flown, part, id, body, LatitudeDeg,
                                                          TowerLongitudeDeg + (east / 97730.0)) is null;
                if (_chairFailed) Log.Warn($"office: the {id.ToLowerInvariant()} could not be set down");
            }
            finally
            {
                _furnishing = false;
                tower.Template.SurfaceHeight = new DistanceReference(GroundSurface);
            }

            return;
        }
    }
}
