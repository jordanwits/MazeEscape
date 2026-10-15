using UnityEngine;

/// <summary>
/// Limbo achievement (owning client — laser hits are detected there too): a sweeping curtain passed through the
/// local player's lane without hitting them, i.e. they ducked under or jumped over it. Tracks which side of each
/// sweep the player's centre is on; the sweep passing them flips that side. A hit marks the curtain's current pass
/// as failed (its knockback flips the side too), and the flag is spent on the next flip.
/// </summary>
public partial class LaserTrap
{
    /// <summary>A side flip bigger than this between two frames is a teleport or a respawn, not a pass.</summary>
    const float LimboMaxFrameJump = 2f;

    float[] _limboPrevOffset;
    bool[] _limboHitThisPass;

    void TrackLimboPasses(double t)
    {
        if (curtains == null || curtains.Length == 0)
            return;

        if (_limboPrevOffset == null || _limboPrevOffset.Length != curtains.Length)
        {
            _limboPrevOffset = new float[curtains.Length];
            _limboHitThisPass = new bool[curtains.Length];
            ResetLimboTracking();
        }

        PlayerController player = ResolveLocalPlayer();
        CharacterController cc = player != null && player.isActiveAndEnabled ? player.GetComponent<CharacterController>() : null;
        PlayerHealth health = player != null ? player.GetComponent<PlayerHealth>() : null;
        if (cc == null || !cc.enabled || health == null || health.IsDead)
        {
            ResetLimboTracking();
            return;
        }

        Vector3 center = transform.InverseTransformPoint(player.transform.TransformPoint(cc.center));
        for (int i = 0; i < curtains.Length; i++)
        {
            Curtain c = curtains[i];
            if (!c.sweep || Mathf.Abs(center.x) > c.halfWidth || center.z < c.sweepMinZ - 1f || center.z > c.sweepMaxZ + 1f)
            {
                _limboPrevOffset[i] = float.NaN;
                _limboHitThisPass[i] = false;
                continue;
            }

            float offset = center.z - EvaluateZ(c, t, out _);
            float previous = _limboPrevOffset[i];
            _limboPrevOffset[i] = offset;
            if (float.IsNaN(previous) || Mathf.Sign(previous) == Mathf.Sign(offset))
                continue;

            if (Mathf.Abs(offset - previous) <= LimboMaxFrameJump && !_limboHitThisPass[i])
                Achievements.Unlock(AchievementId.Limbo);
            _limboHitThisPass[i] = false;
        }
    }

    void NoteLimboHit(Curtain c)
    {
        if (_limboHitThisPass == null)
            return;

        int i = System.Array.IndexOf(curtains, c);
        if (i >= 0 && i < _limboHitThisPass.Length)
            _limboHitThisPass[i] = true;
    }

    void ResetLimboTracking()
    {
        for (int i = 0; i < _limboPrevOffset.Length; i++)
        {
            _limboPrevOffset[i] = float.NaN;
            _limboHitThisPass[i] = false;
        }
    }
}
