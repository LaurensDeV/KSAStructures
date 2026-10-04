using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSAStructures;

/// <summary>
/// Ground clutter kept off the launch pads nearest the camera. The engine clears it round the first
/// four launch pads in a body's list and no others, and it grows from the terrain as dug, so a site
/// past the fourth has trees on the floor of its pit. Which four are first is a matter of which mod
/// added its pads when; which four are nearest is not.
/// </summary>
internal static class ClutterZones
{
    private const int Zones = 4;
    private const double CellMargin = 50.0;

    public static void Install(Harmony harmony)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(GroundClutterPlacementData), "PopulateLaunchPadExclusionZones");
            if (target is null) { Log.Warn("office: no clutter hook; grass and trees grow in what is dug"); return; }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ClutterZones).GetMethod(nameof(Nearest), BindingFlags.NonPublic | BindingFlags.Static)!));
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not hook the clutter zones ({e.GetBaseException().Message})");
        }
    }

    private static void Nearest(ref ClutterCubeCellGrid.ExclusionZoneArray zones, Celestial ____celestial)
    {
        try
        {
            if (____celestial?.BodyTemplate is not { } template || Program.GetMainCamera() is not { } camera) return;

            double3 eye = (camera.PositionEcl - ____celestial.GetPositionEcl()).Transform(____celestial.GetCce2Ccf());
            if (eye.Length() < 1.0) return;
            eye = eye.Normalized();

            Span<float4> best = stackalloc float4[Zones];
            Span<double> near = stackalloc double[Zones];
            near.Fill(double.MaxValue);

            foreach (LocationReference location in template.Locations)
            {
                if (location is not LandmarkReference { IsLaunchPad: true } pad || pad.GetStaticObject() is not { } pads) continue;
                if (pads.FootprintRadius <= 0.0) continue;

                double away = (pad.ForwardCcf - eye).LengthSquared();
                int worst = 0;
                for (int i = 1; i < Zones; i++) if (near[i] > near[worst]) worst = i;
                if (away >= near[worst]) continue;

                near[worst] = away;
                best[worst] = new float4(float3.Pack(pad.ForwardCcf), (float)((pads.FootprintRadius + CellMargin) / ____celestial.MeanRadius));
            }

            for (int i = 0; i < Zones; i++) zones[i] = near[i] < double.MaxValue ? best[i] : float4.Zero;
        }
        catch
        {
        }
    }
}
