using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Decides when the run is lost: death is permanent for the section (see <see cref="NetworkPlayerRespawn"/>),
/// so once no connected player can still act there is nobody left to reach the exit elevator, which only ever
/// waits for the living. The server then tells every peer to run <see cref="TeamWipeSequence"/>, which ends the
/// session and returns everyone to the main menu — the same exit as finishing the last section.
///
/// A living player locked in a jail cell with no way out (<see cref="JailCellSealedReleaseZone.IsJailedWithNoWayOut"/>)
/// counts as out, the same as a dead one: only someone outside the cell could free them.
///
/// Evaluated on the events that can take the count of players who can still act to zero: a player dying, a
/// player disconnecting, and a prisoner running out of ways out of their cell.
/// </summary>
public static class TeamWipeDirector
{
    /// <summary>
    /// Server-only. <paramref name="leavingClientId"/> is a client mid-disconnect whose player object may not
    /// be torn down yet; it is never counted, alive or dead.
    /// </summary>
    public static void ServerEvaluate(ulong? leavingClientId = null)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer || nm.ShutdownInProgress)
            return;
        if (!IsInMazeGameplayScene() || TeamWipeSequence.IsRunning)
            return;

        // The group already escaped: the cab doors are shutting (or shut) on whoever made it aboard, and the
        // section switch about to happen brings every dead player back. A death in that last second is not a
        // wipe — racing the load to the main menu would throw a finished section away.
        if (ElevatorFinishController.ServerAnyCabDeparting())
            return;

        NetworkPlayerRespawn carrier = null;
        int counted = 0;
        bool anyJailed = false;
        foreach (KeyValuePair<ulong, NetworkClient> pair in nm.ConnectedClients)
        {
            if (leavingClientId.HasValue && pair.Key == leavingClientId.Value)
                continue;

            NetworkObject playerObject = pair.Value != null ? pair.Value.PlayerObject : null;
            // Connected but not spawned yet (a late joiner mid-spawn): they arrive alive at the level start.
            if (playerObject == null || !playerObject.IsSpawned)
                return;
            if (!playerObject.TryGetComponent(out PlayerHealth health))
                continue;
            if (!IsOutOfPlay(health, ref anyJailed))
                return;

            counted++;
            if (carrier == null)
                playerObject.TryGetComponent(out carrier);
        }

        if (counted == 0)
            return;

        bool everyoneDead = !anyJailed;
        if (carrier != null)
            carrier.ServerBroadcastTeamWipe(everyoneDead);
        else
            TeamWipeSequence.Begin(everyoneDead);
    }

    /// <summary>A level scene played with no session at all: the only player there just died or is jailed for good.</summary>
    public static void EvaluateWithoutSession()
    {
        if (!IsInMazeGameplayScene() || TeamWipeSequence.IsRunning)
            return;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        int counted = 0;
        bool anyJailed = false;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;
            if (!IsOutOfPlay(player, ref anyJailed))
                return;
            counted++;
        }

        if (counted > 0)
            TeamWipeSequence.Begin(!anyJailed);
    }

    /// <summary>The server's check in a session, the local one without; for callers that run in both.</summary>
    public static void EvaluateAsAuthority()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            ServerEvaluate();
        else
            EvaluateWithoutSession();
    }

    static bool IsOutOfPlay(PlayerHealth health, ref bool anyJailed)
    {
        if (health.IsDead)
            return true;
        if (!JailCellSealedReleaseZone.IsJailedWithNoWayOut(health))
            return false;

        anyJailed = true;
        return true;
    }

    static bool IsInMazeGameplayScene()
    {
        Scene active = SceneManager.GetActiveScene();
        return active.IsValid() && MultiplayerSceneFlow.IsMazeGameplayScene(active.name);
    }
}
