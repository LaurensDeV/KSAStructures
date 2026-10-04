using System.IO;

namespace KSAStructures;

/// <summary>
/// Writes the one file that lets this mod's shader reach KSA's own shader library.
///
/// <para><b>An absolute include resolves and a relative one cannot.</b> shaderc resolves an include
/// against the requesting file, and Core's shaders sit under the install while a mod's sit under the
/// player's Documents: there is no relative path between the two trees. The header's own relative
/// includes then resolve inside the Core tree.</para>
///
/// <para><b>It cannot be committed, because it is the player's install.</b> So it is written beside
/// the mod's own shaders at load, pointing at their copy, and nothing of RocketWerkz's is
/// redistributed. Written every load rather than once, because the path it holds is only true of
/// the install that wrote it.</para>
/// </summary>
internal static class CoreShaderInclude
{
    // In a folder called Content: KSA's shader hot reload keys every include on the text after the
    // first "Content" in its full path, and one with none throws inside that bookkeeping and is
    // logged as an error on every compile. Named for this mod, so another mod's header of the same
    // job is never the one a search finds.
    private const string Folder = "Shaders";
    private const string Subfolder = "Content";
    private const string Generated = "KSAStructuresCore.glsl";

    // Under the game's own Content root. Global.glsl declares the set KSA binds at 0 for every
    // compute pass, the lighting block and the ambient table among it; AtmosphereLuts.glsl reads
    // them; TextureSet.glsl is KSA's bindless textures, which the ambient table is one of.
    private static readonly string[][] Wanted =
    [
        ["Content", "Core", "Shaders", "Common", "Global.glsl"],
        ["Content", "Core", "Shaders", "Atmosphere", "AtmosphereLuts.glsl"],
        ["Content", "Core", "Shaders", "Common", "TextureSet.glsl"],
    ];

    /// <summary>Whether the header was written and names files that are there.</summary>
    public static bool Available { get; private set; }

    /// <summary>
    /// Writes it, next to the mod's own shaders. <paramref name="modFolder"/> is where this mod was
    /// loaded from. Returns false and says why rather than throwing: without it the shader does not
    /// compile, and what is under the ground keeps its daylight.
    /// </summary>
    public static bool Write(string modFolder)
    {
        Available = false;

        try
        {
            // The game's working directory is its install root, which is what makes the Content
            // path resolvable without anybody being asked where the game is.
            List<string> found = [];

            foreach (string[] parts in Wanted)
            {
                string core = Path.GetFullPath(Path.Combine(parts));

                if (!File.Exists(core))
                {
                    Log.Warn($"core shaders: '{core}' is not there, so the dark pass's shader cannot compile");
                    return false;
                }

                found.Add(core);
            }

            string shaders = Path.Combine(modFolder, Folder);
            if (!Directory.Exists(shaders))
            {
                Log.Warn($"core shaders: no '{shaders}' to write into");
                return false;
            }

            // Forward slashes whatever the platform: these are GLSL includes, and a backslash in
            // one is an escape.
            string body = "// Generated at load. Points at this machine's own KSA install;\n"
                          + "// nothing of the game's is copied here. See Ksa/CoreShaderInclude.cs.\n";

            foreach (string core in found) body += $"#include \"{core.Replace('\\', '/')}\"\n";

            string into = Directory.CreateDirectory(Path.Combine(shaders, Subfolder)).FullName;
            File.WriteAllText(Path.Combine(into, Generated), body);

            Available = true;
            Log.Info($"core shaders: KSA's library is included from {Path.GetDirectoryName(found[0])}");

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"core shaders: could not write the include ({e.Message}); the dark pass's shader cannot compile");
            return false;
        }
    }
}
