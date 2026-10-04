using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Rendering.Water.Rendering;

namespace KSAStructures;

/// <summary>
/// The office's vault kept dry. KSA's sea is one sphere round the planet with no land under it, so a
/// pit dug below sea level is under water to everything: a craft in it floats, a kitten swims, and the
/// surface is drawn through it. Inside one roofed volume a craft is told the body has no ocean, and
/// while the camera is in it the ocean is not drawn at all.
/// </summary>
internal static class OfficeDry
{
    // A column under a place on the body's surface, between two radii from its centre. The list is
    // swapped whole: the physics postfix reads it on worker threads.
    private sealed class Volume
    {
        public required IParentBody Body;
        public required double3 AxisCcf;
        public required double RadiusMetres, MinRadius, MaxRadius;
    }

    private static volatile Volume[] _volumes = [];
    private static volatile bool _mainCameraDry;

    public static bool Defined => _volumes.Length > 0;

    /// <summary>Adds a column about a direction from the body's centre, between two radii.</summary>
    public static void Define(Celestial body, double3 axisCcf, double radiusMetres, double minRadius, double maxRadius)
    {
        _volumes = [.. _volumes, new Volume
        {
            Body = body, AxisCcf = axisCcf.Normalized(), RadiusMetres = radiusMetres, MinRadius = minRadius, MaxRadius = maxRadius,
        }];
        Log.Info($"office: dry volume defined, {maxRadius - minRadius:F0} m deep and {radiusMetres * 2.0:F0} m across");
    }

    public static void Forget()
    {
        _volumes = [];
        _mainCameraDry = false;
    }

    /// <summary>Once a frame, on the main thread: whether the main camera is in a volume.</summary>
    public static void Watch()
    {
        try
        {
            _mainCameraDry = Program.MainViewport is { } viewport && viewport.GetCamera()?.NearbyCelestial is { } body
                             && CameraInside(body, viewport);
        }
        catch
        {
            _mainCameraDry = false;
        }
    }

    public static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.RecomputePositionalValues), null, nameof(AfterPositional),
              "craft in the vault float");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.Render), nameof(BeforeOcean), null, "the sea is drawn through the vault");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.RenderUnderwater), nameof(BeforeUnderwater), null,
              "the vault is fogged as if under water");
        Patch(harmony, typeof(OceanRenderer), nameof(OceanRenderer.GetMaxVisibleDepth), null, nameof(AfterMaxVisibleDepth),
              "the vault's floor is black from above sea level");
    }

    private static void Patch(Harmony harmony, Type type, string method, string? prefix, string? postfix, string cost)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(type, method);
            if (target is null) { Log.Warn($"office: no {type.Name}.{method}; {cost}"); return; }

            const BindingFlags Mine = BindingFlags.NonPublic | BindingFlags.Static;
            harmony.Patch(target,
                          prefix: prefix is null ? null : new HarmonyMethod(typeof(OfficeDry).GetMethod(prefix, Mine)!),
                          postfix: postfix is null ? null : new HarmonyMethod(typeof(OfficeDry).GetMethod(postfix, Mine)!));
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not patch {type.Name}.{method} ({e.GetBaseException().Message}); {cost}");
        }
    }

    private static bool Inside(IParentBody body, double3 positionCcf)
    {
        Volume[] volumes = _volumes;
        if (volumes.Length == 0) return false;

        double radius = positionCcf.Length();
        foreach (Volume volume in volumes)
        {
            if (!ReferenceEquals(body, volume.Body) || radius < volume.MinRadius || radius > volume.MaxRadius) continue;

            double along = double3.Dot(positionCcf, volume.AxisCcf);
            if ((radius * radius) - (along * along) <= volume.RadiusMetres * volume.RadiusMetres && along > 0.0) return true;
        }

        return false;
    }

    // Worker threads, several times a sub-step: nothing here may allocate or throw. A craft already
    // floating is placed at the ocean's radius by the engine, so it is left its ocean.
    private static void AfterPositional(ref PhysicsEnvironment __instance, double3 positionClosestParentCcf, in VehicleProperties props)
    {
        try
        {
            if (__instance.OceanRadius <= 0.0 || props.Situation == Situation.Floating) return;
            if (!Inside(__instance.ClosestParent, positionClosestParentCcf)) return;

            __instance.OceanRadius = 0.0;
            __instance.OceanVolume = 0.0;
            __instance.OceanSurfaceArea = 0.0;
        }
        catch
        {
        }
    }

    private static bool BeforeOcean(Celestial atmosphericBody, IViewport inViewport)
    {
        try { return !CameraInside(atmosphericBody, inViewport); }
        catch { return true; }
    }

    private static bool BeforeUnderwater(Celestial? celestial, IViewport inViewport)
    {
        try { return celestial is null || !CameraInside(celestial, inViewport); }
        catch { return true; }
    }

    // Terrain further under the sea than the water is clear is drawn black to a camera above it.
    private static void AfterMaxVisibleDepth(Celestial celestial, ref double __result)
    {
        try { if (_mainCameraDry) __result = -1.0; }
        catch { }
    }

    private static bool CameraInside(Celestial body, IViewport viewport) =>
        Inside(body, (-viewport.GetCamera().GetPositionEgo(body)).Transform(body.GetCce2Ccf()));
}
