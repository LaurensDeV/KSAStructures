namespace KSAStructures;

/// <summary>
/// A copy of MrJeranimo's <c>ModMenuEntryAttribute</c>, so <b>ModMenu</b> lists this mod under its
/// shared <c>Mods</c> menu when a player has it installed.
///
/// <para><b>A copy on purpose, not a dependency.</b> ModMenu scans every loaded assembly and matches
/// the attribute by its type's name alone, so declaring it here is enough to be found, and it is inert
/// when ModMenu is absent.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    : Attribute
{
    public string MenuName { get; } = menuName;

    public string? IsModMenuActivePropertyName { get; } = isModMenuActivePropertyName;
}

/// <summary>Whether ModMenu is loaded, so this mod does not draw its own menu as well.</summary>
internal static class ModMenuPresence
{
    private static bool? _present;

    /// <summary>
    /// True when ModMenu is in the process. Resolved once: assemblies do not come and go, and this
    /// is asked every frame by the menu-bar draw.
    /// </summary>
    public static bool Installed => _present ??= Detect();

    private static bool Detect()
    {
        try
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "ModMenu") return true;
            }
        }
        catch
        {
            // A mod that cannot tell either way should draw its own menu rather than none.
        }

        return false;
    }
}
