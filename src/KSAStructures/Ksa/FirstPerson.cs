using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using RenderCore;
using RenderCore.Animation;

namespace KSAStructures;

/// <summary>
/// The main view through the eyes of the kitten being flown on foot.
///
/// <para>The view is borrowed while the setting is on, and handed back when the setting goes off or
/// the player stops flying a kitten. <see cref="FirstPersonHook"/> keeps the kitten's own body out
/// of that view and its shadow in it, and turns the walk to follow the look.</para>
/// </summary>
internal sealed class FirstPerson(Settings config) : IViewPose
{
    private const double FovDeg = 75.0;

    // From the head bone to the eyes, in model centimetres: up, and forward along the look.
    private const double EyeUpCm = 6.0;
    private const double EyeForwardCm = 9.0;

    private readonly HeadLook _look = new();
    private KittenEva? _kitten;
    private EyesPose? _pose;
    private KsaWorld.MainView _saved;
    private bool _borrowed;

    public IMouseDrag? Orbit => _borrowed ? _look : null;

    /// <summary>The key, from the GUI pass: ImGui is what sees it.</summary>
    public void Keys()
    {
        if (ImGui.GetIO().WantTextInput) return;
        if (KsaWorld.ControlledVehicle is KittenEva && ImGui.IsKeyPressed(ImGuiKey.V, repeat: false))
            config.FirstPerson = !config.FirstPerson;
    }

    public void Drive()
    {
        try
        {
            KittenEva? flown = config.FirstPerson && KsaWorld.InFlightScene && KsaWorld.ControlledVehicle is KittenEva k
                               && KsaWorld.IsAlive(k) ? k : null;

            if (!ReferenceEquals(flown, _kitten)) Leave();
            if (flown is null) return;

            if (_pose is null)
            {
                if (KittenFrame.AvatarOf(flown) is not { } avatar) return;

                _kitten = flown;
                _pose = new EyesPose(flown, avatar);
                avatar.Core.CharacterModel.AnimProcessors.Add(_pose);
            }

            if (!_borrowed)
            {
                _saved = KsaWorld.RememberMainView();
                _borrowed = true;
                if (TryLocalFrame(flown, out _, out double3 n, out double3 e))
                {
                    double3 facing = KittenFrame.DirectionToEcl(flown, new double3(0, 0, 1));
                    _look.LookTowards(Math.Atan2(Vec.Dot(facing, e), Vec.Dot(facing, n)), 0.0);
                }
            }

            FirstPersonHook.Body = _pose;
            FirstPersonHook.Heading = Heading(flown);

            if (TryPose(KsaWorld.PositionEcl(flown), out double3 offset, out double3 forward, out double3 up, out double fov))
                KsaWorld.TryLookFromMainViewport(offset, forward, up, fov, this);
        }
        catch (Exception e)
        {
            Log.Warn($"first person: {e.Message}");
        }
    }

    public void Leave()
    {
        FirstPersonHook.Body = null;
        FirstPersonHook.Heading = null;

        try
        {
            if (_pose is not null && _kitten is not null && KittenFrame.AvatarOf(_kitten) is { } avatar)
                avatar.Core.CharacterModel.AnimProcessors.Remove(_pose);
            if (_borrowed) KsaWorld.TryHandBackMainView(_saved, _kitten, out _, out _);
        }
        catch
        {
            // The kitten is gone; there is nothing to give back to.
        }

        _pose = null;
        _kitten = null;
        _borrowed = false;
    }

    private double3? Heading(KittenEva kitten)
        => TryLocalFrame(kitten, out _, out double3 north, out double3 east)
            ? (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw))
            : null;

    public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                        out double3 upEcl, out double fovDeg)
    {
        offsetFromFollowed = forwardEcl = upEcl = default;
        fovDeg = FovDeg;
        if (_kitten is not { } kitten || _pose?.Head is not { } head
            || !TryLocalFrame(kitten, out double3 up, out double3 north, out double3 east)) return false;

        // Ahead of the head along the look rather than along the body, which lags a turn. Measured
        // against the kitten's own position, with the one the engine hands in: both this frame's.
        double3 heading = (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw));
        double3 headEcl = KittenFrame.ToEcl(kitten, head) - KsaWorld.PositionEcl(kitten) + followedEcl;
        double3 eye = headEcl + (heading * (EyeForwardCm / 100.0)) + (up * (EyeUpCm / 100.0));

        offsetFromFollowed = eye - followedEcl;
        forwardEcl = Vec.Unit((heading * Math.Cos(_look.Pitch)) + (up * Math.Sin(_look.Pitch)));
        upEcl = up;
        return Vec.IsFinite(offsetFromFollowed) && Vec.IsFinite(forwardEcl);
    }

    // Up, north and east over the kitten, in the ecliptic.
    private static bool TryLocalFrame(KittenEva kitten, out double3 up, out double3 north, out double3 east)
    {
        up = north = east = default;
        if (kitten.Parent is not Celestial body) return false;

        up = Vec.Unit(KsaWorld.PositionEcl(kitten) - body.GetPositionEcl());
        double3 axis = double3.Transform(new double3(0, 0, 1), doubleQuat.Inverse(body.GetCce2Ccf()));
        north = Vec.Unit(Vec.RejectFrom(axis, up));
        east = Vec.Cross(north, up);
        return Vec.IsFinite(north) && Vec.Len2(north) > 0.5;
    }

    // Where the head is, and the kitten folded away from its own eyes in the main view.
    private sealed class EyesPose(KittenEva kitten, CharacterAvatar avatar) : IAnimProcessor, IFirstPersonBody
    {
        private int _headBone = -1;

        public KittenEva Kitten => kitten;
        public CharacterAvatar Avatar => avatar;
        public bool ViewmodelApplied { get; private set; }
        public float4x4[]? BodyWorld { get; private set; }
        public double3? Head { get; private set; }

        public float Priority => 1f;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => false;

        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
        }

        // Inside the engine's render: an unposed kitten is better than a thrown frame.
        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt)
        {
            ViewmodelApplied = false;
            try
            {
                if (_headBone < 0) _headBone = skeleton.BoneNames?.IndexOf("Head_M") ?? -1;
                if (_headBone < 0) return;

                Span<float4x4> world = skeleton.WorldTransforms;
                float3 head = world[_headBone].Translation;
                Head = new double3(head.X, head.Y, head.Z);

                if (!FirstPersonHook.DrawingMainViewOf(kitten)) return;

                // Every bone folded to a point at the head, so nothing of the kitten is in front of
                // its eyes. The whole kitten is kept for the shadow, which the hook puts back.
                BodyWorld = BodyWorld is { } kept && kept.Length == world.Length ? kept : new float4x4[world.Length];
                world.CopyTo(BodyWorld);

                float4x4 gone = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, head.X, head.Y, head.Z, 1f);
                for (int i = 0; i < world.Length; i++) world[i] = gone;
                ViewmodelApplied = true;
            }
            catch
            {
            }
        }
    }
}
