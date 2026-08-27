using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// One or more axe blades that hang from hinges and continuously swing back and forth through a full arc
/// (default 180°) while a living player is inside a tripwire zone. Once started, the swing keeps looping
/// until no qualifying player remains in the zone, then eases back to the rest pose.
///
/// On contact the blade deals flat damage and shoves the player along the swing direction WITHOUT
/// ragdolling them — the push briefly overrides the player's own movement so the axe "wins" (it pushes the
/// player, not the other way around).
///
/// Each pivot rotates around the base of its handle (the hinge). The swing angle is server-authoritative
/// and replicated, so every client sees the blades in the same place; damage is applied on the server and
/// the knockback is relayed to the hit player's owner (whose CharacterController is owner-authoritative).
/// Falls back to fully local behaviour when no NetworkManager is listening (offline / single player).
/// Mirrors the detection/idiom of <see cref="PivotSwingTrap"/> but oscillates continuously instead of
/// doing a single swing-and-return, and pushes instead of ragdolling.
/// </summary>
[DisallowMultipleComponent]
public class SwingingAxeTrap : NetworkBehaviour
{
    [Header("Pivots (rotate at the base of each handle)")]
    [Tooltip("Each transform is a hinge pivot whose child is one axe mesh. All pivots swing together.")]
    [SerializeField] Transform[] pivots;
    [Tooltip("Swing axis in each pivot's LOCAL space. With world-aligned pivots, (1,0,0) swings the blade " +
             "down/forward/back through a vertical plane (a pendulum).")]
    [SerializeField] Vector3 localSwingAxis = Vector3.right;
    [Tooltip("Angle (deg) at rest, before the swing starts. The axe's current pose is angle 0.")]
    [SerializeField] float restAngleDegrees = 0f;
    [Tooltip("Total arc swept, in degrees. 180 = swing from the start pose, through the bottom, to the " +
             "mirror pose, then back again.")]
    [SerializeField] float swingArcDegrees = 180f;
    [Tooltip("Seconds for one sweep (rest -> full arc). A full back-and-forth cycle takes twice this.")]
    [SerializeField, Min(0.1f)] float secondsPerSweep = 1.3f;

    [Header("Activation (tripwire)")]
    [Tooltip("Tripwire that STARTS the swing when a player crosses it. Auto-created on the origin below if empty.")]
    [SerializeField] TripwireZone tripwireZone;
    [SerializeField] bool autoCreateTripwireZone = true;
    [Tooltip("Where to auto-create the tripwire (defaults to this transform).")]
    [SerializeField] Transform tripwireOrigin;
    [SerializeField] float tripwireInPlaneRadius = 3f;
    [SerializeField] float tripwireVerticalHalfExtent = 3.5f;
    [Tooltip("Once started, the swing keeps looping until EVERY player is at least this far (horizontal) from " +
             "the trap. Make this much larger than the tripwire so the axe keeps swinging long after a player " +
             "passes — it only resets when they are well clear.")]
    [SerializeField] float resetDistance = 20f;

    [Header("Damage")]
    [SerializeField] float damagePerHit = 25f;
    [Tooltip("Minimum seconds between hits on the same player, so one swing-through deals one hit.")]
    [SerializeField, Min(0.05f)] float perPlayerHitCooldown = 0.6f;
    [Tooltip("Blade hit volume in each pivot's LOCAL space (covers the axe head). Rotates with the swing.")]
    [SerializeField] Vector3 bladeHitLocalCenter = new Vector3(0f, -0.4f, 3.3f);
    [SerializeField] Vector3 bladeHitHalfExtents = new Vector3(0.5f, 0.8f, 1.3f);
    [Tooltip("Layers scanned for players. Defaults to the 'Player' layer when left empty.")]
    [SerializeField] LayerMask playerMask;

    [Header("Push (no ragdoll — the axe takes movement priority)")]
    [Tooltip("Horizontal shove speed (m/s) applied to the player, along the blade's travel direction.")]
    [SerializeField] float pushHorizontalSpeed = 9f;
    [Tooltip("Small upward pop (m/s) so the hit reads as an impact rather than a flat slide.")]
    [SerializeField] float pushUpwardSpeed = 1.5f;
    [Tooltip("Seconds the player's own movement input is suppressed after a hit so the push wins.")]
    [SerializeField, Min(0f)] float pushControlLockSeconds = 0.25f;

    [Header("Audio")]
    [SerializeField] AudioClip swingSwooshClip;
    [SerializeField, Range(0f, 1f)] float swingSwooshVolume = 0.7f;
    [Tooltip("Metallic wack when the blade connects with a player. Broadcast from the server so every peer hears the same hit.")]
    [SerializeField] AudioClip impactClip;
    [SerializeField, Range(0f, 1f)] float impactVolume = 0.9f;

    const float TwoPi = Mathf.PI * 2f;
    const int OverlapBufferSize = 32;

    // Server writes the live swing angle every frame while moving; clients read and apply it so every peer
    // shows the blades in the same place (and the server's own blade colliders drive authoritative damage).
    readonly NetworkVariable<float> _netAngleDeg = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    Quaternion[] _baseLocalRot;
    Vector3[] _prevBladeWorld;
    bool _hasPrevBlade;
    float _phase;                 // 0..2π drives one full back-and-forth cycle (authority/offline only)
    float _currentAngleDeg;
    float _prevDisplayAngleDeg;
    bool _displayPrimed;
    bool _activeLatched;          // true once tripped; stays true until every player is past resetDistance
    readonly Dictionary<PlayerHealth, float> _nextHitTime = new();
    readonly Collider[] _overlap = new Collider[OverlapBufferSize];
    AudioSource _audio;

    // Client-side interpolation of the server-replicated swing angle. The server writes _netAngleDeg every frame but
    // it only replicates at the network tick rate, so reading it raw makes the blade step between ticks on observers.
    // We interpolate from the last displayed angle toward each newly received value over the measured update interval,
    // giving continuous motion a few ms behind the server. Damage is server-authoritative regardless of this visual.
    float _netDisplayAngleDeg;
    float _netFromAngleDeg;
    float _netToAngleDeg;
    float _netSegStartTime;
    float _netSegDuration = 0.05f;
    float _lastNetAngleChangeTime = -1f;
    float _smoothedNetInterval = 0.05f;
    bool _netInterpPrimed;

    static bool IsNetworkActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>Authority over swing/damage: the server when networked, otherwise the local instance.</summary>
    bool IsAuthority => !IsNetworkActive || IsServer;

    void Awake()
    {
        if (tripwireOrigin == null)
            tripwireOrigin = transform;

        CachePivots();

        if (playerMask.value == 0)
        {
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0)
                playerMask = 1 << playerLayer;
        }

        EnsureTripwireZone();
        EnsureAudio();
#if UNITY_EDITOR
        AutoAssignClipInEditor();
#endif
    }

    void CachePivots()
    {
        int count = pivots != null ? pivots.Length : 0;
        _baseLocalRot = new Quaternion[count];
        _prevBladeWorld = new Vector3[count];
        for (int i = 0; i < count; i++)
            if (pivots[i] != null)
                _baseLocalRot[i] = pivots[i].localRotation;
    }

    void EnsureTripwireZone()
    {
        if (tripwireZone != null || !autoCreateTripwireZone || tripwireOrigin == null)
            return;

        tripwireZone = tripwireOrigin.GetComponent<TripwireZone>();
        if (tripwireZone != null)
            return;

        tripwireZone = tripwireOrigin.gameObject.AddComponent<TripwireZone>();
        tripwireZone.Shape = TripwireZone.VolumeShape.Capsule;
        tripwireZone.InPlaneRadius = Mathf.Max(0.1f, tripwireInPlaneRadius);
        tripwireZone.VerticalHalfExtent = Mathf.Max(0.1f, tripwireVerticalHalfExtent);
    }

    void EnsureAudio()
    {
        if (_audio != null)
            return;

        _audio = GetComponent<AudioSource>();
        if (_audio == null)
            _audio = gameObject.AddComponent<AudioSource>();

        _audio.playOnAwake = false;
        _audio.loop = false;
        _audio.spatialBlend = 1f;
        _audio.minDistance = 1f;
        _audio.maxDistance = 45f;
        _audio.rolloffMode = AudioRolloffMode.Linear;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignClipInEditor();
    }

    void AutoAssignClipInEditor()
    {
        if (swingSwooshClip == null)
            swingSwooshClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Swoosh.wav");
        if (impactClip == null)
            impactClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/MetalicWack.wav");
    }
#endif

    void Update()
    {
        float dt = Time.deltaTime;
        float angle;

        if (IsAuthority)
        {
            // Hysteresis: the tripwire STARTS the swing (cross-the-wire), but once started it keeps looping
            // until every player is past the much-larger resetDistance — so it doesn't stop the instant the
            // player steps off the wire.
            bool tripped = tripwireZone != null && tripwireZone.HasQualifyingTarget;
            if (tripped)
                _activeLatched = true;
            else if (_activeLatched && !AnyPlayerWithin(resetDistance))
                _activeLatched = false;

            DriveSwing(_activeLatched, dt);
            angle = _currentAngleDeg;

            if (IsNetworkActive && IsSpawned)
                _netAngleDeg.Value = angle;

            ApplyRotation(angle);

            // Only deal damage while the blade is actually moving (not parked at rest).
            if (_phase > 0.0001f)
                DetectAndDamage(dt);
            else
                _hasPrevBlade = false;
        }
        else
        {
            angle = UpdateInterpolatedNetworkAngle();
            ApplyRotation(angle);
        }

        MaybePlaySwoosh(angle);
    }

    public override void OnNetworkSpawn()
    {
        // Only pure clients interpolate; the server/host drives the swing directly and is the authority.
        if (IsNetworkActive && !IsServer)
        {
            _netDisplayAngleDeg = _netFromAngleDeg = _netToAngleDeg = _netAngleDeg.Value;
            _netAngleDeg.OnValueChanged += OnNetAngleChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        _netAngleDeg.OnValueChanged -= OnNetAngleChanged;
    }

    void OnNetAngleChanged(float previous, float current)
    {
        float now = Time.time;
        if (_lastNetAngleChangeTime >= 0f)
        {
            // Track the actual replication interval (tick rate) so the interpolation segment matches how often new
            // values arrive; clamp to sane bounds so a hitch/first sample can't stretch or collapse the segment.
            float interval = now - _lastNetAngleChangeTime;
            if (interval > 0.0001f && interval < 0.5f)
                _smoothedNetInterval = Mathf.Lerp(_smoothedNetInterval, interval, 0.2f);
        }
        _lastNetAngleChangeTime = now;

        _netFromAngleDeg = _netInterpPrimed ? _netDisplayAngleDeg : current;
        _netToAngleDeg = current;
        _netSegStartTime = now;
        _netSegDuration = Mathf.Max(0.0166f, _smoothedNetInterval);
        _netInterpPrimed = true;
    }

    /// <summary>Client-only: smoothly advance the displayed swing angle toward the latest replicated value.</summary>
    float UpdateInterpolatedNetworkAngle()
    {
        if (!_netInterpPrimed)
        {
            _netDisplayAngleDeg = _netAngleDeg.Value;
            return _netDisplayAngleDeg;
        }

        float t = _netSegDuration > 0.0001f
            ? Mathf.Clamp01((Time.time - _netSegStartTime) / _netSegDuration)
            : 1f;
        // Plain lerp (not LerpAngle): the pendulum angle is a continuous 0..arc value, and consecutive samples are
        // only a few degrees apart, so there is no wraparound to resolve.
        _netDisplayAngleDeg = Mathf.Lerp(_netFromAngleDeg, _netToAngleDeg, t);
        return _netDisplayAngleDeg;
    }

    /// <summary>
    /// Advance the pendulum. While active the phase loops; once the player leaves it continues to the
    /// nearest rest pose (the shorter way) and stops, so the axe finishes its swing rather than snapping.
    /// </summary>
    void DriveSwing(bool active, float dt)
    {
        float omega = Mathf.PI / Mathf.Max(0.1f, secondsPerSweep); // radians/sec; π per sweep

        if (active)
        {
            _phase += omega * dt;
            if (_phase >= TwoPi)
                _phase -= TwoPi;
        }
        else if (_phase > 0f)
        {
            float target = _phase <= Mathf.PI ? 0f : TwoPi;
            _phase = Mathf.MoveTowards(_phase, target, omega * dt);
            if (_phase <= 0.0001f || _phase >= TwoPi - 0.0001f)
                _phase = 0f;
        }

        // Cosine easing: slow at the extremes, fast through the bottom — a natural pendulum.
        _currentAngleDeg = restAngleDegrees + swingArcDegrees * 0.5f * (1f - Mathf.Cos(_phase));
    }

    void ApplyRotation(float angleDeg)
    {
        if (pivots == null)
            return;

        Vector3 axis = localSwingAxis.sqrMagnitude > 1e-6f ? localSwingAxis.normalized : Vector3.right;
        Quaternion swing = Quaternion.AngleAxis(angleDeg, axis);
        for (int i = 0; i < pivots.Length; i++)
        {
            if (pivots[i] == null)
                continue;
            pivots[i].localRotation = _baseLocalRot[i] * swing;
        }
    }

    /// <summary>
    /// True while any living player is within <paramref name="distance"/> (measured horizontally) of the
    /// trap. Keeps the swing looping until everyone is well clear, instead of stopping the moment they step
    /// off the tripwire.
    /// </summary>
    bool AnyPlayerWithin(float distance)
    {
        if (distance <= 0f)
            return false;

        Vector3 center = tripwireOrigin != null ? tripwireOrigin.position : transform.position;
        float sq = distance * distance;
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (p == null || p.IsDead)
                continue;
            Vector3 d = p.transform.position - center;
            d.y = 0f;
            if (d.sqrMagnitude <= sq)
                return true;
        }
        return false;
    }

    void DetectAndDamage(float dt)
    {
        if (pivots == null)
            return;

        int mask = playerMask.value != 0 ? playerMask.value : Physics.DefaultRaycastLayers;
        float invDt = dt > 1e-5f ? 1f / dt : 0f;

        for (int i = 0; i < pivots.Length; i++)
        {
            Transform pivot = pivots[i];
            if (pivot == null)
                continue;

            Vector3 center = pivot.TransformPoint(bladeHitLocalCenter);

            // Blade travel direction this frame (horizontal) — the way we shove the player.
            Vector3 bladeVel = _hasPrevBlade ? (center - _prevBladeWorld[i]) * invDt : Vector3.zero;
            _prevBladeWorld[i] = center;

            int count = Physics.OverlapBoxNonAlloc(
                center, bladeHitHalfExtents, _overlap, pivot.rotation, mask, QueryTriggerInteraction.Ignore);

            for (int h = 0; h < count; h++)
            {
                Collider col = _overlap[h];
                if (col == null)
                    continue;

                PlayerHealth ph = col.GetComponentInParent<PlayerHealth>();
                if (ph == null || ph.IsDead)
                    continue;

                if (_nextHitTime.TryGetValue(ph, out float next) && Time.time < next)
                    continue;
                _nextHitTime[ph] = Time.time + perPlayerHitCooldown;

                Vector3 pushDir = bladeVel;
                pushDir.y = 0f;
                if (pushDir.sqrMagnitude < 1e-4f)
                {
                    // Stationary frame / fallback: shove away from the pivot, horizontally.
                    pushDir = ph.transform.position - pivot.position;
                    pushDir.y = 0f;
                }
                pushDir = pushDir.sqrMagnitude > 1e-6f ? pushDir.normalized : transform.forward;

                ApplyHit(ph, pushDir, center);
            }
        }

        _hasPrevBlade = true;
        PruneHitCooldowns();
    }

    void ApplyHit(PlayerHealth ph, Vector3 pushDirHorizontal, Vector3 impactPosition)
    {
        // Damage is authoritative here (server, or local when offline). PlayerHealth.TakeDamage no-ops on
        // non-server peers, and the result replicates via NetworkPlayerRespawn.
        ph.TakeDamage(damagePerHit);

        Vector3 pushVel = pushDirHorizontal * pushHorizontalSpeed;

        if (IsNetworkActive && IsServer)
        {
            // The impact cue goes out over the RPC only — it loops back to the host, so playing it locally as
            // well would double it there. An unspawned local copy has no RPC channel and falls back.
            if (IsSpawned)
                PlayImpactSfxRpc(impactPosition);
            else
                PlayImpactSfxLocal(impactPosition);

            NetworkObject no = ph.GetComponent<NetworkObject>();
            if (no == null)
                return;

            // Movement is owner-authoritative (OwnerNetworkTransform), so the OWNER must run the push for
            // their position to replicate. Relay to just that client (host loop-back included).
            ApplyAxePushRpc(no.NetworkObjectId, pushVel, pushUpwardSpeed, pushControlLockSeconds,
                RpcTarget.Single(no.OwnerClientId, RpcTargetUse.Temp));
        }
        else
        {
            // Offline / single player.
            PlayImpactSfxLocal(impactPosition);

            PlayerController pc = ph.GetComponent<PlayerController>();
            if (pc != null)
                pc.ApplyExternalPush(pushVel, pushUpwardSpeed, pushControlLockSeconds);
        }
    }

    [Rpc(SendTo.Everyone)]
    void PlayImpactSfxRpc(Vector3 impactPosition)
    {
        PlayImpactSfxLocal(impactPosition);
    }

    /// <summary>
    /// Blade impact at the point it landed rather than from the trap root: with a 3.3m blade offset those are
    /// far enough apart to read wrong. Distances match this trap's own swoosh source.
    /// </summary>
    void PlayImpactSfxLocal(Vector3 impactPosition)
    {
        GameAudioManager.PlaySfxAtPoint(impactClip, impactPosition, impactVolume, 1f, 45f);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ApplyAxePushRpc(ulong playerNetworkObjectId, Vector3 horizontalVelocity, float upwardVelocity,
        float controlLockSeconds, RpcParams rpcParams = default)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject no) || no == null)
            return;

        PlayerController pc = no.GetComponent<PlayerController>();
        if (pc != null)
            pc.ApplyExternalPush(horizontalVelocity, upwardVelocity, controlLockSeconds);
    }

    void PruneHitCooldowns()
    {
        if (_nextHitTime.Count == 0)
            return;

        float now = Time.time;
        List<PlayerHealth> stale = null;
        foreach (KeyValuePair<PlayerHealth, float> kv in _nextHitTime)
        {
            if (kv.Key == null || now >= kv.Value)
            {
                stale ??= new List<PlayerHealth>();
                stale.Add(kv.Key);
            }
        }
        if (stale != null)
            for (int i = 0; i < stale.Count; i++)
                _nextHitTime.Remove(stale[i]);
    }

    void MaybePlaySwoosh(float displayAngle)
    {
        if (!_displayPrimed)
        {
            _prevDisplayAngleDeg = displayAngle;
            _displayPrimed = true;
            return;
        }

        // Whoosh each time the blade crosses the mid-point of the arc (its fastest, lowest point).
        float mid = restAngleDegrees + swingArcDegrees * 0.5f;
        bool crossed = (_prevDisplayAngleDeg - mid) * (displayAngle - mid) < 0f;
        _prevDisplayAngleDeg = displayAngle;

        if (!crossed || swingSwooshClip == null || _audio == null)
            return;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_audio);
        _audio.PlayOneShot(swingSwooshClip, Mathf.Max(0f, swingSwooshVolume));
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (pivots == null)
            return;

        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.7f);
        foreach (Transform pivot in pivots)
        {
            if (pivot == null)
                continue;
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(
                pivot.TransformPoint(bladeHitLocalCenter), pivot.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, bladeHitHalfExtents * 2f);
            Gizmos.matrix = prev;
        }
    }
#endif
}
