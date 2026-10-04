using Brutal.ImGuiApi;

namespace KSAStructures;

/// <summary>The settings: a menu entry beside KSA's own, and a small window behind it.</summary>
internal sealed class Ui(Settings settings)
{
    private const string Title = "KSA Structures";

    public bool Visible;

    private bool _warnedMenuBar;

    /// <summary>
    /// Opens the main menu bar before KSA fills it, so this mod's entry sits alongside KSA's.
    /// Called from <b>before</b> KSA's GUI pass: from after it, <c>BeginMainMenuBar</c> returns
    /// false because the bar has already been ended for the frame.
    /// </summary>
    public void DrawMenuBarEntry()
    {
        // ModMenu draws this mod's entry when it is installed; a second would list it twice.
        if (ModMenuPresence.Installed) return;

        try
        {
            if (!ImGui.BeginMainMenuBar()) return;

            if (ImGui.BeginMenu(Title))
            {
                DrawMenuContents();
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
        catch (Exception e)
        {
            // Inside KSA's own GUI pass; a missing menu is only cosmetic.
            if (_warnedMenuBar) return;

            _warnedMenuBar = true;
            Log.Warn($"menu bar entry failed; V and the lift keys still work: {e.Message}");
        }
    }

    private void DrawMenuContents()
    {
        ImGui.MenuItem("First-person kitten (V)", default, ref settings.FirstPerson, true);
        ImGui.MenuItem("Office garage lamps", default, ref settings.OfficeLamps, true);
        ImGui.MenuItem("Settings window", default, ref Visible, true);
    }

    // Said once rather than per frame: this runs inside a menu build, so a failure repeats while it is open.
    private static bool _warnedModMenu;

    /// <summary>
    /// Called by <b>ModMenu</b>, if the player has it, to fill this mod's entry in its shared menu.
    /// Static because ModMenu resolves an instance only for a couple of hardcoded method names.
    /// </summary>
    [ModMenuEntry(Title)]
    public static void DrawModMenu()
    {
        try
        {
            Current?.DrawMenuContents();
        }
        catch (Exception e)
        {
            if (_warnedModMenu) return;

            _warnedModMenu = true;
            Log.Warn($"ModMenu entry failed: {e.Message}");
        }
    }

    /// <summary>The panel ModMenu should drive. Null until the first draw, which ModMenu may scan before.</summary>
    internal static Ui? Current { get; private set; }

    public void Draw()
    {
        Current = this;
        if (!Visible) return;

        // ###id so the version can ride in the title without the window losing its place.
        if (ImGui.Begin($"{Title} {Build.Version}###KSAStructures", ref Visible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Checkbox("First-person kitten (V)", ref settings.FirstPerson);
            Tip("See through the eyes of a kitten on foot. Hold the right mouse button and drag to look around.");

            ImGui.Checkbox("Office garage lamps", ref settings.OfficeLamps);
            Tip("The ceiling lamps under the office tower. Off, the garage has only what daylight reaches it.");

            ImGui.Separator();
            ImGui.TextDisabled("In the lift: 1 garage, 2 lobby, 3 office.");
            ImGui.TextDisabled("In the office chair: Enter plays the computer.");
            ImGui.Separator();

            if (ImGui.Checkbox("Verbose log", ref settings.VerboseLog))
            {
                Log.Threshold = settings.VerboseLog ? Log.Level.Debug : Log.Level.Info;
                Log.Info(settings.VerboseLog ? "verbose logging on" : "verbose logging off");
            }

            Tip("More detail in the log, which is what a bug report wants.");
            ImGui.TextDisabled("  -> Logs/KSAStructures.log");
        }

        ImGui.End();
    }

    // The width Dear ImGui's own help markers wrap at.
    private const float TipWidthEms = 35f;

    // What the control just drawn does, on hover. Wrapped, because a bare tooltip never is.
    private static void Tip(string text)
    {
        if (!ImGui.BeginItemTooltip()) return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipWidthEms);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
