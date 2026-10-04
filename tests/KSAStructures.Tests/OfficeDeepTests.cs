using Xunit;

namespace KSAStructures.Tests;

public class OfficeDeepTests
{
    // The road's centreline round the shaft, from the north-east landing by the north, the west,
    // the south and the east, a quarter of a metre at a time.
    private static IEnumerable<(double East, double North)> OneTurn()
    {
        const double step = 0.25, west = 28.5, east = 51.5, south = -56.5, north = -33.5;

        for (double e = east; e > west; e -= step) yield return (e, north);
        for (double n = north; n > south; n -= step) yield return (west, n);
        for (double e = west; e < east; e += step) yield return (e, south);
        for (double n = south; n < north; n += step) yield return (east, n);
    }

    [Fact]
    public void TheRoadIsOneUnbrokenDescentFromTheGroundToTheHallFloor()
    {
        double level = 0.0;
        var afterEachTurn = new List<double>();

        // Down to the hall's floor and no further: the road's foot is a step somebody on the
        // floor is lifted onto, which is a rise.
        for (int turn = 0; turn < 8 && level > -OfficeDeep.Depth; turn++)
        {
            foreach ((double east, double north) in OneTurn())
            {
                double under = OfficeDeep.SurfaceUnder(east, north, level);

                // A quarter metre of a leg that drops two metres in sixteen.
                Assert.InRange(under - level, -0.04, 1e-9);
                level = under;
                if (level <= -OfficeDeep.Depth) break;
            }

            afterEachTurn.Add(level);
        }

        Assert.Equal(7, afterEachTurn.Count);
        Assert.Equal(-8.0, afterEachTurn[0], 6);
        Assert.Equal(-16.0, afterEachTurn[1], 6);
        Assert.Equal(-48.0, afterEachTurn[5], 6);
        Assert.Equal(-OfficeDeep.Depth, afterEachTurn[6]);
    }

    [Fact]
    public void TheFirstTwoLegsAreOpenToTheSkyAndTheNextTwoAreRoofed()
    {
        const double fromAbove = 5.0;

        // Half way along the north leg and the west leg, seen from above: the road itself.
        Assert.Equal(-1.0, OfficeDeep.SurfaceUnder(40.0, -33.5, fromAbove), 6);
        Assert.Equal(-3.0, OfficeDeep.SurfaceUnder(28.5, -45.0, fromAbove), 6);

        // The same on the south and east legs: the cover over them.
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(40.0, -56.5, fromAbove));
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(51.5, -45.0, fromAbove));

        // And over the core the road winds round, at any height.
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(40.0, -45.0, fromAbove));
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(40.0, -45.0, -30.0));
    }

    [Theory]
    [InlineData(1.0, 0.08)]      // standing on the cover
    [InlineData(-5.0, -5.0)]     // on the first turn, under the cover
    [InlineData(-4.0, -5.0)]     // a metre over the first turn: still the first turn
    [InlineData(-13.0, -13.0)]   // on the second
    [InlineData(-12.0, -13.0)]   // a metre over the second, with the first overhead
    [InlineData(-21.0, -21.0)]   // on the third
    public void WhoeverAsksGetsTheHighestSurfaceNotAboveThem(double askerUp, double expected)
    {
        // Half way along the south leg, where turn after turn lies under the cover.
        Assert.Equal(expected, OfficeDeep.SurfaceUnder(40.0, -56.5, askerUp), 6);
    }

    [Fact]
    public void TheHallIsItsFloorToSomebodyInItAndTheCoverToSomebodyOverIt()
    {
        Assert.Equal(-OfficeDeep.Depth, OfficeDeep.SurfaceUnder(47.0, -90.0, -50.0));
        Assert.Equal(-OfficeDeep.Depth, OfficeDeep.SurfaceUnder(47.0, -90.0, -OfficeDeep.Depth));
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(47.0, -90.0, 0.0));

        // Just over the hall's ceiling is over the hall.
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(47.0, -90.0, OfficeDeep.HallTop));

        // Beside the hall there is only the cover, however deep the asker says they are.
        Assert.True(OfficeDeep.Covers(0.0, -100.0));
        Assert.Equal(OfficeDeep.Cover, OfficeDeep.SurfaceUnder(0.0, -100.0, -50.0));
    }

    [Fact]
    public void TheCoverLiesOverEverythingDugAndNothingElse()
    {
        Assert.True(OfficeDeep.Covers(40.0, -45.0));
        Assert.True(OfficeDeep.Covers(47.0, -90.0));

        // The tower and its garage are north of it.
        Assert.False(OfficeDeep.Covers(0.0, 0.0));
        Assert.False(OfficeDeep.Covers(6.5, 50.0));
        Assert.False(OfficeDeep.Covers(-30.0, -45.0));
    }

    [Fact]
    public void TheOnePitHoldsTheShaftAndTheHallAndLiesUnderTheCover()
    {
        // The corners of the shaft's walls and the hall's, which the terrain has to be dug under.
        foreach ((double east, double north) in new[] { (24.4, -29.4), (55.6, -29.4), (11.0, -119.0), (83.0, -119.0), (11.0, -60.6), (83.0, -60.6) })
        {
            double fromMiddle = Math.Sqrt(Math.Pow(east - OfficeDeep.PitEast, 2) + Math.Pow(north - OfficeDeep.PitNorth, 2));
            Assert.True(fromMiddle < OfficeDeep.PitRadius, $"({east}, {north}) is {fromMiddle:F1} m from the pit's middle");
        }

        foreach ((double east, double north) in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
        {
            Assert.True(OfficeDeep.Covers(OfficeDeep.PitEast + (east * OfficeDeep.PitRadius), OfficeDeep.PitNorth + (north * OfficeDeep.PitRadius)));
        }
    }

    [Fact]
    public void EveryGeneratedLampIsUnderTheCover()
    {
        Assert.NotEmpty(OfficeDeep.Lamps);

        foreach ((double up, double east, double north, float range, float intensity) in OfficeDeep.Lamps)
        {
            Assert.True(up < 0.0 && up > -OfficeDeep.Depth);
            Assert.True(OfficeDeep.Covers(east, north));
            Assert.True(range > 0f && intensity > 0f);
        }
    }
}
