using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>Builds a craft of one part and sets it down on the ground, for the office's furniture.</summary>
internal static class CraftSpawner
{
    /// <summary>
    /// A craft of one part, set down at a latitude and longitude. Built beside
    /// <paramref name="platform"/>, which only lends it an orbit to exist on until
    /// <see cref="Vehicle.TeleportToLocation"/> puts it on the ground.
    /// </summary>
    public static Vehicle? SpawnPartParked(Vehicle platform, string partId, string id, Celestial body,
                                           double latitudeDeg, double longitudeDeg)
    {
        try
        {
            if (Universe.CurrentSystem is not { } system || platform.Parent is not { } parent) return null;

            // Get, not TryGet: TryGet has no PartTemplate case and answers false for every part.
            PartTemplate template = ModLibrary.Get<PartTemplate>(partId);

            double3 spawnEcl = KsaWorld.PositionEcl(platform) + KsaWorld.LocalUp(platform) * 500.0;
            if (!ToParentInertial(parent, spawnEcl, KsaWorld.VelocityEcl(platform), out double3 posCci, out double3 velCci))
            {
                return null;
            }

            Orbit orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci,
                                                   new byte4(255, 200, 80, 255));

            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            var root = new Part(id, template);
            root.CreateOwnTree();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default, parent, id, root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);

            Log.Info($"parked '{id}' ({partId}) at {latitudeDeg:F5}, {longitudeDeg:F5} on {body.Id}");
            return craft;
        }
        catch (Exception e)
        {
            Log.Error("single-part spawn failed", e);
            return null;
        }
    }

    // Cce is the parent-centred ecliptic frame, so it differs from Ecl only by the body's own
    // position and velocity; Cci is a fixed rotation away from it, and both are non-rotating, so
    // the same quaternion carries position and velocity.
    private static bool ToParentInertial(
        IParentBody parent, double3 posEcl, double3 velEcl, out double3 posCci, out double3 velCci)
    {
        posCci = default;
        velCci = default;

        if (parent is not IPosition parentPos || parent is not IVelocity parentVel) return false;

        double3 posCce = posEcl - parentPos.GetPositionEcl();
        double3 velCce = velEcl - parentVel.GetVelocityEcl();

        doubleQuat cce2Cci = parent.GetCce2Cci();
        posCci = cce2Cci * posCce;
        velCci = cce2Cci * velCce;

        return Vec.IsFinite(posCci) && Vec.IsFinite(velCci);
    }
}
