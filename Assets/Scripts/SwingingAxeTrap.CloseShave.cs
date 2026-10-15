using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Close Shave achievement (authority only): getting through the swinging blades without being hit. A blade swings
/// in the plane perpendicular to its axis, sweeping back and forth along that plane's horizontal direction (the
/// carnival AxeTrap hangs its blades so that is the corridor's length). "Got through" is going from one side of the
/// pivot to the other along that sweep direction, inside the blade's lane and while the trap is swinging. A player
/// arms on whichever side they stand once clear of the bottom of the arc, and earns it on reaching the far side
/// clear of it again; any hit from this trap disarms every blade for that player, so a pass that was struck never
/// counts.
/// </summary>
public partial class SwingingAxeTrap
{
    /// <summary>Distance from the pivot, along the sweep, that counts as clear of the bottom of the arc.</summary>
    const float CloseShaveClearOfArc = 1.5f;
    /// <summary>Distance from the pivot, across the sweep (along the axis), that still counts as in this blade's lane.</summary>
    const float CloseShaveLaneHalfWidth = 2.5f;

    readonly Dictionary<PlayerHealth, sbyte[]> _closeShaveSides = new();

    void UpdateCloseShaveCrossings(bool swinging)
    {
        if (!swinging || pivots == null || pivots.Length == 0)
        {
            _closeShaveSides.Clear();
            return;
        }

        Vector3 axisLocal = localSwingAxis.sqrMagnitude > 1e-6f ? localSwingAxis.normalized : Vector3.right;
        float reach = Mathf.Abs(bladeHitLocalCenter.z) + bladeHitHalfExtents.z + CloseShaveClearOfArc;
        var players = PlayerHealthRegistry.All;
        for (int p = 0; p < players.Count; p++)
        {
            PlayerHealth player = players[p];
            if (player == null || player.IsDead)
            {
                _closeShaveSides.Remove(player);
                continue;
            }

            if (!_closeShaveSides.TryGetValue(player, out sbyte[] sides) || sides.Length != pivots.Length)
            {
                sides = new sbyte[pivots.Length];
                _closeShaveSides[player] = sides;
            }

            Vector3 feet = player.transform.position;
            for (int i = 0; i < pivots.Length; i++)
            {
                Transform pivot = pivots[i];
                if (pivot == null)
                    continue;

                // Rotating about the axis leaves the axis itself unchanged, so both directions are fixed.
                Vector3 axis = pivot.TransformDirection(axisLocal);
                Vector3 sweep = Vector3.Cross(axis, Vector3.up);
                if (sweep.sqrMagnitude < 1e-6f)
                    continue;
                sweep.Normalize();

                Vector3 toPlayer = feet - pivot.position;
                float along = Vector3.Dot(toPlayer, sweep);
                if (Mathf.Abs(Vector3.Dot(toPlayer, axis)) > CloseShaveLaneHalfWidth || Mathf.Abs(along) > reach)
                {
                    sides[i] = 0;
                    continue;
                }

                if (Mathf.Abs(along) < CloseShaveClearOfArc)
                    continue;

                sbyte side = (sbyte)(along > 0f ? 1 : -1);
                if (sides[i] != 0 && sides[i] != side)
                    AchievementAwards.ServerAward(player, AchievementId.CloseShave);
                sides[i] = side;
            }
        }
    }

    void NoteCloseShaveHit(PlayerHealth player)
    {
        if (player != null && _closeShaveSides.TryGetValue(player, out sbyte[] sides))
            System.Array.Clear(sides, 0, sides.Length);
    }
}
