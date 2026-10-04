using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Rendering.Lighting;

namespace KSAStructures;

/// <summary>
/// What makes the office's garage dark and then lights it. A static object casts no shadow, so the
/// sun reaches everything under the ground: a mesh inside the garage's roof and walls is put in the
/// sun's shadow map and the lamps' without ever being drawn, each ceiling lamp is a spot light of the
/// engine's own, and KSAStructuresDark.comp takes the sky's ambient out of what is under the ground.
/// </summary>
internal static class Underground
{
    private static readonly string[] CasterGltfs = ["KSAOffice_ShadowCaster_Glb", "KSAOffice_DeepCaster_Glb"];
    private const string CasterMaterial = "KSAOffice_GarageMaterial";

    private static readonly Type? AssetNameType = typeof(LoadedAssetRef).GetProperty("Id")?.PropertyType;

    private static StaticMeshRenderable[]? _casters;
    private static AccessTools.FieldRef<StaticMeshRenderable, int[]>? _materialIndices;
    private static bool _casterFailed, _saidShadowsOff;
    private static float4x4 _model;
    private static bool _fresh;

    public static bool LampsOn { get; set; } = true;

    // What the bridge can change while the game runs, for finding the look.
    public static bool Darken = true, Shadows = true;
    public static float LampScale = 1f, Fill = 0.08f;

    // What KSAStructuresDark.comp leaves of the sky's ambient under the ground.
    public static float DaylightLeft => Darken ? 0.1f : 1f;
    public const double DarkWithinMetres = 600.0;

    private const double LampHeight = -0.5;
    private const float OuterAngle = 1.3f, InnerAngle = 1.05f;
    private const double ShadowsWithinMetres = 260.0;

    // A shadow slot is the player's graphics setting, shared with part lights and exhaust.
    private const int SlotsLeftForOthers = 4;
    private const uint ShadowTile = 512;

    // The ceiling lamps: east, north, range and intensity, as the site's layout script wrote them and
    // as KSAStructuresDark.comp holds them. Straight down, so nothing above a lamp's own plane is lit and
    // none of it comes up through the roof.
    private static readonly (double Y, double Z, float Range, float Intensity)[] Lamps =
    [
        (6.50, 38.00, 12f, 38f),
        (6.50, 46.00, 12f, 38f),
        (6.50, 54.00, 12f, 38f),
        (6.50, 62.00, 12f, 38f),
        (6.50, 70.00, 12f, 38f),
        (8.55, 81.45, 12f, 38f),
        (18.00, 83.50, 12f, 38f),
        (26.00, 83.50, 12f, 38f),
        (34.00, 83.50, 12f, 38f),
        (42.00, 83.50, 12f, 38f),
        (2.50, 17.50, 11f, 38f),
        (2.50, 24.00, 11f, 38f),
        (2.50, 30.50, 11f, 38f),
        (11.50, 17.50, 11f, 38f),
        (11.50, 24.00, 11f, 38f),
        (11.50, 30.50, 11f, 38f),
        (-0.25, 5.50, 8f, 14f),
        (-0.25, 11.00, 8f, 14f),
        (-1.60, 0.00, 7f, 14f),
    ];

    // What the tunnel's walls would send back onto the flanks of a car on its centreline, which every
    // lamp there is edge-on to: weak lamps along both walls, with no fixture and no shadow.
    private static readonly (double Y, double Z, float Range, float Intensity)[] Fills =
    [
        (4.20, 42.00, 9f, 6f),
        (4.20, 50.00, 9f, 6f),
        (4.20, 58.00, 9f, 6f),
        (4.20, 66.00, 9f, 6f),
        (8.80, 42.00, 9f, 6f),
        (8.80, 50.00, 9f, 6f),
        (8.80, 58.00, 9f, 6f),
        (8.80, 66.00, 9f, 6f),
        (22.00, 81.20, 9f, 6f),
        (22.00, 85.80, 9f, 6f),
        (30.00, 81.20, 9f, 6f),
        (30.00, 85.80, 9f, 6f),
        (38.00, 81.20, 9f, 6f),
        (38.00, 85.80, 9f, 6f),
    ];

    private static readonly float3 Warm = new(1.0f, 0.93f, 0.80f);
    // The garage's lamps and the deep ramp's as one list, since they share the shadow slots.
    private static readonly (double X, double Y, double Z, float Range, float Intensity)[] All =
        [.. Lamps.Select(l => (LampHeight, l.Y, l.Z, l.Range, l.Intensity)), .. OfficeDeep.Lamps];
    private static readonly double[] Nearness = new double[All.Length];
    private static readonly int[] Nearest = new int[All.Length];

    private static double3 _originEcl, _up, _east, _north;
    private static int _darkFrames, _originFrames;

    public static bool TryDarkened(out double3 originEcl, out double3 up, out double3 east, out double3 north)
    {
        (originEcl, up, east, north) = (_originEcl, _up, _east, _north);
        return _originFrames > 0;
    }

    public static void DarkPassRan() => _darkFrames = 4;

    public static void Install(Harmony harmony)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.AddPoolToShadowBucket));
            MethodInfo prefix = typeof(Underground).GetMethod(nameof(BeforeShadowBucket), BindingFlags.NonPublic | BindingFlags.Static)!;
            if (target is null) Log.Warn("office: no shadow hook, the sun shines into the garage");
            else harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            // The lamps' shadow maps are drawn before the sun's cascades and read the same pool, so the
            // caster goes in at whichever of the two the frame reaches first.
            MethodInfo? lamps = AccessTools.Method(typeof(SuperMeshShadowCasterProvider), nameof(SuperMeshShadowCasterProvider.PrepareShadowCasters));
            if (lamps is null) Log.Warn("office: no lamp shadow hook, the garage's lamps shine through its pillars");
            else harmony.Patch(lamps, prefix: new HarmonyMethod(prefix));
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not hook the shadow pass ({e.GetBaseException().Message})");
        }
    }

    public static void Remove()
    {
        _fresh = false;
        _casters = null;
    }

    /// <summary>
    /// Called once per viewport per frame with the building's origin and axes in that viewport's Ego.
    /// </summary>
    public static void Light(IViewport viewport, double3 origin, double3 up, double3 east, double3 north)
    {
        bool main = ReferenceEquals(viewport.GetCamera(), Program.GetMainCamera());
        if (main)
        {
            _originEcl = origin + viewport.GetCamera().PositionEcl;
            (_up, _east, _north) = (up, east, north);
            _originFrames = 4;
            if (_darkFrames > 0) _darkFrames--;
        }

        if (LampsOn && origin.Length() < DarkWithinMetres)
        {
            try
            {
                // The nearest lamps cast shadows, as many as the player's slots allow and only with the
                // camera at the site: chosen here, because a flagged lamp the engine finds no slot for
                // is drawn unshadowed, and its own choice moves with every other light in view.
                int casting = 0;
                if (main && Shadows && origin.Length() < ShadowsWithinMetres)
                {
                    for (int i = 0; i < All.Length; i++)
                    {
                        Nearest[i] = i;
                        Nearness[i] = (origin + (up * All[i].X) + (east * All[i].Y) + (north * All[i].Z)).LengthSquared();
                    }

                    Array.Sort(Nearness, Nearest);
                    casting = Math.Clamp(Program.LightSystem.MaxShadowCasters - SlotsLeftForOthers, 0, All.Length);
                }

                double3 down = -up;
                for (int i = 0; i < All.Length; i++)
                {
                    (double x, double y, double z, float range, float intensity) = All[i];
                    bool casts = Array.IndexOf(Nearest, i, 0, casting) >= 0;
                    Light lamp = KSA.Rendering.Lighting.Light.CreateSpotLight(
                        origin + (up * x) + (east * y) + (north * z), down, range, OuterAngle, InnerAngle, Warm,
                        intensity * LampScale, casts ? ELightFlags.CastsShadows | ELightFlags.SoftShadows : ELightFlags.None);
                    if (casts) lamp.ShadowTileSize = ShadowTile;
                    Program.LightSystem.CreateLightInstance(lamp, viewport);
                }

                foreach ((double y, double z, float range, float intensity) in Fills)
                {
                    Program.LightSystem.CreateLightInstance(
                        KSA.Rendering.Lighting.Light.CreateSpotLight(
                            origin + (up * LampHeight) + (east * y) + (north * z), down, range, OuterAngle, InnerAngle, Warm,
                            intensity * LampScale), viewport);
                }
            }
            catch { }
        }

        if (!main) return;

        // Built here, where the engine builds its own render data, never while a pass is recording.
        if (Program.Editor is not null || Casters() is null) return;

        _model = float4x4.Pack(new double4x4(up.X, up.Y, up.Z, 0.0, east.X, east.Y, east.Z, 0.0,
                                             north.X, north.Y, north.Z, 0.0, origin.X, origin.Y, origin.Z, 1.0));
        _fresh = true;
    }

    // Before the first cascade drawn this frame, which is whichever comes first: not every cascade
    // is redrawn every frame, and the pool they all read is emptied at the frame's end.
    private static void BeforeShadowBucket()
    {
        if (!_fresh) return;
        _fresh = false;

        try
        {
            if (_casters is not { } casters || _materialIndices is null) return;

            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            foreach (StaticMeshRenderable caster in casters)
            {
                int[] materials = _materialIndices(caster);
                for (int i = 0; i < caster.ShadowDepthMeshBucketHandles.Length && i < materials.Length; i++)
                {
                    pool.Add(new ShadowRenderable
                    {
                        Center = _model.Translation,
                        Radius = 260f,
                        BucketHandle = caster.ShadowDepthMeshBucketHandles[i],
                        InstanceData = new InstanceData { model = _model, data = new float4(materials[i], 0f, 1f, 0f) },
                    });
                }
            }
        }
        catch
        {
            // A sunlit garage is the only failure allowed inside the engine's frame.
        }
    }

    private static StaticMeshRenderable[]? Casters()
    {
        if (_casters is not null || _casterFailed) return _casters;

        try
        {
            if (!GameSettings.ShowVesselInCascadeShadows() && !_saidShadowsOff)
            {
                _saidShadowsOff = true;
                Log.Info("office: vessel shadows are off in the graphics settings, so the sun shines into the garage");
            }

            SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
            object materialName = Activator.CreateInstance(AssetNameType!, CasterMaterial)!;
            if (Load(system, "MaterialSystem", materialName) is not GpuObjectAssetRef material)
            {
                throw new InvalidOperationException("the casters' material did not load");
            }

            var built = new List<StaticMeshRenderable>();
            foreach (string id in CasterGltfs)
            {
                object gltfName = Activator.CreateInstance(AssetNameType!, id)!;
                if (Load(system, "GltfSystem", gltfName) is not GltfPbrAssetRef gltf) throw new InvalidOperationException($"'{id}' did not load");

                // The renderable reads a material handle per mesh as it is built, so every slot is filled first.
                for (int i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;

                built.Add((StaticMeshRenderable)Activator.CreateInstance(
                    typeof(StaticMeshRenderable), system.MeshRendererStaticPbr, gltfName, system.MeshRendererStaticPrePass, true)!);
            }

            _materialIndices = AccessTools.FieldRefAccess<StaticMeshRenderable, int[]>("MaterialIndices");
            _casters = [.. built];
            Log.Info($"office: {_casters.Length} shadow casters built");
        }
        catch (Exception e)
        {
            _casterFailed = true;
            Log.Warn($"office: no shadow caster, the sun shines into the garage ({e.GetBaseException().Message})");
        }

        return _casters;
    }

    private static object? Load(SuperMeshRenderSystem system, string managerField, object name)
    {
        object? manager = system.GetType().GetField(managerField, BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
        MethodInfo? load = manager?.GetType().GetMethod("GetOrLoad", [AssetNameType!]);
        return load?.Invoke(manager, [name]);
    }
}
