using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;

namespace KSAStructures;

/// <summary>
/// Every direct touch of KSA's internals that is not the office's own lives here. KSA is
/// pre-release and its API moves, so keeping the surface in one file means a game update breaks
/// one place, not ten.
///
/// All positions and velocities are in the ecliptic frame (Ecl): inertial, metres.
/// </summary>
internal static class KsaWorld
{
    /// <summary>The vehicle the player is currently flying, or null in menus.</summary>
    public static Vehicle? ControlledVehicle => Program.ControlledVehicle;

    /// <summary>
    /// The player has a craft to fly.
    ///
    /// <para><b>This is not "the flight scene is up", and using it as one strands the camera.</b>
    /// <c>Universe.DestroyVehicle</c> clears <c>ControlledVehicle</c> when the craft being flown is
    /// destroyed, and the scene carries straight on — the engine points the view at the wreckage
    /// and keeps rendering. Anything handing the player's view back must ask
    /// <see cref="InFlightScene"/>, or losing a craft is mistaken for leaving flight and the
    /// hand-back is skipped in the one case that most needs it.</para>
    /// </summary>
    public static bool InFlight => Program.ControlledVehicle is { IsDisposed: false };

    /// <summary>
    /// The flight scene is up, whether or not the player has a craft in it.
    ///
    /// <para>What separates a destroyed craft — still in flight, still one live camera, and
    /// everything the mod borrowed still worth handing back — from actually leaving, which is the
    /// editor or the menus. The new scene brings its own camera, so there the recording describes
    /// something that has gone.</para>
    /// </summary>
    public static bool InFlightScene
    {
        get
        {
            try
            {
                return Program.Editor is null && Universe.CurrentSystem is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The simulated seconds KSA's last step actually advanced the world by.
    ///
    /// <para>This — not the player-time delta StarMap hands the frame hook, and not a
    /// difference of clock samples — is what the mod steps on. A paused game reports zero
    /// and a warped one reports the real span, and because it is the step the engine applied
    /// rather than one measured around it, it cannot be a step out of phase with the world.</para>
    /// </summary>
    public static double SimStepSeconds => Universe.GetLastSimStep().DeltaTime;

    // Pure, and in Sim/ so it can be tested. See StepGate.
    private static readonly StepGate<UniverseTime> _stepGate = new();

    /// <summary>
    /// The simulated seconds to integrate now, or zero if the engine has applied no new step
    /// since the last call. <b>Consuming</b> — call once per update and use the result.
    ///
    /// <para><see cref="SimStepSeconds"/> reports the <em>last</em> step, not one since the
    /// previous call, so asking twice without the engine stepping returns it twice. Integrating
    /// it twice adds motion the world never made, and it compounds because it lands in
    /// <c>PositionEcl</c>.</para>
    /// </summary>
    public static double ConsumeSimStep()
    {
        SimStep step = Universe.GetLastSimStep();

        if (_consumedThrough is { } from && !step.NextTime.Equals(from))
        {
            LastLongerOfStepAndSpan = Math.Max(step.DeltaTime, Span(from, step.NextTime));
        }

        _consumedThrough = step.NextTime;

        // The span, not just the step: a frame in which this mod's hook never ran still advanced
        // the world, and GetLastSimStep reports only the most recent one. Taking a screenshot is
        // exactly that case -- ScreenshotCapture sets Program.DrawUI false and KSA guards the call
        // this mod postfixes with it, so the hook is not called at all. Differenced in Int128
        // nanoseconds because absolute universe time is far too large to subtract as double.
        return _stepGate.Consume(step.NextTime, step.DeltaTime, Span);
    }

    private static double Span(UniverseTime from, UniverseTime to)
        => (double)(to.Nanoseconds - from.Nanoseconds) / 1e9;

    private static UniverseTime? _consumedThrough;

    /// <summary>
    /// What the last new step would have been as the longer of the reported step and the span.
    /// Measurement only: summed beside the step taken it can be compared against
    /// <see cref="SimClockNanoseconds"/>, because the longer runs 0.125 ns a frame ahead of the
    /// clock the planets are placed on.
    /// </summary>
    public static double LastLongerOfStepAndSpan { get; private set; }

    /// <summary>Forgets which step was last integrated. For unload and scene changes.</summary>
    public static void ResetSimStepTracking()
    {
        _stepGate.Reset();
        _consumedThrough = null;
    }

    /// <summary>True while the simulation is stopped. KSA defines this as speed exactly zero.</summary>
    public static bool IsPaused => Universe.IsPaused();

    /// <summary>Current timewarp factor; 1.0 is real time, 0.0 is paused. Display only.</summary>
    public static double SimulationSpeed => Universe.SimulationSpeed;

    /// <summary>
    /// Slowest speed worth offering. Below this KSA names the speed "paused" — its SimSpeed
    /// constructor calls anything under 1e-4 paused — and a world that runs while every label
    /// says it is stopped is worse than one that will not go slower.
    ///
    /// <para>It is only the *name*: Universe.IsPaused() tests the speed against exactly zero, so
    /// the world really would keep running. That mismatch is the trap, not a limit.</para>
    /// </summary>
    public const double SlowestSimSpeed = 0.001;

    /// <summary>
    /// Sets the world's simulation speed, including values slower than the in-game controls
    /// reach. KSA's own roller works in tenths, so 0.1x is as slow as it will go; nothing in
    /// the engine enforces that.
    ///
    /// <para><c>SetSimulationSpeed</c> only rejects speeds above <c>SimSpeed.MaxSpeed</c> — there
    /// is no floor — and it assigns the field directly rather than queuing an input event, so a
    /// value set here holds until something else changes it.</para>
    ///
    /// <para>Everything this mod does is already keyed to simulated time, so a slow world needs
    /// no special handling: the step simply comes back smaller and everything scales with it.</para>
    /// </summary>
    /// <returns>False if the value was not finite or not positive; the speed is left alone.</returns>
    public static bool SetSimulationSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0.0) return false;

        Universe.SetSimulationSpeed(new SimSpeed(Math.Max(speed, SlowestSimSpeed)));
        return true;
    }

    /// <summary>
    /// Stops the world, or starts it again at real time.
    ///
    /// <para>Separate from <see cref="SetSimulationSpeed"/>, which refuses anything at or below
    /// zero and clamps to <see cref="SlowestSimSpeed"/>: a caller asking for a slow world and a
    /// caller asking for a stopped one want different things, and a speed argument that silently
    /// becomes a pause is the trap that guard exists to close.</para>
    ///
    /// <para><c>Universe.IsPaused()</c> tests the speed against exactly zero, so this is what makes
    /// that property true.</para>
    /// </summary>
    /// <returns>False only if the call threw.</returns>
    public static bool SetPaused(bool paused)
    {
        try
        {
            Universe.SetSimulationSpeed(new SimSpeed(paused ? 0.0 : 1.0));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True once the vehicle has been destroyed or unloaded.</summary>
    public static bool IsAlive(Vehicle? v) => v is { IsDisposed: false };

    /// <summary>
    /// Every live vehicle in the world, built at most once a frame and shared by everything that
    /// walks it.
    ///
    /// <para>Nothing about the answer is per caller: it is what exists, so walking the system once
    /// per consumer repeats the same work for the same result.</para>
    ///
    /// <para><b>Freshness is a generation, not a frame count.</b> The list may not outlive a
    /// change to the world, because a destroyed vehicle stays in it as a disposed reference where
    /// a fresh walk would simply omit it. So anything that removes a vehicle invalidates it, and
    /// each hook that can start a pass invalidates it once on the way in. Holding it across a
    /// frame is what <see cref="CollectVehicles"/>'s own warning is about, and this does not.</para>
    /// </summary>
    public static IReadOnlyList<Vehicle> Vehicles
    {
        get
        {
            if (_censusFresh) return _census;

            CollectVehicles(_census);
            _censusFresh = true;
            return _census;
        }
    }

    /// <summary>
    /// Throws the shared census away. Called at the top of each pass, and by anything that takes a
    /// vehicle out of the world.
    /// </summary>
    public static void InvalidateCensus() => _censusFresh = false;

    private static readonly List<Vehicle> _census = [];

    private static bool _censusFresh;

    /// <summary>
    /// Appends every vehicle the game currently has loaded into <paramref name="into"/>.
    ///
    /// Reads <c>Universe.CurrentSystem.All</c> rather than <c>Program.VehiclesInFrame</c>.
    /// The latter is a per-frame scratch buffer refilled by <c>RefreshVehiclesInFrame()</c>
    /// at a point in the tick that does not line up with a Harmony postfix on OnFrame - it
    /// reads back empty from there, which silently finds nothing. The system's collection
    /// is the authoritative list and is valid from any hook.
    ///
    /// Copies immediately: the result must not be held across frames, and engine state must not
    /// be under iteration while vehicles are being destroyed.
    /// </summary>
    public static void CollectVehicles(List<Vehicle> into)
    {
        into.Clear();

        try
        {
            if (Universe.CurrentSystem is { } system)
            {
                ReadOnlySpan<Astronomical> all = system.All.AsSpan();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"vehicle enumeration failed: {e.Message}");
        }

        // Belt and braces: if the system collection ever comes back empty, fall back to the
        // per-frame buffer rather than going blind.
        if (into.Count != 0) return;

        try
        {
            ReadOnlySpan<Vehicle> inFrame = Program.VehiclesInFrame;
            for (int i = 0; i < inFrame.Length; i++)
            {
                if (inFrame[i] is { IsDisposed: false } v) into.Add(v);
            }
        }
        catch
        {
            // Nothing more to try.
        }
    }

    public static double3 PositionEcl(Vehicle v) => v.GetPositionEcl();

    /// <summary>
    /// Where a body is, guarded — the anchor something in flight falls back on once the craft it
    /// left is gone. Non-finite rather than throwing, so a caller can decline to move anything this frame.
    /// </summary>
    public static double3 PositionEcl(Celestial body)
    {
        try
        {
            return body.GetPositionEcl();
        }
        catch
        {
            return new double3(double.NaN, double.NaN, double.NaN);
        }
    }

    public static double3 VelocityEcl(Vehicle v) => v.GetVelocityEcl();

    /// <summary>Rough size of a vehicle, used to scale contact and picking checks.</summary>
    public static double MeanRadius(Vehicle v)
    {
        double r = v.MeanRadius;
        return double.IsFinite(r) && r > 0.0 ? r : 5.0;
    }

    public static string DisplayName(Vehicle v)
    {
        try { return string.IsNullOrEmpty(v.Id) ? "unnamed" : v.Id; }
        catch { return "unnamed"; }
    }

    /// <summary>
    /// Local "up" at the platform: the radial-out direction from whatever body it is bound to.
    /// Falls back to the platform's velocity direction, and finally to +Z, so the caller always
    /// gets a usable unit vector.
    /// </summary>
    public static double3 LocalUp(Vehicle platform)
    {
        try
        {
            if (platform.Parent is IPosition parent)
            {
                double3 radial = platform.GetPositionEcl() - parent.GetPositionEcl();
                double3 up = Vec.Unit(radial);
                if (!up.Equals(Vec.Zero)) return up;
            }
        }
        catch
        {
            // Parent can be null or mid-transition during scene changes; fall through.
        }

        double3 alongTrack = Vec.Unit(platform.GetVelocityEcl());
        return alongTrack.Equals(Vec.Zero) ? new double3(0, 0, 1) : alongTrack;
    }

    /// <summary>
    /// The body a craft is at, or null in deep space. What the terrain map is drawn against: its
    /// centre, its rotation axis and its height field all come from here.
    /// </summary>
    public static Celestial? ParentBody(Vehicle platform)
    {
        try
        {
            return platform.Parent as Celestial;
        }
        catch
        {
            return null;
        }
    }

    private static ScreenshotCapture? _shots;

    private static bool _lookedForShots;

    private static bool _warnedAboutShots;

    /// <summary>
    /// Asks the game to save a screenshot of its own framebuffer.
    ///
    /// <para>The alternative is <c>tools/screenshot.sh</c>, which grabs the whole primary display
    /// and so refuses unless the game is in front — which it is not, during an unattended run on a
    /// machine somebody is using. This needs no focus, cannot photograph somebody's desktop, and
    /// writes a clean frame: <c>ui</c> and <c>hud</c> are opt-in, so the default is the scene with
    /// no panel over it, which is what anybody judging an effect wants.</para>
    ///
    /// <para>Files land in <c>Documents/exports/screenshots/ksa_&lt;stamp&gt;_&lt;w&gt;x&lt;h&gt;.png</c>.</para>
    ///
    /// <para><b>One private field is the whole obstacle</b>: <c>ScreenshotCapture.Request</c> is public and
    /// <c>Program._screenshotCapture</c> is not exposed. The game's own <c>screenshot</c> terminal
    /// command reaches it the same way. Reflected once, and a KSA rename turns this off rather than
    /// breaking anything — nothing but a picture is lost.</para>
    /// </summary>
    public static bool TryRequestScreenshot(int scale = 1, string flags = "")
    {
        try
        {
            if (!_lookedForShots)
            {
                _lookedForShots = true;
                _shots = typeof(Program)
                         .GetField("_screenshotCapture", BindingFlags.NonPublic | BindingFlags.Instance)
                         ?.GetValue(Program.Instance) as ScreenshotCapture;

                if (_shots is null && !_warnedAboutShots)
                {
                    _warnedAboutShots = true;
                    Log.Warn("screenshots: Program._screenshotCapture did not resolve; "
                             + "scripted captures will write nothing");
                }
            }

            if (_shots is null) return false;

            // A capture already running is dropped by the engine with its own warning, so asking
            // again while one is in flight costs nothing and loses only that frame.
            _shots.Request(scale, flags);
            return true;
        }
        catch (Exception e)
        {
            if (!_warnedAboutShots)
            {
                _warnedAboutShots = true;
                Log.Warn($"screenshots: could not ask for one: {e.Message}");
            }
            return false;
        }
    }

    // The views this mod may drive, in the order its viewport numbers index. GameViews rather than
    // Views because only a game viewport has a camera and controllers -- which also keeps the
    // part-thumbnail renderer out, since it is not one.
    private static ReadOnlySpan<IGameViewport> GameViewports
    {
        get
        {
            try { return ViewportRegistry.GameViews; }
            catch { return default; }
        }
    }

    // The engine's default field of view, and the widest and narrowest this mod will set: the
    // projection throws for a field of zero or more than half a turn.
    private const double DefaultFovDeg = 50.0;

    private const double MinFovDeg = 1.0;

    private const double MaxFovDeg = 120.0;

    /// <summary>The main view's own field of view (deg), or the engine's default if unreadable.</summary>
    public static double MainViewFovDeg()
    {
        try
        {
            if (Program.MainViewport?.GetCamera() is not { } camera) return DefaultFovDeg;

            double degrees = double.RadiansToDegrees(camera.GetFieldOfView());

            return double.IsFinite(degrees) && degrees > 0.0 && degrees < 180.0
                ? degrees
                : DefaultFovDeg;
        }
        catch
        {
            return DefaultFovDeg;
        }
    }

    /// <summary>
    /// Narrows or widens the main view.
    ///
    /// <para>Clamped here and not merely by the caller: <c>SetFieldOfView</c> does not clamp, and
    /// <c>UpdateProjection</c> throws <c>ArgumentOutOfRangeException</c> for a field of zero or
    /// more than half a turn — out of the frame hook, which takes the mod down with it.</para>
    ///
    /// <para>Has to be rewritten every frame it is wanted. The player's zoom keys route through
    /// <c>ChangeFieldOfView</c>, which clamps to 15°–120°, so a single keypress throws away
    /// anything narrower than 15° and there is no notification that it happened.</para>
    /// </summary>
    public static bool TrySetMainViewFov(double degrees)
    {
        if (!double.IsFinite(degrees)) return false;

        try
        {
            if (Program.MainViewport?.GetCamera() is not { } camera) return false;

            float wanted = (float)Math.Clamp(degrees, MinFovDeg, MaxFovDeg);
            if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) < 1e-3) return true;

            camera.SetFieldOfView(wanted);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not set the field of view: {e.Message}");
            return false;
        }
    }

    /// <summary>What the main view was doing before something borrowed it.</summary>
    /// <param name="FovDeg">
    /// The field of view it was set to. Carried because a borrower may zoom, and one that
    /// hands back everything except the zoom leaves the player at 3° with no control that reaches
    /// it — their own zoom keys clamp at 15° and cannot widen past it.
    /// </param>
    public readonly record struct MainView(IFollowable? Following, CameraMode Mode, double FovDeg,
                                           bool Valid);

    /// <summary>
    /// Records the main view so it can be handed back.
    ///
    /// <para>Taken before the first write, not after. Setting Fixed clears the follow, so a
    /// reading taken afterwards describes the borrowed state and restoring from it leaves the
    /// player at a fixed point in space with no way home.</para>
    /// </summary>
    public static MainView RememberMainView()
    {
        try
        {
            if (Program.MainViewport is not { } viewport) return default;

            return new MainView(viewport.GetCamera()?.Following, viewport.Mode, MainViewFovDeg(), true);
        }
        catch (Exception e)
        {
            Log.Warn($"could not read the main view: {e.Message}");
            return default;
        }
    }

    /// <summary>
    /// Whether something the view was following is still there to go back to.
    ///
    /// <para>A destroyed craft is not, and it is the ordinary case rather than a corner: the thing
    /// a borrower was watching is often the thing that just blew up. The engine has already
    /// answered the same question for itself by pointing the view at the wreckage, so the right
    /// move is to leave that alone — restoring over it puts the camera on a disposed vehicle and
    /// holds it there.</para>
    /// </summary>
    public static bool CanFollow(IFollowable? target) => target switch
    {
        null => false,
        Vehicle v => !v.IsDisposed,
        _ => true,
    };

    /// <summary>
    /// Puts back whatever the view was following before a mod borrowed it.
    ///
    /// <para>Separate from the mode: following something of the mod's own has to be undone even
    /// when the mode never changed, and leaving a camera pointed at an object the mod is about to
    /// forget is how a view ends up stuck on something that no longer exists.</para>
    /// </summary>
    public static bool RestoreFollow(MainView saved)
    {
        if (!saved.Valid || saved.Following is null) return false;

        try
        {
            if (Program.MainViewport?.GetCamera() is not { } camera) return false;

            camera.SetFollow(saved.Following, tidalLocking: true, changeControl: false, alert: false);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not restore what the view was following: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Hands the main view back in the two halves it was taken in, and says whether each half
    /// arrived. Every borrower goes through here, so a refusal cannot mean two different things in
    /// two files.
    ///
    /// <para>Order is the contract: what the view follows is read before anything is written,
    /// because the restore is about to change it and "did the player move this themselves" has no
    /// answer afterwards.</para>
    ///
    /// <para>A follow that cannot be given back is <b>not</b> a refusal. A destroyed craft is the
    /// ordinary way a chase ends, and a follow the player has taken is theirs to
    /// keep — neither has anything to retry, so counting either would leave a caller retrying
    /// something that is never coming back.</para>
    /// </summary>
    /// <returns>False if either half was refused, and the caller still holds the view.</returns>
    public static bool TryHandBackMainView(MainView saved, IFollowable? followed,
                                           out bool mode, out bool follow)
    {
        bool followIsOurs = MainViewFollows(followed);

        mode = BeginRestoreMainView(saved);
        follow = !followIsOurs || !CanFollow(saved.Following) || RestoreFollow(saved);

        return mode && follow;
    }

    // The engine's own controller, kept so it can be put back. Static because there is one main
    // viewport and the swap outlives any single borrower of it.
    private static FixedController? _stockFixedController;

    // The one name rather than a signature that the camera stands on, which no build checks.
    // GameViewport.FixedController is an auto-property whose
    // setter is protected, behind IGameViewport's get-only one, so the backing field is the only
    // way in, checked and warned about if it moves.
    //
    // Null here is not a fault: the engine's own controller stays, which costs the levelled
    // horizon and puts a driven view's aim a frame behind again. Both are worse pictures, not crashes.
    private static FieldInfo? _fixedControllerField;

    private static bool _lookedForFixedControllerField;

    private static FieldInfo? FixedControllerField(IGameViewport viewport)
    {
        if (_lookedForFixedControllerField) return _fixedControllerField;
        _lookedForFixedControllerField = true;

        _fixedControllerField = viewport.GetType().GetField(
            "<FixedController>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        if (_fixedControllerField is null)
        {
            Log.Warn("camera: no FixedController backing field on "
                     + $"{viewport.GetType().Name} - keeping KSA's own controller, so the horizon "
                     + "is not levelled and a driven view's aim lags a frame. If the property has a "
                     + "public setter again, use it and delete this.");
        }

        return _fixedControllerField;
    }

    // Puts LevelHorizonController on the main viewport, once, and answers with whichever
    // controller is now in place. Null only if the viewport has no controller at all.
    //
    // If the swap itself fails the engine's own is returned and everything carries on with the
    // roll it always had -- this is an extension point nobody promised, so it has to be allowed
    // to stop working.
    private static FixedController? LevelTheHorizon(IGameViewport viewport)
    {
        if (viewport.FixedController is LevelHorizonController already) return already;

        try
        {
            if (viewport.BaseCamera is not { } camera) return viewport.FixedController;
            if (FixedControllerField(viewport) is not { } field) return viewport.FixedController;

            var level = new LevelHorizonController(camera);

            _stockFixedController ??= viewport.FixedController;
            field.SetValue(viewport, level);

            // The write is the whole point, and a silent no-op would read exactly like a working
            // swap: everything downstream still finds *a* controller.
            if (!ReferenceEquals(viewport.FixedController, level))
            {
                Log.Warn("camera: the FixedController write did not take - keeping KSA's own");
                _stockFixedController = null;
                return viewport.FixedController;
            }

            Log.Info("camera: levelling the horizon on the main view");

            return level;
        }
        catch (Exception e)
        {
            Log.Warn($"could not level the horizon, using KSA's own controller: {e.Message}");
            return viewport.FixedController;
        }
    }

    /// <summary>Puts KSA's own fixed controller back, if it was ever swapped out.</summary>
    public static void RestoreStockController()
    {
        if (_stockFixedController is not { } stock) return;

        try
        {
            if (Program.MainViewport is { } viewport
                && FixedControllerField(viewport) is { } field)
            {
                field.SetValue(viewport, stock);
            }
            Log.Info("camera: gave the fixed controller back");
        }
        catch (Exception e)
        {
            Log.Warn($"could not restore the fixed controller: {e.Message}");
        }
        finally
        {
            _stockFixedController = null;
        }
    }

    /// <summary>
    /// Points the main view from a place, using Fixed mode as it is meant to be used.
    ///
    /// <para>The camera keeps following whatever it followed. <c>FixedController.OnFrame</c> puts
    /// it at <c>following.GetPositionEcl() + CameraOffset</c> looking along <c>CameraRotation</c>,
    /// so those two fields are the whole interface — and the offset is measured from the followed
    /// craft, not from the world.</para>
    ///
    /// <para><c>CameraRotation</c> must be non-zero before the mode is set. The controller crosses
    /// it with the frame's up and normalises, so a zero vector divides by zero — which is the
    /// entire reason this mode has a reputation for crashing.</para>
    /// </summary>
    /// <param name="offsetFromFollowed">
    /// Where the camera goes <em>relative to the craft the view is following</em>, not an absolute
    /// position. The controller adds it to <c>following.GetPositionEcl()</c> later in the frame,
    /// so an offset derived from that position here is measured against a different instant from
    /// the one it is applied to — which is a frame of the platform's motion, every frame, and
    /// reads as the thing being watched shivering.
    /// </param>
    /// <param name="fovDeg">
    /// The field this borrower wants. Required, and deliberately not optional: a borrower that
    /// says nothing about the field inherits whatever the last one left behind, which can be a
    /// magnified few degrees. Making it part of driving the view is what stops that being possible.
    /// </param>
    /// <param name="pose">
    /// Somewhere the controller may ask again, inside the engine's own viewport pass. Null is the
    /// right answer for anything that does not need to be in phase with the frame, and passing it
    /// every call is what takes the previous borrower's source back off the controller.
    /// </param>
    public static bool TryLookFromMainViewport(double3 offsetFromFollowed, double3 forwardEcl,
                                               double3 upEcl, double fovDeg, IViewPose? pose = null)
    {
        if (!Vec.IsFinite(offsetFromFollowed) || !Vec.IsFinite(forwardEcl)) return false;
        if (Vec.Len2(forwardEcl) < 1e-12) return false;

        try
        {
            if (Program.MainViewport is not { } viewport) return false;
            if (viewport.GetCamera()?.Following is null) return false;

            FixedController? controller = LevelTheHorizon(viewport);
            if (controller is null) return false;

            // Only when the engine is deriving up for itself, which it does whenever the level
            // controller could not be installed. Its axis is then ecliptic +Z, and a view along
            // that divides by zero. LevelHorizonController has no such direction: it falls back
            // for a view along the up it was given rather than refusing, so the chase is never
            // dropped mid-flight.
            if (controller is not LevelHorizonController
                && Math.Abs(Vec.Dot(Vec.Unit(forwardEcl), new double3(0, 0, 1))) > 0.999)
            {
                return false;
            }

            // Set before the mode, every time. A frame drawn in Fixed with a zero rotation is the
            // crash, and setting the mode first leaves exactly that gap.
            controller.CameraRotation = Vec.Unit(forwardEcl);
            controller.CameraOffset = offsetFromFollowed;

            if (controller is LevelHorizonController level)
            {
                level.UpEcl = upEcl;

                // Every call, including with null. A borrower that stops driving must not leave
                // its source installed, or the controller goes on asking a borrower that has let go.
                level.Pose = pose;
            }

            // With the pose and not after it. A borrower supplying a pose source restates the
            // field in phase every frame, so this is the take-over frame's value and the fallback
            // if that source ever refuses.
            TrySetMainViewFov(fovDeg);

            if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not drive the main view: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the main view is currently following this craft.
    ///
    /// <para>Asked before anything borrows the view: a craft on the far side of the world taking
    /// the camera off whatever the player is watching is a hijack, however good the shot.</para>
    /// </summary>
    public static bool MainViewFollows(IFollowable? target)
    {
        if (target is null) return false;

        try
        {
            return ReferenceEquals(Program.MainViewport?.GetCamera()?.Following, target);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Takes the mod off the controller without touching the mode, the follow or the field.
    ///
    /// <para>Forgets the up that was being supplied and the source being asked. The controller
    /// stays installed for the session — nothing but a mod puts a viewport in Fixed mode, so there
    /// is nothing to disturb — but with no up it behaves exactly as KSA's own does, rather than
    /// holding one from a chase that is over.</para>
    /// </summary>
    public static void StopDrivingMainView()
    {
        try
        {
            if (Program.MainViewport?.FixedController is LevelHorizonController level)
            {
                level.UpEcl = Vec.Zero;
                level.Pose = null;
                level.Forget();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not stop driving the main view: {e.Message}");
        }
    }

    public static bool BeginRestoreMainView(MainView saved)
    {
        if (!saved.Valid) return false;

        try
        {
            if (Program.MainViewport is not { } viewport) return false;

            StopDrivingMainView();

            // Before the mode, so a frame drawn during the handover is drawn at the player's own
            // field rather than at the borrower's.
            TrySetMainViewFov(saved.FovDeg);

            if (viewport.Mode != saved.Mode) viewport.SetCameraMode(saved.Mode);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not restore the main view: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens one of the game's spare camera windows and returns the index that names it.
    ///
    /// <para>KSA builds four at startup and leases them out; this is the same call behind its own
    /// <c>View &gt; Add Camera</c>. The lease is what stops two owners driving one window, and it
    /// is given back by KSA itself when the player closes the window — so nothing here has to
    /// track one.</para>
    ///
    /// <para>The window ImGui puts it in can be dragged out of the game onto another monitor:
    /// KSA turns on ImGui's platform viewports, and the window it opens sets no flag against
    /// it.</para>
    /// </summary>
    public static bool TryOpenCameraWindow(out int index)
    {
        index = -1;

        try
        {
            if (!ViewportRegistry.TryOpenSecondaryViewport(out IGameViewport? opened)) return false;

            opened.SetVisible(true);

            // The index is a position in the registry's span, so it is read back rather than
            // remembered -- opening one is exactly the event that can move the others.
            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                if (ReferenceEquals(viewports[i], opened))
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Warn($"camera: could not open a window ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    /// <summary>Hands a camera window back to KSA, as its own close button does.</summary>
    public static void CloseCameraWindow(int index)
    {
        try
        {
            if (TryViewport(index, out IGameViewport viewport)) ViewportRegistry.ReleaseSecondaryViewport(viewport);
        }
        catch { /* already gone */ }
    }

    // ViewportBase.OptionFlags has a protected setter. Clearing HasUi on a window the mod draws
    // itself is what stops KSA wrapping the same picture in its own window; releasing the window
    // puts the defaults back (GameViewport.ResetToDefaults), so nothing here has to restore it.
    private static readonly MethodInfo? OptionFlagsSetter =
        typeof(ViewportBase).GetProperty(nameof(ViewportBase.OptionFlags))?.GetSetMethod(nonPublic: true);

    /// <summary>
    /// Takes KSA's own window and orbit lines off a camera viewport, leaving it rendering, so the
    /// mod can show the picture in a window of its own. False when that cannot be done, and KSA's
    /// window stays.
    /// </summary>
    public static bool TryHideViewportWindow(int index)
    {
        try
        {
            if (OptionFlagsSetter is null) return false;
            if (!TryViewport(index, out IGameViewport viewport)) return false;
            if (viewport.Type != ViewportType.Secondary || !viewport.Visible) return false;

            // Orbit lines too: a view looking past the Moon otherwise shows its orbit across the picture.
            const ViewportOptionFlags taken = ViewportOptionFlags.HasUi | ViewportOptionFlags.RenderOrbitLines;

            ViewportOptionFlags flags = viewport.OptionFlags;
            if ((flags & taken) == 0) return true;

            OptionFlagsSetter.Invoke(viewport, [flags & ~taken]);
            return (viewport.OptionFlags & taken) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Puts one viewport's camera at a point in Ecl, looking along a direction.
    ///
    /// <para>Must be written every frame. Each viewport runs a controller that rewrites its
    /// camera from whatever mode it is in, so this holds only for as long as it keeps being
    /// reapplied — and only if it runs after that controller. The GUI hook does, which is why
    /// the call sits there.</para>
    ///
    /// <para>The window is one KSA built at startup and hands out on request — see
    /// <see cref="TryOpenCameraWindow"/>. A mod still cannot <em>make</em> a viewport, which is
    /// the difference between borrowing a window and stealing the main camera.</para>
    /// </summary>
    /// <param name="fovDeg">The field of view to draw with, clamped here as the main view's is.</param>
    public static bool TryLookFromViewport(int index, double3 eyeEcl, double3 forwardEcl,
                                           double3 upEcl, double fovDeg, double dt)
    {
        if (!Vec.IsFinite(eyeEcl) || !Vec.IsFinite(forwardEcl) || !Vec.IsFinite(upEcl)) return false;
        if (Vec.Len2(forwardEcl) < 1e-12) return false;

        try
        {
            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            if (index < 0 || index >= viewports.Length) return false;

            IGameViewport viewport = viewports[index];

            // A view in Map mode renders the map scene — starfield, orbits, planets as discs —
            // and GetCamera() hands back the *map* camera, so moving it puts the map somewhere
            // else rather than showing the world from here. Fixed is the mode that draws the
            // scene from wherever its camera happens to be, which is the whole point.
            //
            // Unfollow before Fixed for the same reason as the main view: FixedController divides
            // by zero on its own default CameraRotation whenever the camera it drives is following
            // something. Every frame rather than only when the mode changes, because a follow can
            // arrive at any time and this window is a likely place for one: KSA's vessel-next and
            // vessel-previous act on the *hovered* viewport, so the cursor resting over the window
            // is enough to attach one. Once attached, the controller places the camera at
            // following + CameraOffset — an offset this path never writes, because it aims with
            // LookAt — which parks the view inside whichever craft was switched to.
            try
            {
                if (viewport.GetCamera() is { Following: not null } followed)
                {
                    followed.Unfollow(changeControl: false);
                }
            }
            catch { /* a view that cannot be unfollowed is still worth trying to aim */ }

            if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);

            Camera camera = viewport.BaseCamera;
            if (camera is null) return false;

            // Position and orientation together: setting one without the other leaves a frame
            // drawn from the old place looking the new way.
            camera.LookAt(eyeEcl, eyeEcl + Vec.Unit(forwardEcl) * 1000.0, upEcl);

            // SetFieldOfView does not clamp, and the projection throws outside (0, 180).
            float wanted = (float)Math.Clamp(double.IsFinite(fovDeg) ? fovDeg : DefaultFovDeg,
                                             MinFovDeg, MaxFovDeg);
            if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) >= 1e-3)
            {
                camera.SetFieldOfView(wanted);
            }

            SetNearbyContext(camera);

            // LookAt sets the rotation alone; the view matrix the render, the culling and every
            // projection read is rebuilt in Camera.OnFrame, which ran in the viewport pass before
            // this. Without it the window draws with last frame's aim: one frame of the line of
            // view's swing, a fixed angle that judders with the frame time and grows with the zoom.
            camera.OnFrame(dt);

            // Setting the fields is not enough on its own: the sky and atmosphere are shaded from
            // data the engine uploads per viewport, which by this point in the frame already holds
            // where the camera *was*. These recompute and re-upload it for the new position.
            if (Program.Instance is { } program)
            {
                program.UpdateShaderData(dt, viewport);
                program.SetCameraUbo(viewport);
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"camera: could not take view {index} ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }

    // The engine derives this context for the main camera alone (Program.OnFrameCelestials), and
    // a window's terrain LOD, sky and grey-ball suppression all read it. Derived from the window's
    // own camera rather than copied from the main one: the two can be hundreds of kilometres
    // apart, and the copied altitude picks a planet mesh LOD for where the player is.
    private static void SetNearbyContext(Camera camera)
    {
        Celestial? body = Program.FindNearbyCelestial(camera);

        if (body is null || !body.IsBillboarded())
        {
            camera.NearbyCelestial = null;
            return;
        }

        double distanceKm = camera.DistanceTo(body.GetPositionEcl()) * 0.001;
        double surfaceKm = distanceKm - body.MeanRadius * 0.001;

        // The engine's own cut-off for a nearby body, so a window and the main view agree on it.
        camera.NearbyCelestial = surfaceKm > 80000.0 ? null : body;
        camera.DistanceToNearbyCelestialKm = distanceKm;
        camera.DistanceToNearbyCelestialSurfaceMeanKm = surfaceKm;
        camera.NearbyCelestialTerrainHeight = body.GetTerrainHeight(camera) * 0.001;
        camera.CurrentAltitudeKm = Program.GetCurrentAltitudeKm(camera);
    }

    /// <summary>A camera window's rendered picture as an ImGui texture, and the size it renders at.</summary>
    public static bool TryViewportTexture(int index, out ImTextureRef texture, out float2 size)
    {
        texture = default;
        size = default;
        try
        {
            if (!TryViewport(index, out IGameViewport viewport)) return false;
            texture = viewport.ImGuiTexture;
            size = new float2(viewport.Width, viewport.Height);
            return size.X > 0f && size.Y > 0f;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The image a camera window's finished picture is in, and a number that changes whenever the
    /// engine rebuilds it -- a resize does.
    /// </summary>
    public static bool TryViewportImage(int index, out Brutal.VulkanApi.VkImageView view, out int generation)
    {
        view = default;
        generation = -1;
        try
        {
            if (!TryViewport(index, out IGameViewport viewport) || viewport is not ViewportBase { MainTarget.ColorImage: { } image }) return false;

            view = image.ImageView;
            generation = image.Generation;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ResizeViewport(int index, int2 size)
    {
        try
        {
            if (TryViewport(index, out IGameViewport viewport) && (viewport.Width != size.X || viewport.Height != size.Y))
                viewport.RequestResize(size);
        }
        catch
        {
            // The picture stays the size the engine gave it.
        }
    }

    // One of the game's camera windows, by index. The registry's
    // order is not promised, so an index means something only for as long as the span it was read
    // from -- which is one frame. Every caller resolves afresh.
    private static bool TryViewport(int index, out IGameViewport viewport)
    {
        viewport = null!;

        ReadOnlySpan<IGameViewport> viewports = GameViewports;
        if (index < 0 || index >= viewports.Length) return false;

        viewport = viewports[index];
        return viewport is not null;
    }

}
