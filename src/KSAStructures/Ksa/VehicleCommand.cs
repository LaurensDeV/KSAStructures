using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>The one place this mod writes to a craft's physics state.</summary>
internal static class VehicleCommand
{
    /// <summary>
    /// Change a craft's velocity, and optionally its position, by a given amount in its own frame,
    /// off rails. Only from a prefix on <c>Vehicle.PrepareWorker</c>: the engine copies the state
    /// for its worker straight after, and a write made anywhere else is overwritten by the worker's
    /// result before anything reads it.
    /// </summary>
    public static bool TryChangeVelocity(Vehicle craft, double3 changeAsmb, double3 moveAsmb = default)
    {
        if (!KsaWorld.IsAlive(craft) || !craft.HasPhysicsBubble || !Vec.IsFinite(changeAsmb)) return false;

        PhysicsStates states = craft.GetPhysicsStatesMutable();
        if (craft.Situation.IsOnRails())
        {
            states.UpdateFromAnalytic(craft.Orbit, in craft.Orbit.StateVectors, craft.Body2Cce, craft.BodyRates,
                                      Situation.Maneuvering);
        }

        states.Kinematic.VelocityPhys += changeAsmb.Transform(states.Kinematic.Body2Phys);
        if (Vec.IsFinite(moveAsmb)) states.Kinematic.PositionPhys += moveAsmb.Transform(states.Kinematic.Body2Phys);
        states.Props.SetOnRails(isOnRails: false);
        craft.SetFlightPlan(new FlightPlan(states.ComputeOrbit(craft.OrbitColor), craft.Hash));
        return true;
    }
}
