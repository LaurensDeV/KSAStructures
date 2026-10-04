using System.Reflection;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using RenderCore;

namespace KSAStructures;

/// <summary>
/// The sky's ambient taken out of what is under the ground: <c>KSAStructuresDark.comp</c>, one
/// dispatch over the main view while the camera is at the site.
///
/// <para><b>A prefix on <c>SunbloomRenderer.Render</c>, which is public and hands over the command
/// buffer.</b> Immediately before it the engine has put the scene colour into a storage layout and
/// the depth into a sampled one, for its own screen-space particles, and bloom and the tonemap are
/// still to come: a pass here inherits those barriers and is graded with the scene.</para>
///
/// <para><b>First among the prefixes there, and it leaves a barrier behind.</b> Another mod may
/// dispatch into the same image from the same hook, and a compute pass may not read what the one
/// before it wrote without one.</para>
///
/// <para><b>Nothing in the prefix may throw.</b> It runs inside the engine's render loop, where an
/// exception is the game rather than a log line.</para>
/// </summary>
internal static class DarkPass
{
    public const string ShaderId = "KSAStructuresDarkCompute";

    private const string HarmonyId = "com.kesslersystems.ksastructures.darkpass";

    // The shader's local size.
    private const int Group = 8;

    private const int FaultLimit = 3;

    private static readonly ProfilerTag GpuTag = new("KSAStructures Dark"u8);

    private static Harmony? _harmony;
    private static bool _warned, _shaderMissing;
    private static int _faults;

    // What one viewport's pass is drawn with. The descriptor sets name the images, so a different
    // target, size or light result is a rebuild.
    private sealed class View
    {
        public required IRenderImage Target;
        public required int Width;
        public required int Height;
        public ComputePipelineWrapper? Pipeline;
        public VkImageView Irradiance;
        public VkImageView Specular;
    }

    private static readonly Dictionary<int, View> Views = [];

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Push
    {
        public float4x4 InvViewProj;
        public float4 Origin;          // the building's origin, camera-relative, and what is left of the ambient
        public float4 Up;              // the building's axes, and the lamps' brightness
        public float4 East;            // and the light the rooms return
        public float4 North;           // and whether parts are drawn with ambient at all
    }

    /// <summary>Whether the pass is wired into the frame.</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// Puts <c>SunbloomRenderer.Render</c> in this assembly's metadata, so a KSA signature change is
    /// a build error rather than a garage that quietly keeps its daylight. Never called.
    /// </summary>
    public static void PinTheSignature()
    {
        SunbloomRenderer? never = null;
        never?.Render(default, null!, 0);
    }

    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = typeof(SunbloomRenderer).GetMethod(
                nameof(SunbloomRenderer.Render), [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);

            if (target is null)
            {
                Log.Warn("dark pass: KSA has no SunbloomRenderer.Render(CommandBuffer, IViewport, int) to hook; "
                         + "what is under the ground keeps its daylight");
                return;
            }

            MethodInfo prefix = typeof(DarkPass).GetMethod(nameof(BeforeSunbloom), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });

            Installed = true;
            Log.Info("dark pass: runs before bloom, via SunbloomRenderer.Render");
        }
        catch (Exception e)
        {
            Log.Warn($"dark pass: could not hook the renderer ({e.Message}); what is under the ground keeps its daylight");
        }
    }

    public static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); }
        catch { /* Going away anyway. */ }

        Release();
        _harmony = null;
        Installed = false;
    }

    /// <summary>Drops the pipelines, so the next frame builds them against the shader as it is now.</summary>
    public static void Release()
    {
        Views.Clear();
        _shaderMissing = false;
        _faults = 0;
    }

    private static bool BeforeSunbloom(CommandBuffer commandBuffer, IViewport viewport)
    {
        if (_faults >= FaultLimit) return true;

        try
        {
            Record(commandBuffer, viewport);
        }
        catch (Exception e)
        {
            Views.Clear();
            Warn($"the pass threw and is standing down ({e.Message})");
            _faults++;
        }

        return true;
    }

    private static void Record(CommandBuffer commandBuffer, IViewport viewport)
    {
        // Main view only: the engine's light result is the main view's, and without it the lamps'
        // share of a pixel cannot be told from the ambient's.
        if (!ReferenceEquals(viewport, Program.MainViewport)) return;

        if (!Underground.TryDarkened(out double3 originEcl, out double3 up, out double3 east, out double3 north)) return;

        if (viewport.OffscreenTarget is not { } target) return;
        if (target.ColorImage is not { } colour || target.DepthImage is not { } depth) return;

        int width = (int)target.Extent.Width;
        int height = (int)target.Extent.Height;
        if (width <= 0 || height <= 0) return;

        if (Program.GetRenderCamera() is not { } camera) return;

        double3 origin = originEcl - camera.PositionEcl;
        if (!Vec.IsFinite(origin) || origin.Length() > Underground.DarkWithinMetres) return;

        if (Program.Instance?.PrePassRenderer is not { OpaqueHasValidDepth: true }) return;

        // With the sky's lighting off in the settings a static object's ambient is another
        // expression altogether, and the pass stands down.
        if (!GameSettings.Current.Graphics.Atmosphere || camera.NearbyCelestial?.BodyTemplate.AtmosphereReference is null) return;

        if (!TryLightResult(ref _irradianceField, "_diffuseIrradianceImage", out RenderImage? irradiance)
            || !TryLightResult(ref _specularField, "_specularIrradianceImage", out RenderImage? specular))
        {
            Warn("KSA's light pre-pass result could not be read; what is under the ground keeps its daylight");
            return;
        }

        View view = ViewFor(viewport, colour, width, height);
        if (view.Pipeline is null || !view.Irradiance.Equals(irradiance!.ImageView) || !view.Specular.Equals(specular!.ImageView))
        {
            if (!Build(view, depth, irradiance!, specular!)) return;
        }

        Push push = new()
        {
            InvViewProj = camera.VPInv.viewProjection,
            Origin = new float4((float)origin.X, (float)origin.Y, (float)origin.Z, Underground.DaylightLeft),
            Up = new float4((float)up.X, (float)up.Y, (float)up.Z, Underground.LampsOn ? Underground.LampScale : 0f),
            East = new float4((float)east.X, (float)east.Y, (float)east.Z, Underground.Fill),
            North = new float4((float)north.X, (float)north.Y, (float)north.Z, GameSettings.ShowReflections() ? 1f : 0f),
        };

        using (commandBuffer.TagRegion(GpuTag))
        {
            // Into a layout a compute shader may sample, through KSA's own tracked state, so the
            // engine barriers them back out next frame from wherever this left them.
            Span<VkImageMemoryBarrier2> one = stackalloc VkImageMemoryBarrier2[1];
            BarrierBatch toSample = new(one);
            toSample.Add(irradiance!, ImageBarrierInfo.Presets.SampledReadC);
            toSample.SubmitAndFlush(commandBuffer);
            toSample = new(one);
            toSample.Add(specular!, ImageBarrierInfo.Presets.SampledReadC);
            toSample.SubmitAndFlush(commandBuffer);

            Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
            sets[0] = Program.Instance.TextureSystem.DescriptorSet;
            view.Pipeline!.BindPipeline(commandBuffer, viewport.ShaderSlot, sets, default, push);
            commandBuffer.Dispatch((view.Width + Group - 1) / Group, (view.Height + Group - 1) / Group, 1);

            // Whatever dispatches into this image next, in this mod or another, reads what this wrote.
            Hazard(commandBuffer);
        }

        Underground.DarkPassRan();
    }

    private static View ViewFor(IViewport viewport, IRenderImage colour, int width, int height)
    {
        if (Views.TryGetValue(viewport.ShaderSlot, out View? view)
            && ReferenceEquals(view.Target, colour) && view.Width == width && view.Height == height)
        {
            return view;
        }

        view = new View { Target = colour, Width = width, Height = height };
        Views[viewport.ShaderSlot] = view;
        return view;
    }

    private static bool Build(View view, IRenderImage depth, RenderImage irradiance, RenderImage specular)
    {
        view.Pipeline = null;
        if (_shaderMissing) return false;

        if (!ModLibrary.TryGet<ShaderReference>(ShaderId, out var shader) || shader is null)
        {
            _shaderMissing = true;
            Warn($"no shader '{ShaderId}'; what is under the ground keeps its daylight");
            return false;
        }

        Renderer renderer = Program.GetRenderer();

        IRenderImage[] storageTargets = [view.Target];
        IRenderImage[] depthTargets = [depth];
        VkPushConstantRange[] ranges =
        [
            new VkPushConstantRange
            {
                Offset = (ByteSize32)0,
                Size = (ByteSize32)Marshal.SizeOf<Push>(),
                StageFlags = VkShaderStageFlags.ComputeBit,
            },
        ];

        VkDescriptorSetLayout[] external = [Program.Instance.TextureSystem.Layout];
        view.Pipeline = new ComputePipelineWrapper(
            storageTargets, depthTargets, default, default, shader,
            external, ranges, renderer.MaxFramesInFlight, renderer,
            "KSAStructures.Dark", Program.PointClampedSampler, Program.LinearClampedSampler,
            colorSamplerLinearViewsReadOnlyLayout: [irradiance.ImageView, specular.ImageView]);
        view.Irradiance = irradiance.ImageView;
        view.Specular = specular.ImageView;

        return true;
    }

    // One compute-write to compute-read barrier, so a dispatch sees what the one before it wrote.
    private static void Hazard(CommandBuffer commandBuffer)
    {
        Span<VkMemoryBarrier2> one = stackalloc VkMemoryBarrier2[1];
        BarrierBatch batch = new(one, default, default);

        VkMemoryBarrier2 barrier = new()
        {
            SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = VkAccessFlags2.ShaderWriteBit,
            DstStageMask = VkPipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = VkAccessFlags2.ShaderReadBit | VkAccessFlags2.ShaderWriteBit,
        };

        batch.Add(in barrier);
        batch.SubmitAndFlush(commandBuffer);
    }

    private static FieldInfo? _irradianceField, _specularField;

    // What KSA's light pre-pass wrote for the main view: the lamps' light on a pixel, apart from
    // the ambient. Private fields, and without them the pass is off rather than guessed.
    private static bool TryLightResult(ref FieldInfo? field, string name, out RenderImage? image)
    {
        image = null;
        try
        {
            if (Program.LightSystem is not ClusteredLightSystem lights) return false;

            field ??= typeof(ClusteredLightSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            image = field?.GetValue(lights) as RenderImage;
        }
        catch
        {
            image = null;
        }

        return image is not null;
    }

    private static void Warn(string what)
    {
        if (_warned) return;

        _warned = true;
        Log.Warn($"dark pass: {what}");
    }
}
