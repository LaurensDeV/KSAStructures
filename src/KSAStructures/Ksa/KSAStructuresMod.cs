using KSA;
using StarMap.API;

namespace KSAStructures;

/// <summary>
/// StarMap entry point. StarMap loads the assembly named by mod.toml's EntryAssembly, instantiates
/// the first type carrying <see cref="StarMapModAttribute"/>, and dispatches to the attributed
/// methods below.
///
/// <para>Frame work is wrapped so a fault degrades the mod instead of taking the game down, and
/// repeated faults disable it rather than filling the log.</para>
/// </summary>
[StarMapMod]
public sealed class KSAStructuresMod
{
    private const int FaultLimit = 10;

    private readonly Settings _settings = new();
    private readonly FrameLatch _frame = new();
    private readonly OfficeComputer _computer = new();
    private FirstPerson? _firstPerson;

    private double _lastSimStep;
    private Ui? _ui;
    private Bridge? _bridge;
    private int _faults;
    private bool _disabled;

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        // Before anything asks KSA to compile a shader: the generated header names this machine's
        // own install, and a .comp that includes it will not compile until it is there.
        CoreShaderInclude.Write(mod.DirectoryPath);
        Office.Owner = mod;

        Log.Info($"loading (mod id: {mod.Id})");
        Log.Info($"KSAStructures {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        // Here and nowhere later: the pits are terrain decals, and the planet's renderer sizes its
        // buffers from the modifier count when it is built.
        Office.Place();

        // Steps a frame the GUI pass skipped before the render rather than after it. A refusal
        // degrades to the frame postfix, so it costs a frame and nothing else.
        PreRenderHook.Install(StepOnce);

        // The only way to see a save load: StarMap has no hook for one, and the mod never leaves the
        // flight scene across it.
        WorldReloadHook.Install();

        FirstPersonHook.Install();
        _firstPerson = new FirstPerson(_settings);
        DarkPass.Install();

        if (Build.Developer) _bridge = new Bridge(_settings);

        Log.Info(Build.Developer
                     ? "developer install: the bridge is listening"
                     : "player install");

        _ui = new Ui(_settings);
        Log.Info("ready - settings are under 'KSA Structures' in the menu bar");
    }

    /// <summary>
    /// The frame postfix: the fallback step, and the bridge.
    ///
    /// <para>StarMap passes a <em>player-time</em> delta, and nothing that moves with the world may
    /// integrate it: player time runs through a pause and ignores timewarp. The simulation clock is
    /// <see cref="KsaWorld.ConsumeSimStep"/>.</para>
    /// </summary>
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        try
        {
            if (_disabled || _ui is null) return;

            // A no-op on every frame the GUI pass ran. See StepOnce.
            StepOnce(dtPlayer);

            // After the step, so a command sees this frame's world and its simulated step.
            _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0);
        }
        catch (Exception e)
        {
            Fault("frame", e);
        }
        finally
        {
            // Unconditionally: this is the only hook KSA always calls, so a release skipped here stops
            // the mod for the session rather than for a frame.
            _frame.EndFrame();
        }
    }

    // One simulation step, from whichever hook reaches it first this frame. The GUI pass is
    // preferred because it runs before the render; F2 hides the UI and skips that pass, so the
    // pre-render hook and then the frame postfix are the fallbacks.
    private void StepOnce(double dtPlayer)
    {
        if (_disabled || _ui is null) return;
        if (!_frame.Claim()) return;

        KsaWorld.InvalidateCensus();

        if (WorldReloadHook.ConsumeReload())
        {
            Log.Info("a save was loaded - forgetting the previous world");
            KsaWorld.ResetSimStepTracking();
        }

        // The scene, not the craft: KSA clears the controlled vehicle when it is destroyed and the
        // flight carries straight on.
        if (KsaWorld.InFlightScene)
        {
            // Consumed, not peeked: the engine answers with the last step, so asking twice without
            // it having stepped returns the same one.
            double dtSim = KsaWorld.ConsumeSimStep();
            _lastSimStep = double.IsFinite(dtSim) && dtSim > 0.0 ? dtSim : 0.0;

            Office.Update(_lastSimStep);
        }

        // Outside the flight gate, so a view is still handed back on the way out. Driving a borrowed
        // view is not drawing: with the UI hidden the GUI pass does not run, and a view nothing
        // restates freezes at its last offset while the world carries on.
        _firstPerson?.Drive();
        _computer.Drive(dtPlayer);
        _bridge?.DriveCamera();
    }

    /// <summary>
    /// Opens the main menu bar before KSA fills it, so this mod's entry sits beside File and Universe.
    /// </summary>
    [StarMapBeforeGui]
    public void OnBeforeGui(double dt)
    {
        if (_disabled || _ui is null) return;

        try { _ui.DrawMenuBarEntry(); }
        catch { /* Cosmetic. Never take KSA's GUI pass down for a menu item. */ }
    }

    /// <summary>The settings window, and what ImGui has to be asked for: the keys and the monitor's picture.</summary>
    [StarMapAfterGui]
    public void OnAfterGui(double dt)
    {
        if (_disabled || _ui is null) return;

        try
        {
            // First, so the drawing below reads the state this frame produced.
            StepOnce(dt);

            _ui.Draw();
            _firstPerson?.Keys();
            _computer.Draw();
            Office.Keys();
            Underground.LampsOn = _settings.OfficeLamps;
        }
        catch (Exception e)
        {
            Fault("gui", e);
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        StandDown();

        // The mod's own camera controller would otherwise outlive it for the rest of the session.
        KsaWorld.RestoreStockController();

        KsaWorld.ResetSimStepTracking();
        _ui = null;
        Log.Info("unloaded");

        // Last: the log batches its writes, so without this the tail of the session never reaches disk.
        Log.Shutdown();
    }

    // Hands back every view and takes every patch off the engine.
    private void StandDown()
    {
        _bridge?.Release();
        _firstPerson?.Leave();
        _computer.Stop();
        FirstPersonHook.Remove();
        Office.Remove();
        DarkPass.Remove();
        PreRenderHook.Remove();
        WorldReloadHook.Remove();
    }

    private void Fault(string where, Exception e)
    {
        _faults++;
        Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

        if (_faults < FaultLimit) return;

        _disabled = true;
        StandDown();
        Log.Error("too many faults - KSAStructures disabled for this session");
    }
}
