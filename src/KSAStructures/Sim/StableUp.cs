using Brutal.Numerics;

namespace KSAStructures;

/// <summary>
/// A camera's roll reference carried from frame to frame, so a view whose wanted up passes along its
/// own line of sight turns smoothly rather than flipping.
/// </summary>
public static class StableUp
{
    /// <summary>
    /// The up to build a view basis from: last frame's, corrected towards the one wanted by at
    /// most <paramref name="maxStepRad"/>, and by less than that the worse a reference it is.
    ///
    /// <para><b>Corrected rather than chosen.</b> Picking between the wanted up and the carried one
    /// at a threshold flips the picture: a switching rule is discontinuous wherever the switch is,
    /// and the two rules disagree by however far the carried one has drifted. Moving the threshold
    /// moves the flip; it does not remove it. Measured at 89° of roll for 1.3° of aim, with the
    /// view sitting exactly on the cutoff.</para>
    ///
    /// <para>So there is no cutoff. The reference is always the carried one, and the wanted one
    /// only ever pulls it — quickly where it is a good reference, not at all where the view lies
    /// along it and it says nothing. A stabilised head is a control loop, not a lookup.</para>
    ///
    /// <para><paramref name="lastUp"/> zero seeds it: with nothing carried there is nothing to
    /// correct, so the wanted one is taken outright. False only when neither is usable, which is a
    /// view along its own up on the very frame it was taken.</para>
    /// </summary>
    /// <param name="up">
    /// Orthogonal to <paramref name="forwardEcl"/>, so the caller hands it straight back next
    /// frame without it drifting into the view.
    /// </param>
    public static bool Try(double3 forwardEcl, double3 preferredUp, double3 lastUp,
                                   double maxStepRad, out double3 up)
    {
        up = Vec.Zero;

        double3 forward = Vec.Unit(forwardEcl);
        if (Vec.Len2(forward) < 0.5) return false;

        bool wanted = Across(forward, preferredUp, out double3 target, out double authority);
        bool carried = Across(forward, lastUp, out double3 held, out _);

        // Nothing carried yet, or nothing to carry towards: whichever exists is the answer.
        if (!carried) { up = target; return wanted; }
        if (!wanted) { up = held; return true; }

        // How far the wanted up may pull this frame. Scaled by how much of it lies across the
        // view: none of it does when the view is along it, and that is exactly when its direction
        // is meaningless — so it stops pulling instead of being switched away from.
        double step = Math.Max(0.0, maxStepRad) * authority;
        double apart = Vec.AngleBetween(held, target);

        if (apart <= step || step <= 0.0)
        {
            up = apart <= step ? target : held;
            return true;
        }

        double3 axis = Vec.Cross(held, target);
        if (Vec.Len2(axis) < 1e-18) { up = held; return true; }

        up = Vec.Unit(doubleQuat.CreateFromAxisAngle(Vec.Unit(axis), step) * held);
        return true;
    }

    // The part of a candidate lying across the view, and how much of it there is. The length is
    // the sine of the angle between them, which is precisely how good a roll reference it is.
    private static bool Across(double3 forward, double3 candidate, out double3 across,
                               out double authority)
    {
        across = Vec.Zero;
        authority = 0.0;

        double3 unit = Vec.Unit(candidate);
        if (Vec.Len2(unit) < 0.5) return false;

        double3 rejected = Vec.RejectFrom(unit, forward);
        double length = Vec.Len(rejected);
        if (length < 1e-6) return false;

        across = rejected / length;
        authority = length;
        return true;
    }
}
