using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using Brutal;
using Brutal.VulkanApi.Abstractions;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KSAStructures;

/// <summary>
/// The ground under the office: the pits dug at load, the rim's rock held down, and the terrain
/// height answered as the roof, a ramp or the floor to whoever stands on it.
/// </summary>
internal static partial class Office
{
    // The vault's pit: a circle under the lift's lobby, inside the ground the garage already dug.
    private const double VaultEast = -4.0, VaultNorth = 0.0, VaultRadius = 6.2;

    private static double ReadVaultDepth()
    {
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(typeof(Office).Assembly.Location) ?? string.Empty, "office-vault.txt");
            return File.Exists(file) && double.TryParse(File.ReadAllText(file).Trim(), System.Globalization.NumberStyles.Float,
                                                         System.Globalization.CultureInfo.InvariantCulture, out double depth) && depth > 10.0
                       ? depth : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    // Two terrain decals over the middle of everything dug, after Core's own flattening round the
    // pad (order 9999). The first levels the ground to the pad's own height, so it meets the pad's
    // flattening without a step and the grass cover can lie a hand above it everywhere; the second
    // digs.
    private const string PitXml = """
        <Modifiers>
          <Modifier Type="Decal" Name="KSAOffice_GarageLevel">
            <Amplitude Value="0" />
            <Order Value="10000" />
            <Radius Value="240" />
            <Rotation Degrees="0" />
            <Location Id="KSAOffice-GarageLevel">
              <Latitude Degrees="28.60815444" />
              <Longitude Degrees="-80.60567094" />
            </Location>
            <AltitudeOffset Km="16.97" />
            <SmoothFactor Value="0.4" />
            <Additive Value="false" />
            <HeightMap Id="KSAOffice_GarageLevelMap" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
          </Modifier>
          <Modifier Type="Decal" Name="KSAOffice_GaragePit">
            <Amplitude Value="12" />
            <Order Value="10001" />
            <Radius Value="66" />
            <Rotation Degrees="0" />
            <Location Id="KSAOffice-Garage">
              <Latitude Degrees="28.60868304" />
              <Longitude Degrees="-80.60575894" />
            </Location>
            <SmoothFactor Value="0.05" />
            <Additive Value="true" />
            <HeightMap Id="KSAOffice_GaragePit" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
          </Modifier>
        </Modifiers>
        """;

    // An absolute level rather than a map: a map's depth is its amplitude over 255, and its neutral
    // grey is not quite half, which at this depth lifts everything round the pit by decimetres.
    private static string VaultXml() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
          <Modifier Type="Decal" Name="KSAOffice_VaultLevel">
            <Amplitude Value="0" />
            <Order Value="10020" />
            <Radius Value="{VaultRadius}" />
            <Rotation Degrees="0" />
            <Location Id="KSAOffice-Vault">
              <Latitude Degrees="{LatitudeDeg + (VaultNorth / 110860.0)}" />
              <Longitude Degrees="{TowerLongitudeDeg + ((VaultEast + 3.0) / 97730.0)}" />
            </Location>
            <AltitudeOffset Km="{16.97 - VaultDepth}" />
            <SmoothFactor Value="0.05" />
            <Additive Value="false" />
            <HeightMap Id="KSAOffice_VaultLevelMap" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
          </Modifier>

        """);

    // The deep ramp's pit, an absolute level like the vault's. One decal for the shaft and the hall
    // together, and one levelling for the garage's ground and this: a planet has sixteen terrain
    // textures, a decal takes one whatever map it names, and Earth's own take nine.
    private static string DeepXml() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
          <Modifier Type="Decal" Name="KSAOffice_DeepLevel">
            <Amplitude Value="0" />
            <Order Value="10010" />
            <Radius Value="{OfficeDeep.PitRadius}" />
            <Rotation Degrees="0" />
            <Location Id="KSAOffice-Deep">
              <Latitude Degrees="{LatitudeDeg + (OfficeDeep.PitNorth / 110860.0)}" />
              <Longitude Degrees="{TowerLongitudeDeg + ((OfficeDeep.PitEast + 3.0) / 97730.0)}" />
            </Location>
            <AltitudeOffset Km="{16.97 - OfficeDeep.Depth - 0.5}" />
            <SmoothFactor Value="0.05" />
            <Additive Value="false" />
            <HeightMap Id="KSAOffice_DeepLevelMap" Path="Textures/KSAOffice_GaragePit.png" Category="TerrainHeight" />
          </Modifier>

        """);

    // At load only: the planet's renderer sizes its buffers from the modifier count when it is
    // built, and one added afterwards overruns them. Into both lists, because the count is read off
    // one and the modifiers are walked off the other.
    private static void Dig(CelestialTemplate earth)
    {
        try
        {
            if (Owner is null || earth.TerrainReference is not { } terrain) { Log.Warn("office: no terrain to dig the garage in"); return; }

            var serializer = new XmlSerializer(typeof(DecalModifierReference), new XmlRootAttribute("Modifier"));
            var document = new System.Xml.XmlDocument();
            document.LoadXml(PitXml.Replace("</Modifiers>", DeepXml() + (VaultDepth > 0.0 ? VaultXml() : string.Empty) + "</Modifiers>"));

            foreach (System.Xml.XmlNode node in document.DocumentElement!.ChildNodes)
            {
                if (node is not System.Xml.XmlElement element) continue;

                using var reader = new System.Xml.XmlNodeReader(element);
                if (serializer.Deserialize(reader) is not DecalModifierReference decal) continue;
                if (terrain.TerrainModifiers.Exists(m => m.Name == decal.Name)) continue;

                decal.OnDataLoad(Owner);
                decal.HeightMap?.Load();
                terrain.ProceduralModifiers.Modifiers.Add(decal);
                terrain.TerrainModifiers.Add(decal);
            }

            terrain.TerrainModifiers.Sort((a, b) => a.Order.Value.CompareTo(b.Order));

            Log.Info($"office: garage pit dug, {terrain.TerrainModifiers.Count} terrain modifiers on Earth");
        }
        catch (Exception e)
        {
            Log.Warn($"office: could not dig the garage ({e.GetBaseException().Message})");
        }
    }

    // ---- the rim's rock, held down while the camera is at the garage -------------------------------

    private const double TameWithinMetres = 300.0;
    private static bool _tamed, _tameFailed;
    private static readonly Dictionary<int, float> CliffDisplacement = [];

    // The pit's rim is drawn as a band of cliff a cell wide, and within 50 m of the camera cliff is
    // pushed up to 0.3 m out of the ground where grass is pushed 0.06 m: through the grass cover.
    // While the camera is within sight of the garage the cliff's displacement is held to the
    // grass's own, in the renderer's live material buffer as KSA's terrain editor writes it, and
    // put back when the camera leaves. Nothing on disk or in the template changes, and no cliff
    // anywhere else is within the 50 m the displacement is drawn at.
    private static void TameTheRim(Celestial? earth)
    {
        if (_tameFailed) return;

        try
        {
            bool near = false;
            if (earth is not null && _tower is not null && Program.GetMainCamera() is { } camera)
            {
                double3 site = earth.GetSurfacePositionEclFromCce(_tower.ForwardCcf.Transform(earth.GetCcf2Cce()));
                near = Vec.Len(camera.PositionEcl - site) < TameWithinMetres;
            }

            if (near == _tamed) return;
            if (earth?.BodyTemplate?.TerrainReference?.BiomeMaterials is not { } biomes) return;

            PlanetRenderer renderer = Program.GetPlanetRenderer();
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            if (typeof(PlanetRenderer).GetField("_biomeMaterialsMap", Private)?.GetValue(renderer) is not MappedMemory map
                || typeof(PlanetRenderer).GetField("_biomeMaterialSlot", Private)?.GetValue(renderer) is not Dictionary<Celestial, ByteSize> slots
                || !slots.TryGetValue(earth, out ByteSize slot))
            {
                _tameFailed = true;
                Log.Warn("office: the planet's material buffer has moved; rock may show at the garage's rim from close by");
                return;
            }

            // Two entries a biome, ground then slope, in the template's own order.
            Span<PlanetRenderer.BiomeMaterial> materials = map.Offset(slot).AsSpan<PlanetRenderer.BiomeMaterial>();
            for (int i = 0; i < biomes.Materials.Count; i++)
            {
                if (biomes.Materials[i].BiomeName is not ("Grass" or "Beach")) continue;

                int ground = i * 2, slope = ground + 1;
                if (slope >= materials.Length) continue;

                if (near)
                {
                    CliffDisplacement[slope] = materials[slope].displacementScale;
                    materials[slope].displacementScale = Math.Min(materials[slope].displacementScale, materials[ground].displacementScale);
                }
                else if (CliffDisplacement.TryGetValue(slope, out float was))
                {
                    materials[slope].displacementScale = was;
                }
            }

            _tamed = near;
        }
        catch (Exception e)
        {
            _tameFailed = true;
            Log.Warn($"office: could not hold the rim's rock down ({e.GetBaseException().Message})");
        }
    }

    // ---- the roof, for anything whose wheels follow the terrain -----------------------------------

    // The craft whose worker the engine is preparing, on the thread preparing it. A mod that drives
    // wheels does it from a prefix on that method, because nothing written anywhere else survives,
    // so a terrain height asked for in between is asked on that craft's behalf.
    [ThreadStatic] private static Vehicle? _stepping;

    // A wheel that is a spring against the terrain's height reaches, over the garage, for the floor
    // of the pit. So while the engine prepares a craft that stands at or above the roof, the
    // terrain's height over the dug ground answers as the roof; for a craft in the garage, and for
    // nothing down there is it ever the roof. Asked outside that window - a cursor's ray walked to
    // the ground, the camera's own clamp - it answers as the roof to a viewer above the roof and as
    // the pit to one under it, which is what each of them is looking at. On
    // GetTerrainHeightFromDirCce alone: physics reads the fixed-frame call under it, and it
    // needs the pit.
    private static void HoldUpWheels()
    {
        try
        {
            MethodInfo? worker = typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);
            MethodInfo? height = typeof(Celestial).GetMethod(nameof(Celestial.GetTerrainHeightFromDirCce), [typeof(double3), typeof(bool)]);
            if (worker is null || height is null)
            {
                Log.Warn("office: no terrain height to answer for the roof; a car on it sits on its belly");
                return;
            }

            MethodInfo Own(string name) => typeof(Office).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(worker, prefix: new HarmonyMethod(Own(nameof(BeforeWorker))) { priority = Priority.First },
                          finalizer: new HarmonyMethod(Own(nameof(AfterWorker))) { priority = Priority.Last });
            harmony.Patch(height, postfix: new HarmonyMethod(Own(nameof(AfterTerrainHeight))));

            // Any craft set down by latitude and longitude over the roof stands on it: a mover in
            // this mod or another, the launch menu, the console.
            if (typeof(Vehicle).GetMethod(nameof(Vehicle.TeleportToLocation), [typeof(Celestial), typeof(double), typeof(double)]) is { } teleport)
            {
                harmony.Patch(teleport, prefix: new HarmonyMethod(Own(nameof(BeforeTeleport))),
                              finalizer: new HarmonyMethod(Own(nameof(AfterTeleport))));
            }
            Log.Info("office: the garage roof holds up wheels that follow the terrain");
        }
        catch (Exception e)
        {
            Log.Warn($"office: the garage roof cannot hold up wheels ({e.GetBaseException().Message})");
        }
    }

    private static void BeforeTeleport(Vehicle __instance, Celestial celestial, double lat, double lon, out IDisposable __state)
    {
        __state = Nothing.Instance;
        try
        {
            // Its box as it will stand, +Y east and +Z north.
            float3 half = __instance.BoundingBoxHalfExtentsAsmb;
            __state = OnTheRoofAt(celestial, lat, lon, half.Y, half.Z);
        }
        catch
        {
        }
    }

    private static Exception? AfterTeleport(IDisposable? __state, Exception? __exception)
    {
        try { __state?.Dispose(); } catch { }
        return __exception;
    }

    // Inside the engine's frame loop: nothing here may throw.
    private static void BeforeWorker(Vehicle __instance)
    {
        _stepping = __instance;

        // The engine copies the craft's state for its worker straight after this, so a rider is
        // moved here or the move is overwritten before anything reads it.
        try { Carry(__instance); } catch { }
    }

    private static void AfterWorker() => _stepping = null;

    // How far from the tower's landmark anything dug lies, with a margin: the height answer is the
    // game's own beyond it, for every caller.
    private const double SiteReachMetres = 260.0;
    private static Celestial? _groundOf;
    private static double _groundHeight;

    private static void AfterTerrainHeight(Celestial __instance, double3 positionDirCce, ref double __result)
    {
        try
        {
            if (_tower is null || _stepping is KittenEva) return;
            if (!ReferenceEquals(__instance.BodyTemplate, _earth)) return;

            double3 dirCcf = positionDirCce.Transform(__instance.GetCcf2Cce().Inverse());
            // Only over the site itself: within its reach of the tower, as an angle at the body's centre.
            double reach = SiteReachMetres / __instance.MeanRadius;
            if (double3.Dot(dirCcf, _tower.ForwardCcf) < 1.0 - (0.5 * reach * reach)) return;

            if (!ReferenceEquals(_groundOf, __instance))
            {
                _groundHeight = __instance.GetTerrainHeightFromDirCcf(_tower.ForwardCcf);
                _groundOf = __instance;
            }

            double ground = _groundHeight;
            double roof = ground + 0.08;
            double dug = ground - __result;
            if (dug < 0.05) return;

            _tower.GetAxesCcf(out _, out double3 east, out double3 north);
            double3 from = (dirCcf - _tower.ForwardCcf) * (__instance.MeanRadius + ground);
            double y = double3.Dot(from, east) - 3.0, z = double3.Dot(from, north) + LandmarkNorth;

            // A ramp is its slab to everybody: the terrain under it is only there to be dug.
            if (RampTop(y, z) is { } slab)
            {
                __result = Math.Max(__result, ground + slab);
                return;
            }

            // Who is asking stands above the roof and gets the roof, or under it and gets the floor.
            // A camera only once it is clear of the roof by the half metre its own clamp keeps over
            // the ground: one rising through the roof's height, in the lift, is otherwise told the
            // ground has jumped up under it and is thrown up to meet it.
            double3 asker = _stepping is { } craft ? craft.GetPositionCce()
                          : Program.GetMainCamera() is { } camera ? camera.PositionEcl - __instance.GetPositionEcl()
                          : default;
            double clear = _stepping is null ? 0.55 : -0.3;
            double askerHeight = asker.Length() - __instance.MeanRadius;

            // Over the deep ramp the terrain is one pit under several things, and which of them is
            // the ground depends on how high whoever is asking stands.
            if (OfficeDeep.Covers(y, z))
            {
                __result = ground + OfficeDeep.SurfaceUnder(y, z, askerHeight - ground + (_stepping is null ? 0.0 : 0.3));
                return;
            }

            if (askerHeight >= roof + clear) __result = roof;
            else if (dug > 5.2 && askerHeight > ground + Floor - 1.5) __result = ground + Floor;
        }
        catch
        {
        }
    }

    // The building's frame: +Y east, +Z north, heights over the levelled ground. Everything dug
    // is read off the terrain itself; only the ramps are restated, from the garage's mesh.
    private const double Floor = -5.0;

    // Each open ramp: where its slab leaves the floor, where it reaches the ground east of that,
    // and its width.
    private static readonly (double Y0, double Y1, double Z0, double Z1)[] Ramps =
    [
        (45.0, 64.7, 80.0, 87.0),
    ];

    // The top of the ramp slab at a place, or null where there is no ramp.
    private static double? RampTop(double y, double z)
    {
        foreach ((double y0, double y1, double z0, double z1) in Ramps)
            if (y > y0 && y <= y1 + 0.1 && z >= z0 && z <= z1) return Floor + ((y - y0) * ((0.03 - Floor) / (y1 - y0)));

        return null;
    }

    // For as long as the answer is held, a craft set down at this place stands on the roof over the
    // dug ground where there is one, and not on the floor under it. The game's own placement stands
    // a craft on the highest ground under the four corners of its box and adds a launch pad's
    // surface height inside its footprint, so the height lent is the roof's over that corner.
    private static IDisposable OnTheRoofAt(Celestial body, double latitudeDeg, double longitudeDeg, double halfEast, double halfNorth)
    {
        if (_furnishing || _tower?.GetStaticObject() is not { } tower || !ReferenceEquals(body.BodyTemplate, _earth)) return Nothing.Instance;

        double y = ((longitudeDeg - TowerLongitudeDeg) * 97730.0) - 3.0;
        double z = (latitudeDeg - LatitudeDeg) * 110860.0;
        if (Math.Abs(y) > 200.0 || Math.Abs(z) > 200.0 || RampTop(y, z) is not null) return Nothing.Instance;
        if (LeastDug(body, y, z, 0.0, 0.0) < 0.3) return Nothing.Instance;

        tower.Template.SurfaceHeight = new DistanceReference(LeastDug(body, y, z, halfEast, halfNorth) + 0.08);
        return new Lent(tower);
    }

    // How far the ground is dug under the least-dug corner of a box standing at a place in the
    // building's frame: what the game's own placement stands a craft on.
    private static double LeastDug(Celestial body, double y, double z, double halfEast, double halfNorth)
    {
        double ground = body.GetTerrainHeightFromDirCcf(_tower!.ForwardCcf);
        _tower.GetAxesCcf(out _, out double3 east, out double3 north);

        double least = double.MaxValue;
        foreach (double dy in (ReadOnlySpan<double>)[-halfEast, halfEast])
        {
            foreach (double dz in (ReadOnlySpan<double>)[-halfNorth, halfNorth])
            {
                double3 at = (_tower.ForwardCcf * (body.MeanRadius + ground)) + (east * (y + dy + 3.0)) + (north * (z + dz - LandmarkNorth));
                least = Math.Min(least, Math.Max(0.0, ground - body.GetTerrainHeightFromDirCcf(at.Normalized())));
            }
        }

        return least;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    private sealed class Lent(StaticObject tower) : IDisposable
    {
        public void Dispose() => tower.Template.SurfaceHeight = new DistanceReference(GroundSurface);
    }
}
