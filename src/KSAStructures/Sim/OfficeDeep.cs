namespace KSAStructures;

/// <summary>
/// The office's deep ramp and hall as numbers: a square spiral road down a shaft into a hall, laid
/// out by tools/model/office-deep.py, which writes the mesh, the colliders and the lamps to match.
/// The frame is the building's: up from the levelled ground, east and north of its origin.
/// </summary>
public static partial class OfficeDeep
{
    public const double Depth = 54.0;
    public const double HallTop = -40.0;

    private const double Turn = 8.0, Leg = 2.0;
    private const double ShaftWest = 25.0, ShaftEast = 55.0, ShaftSouth = -60.0, ShaftNorth = -30.0;
    private const double CoreWest = 32.0, CoreEast = 48.0, CoreSouth = -53.0, CoreNorth = -37.0;
    private const double HallWest = 11.0, HallEast = 83.0, HallSouth = -119.0, HallNorth = -60.0;

    // What the ground over all of it is drawn at, and how far that cover reaches.
    public const double Cover = 0.08;
    private const double CoverWest = -20.0, CoverEast = 110.0, CoverSouth = -143.0, CoverNorth = -13.0;

    /// <summary>The one pit under the shaft and the hall: east, north and radius, an absolute level.</summary>
    public const double PitEast = 45.0, PitNorth = -78.0, PitRadius = 62.0;

    public static bool Covers(double east, double north) =>
        east > CoverWest && east < CoverEast && north > CoverSouth && north < CoverNorth;

    /// <summary>
    /// What a wheel or a camera stands on at a place over the dug ground: the highest of the road's
    /// turns, the hall's floor and the cover that is not above whoever is asking, since the terrain
    /// under all of them is one pit and several of them lie over one another.
    /// </summary>
    public static double SurfaceUnder(double east, double north, double askerUp)
    {
        double reach = askerUp + 0.3;

        bool inShaft = east >= ShaftWest && east <= ShaftEast && north >= ShaftSouth && north <= ShaftNorth;
        if (inShaft)
        {
            bool inCore = east > CoreWest && east < CoreEast && north > CoreSouth && north < CoreNorth;
            if (inCore) return Cover;

            // The first turn's level here, the road running down from the north-east landing by the
            // north, the west, the south and the east.
            bool northSide = north >= CoreNorth, southSide = north <= CoreSouth;
            bool westSide = east <= CoreWest, eastSide = east >= CoreEast;
            double first;
            bool open;
            if (northSide && eastSide) { first = 0.0; open = true; }
            else if (northSide && westSide) { first = -Leg; open = true; }
            else if (southSide && westSide) { first = -2.0 * Leg; open = false; }
            else if (southSide && eastSide) { first = -3.0 * Leg; open = false; }
            else if (northSide) { first = -Leg * (CoreEast - east) / (CoreEast - CoreWest); open = true; }
            else if (westSide) { first = -Leg - (Leg * (CoreNorth - north) / (CoreNorth - CoreSouth)); open = true; }
            else if (southSide) { first = (-2.0 * Leg) - (Leg * (east - CoreWest) / (CoreEast - CoreWest)); open = false; }
            else { first = (-3.0 * Leg) - (Leg * (north - CoreSouth) / (CoreNorth - CoreSouth)); open = false; }

            // The first two legs are open to the sky; the rest of the first turn is under a roof.
            if (!open && Cover <= reach) return Cover;

            for (int k = 0; k < 8; k++)
            {
                double level = first - (Turn * k);
                if (level < -Depth) break;
                if (level <= reach) return level;
            }

            return -Depth;
        }

        bool inHall = east >= HallWest && east <= HallEast && north >= HallSouth && north <= HallNorth;
        if (inHall && reach < HallTop) return -Depth;

        return Cover;
    }
}
