using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Mutant's procedural "wrongness", layered over the animator pose in LateUpdate on EVERY peer:
/// stretching arms (Long Reach, Scythe Sweep), the spin that carries the sweep, the jerky owl neck, the
/// constant twitching, the bow's deepened curl + head snap, and the shriek's neck stretch / head swell.
///
/// Nothing here crosses the network as bone data. The arm timelines are started by the AI (server) and by
/// <see cref="NetworkMutantAvatar"/>'s RPCs (clients) with the same parameters, so every peer plays the same
/// motion; the bow and the shriek key off the replicated animator state; the owl neck follows a replicated
/// look target. Twitches are deliberately unsynchronised — random jerks don't need to match.
///
/// Stretch technique: the upper-arm bone is scaled along its own length axis (Mixamo bones point down +Y),
/// which lengthens the upper arm AND the forearm (inherited) uniformly from the shoulder pivot, and the hand
/// is counter-scaled back to normal size. Both joints are straightened along the aim first, so the inherited
/// non-uniform scale never shears: a twist about a bone's own Y is symmetric under (t, s, t).
/// </summary>
[DisallowMultipleComponent]
public class MutantLimbRig : MonoBehaviour
{
    public enum ArmSide { Left = 0, Right = 1 }

    /// <summary>Sound cues raised on every peer; <see cref="MutantAI"/> plays them locally.</summary>
    public enum Cue { ArmWindup, ArmFire, ArmSnapBack, ArmReel, NeckCrack, SweepWindup, SweepSpin }

    public event System.Action<Cue, Vector3> CueRaised;

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] SkinnedMeshRenderer bodyRenderer;

    [Header("Stretch")]
    [Tooltip("Cross-section scale of the sleeve at extreme stretch (reached at 8x length). 1 = no thinning; " +
             "lower reads more like taffy than a pole.")]
    [SerializeField, Range(0.3f, 1f)] float stretchedThickness = 0.72f;
    [Tooltip("Wrist-to-palm distance: a reach aims the palm, not the wrist, at its target.")]
    [SerializeField, Min(0f)] float palmOffset = 0.12f;
    [SerializeField, Min(0.01f)] float aimBlendInSeconds = 0.06f;
    [SerializeField, Min(0.01f)] float aimBlendOutSeconds = 0.12f;
    [Tooltip("Tremble on the cocked arm while a reach winds up — the telegraph (degrees).")]
    [SerializeField, Min(0f)] float windupTrembleDegrees = 5f;
    [Tooltip("Sweep arms aim this far forward of straight sideways (degrees), so they lead the spin.")]
    [SerializeField, Range(0f, 45f)] float sweepArmForwardBias = 12f;
    [Tooltip("Height of the sweeping arms above the Mutant's feet (metres). A crouching player must be under it.")]
    [SerializeField] float sweepArmHeight = 1.55f;

    [Header("Owl neck")]
    [SerializeField] bool owlNeck = true;
    [SerializeField, Range(0f, 180f)] float maxNeckYaw = 165f;
    [Tooltip("Share of the turn taken by the neck bone; the head takes the rest.")]
    [SerializeField, Range(0f, 1f)] float neckShare = 0.35f;
    [Tooltip("The head doesn't track smoothly — it jumps toward the target in steps at these intervals.")]
    [SerializeField] Vector2 neckStepInterval = new Vector2(0.05f, 0.17f);
    [SerializeField] Vector2 neckStepFraction = new Vector2(0.45f, 0.9f);
    [SerializeField, Min(0.01f)] float neckStepSeconds = 0.045f;
    [Tooltip("Slow curious tilt (roll) layered on while watching someone.")]
    [SerializeField, Min(0f)] float headTiltDegrees = 16f;
    [Tooltip("Crossing this much head yaw plays the neck crack.")]
    [SerializeField, Min(0f)] float neckCrackYaw = 100f;
    [SerializeField, Range(0f, 180f)] float crawlNeckYawLimit = 50f;

    [Header("Twitch")]
    [SerializeField, Range(0f, 3f)] float twitchIntensity = 1f;
    [SerializeField] Vector2 twitchInterval = new Vector2(0.35f, 1.6f);
    [SerializeField] Vector2 twitchAngle = new Vector2(6f, 20f);
    [SerializeField, Min(0.02f)] float twitchDuration = 0.2f;

    [Header("Bow (keyed off the replicated animator state)")]
    [SerializeField] string bowStateName = "Bow";
    [Tooltip("Extra forward curl on Spine / Spine1 / Spine2 at the bottom of the bow — the clip itself is only " +
             "an 8 cm nod on this rig.")]
    [SerializeField] Vector3 bowSpineCurlDegrees = new Vector3(14f, 18f, 20f);
    [SerializeField] Vector2 bowCurlWindow = new Vector2(0.05f, 0.5f);
    [Tooltip("Normalized bow time where the head snaps back up (with the neck crack).")]
    [SerializeField, Range(0f, 1f)] float bowSnapNormalizedTime = 0.5f;
    [SerializeField] float bowSnapDegrees = 32f;

    [Header("Shriek (keyed off the replicated animator state)")]
    [SerializeField] string screamStateName = "Scream";
    [Tooltip("Normalized Scream-state window of the swell. The clip's mouth-open crouch is 1.0–1.8 s of 2.8 s.")]
    [SerializeField] Vector2 shriekWindow = new Vector2(0.34f, 0.66f);
    [SerializeField, Min(0f)] float shriekHeadSwell = 0.38f;
    [Tooltip("Extra neck length at the peak of the shriek, in the neck bone's local units.")]
    [SerializeField, Min(0f)] float shriekNeckStretch = 0.16f;

    [Header("States")]
    [SerializeField] string deathStateName = "Death";
    [SerializeField] string crawlStateName = "Crawl";
    [Tooltip("States during which the owl neck lets go of the head (the clip owns it).")]
    [SerializeField] string[] neckFreeStates = { "Bow", "Reach", "Slam", "Claw", "SweepHold", "Scream", "Stagger", "Flinch", "Death" };
    [Tooltip("After death, twitching keeps going and fades out over this many seconds.")]
    [SerializeField, Min(0f)] float deathTwitchSeconds = 5f;

    enum ArmMode { Idle, Windup, Reach, Sweep }

    sealed class Arm
    {
        public Transform upper;
        public Transform fore;
        public Transform hand;
        public float naturalLocalLength; // upper→fore + fore→hand, unscaled local units
        public ArmMode mode;
        public float windupEnd;
        public float fireTime;
        public float extendSeconds;
        public Vector3 aimPoint;
        public float maxLength;
        public float retractStart = -1f;
        public float retractSeconds;
        public float retractFromLength;
        public bool reeling; // a caught player is being hauled in: smooth pull, not the snap-back
        public float weight;
        public float length;
        public float side; // +1 right, -1 left
    }

    struct Twitch
    {
        public Transform bone;
        public Vector3 axis;
        public float degrees;
        public float start;
        public float duration;
    }

    readonly Arm[] _arms = { new Arm { side = -1f }, new Arm { side = 1f } };
    readonly List<Twitch> _twitches = new();
    readonly List<(Transform bone, float weight)> _twitchBones = new();
    readonly HashSet<int> _neckFreeHashes = new();

    Transform _hips, _spine, _spine1, _spine2, _neck, _head;
    Vector3 _headBaseLocalPos;
    bool _bound;

    // Sweep timeline (shared by both arms + the hips spin).
    bool _sweepActive;
    float _sweepStart;
    float _sweepWindup;
    float _sweepSpinSeconds;
    float _sweepSpinDegrees;
    float _sweepHoldSeconds;
    float _sweepRetractSeconds;
    float _sweepArmLength;
    float _sweepBaseYaw;

    // Owl neck.
    Transform _lookTarget;
    float _neckYaw;
    float _neckStepFrom;
    float _neckStepTo;
    float _neckStepStart = -1f;
    float _nextNeckStep;
    bool _neckCracked;

    // Bow snap.
    bool _bowSnapPlayed;
    float _bowSnapStart = -1f;

    float _nextTwitchTime;
    float _deathTime = -1f;

    public float TwitchIntensity
    {
        get => twitchIntensity;
        set => twitchIntensity = Mathf.Max(0f, value);
    }

    public float SweepArmHeight => sweepArmHeight;

    void Reset()
    {
        animator = GetComponent<Animator>();
        bodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
    }

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (bodyRenderer == null)
            bodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>(true);

        Bind();

        for (int i = 0; i < neckFreeStates.Length; i++)
            _neckFreeHashes.Add(Animator.StringToHash(neckFreeStates[i]));
    }

    void Bind()
    {
        Transform Find(string bone) => FindDeep(transform, "mixamorig:" + bone);

        _hips = Find("Hips");
        _spine = Find("Spine");
        _spine1 = Find("Spine1");
        _spine2 = Find("Spine2");
        _neck = Find("Neck");
        _head = Find("Head");

        BindArm(_arms[(int)ArmSide.Left], Find("LeftArm"), Find("LeftForeArm"), Find("LeftHand"));
        BindArm(_arms[(int)ArmSide.Right], Find("RightArm"), Find("RightForeArm"), Find("RightHand"));

        if (_head != null)
            _headBaseLocalPos = _head.localPosition;

        _twitchBones.Clear();
        AddTwitchBone(_neck, 1f);
        AddTwitchBone(_head, 1.2f);
        AddTwitchBone(_spine2, 0.5f);
        AddTwitchBone(Find("LeftShoulder"), 0.8f);
        AddTwitchBone(Find("RightShoulder"), 0.8f);
        AddTwitchBone(Find("LeftForeArm"), 0.7f);
        AddTwitchBone(Find("RightForeArm"), 0.7f);
        AddTwitchBone(Find("LeftHand"), 1f);
        AddTwitchBone(Find("RightHand"), 1f);
        foreach (string finger in new[] { "Index", "Middle", "Ring", "Pinky" })
        {
            AddTwitchBone(Find("LeftHand" + finger + "1"), 1.6f);
            AddTwitchBone(Find("RightHand" + finger + "1"), 1.6f);
        }

        _bound = _hips != null && _head != null && _neck != null
            && _arms[0].upper != null && _arms[1].upper != null;
        if (!_bound)
            Debug.LogWarning($"[MutantLimbRig] {name}: could not find the Mixamo bones — procedural effects are off.", this);
    }

    static void BindArm(Arm arm, Transform upper, Transform fore, Transform hand)
    {
        arm.upper = upper;
        arm.fore = fore;
        arm.hand = hand;
        if (upper != null && fore != null && hand != null)
            arm.naturalLocalLength = fore.localPosition.magnitude + hand.localPosition.magnitude;
    }

    void AddTwitchBone(Transform bone, float weight)
    {
        if (bone != null)
            _twitchBones.Add((bone, weight));
    }

    // ------------------------------------------------------------------
    // Public API — called identically on the server and (via RPC) on clients
    // ------------------------------------------------------------------

    public void SetLookTarget(Transform target) => _lookTarget = target;

    /// <summary>The arm cocks and trembles for <paramref name="seconds"/> (the telegraph) — no stretch yet.</summary>
    public void BeginReachWindup(ArmSide side, float seconds)
    {
        Arm arm = _arms[(int)side];
        arm.mode = ArmMode.Windup;
        arm.windupEnd = Time.time + Mathf.Max(0f, seconds);
        arm.retractStart = -1f;
        arm.reeling = false;
        Raise(Cue.ArmWindup, arm.upper);
    }

    /// <summary>
    /// The arm fires at <paramref name="aimPoint"/> (palm target), out to at most <paramref name="maxLength"/>
    /// (the server already cut that short at the first wall). Length over time follows
    /// <see cref="ReachLengthAt"/>, which the server's catch test uses too.
    /// </summary>
    public void FireReach(ArmSide side, Vector3 aimPoint, float extendSeconds, float maxLength)
    {
        Arm arm = _arms[(int)side];
        arm.mode = ArmMode.Reach;
        arm.fireTime = Time.time;
        arm.extendSeconds = Mathf.Max(0.01f, extendSeconds);
        arm.aimPoint = aimPoint;
        arm.maxLength = Mathf.Max(0.1f, maxLength);
        arm.retractStart = -1f;
        arm.reeling = false;
        // The arm whips straight the instant it fires — no aim blend. Stretching waits for a fully straight arm
        // (see ApplyArm), so any blend here would hold the visible limb short while the server's catch test
        // (ReachLengthAt from this same instant) is already out at the target: the victim got pinned to an
        // unstretched hand beside him.
        arm.weight = 1f;
        if (arm.length <= 0f)
            arm.length = NaturalWorldLength(arm);
        Raise(Cue.ArmFire, arm.upper);
    }

    /// <summary>
    /// Something was caught: the arm stops where it is and hauls back in over <paramref name="seconds"/> with a
    /// steady pull — whoever is pinned to the hand (<see cref="NetworkPlayerRagdoll.BeginHeldByEnemyFromServer"/>)
    /// comes with it, on every peer, with no extra traffic.
    /// </summary>
    public void ReelArm(ArmSide side, float seconds)
    {
        Arm arm = _arms[(int)side];
        if (arm.mode != ArmMode.Reach || arm.retractStart >= 0f)
            return;

        arm.retractStart = Time.time;
        arm.retractSeconds = Mathf.Max(0.05f, seconds);
        arm.retractFromLength = Mathf.Max(arm.length, NaturalWorldLength(arm));
        arm.reeling = true;
        Raise(Cue.ArmReel, arm.hand);
    }

    public void RetractArm(ArmSide side, float seconds)
    {
        Arm arm = _arms[(int)side];
        if (arm.mode == ArmMode.Idle || arm.retractStart >= 0f)
            return;

        if (arm.mode == ArmMode.Windup)
        {
            arm.mode = ArmMode.Idle;
            return;
        }

        arm.retractStart = Time.time;
        arm.retractSeconds = Mathf.Max(0.01f, seconds);
        arm.retractFromLength = Mathf.Max(arm.length, NaturalWorldLength(arm));
        arm.reeling = false;
        Raise(Cue.ArmSnapBack, arm.hand);
    }

    /// <summary>
    /// Both arms out sideways at chest height, stretched to <paramref name="armLength"/>, while the body spins
    /// <paramref name="spinDegrees"/> about <paramref name="baseYaw"/> (the server's facing, passed explicitly so
    /// an observer's interpolated transform can't skew it) — see <see cref="SweepSpinDegreesAt"/> for the shared
    /// timing the server's hit test also uses.
    /// </summary>
    public void BeginSweep(float windupSeconds, float spinSeconds, float spinDegrees, float holdSeconds,
        float retractSeconds, float armLength, float baseYaw)
    {
        _sweepActive = true;
        _sweepStart = Time.time;
        _sweepWindup = Mathf.Max(0f, windupSeconds);
        _sweepSpinSeconds = Mathf.Max(0.05f, spinSeconds);
        _sweepSpinDegrees = spinDegrees;
        _sweepHoldSeconds = Mathf.Max(0f, holdSeconds);
        _sweepRetractSeconds = Mathf.Max(0.05f, retractSeconds);
        _sweepArmLength = Mathf.Max(0.5f, armLength);
        _sweepBaseYaw = baseYaw;

        for (int i = 0; i < _arms.Length; i++)
        {
            Arm arm = _arms[i];
            arm.mode = ArmMode.Sweep;
            arm.retractStart = -1f;
            arm.reeling = false;
            if (arm.length <= 0f)
                arm.length = NaturalWorldLength(arm);
        }

        Raise(Cue.SweepWindup, _hips);
    }

    /// <summary>Wrist distance from the shoulder <paramref name="elapsed"/> seconds after a reach fires (ease-out).</summary>
    public static float ReachLengthAt(float elapsed, float extendSeconds, float natural, float target)
    {
        float p = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, extendSeconds));
        float eased = 1f - (1f - p) * (1f - p) * (1f - p);
        return Mathf.Lerp(natural, target, eased);
    }

    /// <summary>Longest a straight limb can reach along <paramref name="dir"/> from <paramref name="origin"/> before a wall.</summary>
    public float ClearReach(Vector3 origin, Vector3 dir, float maxLength)
    {
        int mask = EnvironmentMask();
        if (Physics.SphereCast(origin, 0.12f, dir, out RaycastHit hit, maxLength + palmOffset, mask, QueryTriggerInteraction.Ignore))
            return Mathf.Max(0.2f, hit.distance - palmOffset - 0.05f);
        return maxLength;
    }

    public float PalmOffset => palmOffset;

    /// <summary>Stagger / death / flashbang: every stretched limb snaps back now.</summary>
    public void CancelAll(float retractSeconds = 0.15f)
    {
        if (_sweepActive)
        {
            _sweepActive = false;
            for (int i = 0; i < _arms.Length; i++)
                if (_arms[i].mode == ArmMode.Sweep)
                    _arms[i].mode = ArmMode.Reach; // hand the arm to the generic retract path
        }

        RetractArm(ArmSide.Left, retractSeconds);
        RetractArm(ArmSide.Right, retractSeconds);
    }

    public bool IsArmBusy(ArmSide side) => _arms[(int)side].mode != ArmMode.Idle;

    public bool IsSweeping => _sweepActive;

    public Vector3 PalmWorldPosition(ArmSide side)
    {
        Arm arm = _arms[(int)side];
        return arm.hand != null ? arm.hand.position + arm.hand.up * palmOffset : transform.position;
    }

    public Vector3 ShoulderWorldPosition(ArmSide side)
    {
        Arm arm = _arms[(int)side];
        return arm.upper != null ? arm.upper.position : transform.position + Vector3.up * 1.7f;
    }

    /// <summary>Straight-arm reach at natural length, shoulder to palm, in world metres (includes the rig's scale).</summary>
    public float NaturalReach(ArmSide side) => NaturalWorldLength(_arms[(int)side]) + palmOffset;

    /// <summary>Shoulder-to-wrist length of the unstretched arm in world metres — the "length" a reach starts from.</summary>
    public float NaturalArmLength(ArmSide side) => NaturalWorldLength(_arms[(int)side]);

    /// <summary>
    /// Spin angle of the sweep at <paramref name="elapsed"/> seconds after it began: 0 through the wind-up,
    /// eased across the spin, then held. Shared with the server's hit test so the damage lands exactly where
    /// every peer sees the arms.
    /// </summary>
    public static float SweepSpinDegreesAt(float elapsed, float windupSeconds, float spinSeconds, float spinDegrees)
    {
        if (elapsed <= windupSeconds)
            return 0f;

        float p = Mathf.Clamp01((elapsed - windupSeconds) / Mathf.Max(0.05f, spinSeconds));
        // Ease-in-out so the whip accelerates through the middle of the turn.
        float eased = p * p * (3f - 2f * p);
        return eased * spinDegrees;
    }

    /// <summary>World yaw (degrees) an arm points along at a given spin angle — see <see cref="SweepSpinDegreesAt"/>.</summary>
    public float SweepArmYaw(ArmSide side, float baseYaw, float spinDegrees)
    {
        float sideSign = side == ArmSide.Right ? 1f : -1f;
        // The arms lead the direction of spin slightly.
        float lead = Mathf.Sign(spinDegrees == 0f ? 1f : spinDegrees) * sideSign * sweepArmForwardBias;
        return baseYaw + spinDegrees + sideSign * 90f - lead;
    }

    // ------------------------------------------------------------------
    // Per-frame
    // ------------------------------------------------------------------

    void LateUpdate()
    {
        if (!_bound)
            return;

        AnimatorStateInfo state = animator != null ? animator.GetCurrentAnimatorStateInfo(0) : default;
        bool dead = animator != null && IsInState(deathStateName);
        if (dead && _deathTime < 0f)
        {
            _deathTime = Time.time;
            CancelAll(0.2f);
        }

        ResetScales();

        UpdateSweepSpin();
        if (!dead)
        {
            UpdateBow(state);
            UpdateOwlNeck(state);
            UpdateShriek(state);
        }

        UpdateArms();
        UpdateTwitches(dead);
        UpdateRendererBounds();
    }

    void ResetScales()
    {
        for (int i = 0; i < _arms.Length; i++)
        {
            Arm arm = _arms[i];
            if (arm.upper == null)
                continue;
            arm.upper.localScale = Vector3.one;
            arm.fore.localScale = Vector3.one;
            arm.hand.localScale = Vector3.one;
        }

        _head.localScale = Vector3.one;
        _head.localPosition = _headBaseLocalPos;
    }

    float UpdateSweepSpin()
    {
        if (!_sweepActive)
            return 0f;

        float elapsed = Time.time - _sweepStart;
        float total = _sweepWindup + _sweepSpinSeconds + _sweepHoldSeconds + _sweepRetractSeconds;
        if (elapsed >= total)
        {
            _sweepActive = false;
            for (int i = 0; i < _arms.Length; i++)
            {
                _arms[i].mode = ArmMode.Idle;
                _arms[i].weight = 0f;
                _arms[i].length = NaturalWorldLength(_arms[i]);
            }
            return 0f;
        }

        float spin = SweepSpinDegreesAt(elapsed, _sweepWindup, _sweepSpinSeconds, _sweepSpinDegrees);

        // The whole body turns about the vertical axis through his feet. Purely visual: the capsule and the
        // replicated transform stay put, so the spin is identical on every peer with no rotation traffic.
        // (Relative to the yaw he had when the sweep began, in case the AI turns him meanwhile.)
        float relative = spin + Mathf.DeltaAngle(transform.eulerAngles.y, _sweepBaseYaw);
        if (Mathf.Abs(relative) > 0.01f)
        {
            Quaternion turn = Quaternion.AngleAxis(relative, Vector3.up);
            Vector3 pivot = transform.position;
            _hips.position = pivot + turn * (_hips.position - pivot);
            _hips.rotation = turn * _hips.rotation;
        }

        return spin;
    }

    void UpdateArms()
    {
        for (int i = 0; i < _arms.Length; i++)
        {
            Arm arm = _arms[i];
            if (arm.upper == null)
                continue;

            switch (arm.mode)
            {
                case ArmMode.Windup:
                    if (Time.time >= arm.windupEnd)
                    {
                        arm.mode = ArmMode.Idle;
                        break;
                    }
                    // The cocked arm shakes against its own restraint.
                    float t = windupTrembleDegrees;
                    arm.upper.rotation = Quaternion.Euler(
                        (Mathf.PerlinNoise(Time.time * 31f, i) - 0.5f) * 2f * t,
                        (Mathf.PerlinNoise(i, Time.time * 27f) - 0.5f) * 2f * t,
                        0f) * arm.upper.rotation;
                    break;

                case ArmMode.Reach:
                    UpdateReachArm(arm);
                    break;

                case ArmMode.Sweep:
                    UpdateSweepArm(arm, (ArmSide)i);
                    break;
            }
        }
    }

    void UpdateReachArm(Arm arm)
    {
        Vector3 shoulder = arm.upper.position;
        Vector3 toGoal = arm.aimPoint - shoulder;
        float goalDistance = toGoal.magnitude;
        Vector3 dir = goalDistance > 0.001f ? toGoal / goalDistance : transform.forward;
        float natural = NaturalWorldLength(arm);

        // Wrist distance that puts the palm on the goal, never past the server's wall-limited maximum.
        float target = Mathf.Clamp(goalDistance - palmOffset, natural, arm.maxLength);

        float length;
        if (arm.retractStart >= 0f)
        {
            float r = Mathf.Clamp01((Time.time - arm.retractStart) / arm.retractSeconds);
            // A miss snaps back hard (slow start, violent finish); a catch is hauled in with a steady pull.
            float shaped = arm.reeling ? r * r * (3f - 2f * r) : r * r * r;
            length = Mathf.Lerp(arm.retractFromLength, natural, shaped);
            // Stay fully aimed until the limb is home — the stretch only applies to a straight arm.
            if (r < 1f)
                arm.weight = 1f;
            if (r >= 1f)
            {
                arm.weight = Mathf.MoveTowards(arm.weight, 0f, Time.deltaTime / aimBlendOutSeconds);
                if (arm.weight <= 0f)
                {
                    arm.mode = ArmMode.Idle;
                    arm.length = natural;
                    arm.reeling = false;
                    return;
                }
            }
        }
        else
        {
            arm.weight = Mathf.MoveTowards(arm.weight, 1f, Time.deltaTime / aimBlendInSeconds);
            length = ReachLengthAt(Time.time - arm.fireTime, arm.extendSeconds, natural, target);
        }

        arm.length = length;
        ApplyArm(arm, dir, length, natural);
    }

    void UpdateSweepArm(Arm arm, ArmSide side)
    {
        float elapsed = Time.time - _sweepStart;
        float natural = NaturalWorldLength(arm);
        float retractStart = _sweepWindup + _sweepSpinSeconds + _sweepHoldSeconds;

        float length;
        if (elapsed < _sweepWindup)
        {
            // Arms rise and start to creak outward during the wind-up — the tell to crouch.
            float w = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, _sweepWindup));
            length = Mathf.Lerp(natural, Mathf.Lerp(natural, _sweepArmLength, 0.3f), w * w);
        }
        else if (elapsed < retractStart)
        {
            float p = Mathf.Clamp01((elapsed - _sweepWindup) / 0.12f);
            length = Mathf.Lerp(Mathf.Lerp(natural, _sweepArmLength, 0.3f), _sweepArmLength, p);
        }
        else
        {
            float r = Mathf.Clamp01((elapsed - retractStart) / _sweepRetractSeconds);
            length = Mathf.Lerp(_sweepArmLength, natural, r * r);
        }

        arm.weight = Mathf.MoveTowards(arm.weight, elapsed < retractStart + _sweepRetractSeconds * 0.8f ? 1f : 0f,
            Time.deltaTime / aimBlendInSeconds);
        arm.length = length;

        float spin = SweepSpinDegreesAt(elapsed, _sweepWindup, _sweepSpinSeconds, _sweepSpinDegrees);
        float yaw = SweepArmYaw(side, _sweepBaseYaw, spin);
        Vector3 flat = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

        // Hold the arms level at the sweep height regardless of the crouched body pose under them.
        Vector3 shoulder = arm.upper.position;
        float drop = (transform.position.y + sweepArmHeight) - shoulder.y;
        float horizontal = Mathf.Max(0.5f, length);
        Vector3 dir = (flat * horizontal + Vector3.up * Mathf.Clamp(drop, -0.6f, 0.6f)).normalized;

        // A wall stops the limb rather than letting it sweep through the porcelain: in a 4 m corridor the
        // arms press flat against both walls and only run full length down the corridor's axis. The server's
        // hit test uses the same wall check (line of sight at arm height), so what reaches you is what you see.
        if (length > natural + 0.05f)
            length = Mathf.Max(natural, Mathf.Min(length, ClearReach(shoulder, dir, length)));

        ApplyArm(arm, dir, length, natural);
    }

    int _environmentMask = -1;

    /// <summary>Walls and props only — never actors (players, enemies, the Mutant's own capsule).</summary>
    int EnvironmentMask()
    {
        if (_environmentMask != -1)
            return _environmentMask;

        int mask = Physics.DefaultRaycastLayers;
        foreach (string layer in new[] { "Player", "Enemy", "Clown", "Jailor", "Ignore Raycast" })
        {
            int index = LayerMask.NameToLayer(layer);
            if (index >= 0)
                mask &= ~(1 << index);
        }
        _environmentMask = mask;
        return mask;
    }

    void ApplyArm(Arm arm, Vector3 dir, float length, float natural)
    {
        float w = Mathf.Clamp01(arm.weight);
        if (w <= 0f)
            return;

        Quaternion upperAnim = arm.upper.rotation;
        Quaternion upperAimed = Quaternion.FromToRotation(arm.upper.up, dir) * upperAnim;
        arm.upper.rotation = Quaternion.Slerp(upperAnim, upperAimed, w);

        // Straighten the elbow and wrist along the same line so the stretch below cannot shear.
        Quaternion foreAimed = Quaternion.FromToRotation(arm.fore.up, dir) * arm.fore.rotation;
        arm.fore.rotation = Quaternion.Slerp(arm.fore.rotation, foreAimed, w);
        Quaternion handAimed = Quaternion.FromToRotation(arm.hand.up, dir) * arm.hand.rotation;
        arm.hand.rotation = Quaternion.Slerp(arm.hand.rotation, handAimed, w);

        // Only stretch once the arm is fully straight (w == 1): scaling a bent arm is what shears.
        if (w < 0.999f || natural <= 0.001f)
            return;

        float s = Mathf.Max(1f, length / natural);
        if (s <= 1.0001f)
            return;

        float thin = Mathf.Lerp(1f, stretchedThickness, Mathf.InverseLerp(1f, 8f, s));
        arm.upper.localScale = new Vector3(thin, s, thin);
        arm.fore.localScale = Vector3.one;
        arm.hand.localScale = new Vector3(1f / thin, 1f / s, 1f / thin);
    }

    float NaturalWorldLength(Arm arm)
    {
        if (arm.upper == null)
            return 0.7f;
        float scale = arm.upper.parent != null ? arm.upper.parent.lossyScale.y : 1f;
        return arm.naturalLocalLength * scale;
    }

    void UpdateOwlNeck(AnimatorStateInfo state)
    {
        float desired = 0f;
        bool free = _neckFreeHashes.Contains(state.shortNameHash) || _sweepActive;
        if (owlNeck && !free && _lookTarget != null)
        {
            Vector3 toTarget = _lookTarget.position - transform.position;
            toTarget.y = 0f;
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (toTarget.sqrMagnitude > 0.01f && fwd.sqrMagnitude > 1e-4f)
            {
                float limit = state.shortNameHash == Animator.StringToHash(crawlStateName) ? crawlNeckYawLimit : maxNeckYaw;
                desired = Mathf.Clamp(Vector3.SignedAngle(fwd, toTarget, Vector3.up), -limit, limit);
            }
        }

        // Jerky tracking: the head jumps toward where it wants to be in discrete steps, never a smooth pan.
        if (Time.time >= _nextNeckStep)
        {
            _nextNeckStep = Time.time + Random.Range(neckStepInterval.x, neckStepInterval.y);
            _neckStepFrom = _neckYaw;
            float remaining = desired - _neckYaw;
            _neckStepTo = Mathf.Abs(remaining) < 3f
                ? desired
                : _neckYaw + remaining * Random.Range(neckStepFraction.x, neckStepFraction.y);
            _neckStepStart = Time.time;
        }

        if (_neckStepStart >= 0f)
        {
            float p = Mathf.Clamp01((Time.time - _neckStepStart) / neckStepSeconds);
            _neckYaw = Mathf.Lerp(_neckStepFrom, _neckStepTo, p);
        }

        float absYaw = Mathf.Abs(_neckYaw);
        if (!_neckCracked && absYaw >= neckCrackYaw)
        {
            _neckCracked = true;
            Raise(Cue.NeckCrack, _neck);
        }
        else if (_neckCracked && absYaw < neckCrackYaw * 0.6f)
            _neckCracked = false;

        if (Mathf.Abs(_neckYaw) < 0.05f && _lookTarget == null)
            return;

        // Watching someone: a slow, curious roll on top of the yaw.
        float tilt = _lookTarget != null && !free
            ? Mathf.Sin(Time.time * 0.9f) * headTiltDegrees * Mathf.Clamp01(absYaw / 45f + 0.35f)
            : 0f;

        _neck.rotation = Quaternion.AngleAxis(_neckYaw * neckShare, Vector3.up) * _neck.rotation;
        _head.rotation = Quaternion.AngleAxis(_neckYaw * (1f - neckShare), Vector3.up) * _head.rotation;
        if (Mathf.Abs(tilt) > 0.01f)
            _head.rotation = Quaternion.AngleAxis(tilt, _head.up) * _head.rotation;
    }

    void UpdateBow(AnimatorStateInfo state)
    {
        if (state.shortNameHash != Animator.StringToHash(bowStateName))
        {
            _bowSnapPlayed = false;
            _bowSnapStart = -1f;
            return;
        }

        float n = state.normalizedTime;
        if (n > bowCurlWindow.x && n < bowCurlWindow.y)
        {
            float p = Mathf.InverseLerp(bowCurlWindow.x, bowCurlWindow.y, n);
            float curl = Mathf.Sin(p * Mathf.PI);
            Vector3 right = transform.right;
            if (_spine != null) _spine.rotation = Quaternion.AngleAxis(bowSpineCurlDegrees.x * curl, right) * _spine.rotation;
            if (_spine1 != null) _spine1.rotation = Quaternion.AngleAxis(bowSpineCurlDegrees.y * curl, right) * _spine1.rotation;
            if (_spine2 != null) _spine2.rotation = Quaternion.AngleAxis(bowSpineCurlDegrees.z * curl, right) * _spine2.rotation;
        }

        if (!_bowSnapPlayed && n >= bowSnapNormalizedTime)
        {
            _bowSnapPlayed = true;
            _bowSnapStart = Time.time;
            Raise(Cue.NeckCrack, _neck);
        }

        if (_bowSnapStart >= 0f)
        {
            // The head whips up and back past level, then settles — like something pulled it on a string.
            float e = Time.time - _bowSnapStart;
            float snap = e < 0.05f ? e / 0.05f : Mathf.Exp(-(e - 0.05f) * 7f);
            _neck.rotation = Quaternion.AngleAxis(-bowSnapDegrees * snap, transform.right) * _neck.rotation;
        }
    }

    void UpdateShriek(AnimatorStateInfo state)
    {
        if (state.shortNameHash != Animator.StringToHash(screamStateName))
            return;

        float n = state.normalizedTime;
        if (n <= shriekWindow.x || n >= shriekWindow.y)
            return;

        float p = Mathf.InverseLerp(shriekWindow.x, shriekWindow.y, n);
        // Fast swell, hold, slower collapse.
        float e = p < 0.2f ? p / 0.2f : p > 0.7f ? 1f - (p - 0.7f) / 0.3f : 1f;
        e = Mathf.Clamp01(e);

        _head.localPosition = _headBaseLocalPos + Vector3.up * (shriekNeckStretch * e);
        _head.localScale = Vector3.one * (1f + shriekHeadSwell * e);
    }

    void UpdateTwitches(bool dead)
    {
        float intensity = twitchIntensity;
        if (dead)
        {
            float since = Time.time - _deathTime;
            intensity = since < deathTwitchSeconds ? 1.6f * (1f - since / deathTwitchSeconds) : 0f;
        }

        if (intensity > 0.01f && Time.time >= _nextTwitchTime && _twitchBones.Count > 0)
        {
            _nextTwitchTime = Time.time + Random.Range(twitchInterval.x, twitchInterval.y) / Mathf.Max(0.2f, intensity);
            (Transform bone, float weight) pick = _twitchBones[Random.Range(0, _twitchBones.Count)];
            _twitches.Add(new Twitch
            {
                bone = pick.bone,
                axis = Random.onUnitSphere,
                degrees = Random.Range(twitchAngle.x, twitchAngle.y) * pick.weight * Mathf.Min(1.6f, intensity),
                start = Time.time,
                duration = twitchDuration * Random.Range(0.7f, 1.4f)
            });
        }

        for (int i = _twitches.Count - 1; i >= 0; i--)
        {
            Twitch tw = _twitches[i];
            float p = (Time.time - tw.start) / tw.duration;
            if (p >= 1f || tw.bone == null)
            {
                _twitches.RemoveAt(i);
                continue;
            }

            if (IsStretchedArmBone(tw.bone))
                continue;

            // Near-instant jerk, then a slower settle.
            float e = p < 0.15f ? p / 0.15f : 1f - (p - 0.15f) / 0.85f;
            tw.bone.localRotation = tw.bone.localRotation * Quaternion.AngleAxis(tw.degrees * e, tw.axis);
        }
    }

    bool IsStretchedArmBone(Transform bone)
    {
        for (int i = 0; i < _arms.Length; i++)
        {
            Arm arm = _arms[i];
            if (arm.mode == ArmMode.Idle || arm.mode == ArmMode.Windup)
                continue;
            if (bone == arm.upper || bone == arm.fore || bone == arm.hand)
                return true;
        }
        return false;
    }

    void UpdateRendererBounds()
    {
        if (bodyRenderer == null)
            return;

        // A 9 m arm leaves the renderer's authored bounds; only pay for per-frame bounds while it's out.
        bool stretched = _sweepActive || _arms[0].mode == ArmMode.Reach || _arms[1].mode == ArmMode.Reach;
        if (bodyRenderer.updateWhenOffscreen != stretched)
            bodyRenderer.updateWhenOffscreen = stretched;
    }

    bool IsInState(string stateName)
    {
        if (animator == null)
            return false;
        int hash = Animator.StringToHash(stateName);
        if (animator.GetCurrentAnimatorStateInfo(0).shortNameHash == hash)
            return true;
        return animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).shortNameHash == hash;
    }

    void Raise(Cue cue, Transform at)
    {
        CueRaised?.Invoke(cue, at != null ? at.position : transform.position);
    }

    static Transform FindDeep(Transform root, string boneName)
    {
        if (root.name == boneName)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), boneName);
            if (found != null)
                return found;
        }
        return null;
    }
}
