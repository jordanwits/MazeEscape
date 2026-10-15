using UnityEngine;

/// <summary>
/// "Find this room" achievement marker, placed on a room's maze-piece root (SeveranceRoom2's disco, SeveranceRoom3's
/// park). Polls whether the LOCAL living player stands inside the box and unlocks <see cref="achievement"/> for them
/// directly — every peer builds the maze itself and the player's own position is exact here, so no networking is
/// needed. Same local-space box idea as <see cref="CarnivalMainRoomZone"/>; tune it against the gizmo.
/// </summary>
[DisallowMultipleComponent]
public sealed class AchievementRoomZone : MonoBehaviour
{
    const float PollSeconds = 0.25f;

    [SerializeField] AchievementId achievement = AchievementId.DiscoInferno;

    [SerializeField, Tooltip("Interior volume center in the piece root's local space.")]
    Vector3 localCenter = new Vector3(0f, 3.5f, 0f);

    [SerializeField, Tooltip("Interior volume size in the piece root's local space. Keep it inside the walls so standing in a doorway does not count.")]
    Vector3 localSize = new Vector3(15f, 6f, 15f);

    float _nextPollTime;
    bool _done;

    void Update()
    {
        if (_done || Time.time < _nextPollTime)
            return;

        _nextPollTime = Time.time + PollSeconds;
        if (Achievements.IsUnlocked(achievement))
        {
            _done = true;
            return;
        }

        if (!TryGetLocalLivingPlayer(out PlayerHealth player) || !Contains(player.transform.position))
            return;

        _done = true;
        Achievements.Unlock(achievement);
    }

    bool Contains(Vector3 worldPoint)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint);
        Vector3 half = localSize * 0.5f;
        return Mathf.Abs(local.x - localCenter.x) <= half.x
            && Mathf.Abs(local.y - localCenter.y) <= half.y
            && Mathf.Abs(local.z - localCenter.z) <= half.z;
    }

    /// <summary>The player this machine controls, if alive — a spectating ghost has not found anything.</summary>
    static bool TryGetLocalLivingPlayer(out PlayerHealth player)
    {
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth candidate = players[i];
            if (candidate != null
                && !candidate.IsDead
                && candidate.TryGetComponent(out PlayerController controller)
                && controller.HasLocalControl)
            {
                player = candidate;
                return true;
            }
        }

        player = null;
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.9f, 0.4f, 1f, 0.5f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(localCenter, localSize);
    }
}
