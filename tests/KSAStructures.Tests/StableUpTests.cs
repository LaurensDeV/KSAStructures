using Brutal.Numerics;
using Xunit;

namespace KSAStructures.Tests;

/// <summary>A camera roll reference carried through the singularity where the wanted up lies along the view.</summary>
public class StableUpTests
{
    // One frame of correction at the controller's rate: 180 deg/s at 60 fps.
    private const double Step = Math.PI / 60.0;

    /// <summary>
    /// The fault this exists for, and the only one that matters: a view creeping past its own up
    /// must not have its roll jump. Anything that <em>switches rule</em> at the singularity flips
    /// the picture through half a turn, which in game is the whole view inverting as the head's
    /// elevation crosses zero looking straight down a rocket.
    /// </summary>
    [Fact]
    public void TheRollDoesNotJumpAsTheViewCreepsPastItsOwnUp()
    {
        double3 preferred = new(0, 0, 1);

        // Sweeping through straight down, a quarter of a degree at a time. Starting well outside
        // the cone on purpose: a head arrives at the pole from somewhere, and with no previous
        // answer at all there is nothing to be continuous with -- which the refusal test covers.
        double3 last = Vec.Zero;
        double3 previousUp = Vec.Zero;
        double worst = 0.0;
        double worstRatio = 0.0;

        for (double off = -60.0; off <= 60.0; off += 0.25)
        {
            double a = double.DegreesToRadians(off);
            double3 forward = Vec.Unit(new double3(Math.Sin(a), 0, -Math.Cos(a)));

            Assert.True(StableUp.Try(forward, preferred, last, Step, out double3 up));

            if (Vec.Len2(previousUp) > 0.5)
            {
                double rolled = Vec.AngleBetween(previousUp, up);
                worst = Math.Max(worst, rolled);

                // The measured fault was amplification rather than a jump: a quarter degree of aim
                // swinging the roll by tens. Pinning the ratio is what stops a future threshold
                // being set back to where the answer exists but is worthless.
                worstRatio = Math.Max(worstRatio, rolled / double.DegreesToRadians(0.25));
            }

            previousUp = up;
            last = up;
        }

        Assert.True(worst < 0.05,
            $"the roll jumped {double.RadiansToDegrees(worst):F1} deg in one quarter-degree step");

        Assert.True(worstRatio < 2.5,
            $"a degree of aim moved the roll {worstRatio:F1} degrees at worst");
    }

    /// <summary>
    /// With nothing carried there is nothing to correct, so the wanted up is taken outright. That
    /// is what makes the first frame of a levelled view level rather than arbitrary.
    /// </summary>
    [Fact]
    public void WithNothingCarriedThePreferredUpIsTakenOutright()
    {
        double3 forward = new(1, 0, 0);
        double3 preferred = new(0, 0, 1);

        Assert.True(StableUp.Try(forward, preferred, Vec.Zero, Step, out double3 up));

        Assert.Equal(0.0, Vec.AngleBetween(up, preferred), 9);
    }

    /// <summary>
    /// And a carried one is pulled towards it a frame at a time rather than snapping, which is
    /// what levelling *is*: a control loop, not a lookup.
    /// </summary>
    [Fact]
    public void ACarriedUpIsCorrectedTowardsThePreferredOverSeveralFrames()
    {
        double3 forward = new(1, 0, 0);
        double3 preferred = new(0, 0, 1);
        double3 up = new(0, 1, 0);          // a quarter turn out

        Assert.True(StableUp.Try(forward, preferred, up, Step, out double3 after));

        double moved = Vec.AngleBetween(up, after);
        Assert.True(moved > 0.0 && moved <= Step + 1e-9,
            $"one frame moved the roll {double.RadiansToDegrees(moved):F2} deg");

        // And it gets there, rather than creeping forever.
        for (int frame = 0; frame < 200; frame++)
        {
            StableUp.Try(forward, preferred, up, Step, out up);
        }

        Assert.Equal(0.0, Vec.AngleBetween(up, preferred), 6);
    }

    [Fact]
    public void TheAnswerIsAlwaysOrthogonalToTheView()
    {
        double3 forward = Vec.Unit(new double3(1, 0.4, -0.2));

        Assert.True(StableUp.Try(forward, new double3(0, 0, 1), Vec.Zero, Step, out double3 up));

        Assert.Equal(0.0, Vec.Dot(up, forward), 9);
        Assert.Equal(1.0, Vec.Len(up), 9);
    }

    /// <summary>
    /// Both unusable is a view along its own up on the very frame it was taken: there is nothing
    /// continuous to be had, because there is nothing to be continuous with.
    /// </summary>
    [Fact]
    public void WithNothingUsableItRefusesRatherThanInventingARoll()
    {
        double3 forward = new(0, 0, 1);

        Assert.False(StableUp.Try(forward, forward, Vec.Zero, Step, out _));
        Assert.False(StableUp.Try(Vec.Zero, new double3(0, 0, 1), Vec.Zero, Step, out _));
    }

    /// <summary>
    /// The invariant that forbids the whole class, rather than one geometry of it: the answer can
    /// never move further in a frame than it was allowed to.
    ///
    /// <para>Three versions of this flipped the picture, each at a different view angle, because
    /// each chose between two answers and the choice could change from one frame to the next. A
    /// bound on the movement itself cannot be satisfied by a rule that switches — which is the
    /// point of asserting it here rather than asserting the absence of the three faults.</para>
    /// </summary>
    [Fact]
    public void TheAnswerNeverMovesFurtherInOneFrameThanItWasAllowed()
    {
        double3 preferred = new(0, 0, 1);
        double worst = 0.0;

        // Every view angle from along the up to across it, and every carried roll around each,
        // including the ones that sat exactly on the thresholds the earlier versions used.
        for (double off = 0.0; off <= 180.0; off += 1.0)
        {
            double a = double.DegreesToRadians(off);
            double3 forward = Vec.Unit(new double3(Math.Sin(a), 0, Math.Cos(a)));

            for (double roll = 0.0; roll < 360.0; roll += 15.0)
            {
                double r = double.DegreesToRadians(roll);

                // A carried up at that roll about the view, which is the shape one always has.
                if (!StableUp.Try(forward, preferred, Vec.Zero, Step, out double3 seed))
                {
                    continue;
                }

                double3 carried = Vec.Unit(doubleQuat.CreateFromAxisAngle(forward, r) * seed);

                if (!StableUp.Try(forward, preferred, carried, Step, out double3 up)) continue;

                worst = Math.Max(worst, Vec.AngleBetween(carried, up));
            }
        }

        Assert.True(worst <= Step + 1e-9,
            $"the roll moved {double.RadiansToDegrees(worst):F2} deg against an allowance of "
            + $"{double.RadiansToDegrees(Step):F2}");
    }
}
