using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The "flashbanged" state on an enemy: for its duration the AI stops sensing, chasing and attacking, and
/// instead staggers forward on a slow, continuously curving heading — it wanders in circles until its sight
/// comes back. Added at runtime by <see cref="FlashbangGrenade"/> on the SERVER only (enemy movement is
/// server-authoritative and replicates through the enemy's NetworkTransform, so clients see the circling for
/// free) and destroys itself when the timer runs out.
///
/// Each AI keeps a cached reference and asks <see cref="IsBlinded"/> once per frame; the static
/// <see cref="ActiveCount"/> gate means that costs nothing at all while no flashbang is live.
/// </summary>
[DisallowMultipleComponent]
public sealed class EnemyBlindEffect : MonoBehaviour
{
    /// <summary>How many enemies are blinded right now, anywhere. 0 = the per-AI lookup can be skipped.</summary>
    public static int ActiveCount { get; private set; }

    const float MinTurnDegreesPerSecond = 55f;
    const float MaxTurnDegreesPerSecond = 130f;
    const float ProbeDistance = 1.4f;

    /// <summary>Fraction of the enemy's normal speed it manages while blind — a groping stumble, not a run.</summary>
    public const float WanderSpeedScale = 0.45f;

    float _endTime;
    float _turnSign = 1f;
    float _turnDegreesPerSecond = 90f;
    float _nextTurnShuffleTime;

    public bool IsActive => Time.time < _endTime;
    public float SecondsRemaining => Mathf.Max(0f, _endTime - Time.time);

    /// <summary>
    /// Server-side: blind <paramref name="enemyRoot"/> for <paramref name="seconds"/>, extending an existing
    /// blind rather than restarting it short. Returns the effect, or null if the target can't be blinded.
    /// </summary>
    public static EnemyBlindEffect Apply(Component enemyRoot, float seconds)
    {
        if (enemyRoot == null || seconds <= 0f)
            return null;

        GameObject go = enemyRoot.gameObject;
        if (!go.TryGetComponent(out EnemyBlindEffect effect))
            effect = go.AddComponent<EnemyBlindEffect>();

        effect.Begin(seconds);
        if (enemyRoot is IBlindableEnemy blindable)
            blindable.OnFlashbangBlinded(seconds);

        return effect;
    }

    /// <summary>
    /// Per-frame check for an AI. <paramref name="cached"/> is the AI's own field: it stays null (and costs
    /// one branch) until something on the level is actually blinded, and goes null again by itself when the
    /// effect destroys its component.
    /// </summary>
    public static bool IsBlinded(ref EnemyBlindEffect cached, GameObject enemyRoot)
    {
        if (cached == null)
        {
            if (ActiveCount <= 0 || enemyRoot == null)
                return false;
            cached = enemyRoot.GetComponent<EnemyBlindEffect>();
        }

        return cached != null && cached.IsActive;
    }

    void Begin(float seconds)
    {
        float end = Time.time + seconds;
        if (end > _endTime)
            _endTime = end;

        _turnSign = Random.value < 0.5f ? -1f : 1f;
        _turnDegreesPerSecond = Random.Range(MinTurnDegreesPerSecond, MaxTurnDegreesPerSecond);
        _nextTurnShuffleTime = Time.time + Random.Range(1.2f, 2.4f);

        if (!enabled)
            enabled = true;
    }

    void OnEnable()
    {
        ActiveCount++;
    }

    void OnDisable()
    {
        // Clamped: play-mode exit (and domain-reload-free enter-play) can tear down an effect whose OnEnable
        // never ran in this session, and a negative count would permanently disable the per-AI lookup.
        if (ActiveCount > 0)
            ActiveCount--;
    }

    void Update()
    {
        // Server-side only (the component is never added on clients), so this just retires the effect.
        if (!IsActive)
            Destroy(this);
    }

    /// <summary>
    /// One frame of the blind stumble: turns the body a little further around its arc and hands back the
    /// horizontal velocity for the AI's own <c>ApplyMovement</c>. Turning here rather than letting
    /// ApplyMovement steer means the enemy always faces where it is groping, and the AI's RotateTowards is a
    /// no-op on top of it.
    /// </summary>
    /// <param name="body">The enemy root transform.</param>
    /// <param name="speed">The enemy's normal move speed; the stumble runs at <see cref="WanderSpeedScale"/> of it.</param>
    public Vector3 TickWanderVelocity(Transform body, float speed)
    {
        if (body == null)
            return Vector3.zero;

        // Re-roll the arc now and then so it reads as aimless groping rather than one perfect circle.
        if (Time.time >= _nextTurnShuffleTime)
        {
            _nextTurnShuffleTime = Time.time + Random.Range(1.2f, 2.4f);
            _turnDegreesPerSecond = Random.Range(MinTurnDegreesPerSecond, MaxTurnDegreesPerSecond);
            if (Random.value < 0.35f)
                _turnSign = -_turnSign;
        }

        float wanderSpeed = Mathf.Max(0f, speed) * WanderSpeedScale;

        // Probe the arc against the NavMesh rather than the collision world: these AIs are all NavMesh-bound,
        // and a walkable-surface probe also keeps the stumble off ledges and out of pits, which a wall
        // spherecast would not.
        // The NavMesh raycast needs a start that is genuinely ON the mesh, so the body position is sampled
        // onto it first; an enemy that has been shoved off the mesh entirely just keeps circling and lets its
        // own AI's off-mesh recovery deal with it.
        bool blocked = false;
        if (NavMesh.SamplePosition(body.position, out NavMeshHit onMesh, 1.5f, NavMesh.AllAreas))
        {
            Vector3 ahead = onMesh.position + body.forward * ProbeDistance;
            blocked = NavMesh.Raycast(onMesh.position, ahead, out NavMeshHit edge, NavMesh.AllAreas)
                && edge.distance < ProbeDistance * 0.9f;
        }

        if (blocked)
        {
            // Turn away hard and give up most of the forward push for this frame, so a blinded enemy paws
            // along a wall instead of grinding into it.
            _turnSign = -_turnSign;
            _turnDegreesPerSecond = MaxTurnDegreesPerSecond;
            _nextTurnShuffleTime = Time.time + Random.Range(0.8f, 1.6f);
            wanderSpeed *= 0.25f;
        }

        body.rotation = Quaternion.AngleAxis(_turnSign * _turnDegreesPerSecond * Time.deltaTime, Vector3.up)
            * body.rotation;

        return body.forward * wanderSpeed;
    }
}
