using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using RenderCore;
using RenderCore.Animation;
using RenderCore.Systems;

namespace KSAStructures;

/// <summary>A kitten seen from its own eyes: what the main view draws of it, and the whole kitten behind that.</summary>
internal interface IFirstPersonBody
{
    KittenEva Kitten { get; }
    CharacterAvatar Avatar { get; }

    /// <summary>Whether the skeleton about to be drawn is the stripped one the main view sees.</summary>
    bool ViewmodelApplied { get; }

    /// <summary>The whole kitten as the view being drawn would otherwise have skinned it, in model space.</summary>
    float4x4[]? BodyWorld { get; }
}

/// <summary>
/// What a first-person kitten needs from the engine: its facing held where the camera looks, the view
/// being drawn known to its pose, and its shadow taken from the whole kitten.
///
/// <para><b>It faces where the player looks.</b> KSA's ground locomotion moves a kitten relative to the
/// camera and turns it to face whichever way it walks (<c>KittenLocomotion.StepGrounded</c> sets
/// <c>FacingDirPhys</c> to the move), so left and right turn it away from the view. With the facing held
/// on the look the same keys side-step. It runs on the vehicle worker's thread, so the look is handed
/// over in a volatile field.</para>
///
/// <para><b>Only the main view is stripped.</b> <c>KittenEva.UpdateRenderData</c> runs once per view, and
/// the skeleton is rebuilt for each before its pose processors' <c>UpdateSkeleton</c>, so a prefix here
/// tells the pose which view it is posing. Every other view — the crew portraits among them — draws the
/// whole kitten.</para>
///
/// <para><b>Its shadow is the whole kitten's.</b> <c>AnimatedRenderable.Draw</c> puts the skeleton it was
/// drawn with into the shadow pool, which for the main view is the stripped one; a postfix takes those
/// entries back out and puts in the whole kitten, skinned a second time. The helmet and visor hang off the
/// collapsed head, so their shadows are moved back onto the whole kitten's head the same way.</para>
///
/// <para><b>Nothing in any of these may throw</b>: each runs inside the engine's frame.</para>
/// </summary>
internal static class FirstPersonHook
{
    private const string HarmonyId = "com.kesslersystems.ksastructures.firstperson";

    // The attachment's own turn, applied before the socket's bone by KittenRenderable.
    private static readonly float4x4 SocketTurn = float4x4.CreateRotationZ(-MathF.PI / 2f) * float4x4.CreateRotationX(-MathF.PI / 2f);

    private static Harmony? _harmony;
    private static volatile float[]? _heading;
    private static KittenEva? _drawingMain;
    private static int _poolBefore = -1;
    private static int _meshPoolBefore = -1;
    private static AccessTools.FieldRef<AnimatedRenderable, int[]>? _materialIndices;
    private static AnimatedRenderable? _described;
    private static float4x4[] _inverseBind = [];
    private static int[] _shadowMeshes = [];

    /// <summary>The kitten in first person, or null for none.</summary>
    public static IFirstPersonBody? Body { get; set; }

    public static bool Installed { get; private set; }

    /// <summary>
    /// The way a first-person kitten faces along the ground, in the ecliptic, or null to let KSA turn it.
    /// Handed to the walk in place of the camera's forward too: KSA flattens that onto the ground, and
    /// looking nearly straight down it falls back on the camera's up, which points behind the view.
    /// </summary>
    public static double3? Heading
    {
        set => _heading = value is { } h && Vec.IsFinite(h) ? [(float)h.X, (float)h.Y, (float)h.Z] : null;
    }

    /// <summary>Whether the view being drawn now is the main one, looking out of this kitten.</summary>
    public static bool DrawingMainViewOf(KittenEva kitten) => ReferenceEquals(_drawingMain, kitten);

    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? render = AccessTools.Method(typeof(KittenEva), nameof(KittenEva.UpdateRenderData));
            MethodInfo? draw = AccessTools.Method(typeof(AnimatedRenderable), nameof(AnimatedRenderable.Draw));
            MethodInfo? drawMesh = AccessTools.Method(typeof(StaticMeshRenderable), nameof(StaticMeshRenderable.Draw));
            _materialIndices = AccessTools.FieldRefAccess<AnimatedRenderable, int[]>("MaterialIndices");
            if (render is null || draw is null || drawMesh is null)
            {
                Log.Warn("first person cannot hide the kitten: KSA has moved KittenEva.UpdateRenderData or a Draw");
                return;
            }

            _harmony = new Harmony(HarmonyId);

            if (AccessTools.Method(typeof(KittenLocomotion), nameof(KittenLocomotion.StepGrounded)) is { } step)
            {
                _harmony.Patch(step, prefix: Hook(nameof(BeforeStepGrounded)), postfix: Hook(nameof(AfterStepGrounded)));
            }
            else
            {
                Log.Warn("a first-person kitten cannot face where it looks: KSA has no KittenLocomotion.StepGrounded");
            }

            _harmony.Patch(render, prefix: Hook(nameof(BeforeUpdateRenderData)), postfix: Hook(nameof(AfterUpdateRenderData)));
            _harmony.Patch(draw, prefix: Hook(nameof(BeforeDraw)), postfix: Hook(nameof(AfterDraw)));
            _harmony.Patch(drawMesh, prefix: Hook(nameof(BeforeDrawMesh)), postfix: Hook(nameof(AfterDrawMesh)));
            Installed = true;
            Log.Info("first person hides the kitten from its own view and keeps its shadow, via KittenEva.UpdateRenderData and the two Draws");
        }
        catch (Exception e)
        {
            Log.Warn($"first person cannot hide the kitten: {e.GetBaseException().Message}");
        }
    }

    public static void Remove()
    {
        Body = null;
        _heading = null;
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch
        {
            // Unloading anyway.
        }

        _harmony = null;
        Installed = false;
    }

    private static HarmonyMethod Hook(string name)
        => new(typeof(FirstPersonHook).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);

    // The inputs are the kitten's own copy, written from the camera every step, so replacing the forward
    // changes nothing but this step's walk.
    private static void BeforeStepGrounded(ref CharacterControlInputs inputs)
    {
        try
        {
            if (_heading is { Length: 3 } h) inputs.CameraForwardCce = new float3(h[0], h[1], h[2]);
        }
        catch
        {
            // The camera's own forward is the only failure allowed.
        }
    }

    // Only the flown kitten's inputs carry a camera, so the rest are left alone.
    private static void AfterStepGrounded(ref LocomotionCommand __result, in CharacterControlInputs inputs,
                                          in LocomotionFacts facts, ref LocomotionState state)
    {
        try
        {
            if (_heading is null || inputs.CameraForwardCce.LengthSquared() < 1e-6f) return;

            float3 forward = float3.Transform(inputs.CameraForwardCce, facts.Cce2Phys);
            float3 up = facts.UpDirPhys;
            float3 flat = forward - (up * float3.Dot(forward, up));
            if (flat.LengthSquared() < 0.0025f) return;

            flat = flat.Normalized();
            __result.FacingDirPhys = flat;
            state.FacingDirPhys = flat;
        }
        catch
        {
            // A facing left as KSA chose it is the only failure allowed.
        }
    }

    private static void BeforeUpdateRenderData(KittenEva __instance, IViewport viewport)
    {
        try
        {
            _drawingMain = Body is { } b && ReferenceEquals(__instance, b.Kitten) && ReferenceEquals(viewport, Program.MainViewport)
                ? __instance
                : null;
        }
        catch
        {
            _drawingMain = null;
        }
    }

    private static void AfterUpdateRenderData() => _drawingMain = null;

    private static void BeforeDraw(AnimatedRenderable __instance)
    {
        _poolBefore = -1;
        try
        {
            if (Body is { ViewmodelApplied: true } b && ReferenceEquals(__instance, b.Avatar.Core.CharacterModel))
            {
                _poolBefore = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool.Count;
            }
        }
        catch
        {
            _poolBefore = -1;
        }
    }

    // The stripped kitten's shadow out, the whole kitten's in: skinned again off the skeleton the other
    // views draw, into the same per-frame bone buffer the draw itself allocates from.
    private static void AfterDraw(AnimatedRenderable __instance)
    {
        int before = _poolBefore;
        _poolBefore = -1;
        try
        {
            if (before < 0 || Body is not { BodyWorld: { } body } || _materialIndices is null) return;

            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            if (pool.Count > before) pool.RemoveRange(before, pool.Count - before);

            Describe(__instance);
            int bones = __instance.Skeleton.BoneCount;
            if (_inverseBind.Length < bones || body.Length < bones) return;

            Span<float4x4> skin = Program.Instance.SuperMeshRenderSystem.AllocateSkinningMatrices(bones, out int index);
            for (int i = 0; i < bones; i++) skin[i] = _inverseBind[i] * body[i];

            int[] materials = _materialIndices(__instance);
            float4x4 model = __instance.Transform;
            float scale = float.Max(model.X.XYZ.Length(), float.Max(model.Y.XYZ.Length(), model.Z.XYZ.Length()));
            foreach (int j in _shadowMeshes)
            {
                if (j >= __instance.DepthMeshBucketHandles.Length || j >= materials.Length) continue;
                pool.Add(new ShadowRenderable
                {
                    Radius = (__instance.GltfAssetRef.Meshes[j]?.BoundingRadius ?? 2f) * scale,
                    BucketHandle = __instance.DepthMeshBucketHandles[j],
                    InstanceData = new InstanceData { model = model, data = new float4(materials[j], index, 1f, 0f) },
                });
            }
        }
        catch
        {
            // A missing shadow is the only failure allowed.
        }
    }

    private static void BeforeDrawMesh(StaticMeshRenderable __instance)
    {
        _meshPoolBefore = -1;
        try
        {
            if (_drawingMain is not null && Body is { ViewmodelApplied: true } b && HelmetTransform(b, __instance) is not null)
            {
                _meshPoolBefore = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool.Count;
            }
        }
        catch
        {
            _meshPoolBefore = -1;
        }
    }

    // The helmet is drawn off the collapsed head, so its shadow is put back on the whole kitten's.
    private static void AfterDrawMesh(StaticMeshRenderable __instance)
    {
        int before = _meshPoolBefore;
        _meshPoolBefore = -1;
        try
        {
            if (before < 0 || Body is not { BodyWorld: { } body } b || HelmetTransform(b, __instance) is not { } own) return;

            ref CharacterAvatar.Helmet helmet = ref b.Avatar.Attachments.Helmet;
            if (helmet.SocketIndex >= body.Length) return;

            float4x4 drawn = own * (SocketTurn * (body[helmet.SocketIndex] * b.Avatar.Core.CharacterModel.Transform));
            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            for (int i = before; i < pool.Count; i++)
            {
                ShadowRenderable s = pool[i];
                s.InstanceData.model = drawn;
                pool[i] = s;
            }
        }
        catch
        {
            // As above.
        }
    }

    private static float4x4? HelmetTransform(IFirstPersonBody body, StaticMeshRenderable mesh)
    {
        ref CharacterAvatar.Helmet helmet = ref body.Avatar.Attachments.Helmet;
        if (ReferenceEquals(mesh, helmet.HelmetMesh)) return helmet.HelmetTransform;
        if (ReferenceEquals(mesh, helmet.VisorMesh)) return helmet.VisorTransform;
        return null;
    }

    // The bind pose and which meshes cast a shadow, fixed when the kitten is dressed. Read by reflection,
    // because their list type is in an assembly this mod does not reference.
    private static void Describe(AnimatedRenderable model)
    {
        if (ReferenceEquals(model, _described)) return;
        _described = model;

        IEnumerable<T> Read<T>(object? owner, Type type, string field) => AccessTools.Field(type, field)?.GetValue(owner) as IEnumerable<T> ?? [];
        _inverseBind = [.. Read<float4x4>(model.Skeleton, typeof(Skeleton), "InverseBindPose")];
        HashSet<int> ignored = [.. Read<int>(model, typeof(AnimatedRenderable), "ShadowIgnoreIndicies")];
        _shadowMeshes = [.. Enumerable.Range(0, model.DepthMeshBucketHandles.Length).Where(i => !ignored.Contains(i))];
    }

    // Never called. Puts the patched method in this assembly's metadata for the API record.
    private static LocomotionCommand PinTheWalk(in CharacterControlInputs inputs, in ManualControlInputs manual,
                                                in LocomotionFacts facts, in KittenLocomotionTuning tuning, ref LocomotionState state)
        => KittenLocomotion.StepGrounded(in inputs, in manual, in facts, in tuning, ref state);

    // Never called. Puts the patched methods in this assembly's metadata for the API record.
    private static void PinTheRender(KittenEva kitten, IViewport viewport, int frame, AnimatedRenderable model,
                                     StaticMeshRenderable mesh, ViewHandle view)
    {
        kitten.UpdateRenderData(viewport, frame);
        model.Draw(view);
        mesh.Draw(view);
    }
}
