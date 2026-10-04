using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// A camera held for a composed shot, from the bridge's <c>camera</c>: set east, north and up of the
/// craft being flown, aimed at it or at another craft, at a field of view of its own. Everything is
/// measured against the followed craft and the separations sampled together, so nothing in it
/// carries a frame of the planet's travel.
/// </summary>
internal sealed class SceneCamera : IViewPose
{
    public required Vehicle Anchor { get; init; }
    public double3 EastNorthUp { get; init; }
    public Vehicle? AtCraft { get; init; }
    public double AtUp { get; init; }
    public double FovDeg { get; init; } = 50.0;

    public IMouseDrag? Orbit => null;

    /// <summary>The eye's offset from the anchor and where it looks, in the ecliptic.</summary>
    public bool TryLook(out double3 offset, out double3 forward, out double3 up)
    {
        offset = forward = up = default;
        if (!KsaWorld.IsAlive(Anchor) || Anchor.Parent is not Celestial body) return false;

        double3 anchor = KsaWorld.PositionEcl(Anchor);
        up = Vec.Unit(anchor - body.GetPositionEcl());
        double3 axis = new double3(0, 0, 1).Transform(body.GetCce2Ccf().Inverse());
        double3 north = Vec.Unit(axis - (up * Vec.Dot(axis, up)));
        double3 east = Vec.Cross(north, up);
        offset = (east * EastNorthUp.X) + (north * EastNorthUp.Y) + (up * EastNorthUp.Z);

        double3 target = up * AtUp;
        if (AtCraft is { } craft && KsaWorld.IsAlive(craft)) target += KsaWorld.PositionEcl(craft) - anchor;

        forward = Vec.Unit(target - offset);

        // Looking straight down, the vertical is along the view: north is the up then.
        if (Math.Abs(Vec.Dot(forward, up)) > 0.8) up = north;
        return Vec.IsFinite(forward) && Vec.Len2(forward) > 0.5 && Vec.IsFinite(offset);
    }

    /// <summary>Points the main view. Once a frame, with the step.</summary>
    public bool Drive() => TryLook(out double3 offset, out double3 forward, out double3 up)
                           && KsaWorld.TryLookFromMainViewport(offset, forward, up, FovDeg, this);

    public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl, out double3 upEcl,
                        out double fovDeg)
    {
        fovDeg = FovDeg;
        return TryLook(out offsetFromFollowed, out forwardEcl, out upEcl);
    }
}
