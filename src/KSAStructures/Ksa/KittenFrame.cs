using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// A kitten's model space against the world: the skeleton's centimetres, +Y up, +Z its facing and +X
/// its left.
///
/// <para>The same chain the engine draws it through (<c>KittenEva.UpdateRenderData</c>,
/// <c>KittenRenderable.ModelToBodyMatrix</c>): scaled by the avatar's <c>Scale</c>, turned a quarter about
/// X and then about Z into the vehicle's body frame, less its centre of mass, then by <c>Body2Cce</c>
/// from the vehicle's position. Rebuilt here rather than read off a drawn matrix because a drawn one
/// belongs to whichever camera drew it last.</para>
/// </summary>
internal static class KittenFrame
{
    // KittenEva.Renderable is public; the avatar inside it is not.
    private static readonly System.Reflection.FieldInfo? AvatarField =
        typeof(KittenRenderable).GetField("_characterAvatar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The avatar a kitten is drawn through, or null when the private field has moved.</summary>
    public static CharacterAvatar? AvatarOf(KittenEva kitten) => AvatarField?.GetValue(kitten.Renderable) as CharacterAvatar;

    private static readonly doubleQuat ModelToBody = doubleQuat.Concatenate(
        doubleQuat.CreateFromAxisAngle(new double3(1, 0, 0), -Math.PI / 2.0),
        doubleQuat.CreateFromAxisAngle(new double3(0, 0, 1), -Math.PI / 2.0));

    private static double ScaleOf(KittenEva kitten) => AvatarOf(kitten)?.Core.Scale ?? 0.01;

    private static double3 CentreOfMass(Vehicle v)
    {
        float3 c = v.CenterOfMassAsmbF;
        return new double3(c.X, c.Y, c.Z);
    }

    /// <summary>A model-space point, in the ecliptic.</summary>
    public static double3 ToEcl(KittenEva kitten, double3 modelCm) => ToEcl(kitten, modelCm, kitten.Body2Cce);

    private static double3 ToEcl(KittenEva kitten, double3 modelCm, doubleQuat body2Cce)
    {
        double3 body = double3.Transform(modelCm * ScaleOf(kitten), ModelToBody) - CentreOfMass(kitten);
        return kitten.GetPositionEcl() + double3.Transform(body, body2Cce);
    }

    /// <summary>A model-space direction, in the ecliptic.</summary>
    public static double3 DirectionToEcl(KittenEva kitten, double3 model)
        => double3.Transform(double3.Transform(model, ModelToBody), kitten.Body2Cce);

    /// <summary>Model space to a camera's frame, the matrix the engine draws the kitten with from that camera.</summary>
    public static float4x4 ModelToEgo(KittenEva kitten, double3 cameraEcl)
    {
        float scale = (float)ScaleOf(kitten);
        double3 com = CentreOfMass(kitten);
        return float4x4.CreateScale(scale) * float4x4.CreateRotationX(-MathF.PI / 2f) * float4x4.CreateRotationZ(-MathF.PI / 2f)
               * float4x4.CreateTranslation(float3.Pack(-com))
               * float4x4.CreateFromQuaternion(floatQuat.Pack(kitten.Body2Cce))
               * float4x4.CreateTranslation(float3.Pack(kitten.GetPositionEcl() - cameraEcl));
    }

    /// <summary>An ecliptic point, in model space.</summary>
    public static double3 FromEcl(KittenEva kitten, double3 ecl)
    {
        double3 body = double3.Transform(ecl - kitten.GetPositionEcl(), doubleQuat.Inverse(kitten.Body2Cce)) + CentreOfMass(kitten);
        return double3.Transform(body, doubleQuat.Inverse(ModelToBody)) / ScaleOf(kitten);
    }

    /// <summary>An ecliptic direction, in model space.</summary>
    public static double3 DirectionFromEcl(KittenEva kitten, double3 ecl)
        => double3.Transform(double3.Transform(ecl, doubleQuat.Inverse(kitten.Body2Cce)), doubleQuat.Inverse(ModelToBody));
}
