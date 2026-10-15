using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Red laser hazard for one straight corridor cell. A trap is a set of <see cref="Curtain"/>s — flat sheets of
/// horizontal beams spanning the corridor width over a height band — and each curtain either sweeps back and forth
/// along the cell (the duck-under and jump-over variants) or pulses on and off in place (the three-wall variant).
///
/// Timing is a pure function of the shared network clock (<c>NetworkManager.ServerTime</c>, or local time offline)
/// plus a per-instance offset hashed from the trap's spawn position, so every peer computes the same laser state
/// without replicating anything per frame.
///
/// Hits are detected by the player who gets hit: each peer tests only its own local player against the lasers it is
/// rendering, so a clean jump or a dash through a gap on screen is a clean pass. That is a deliberate exception to the
/// server-detects-every-trap-hit rule (see <see cref="SwingingAxeTrap"/>): the laser state is identical everywhere, so
/// the owner's view is the fair one, while a server-side test would judge a joined player against a body it only sees
/// in the past. The owner applies its own knockback straight away; the server still applies the damage (after a
/// sanity check on distance and rate) and broadcasts the zap sound.
/// </summary>
[DisallowMultipleComponent]
public partial class LaserTrap : NetworkBehaviour
{
    [Serializable]
    public class Curtain
    {
        [Tooltip("Moves along the trap's local Z and parents this curtain's beams and emitter rails.")]
        public Transform root;
        [Tooltip("Bottom of the blocked band, metres above the trap's floor (its origin).")]
        public float bottom;
        [Tooltip("Top of the blocked band, metres above the trap's floor.")]
        public float top = 0.42f;
        [Tooltip("Half the corridor width the beams span (trap-local X).")]
        public float halfWidth = 2f;
        [Tooltip("Half thickness of the hit volume along trap-local Z.")]
        public float halfThickness = 0.05f;

        [Header("Sweep (moves back and forth along the cell)")]
        public bool sweep;
        public float sweepMinZ = -2.8f;
        public float sweepMaxZ = 2.8f;
        [Tooltip("Metres per second. The curtain travels at this constant speed and reverses at each end.")]
        public float sweepSpeed = 1.6f;
        [Tooltip("Where in its back-and-forth cycle (0..1) this curtain starts.")]
        [Range(0f, 1f)] public float sweepPhase;

        [Header("Pulse (switches on and off in place)")]
        public bool pulse;
        public float onSeconds = 1.5f;
        public float offSeconds = 1f;
        [Tooltip("Shifts this curtain's on/off cycle. Give each curtain its own so they don't switch together.")]
        public float pulsePhaseSeconds;

        [NonSerialized] public Renderer[] beams;
    }

    enum CurtainState { Off, Warning, On }

    [Header("Curtains")]
    [SerializeField] Curtain[] curtains = Array.Empty<Curtain>();
    [Tooltip("Seconds of flicker (harmless) before a pulsing curtain switches on.")]
    [SerializeField, Min(0f)] float pulseWarningSeconds = 0.4f;

    [Header("Beam look")]
    [Tooltip("HDR beam colour. Values above 1 feed bloom.")]
    [SerializeField, ColorUsage(false, true)] Color beamColor = new Color(3f, 0.06f, 0.04f, 1f);
    [Tooltip("Beam brightness during the pre-fire flicker, as a fraction of full.")]
    [SerializeField, Range(0f, 1f)] float warningBrightness = 0.35f;
    [SerializeField, Min(1f)] float warningFlickerHz = 14f;

    [Header("Hit")]
    [SerializeField] float damagePerHit = 25f;
    [Tooltip("Minimum seconds between two hits on the same player from this trap. Keep it shorter than the time it " +
             "takes to walk back into a wall after its knockback, or a player can tank one hit and stroll through.")]
    [SerializeField, Min(0.05f)] float perPlayerHitCooldown = 0.45f;
    [Tooltip("Knockback speed (m/s). Sweeping curtains throw the player behind the beam (the side it already passed); " +
             "static walls push the player back to the side they came from.")]
    [SerializeField] float sweepPushSpeed = 7f;
    [SerializeField] float wallPushSpeed = 8f;
    [SerializeField] float pushUpwardSpeed = 1f;
    [SerializeField, Min(0f)] float pushControlLockSeconds = 0.3f;
    [Tooltip("Server sanity check: a reported hit is ignored when the reporting player is farther than this " +
             "(horizontal) from the trap.")]
    [SerializeField] float serverValidateRadius = 6f;

    [Header("Audio")]
    [Tooltip("Looping laser buzz. Its volume follows how many curtains are live, so pulsing walls fade with it.")]
    [SerializeField] AudioClip humLoopClip;
    [SerializeField, Range(0f, 1f)] float humVolume = 0.2f;
    [Tooltip("Played on every peer when a player is hit.")]
    [SerializeField] AudioClip zapClip;
    [SerializeField, Range(0f, 1f)] float zapVolume = 0.9f;
    [SerializeField] float audioMaxDistance = 16f;

    // Only read by the editor-time beam builder, but serialized in every build so the layout matches.
    [Header("Editor: beam builder")]
    [Tooltip("Vertical spacing between beams when rebuilding the visuals.")]
    [SerializeField] float editorBeamSpacing = 0.25f;
    [SerializeField] float editorBeamThickness = 0.03f;
    [SerializeField] Material editorBeamMaterial;
    [SerializeField] Material editorHousingMaterial;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    // Server-side rate limit per reporting client, a little shorter than the owner's own cooldown so network jitter
    // never drops a real second hit.
    const float ServerCooldownFraction = 0.7f;
    const float LocalPlayerRefreshSeconds = 1f;

    MaterialPropertyBlock _block;
    AudioSource _hum;
    double _instanceOffsetSeconds;
    bool _offsetResolved;
    float _nextLocalHitTime;
    PlayerController _localPlayer;
    float _nextLocalPlayerLookup;
    readonly Dictionary<ulong, float> _serverNextHitTime = new();

    static bool IsNetworkActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    void Awake()
    {
        _block = new MaterialPropertyBlock();
        for (int i = 0; i < curtains.Length; i++)
        {
            Curtain c = curtains[i];
            c.beams = c.root != null ? CollectBeams(c.root) : Array.Empty<Renderer>();
        }
#if UNITY_EDITOR
        AutoAssignClipsInEditor();
#endif
        EnsureHum();
    }

    void Start()
    {
        ResolveInstanceOffset();
    }

    /// <summary>
    /// Every peer derives the same start offset from where the trap stands, so two laser traps in one run don't
    /// share a rhythm. Read after spawn placement: the server positions the instance right after Instantiate (so
    /// Awake still sees the origin) and clients receive it already placed.
    /// </summary>
    void ResolveInstanceOffset()
    {
        if (_offsetResolved)
            return;
        _offsetResolved = true;

        Vector3 p = transform.position;
        unchecked
        {
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(p.x * 2f);
            h = h * 31 + Mathf.RoundToInt(p.y * 2f);
            h = h * 31 + Mathf.RoundToInt(p.z * 2f);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            _instanceOffsetSeconds = (h & 0xFFFF) / 65535.0 * 120.0;
        }
    }

    double SharedTime()
    {
        NetworkManager nm = NetworkManager.Singleton;
        double t = nm != null && nm.IsListening ? nm.ServerTime.Time : Time.timeAsDouble;
        return t + _instanceOffsetSeconds;
    }

    void Update()
    {
        if (!_offsetResolved)
            ResolveInstanceOffset();

        double t = SharedTime();
        int liveCount = 0;

        for (int i = 0; i < curtains.Length; i++)
        {
            Curtain c = curtains[i];
            float z = EvaluateZ(c, t, out _);
            if (c.root != null && c.sweep)
            {
                Vector3 lp = c.root.localPosition;
                lp.z = z;
                c.root.localPosition = lp;
            }

            CurtainState state = EvaluateState(c, t, out float stateTime);
            ApplyBeamVisuals(c, state, stateTime);

            if (state == CurtainState.On)
                liveCount++;
        }

        UpdateHum(liveCount);
        DetectLocalPlayerHit(t);
    }

    // ---- laser timing ------------------------------------------------------------------------------------

    /// <summary>Curtain position along trap-local Z at shared time <paramref name="t"/>, and its travel sign.</summary>
    float EvaluateZ(Curtain c, double t, out float direction)
    {
        direction = 0f;
        if (!c.sweep)
            return c.root != null ? c.root.localPosition.z : 0f;

        float length = Mathf.Max(0.01f, c.sweepMaxZ - c.sweepMinZ);
        double cycle = 2.0 * length / Mathf.Max(0.05f, c.sweepSpeed);
        double u = t / cycle + c.sweepPhase;
        u -= Math.Floor(u);

        // Constant speed out and back: a machine scanner, so the player can read and time the pass.
        if (u < 0.5)
        {
            direction = 1f;
            return c.sweepMinZ + (float)(u * 2.0) * length;
        }
        direction = -1f;
        return c.sweepMaxZ - (float)((u - 0.5) * 2.0) * length;
    }

    /// <summary>On/warning/off for a pulsing curtain; sweeping and plain curtains are always on.</summary>
    CurtainState EvaluateState(Curtain c, double t, out float stateTime)
    {
        stateTime = 0f;
        if (!c.pulse)
            return CurtainState.On;

        double period = Mathf.Max(0.1f, c.onSeconds + c.offSeconds);
        double p = (t + c.pulsePhaseSeconds) % period;
        if (p < 0)
            p += period;

        if (p < c.onSeconds)
        {
            stateTime = (float)p;
            return CurtainState.On;
        }

        double untilOn = period - p;
        stateTime = (float)(p - c.onSeconds);
        return untilOn <= pulseWarningSeconds ? CurtainState.Warning : CurtainState.Off;
    }

    void ApplyBeamVisuals(Curtain c, CurtainState state, float stateTime)
    {
        if (c.beams == null)
            return;

        bool visible;
        float brightness;
        switch (state)
        {
            case CurtainState.On:
                visible = true;
                brightness = 1f;
                break;
            case CurtainState.Warning:
                // Square-wave flicker at a fixed rate, phase-locked to the shared clock so every peer flickers alike.
                visible = Mathf.Repeat(stateTime * warningFlickerHz, 1f) < 0.5f;
                brightness = warningBrightness;
                break;
            default:
                visible = false;
                brightness = 0f;
                break;
        }

        if (visible)
            _block.SetColor(BaseColorId, beamColor * brightness);

        for (int b = 0; b < c.beams.Length; b++)
        {
            Renderer r = c.beams[b];
            if (r == null)
                continue;
            if (r.enabled != visible)
                r.enabled = visible;
            if (visible)
                r.SetPropertyBlock(_block);
        }
    }

    // ---- local hit detection (owner-side) ------------------------------------------------------------------

    void DetectLocalPlayerHit(double t)
    {
        TrackLimboPasses(t);

        if (Time.time < _nextLocalHitTime)
            return;

        PlayerController player = ResolveLocalPlayer();
        if (player == null || !player.isActiveAndEnabled)
            return;

        CharacterController cc = player.GetComponent<CharacterController>();
        // Disabled while ragdolled, carried or dead: nothing to stand in the beam.
        if (cc == null || !cc.enabled)
            return;

        PlayerHealth health = player.GetComponent<PlayerHealth>();
        if (health == null || health.IsDead)
            return;

        // Capsule in trap space. Players and the trap are both upright, so the capsule's core segment is vertical.
        Transform pt = player.transform;
        Vector3 center = transform.InverseTransformPoint(pt.TransformPoint(cc.center));
        Vector3 scale = pt.lossyScale;
        float radius = cc.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float halfSegment = Mathf.Max(0f, cc.height * 0.5f * Mathf.Abs(scale.y) - radius);
        float segBottom = center.y - halfSegment;
        float segTop = center.y + halfSegment;

        // Cheap reject: nowhere near this cell.
        if (Mathf.Abs(center.x) > 4f || Mathf.Abs(center.z) > 4f)
            return;

        for (int i = 0; i < curtains.Length; i++)
        {
            Curtain c = curtains[i];
            if (EvaluateState(c, t, out _) != CurtainState.On)
                continue;

            float z = EvaluateZ(c, t, out float direction);
            float dx = Mathf.Max(0f, Mathf.Abs(center.x) - c.halfWidth);
            float dz = Mathf.Max(0f, Mathf.Abs(center.z - z) - c.halfThickness);
            float dy = Mathf.Max(0f, Mathf.Max(segBottom - c.top, c.bottom - segTop));
            if (dx * dx + dy * dy + dz * dz > radius * radius)
                continue;

            OnLocalPlayerHit(player, health, c, center, z, direction);
            return;
        }
    }

    void OnLocalPlayerHit(PlayerController player, PlayerHealth health, Curtain c, Vector3 localCenter, float curtainZ,
        float sweepDirection)
    {
        _nextLocalHitTime = Time.time + perPlayerHitCooldown;
        NoteLimboHit(c);

        // A sweeping curtain throws you behind itself (the stretch it already cleared); a fixed wall pushes you back
        // out the side you walked in from.
        float side;
        float speed;
        if (c.sweep && sweepDirection != 0f)
        {
            side = -sweepDirection;
            speed = sweepPushSpeed;
        }
        else
        {
            side = localCenter.z - curtainZ >= 0f ? 1f : -1f;
            speed = wallPushSpeed;
        }

        Vector3 pushDir = transform.TransformDirection(new Vector3(0f, 0f, side));
        pushDir.y = 0f;
        pushDir = pushDir.sqrMagnitude > 1e-6f ? pushDir.normalized : -transform.forward;
        player.ApplyExternalPush(pushDir * speed, pushUpwardSpeed, pushControlLockSeconds);

        Vector3 hitPoint = transform.TransformPoint(new Vector3(localCenter.x,
            Mathf.Clamp(localCenter.y, c.bottom, c.top), curtainZ));

        if (IsNetworkActive && IsSpawned)
        {
            if (IsServer)
                ServerApplyHit(health, NetworkManager.LocalClientId, hitPoint);
            else
                ReportLaserHitRpc(hitPoint);
        }
        else
        {
            // Offline / single player.
            health.TakeDamage(damagePerHit);
            PlayZapLocal(hitPoint);
        }
    }

    PlayerController ResolveLocalPlayer()
    {
        if (_localPlayer != null && _localPlayer.HasLocalControl)
            return _localPlayer;

        if (Time.time < _nextLocalPlayerLookup)
            return null;
        _nextLocalPlayerLookup = Time.time + LocalPlayerRefreshSeconds;

        _localPlayer = null;
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (p == null)
                continue;
            PlayerController controller = p.GetComponent<PlayerController>();
            if (controller != null && controller.HasLocalControl)
            {
                _localPlayer = controller;
                break;
            }
        }
        return _localPlayer;
    }

    // ---- server ------------------------------------------------------------------------------------------

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ReportLaserHitRpc(Vector3 hitPoint, RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(sender, out NetworkClient client) || client.PlayerObject == null)
            return;

        PlayerHealth health = client.PlayerObject.GetComponent<PlayerHealth>();
        if (health == null)
            health = client.PlayerObject.GetComponentInChildren<PlayerHealth>();
        if (health == null)
            return;

        ServerApplyHit(health, sender, hitPoint);
    }

    void ServerApplyHit(PlayerHealth health, ulong clientId, Vector3 hitPoint)
    {
        if (health == null || health.IsDead)
            return;

        Vector3 offset = health.transform.position - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > serverValidateRadius * serverValidateRadius)
            return;

        float now = Time.time;
        if (_serverNextHitTime.TryGetValue(clientId, out float next) && now < next)
            return;
        _serverNextHitTime[clientId] = now + perPlayerHitCooldown * ServerCooldownFraction;

        health.TakeDamage(damagePerHit);
        PlayZapRpc(hitPoint);
    }

    [Rpc(SendTo.Everyone)]
    void PlayZapRpc(Vector3 hitPoint)
    {
        PlayZapLocal(hitPoint);
    }

    // ---- audio -------------------------------------------------------------------------------------------

    void EnsureHum()
    {
        if (_hum != null || humLoopClip == null)
            return;

        _hum = gameObject.AddComponent<AudioSource>();
        _hum.clip = humLoopClip;
        _hum.loop = true;
        _hum.playOnAwake = false;
        _hum.spatialBlend = 1f;
        _hum.rolloffMode = AudioRolloffMode.Linear;
        _hum.minDistance = 1f;
        _hum.maxDistance = audioMaxDistance;
        _hum.volume = 0f;
        _hum.dopplerLevel = 0f;
    }

    void UpdateHum(int liveCount)
    {
        if (_hum == null)
            return;

        if (!_hum.isPlaying && Application.isPlaying)
        {
            GameAudioManager.RouteSfxSource(_hum);
            // Start at a random point so neighbouring traps don't beat against each other.
            _hum.time = UnityEngine.Random.Range(0f, Mathf.Max(0f, humLoopClip.length - 0.05f));
            _hum.Play();
        }

        float target = curtains.Length > 0 ? humVolume * liveCount / curtains.Length : 0f;
        _hum.volume = Mathf.MoveTowards(_hum.volume, target, Time.deltaTime * 4f);
    }

    void PlayZapLocal(Vector3 point)
    {
        GameAudioManager.PlaySfxAtPoint(zapClip, point, zapVolume, 1.5f, audioMaxDistance + 6f);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    const string BeamPrefix = "Beam";

    static Renderer[] CollectBeams(Transform root)
    {
        var list = new List<Renderer>();
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            if (r.name.StartsWith(BeamPrefix, StringComparison.Ordinal))
                list.Add(r);
        return list.ToArray();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignClipsInEditor();
    }

    void AutoAssignClipsInEditor()
    {
        const string dir = "Assets/Audio/SFX/Severance/Lasers/";
        if (humLoopClip == null)
            humLoopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(dir + "LaserBuzz.wav");
        if (zapClip == null)
            zapClip = AssetDatabase.LoadAssetAtPath<AudioClip>(dir + "PlayerZap.wav");
    }

    /// <summary>
    /// Regenerates every curtain's beams and emitter rails (and the wall tracks under sweeping curtains) from the
    /// curtain settings. Edit-time only; the result is saved into the prefab.
    /// </summary>
    [ContextMenu("Rebuild Beam Visuals")]
    void RebuildBeamVisuals()
    {
        Transform tracks = transform.Find("Tracks");
        if (tracks != null)
            DestroyImmediate(tracks.gameObject);
        tracks = new GameObject("Tracks").transform;
        tracks.SetParent(transform, false);

        foreach (Curtain c in curtains)
        {
            if (c.root == null)
                continue;

            for (int i = c.root.childCount - 1; i >= 0; i--)
                DestroyImmediate(c.root.GetChild(i).gameObject);

            float railDepth = 0.14f;
            float railWidth = 0.08f;
            float railInner = c.halfWidth - railWidth;
            float bandHeight = c.top - c.bottom;

            for (int s = -1; s <= 1; s += 2)
            {
                CreateBox(c.root, "EmitterRail", new Vector3(s * (c.halfWidth - railWidth * 0.5f), (c.bottom + c.top) * 0.5f, 0f),
                    new Vector3(railWidth, bandHeight + 0.08f, railDepth), editorHousingMaterial);
            }

            int count = Mathf.Max(1, Mathf.FloorToInt(bandHeight / Mathf.Max(0.05f, editorBeamSpacing)) + 1);
            float step = count > 1 ? bandHeight / (count - 1) : 0f;
            float inset = editorBeamThickness * 0.5f;
            for (int b = 0; b < count; b++)
            {
                float y = count > 1 ? Mathf.Lerp(c.bottom + inset, c.top - inset, b / (float)(count - 1)) : (c.bottom + c.top) * 0.5f;
                CreateBox(c.root, BeamPrefix + b, new Vector3(0f, y, 0f),
                    new Vector3(railInner * 2f, editorBeamThickness, editorBeamThickness), editorBeamMaterial);
            }

            if (!c.sweep)
                continue;

            float length = c.sweepMaxZ - c.sweepMinZ + railDepth;
            float midZ = (c.sweepMinZ + c.sweepMaxZ) * 0.5f;
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * (c.halfWidth - 0.01f);
                CreateBox(tracks, "Track", new Vector3(x, c.bottom - 0.06f, midZ), new Vector3(0.02f, 0.04f, length), editorHousingMaterial);
                CreateBox(tracks, "Track", new Vector3(x, c.top + 0.06f, midZ), new Vector3(0.02f, 0.04f, length), editorHousingMaterial);
            }
        }
    }

    static void CreateBox(Transform parent, string name, Vector3 localPos, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = size;
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        foreach (Curtain c in curtains)
        {
            float height = c.top - c.bottom;
            Gizmos.color = new Color(1f, 0.1f, 0.1f, 0.8f);
            if (c.sweep)
            {
                Gizmos.DrawWireCube(new Vector3(0f, c.bottom + height * 0.5f, c.sweepMinZ), new Vector3(c.halfWidth * 2f, height, c.halfThickness * 2f));
                Gizmos.DrawWireCube(new Vector3(0f, c.bottom + height * 0.5f, c.sweepMaxZ), new Vector3(c.halfWidth * 2f, height, c.halfThickness * 2f));
            }
            else if (c.root != null)
            {
                Gizmos.DrawWireCube(new Vector3(0f, c.bottom + height * 0.5f, c.root.localPosition.z), new Vector3(c.halfWidth * 2f, height, c.halfThickness * 2f));
            }
        }
    }
#endif
}
