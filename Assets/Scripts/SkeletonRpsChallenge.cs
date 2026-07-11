using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public enum SkeletonRpsChoice : byte
{
    Rock = 0,
    Paper = 1,
    Scissors = 2,
}

public enum SkeletonRpsRejectReason : byte
{
    None = 0,
    Unavailable = 1,
    /// <summary>This player already used their one game against this skeleton.</summary>
    AlreadyPlayed = 2,
    /// <summary>The cell is not sealed (door unlocked/open), so there is nothing to win.</summary>
    DoorNotLocked = 3,
    OutOfRange = 4,
}

/// <summary>Outcome of one submitted throw, returned by the authority to the throwing player.</summary>
public struct SkeletonRpsThrowResult : INetworkSerializable
{
    public bool Accepted;
    public byte RejectReason;
    public byte PlayerChoice;
    public byte SkeletonChoice;
    public byte PlayerRoundWins;
    public byte SkeletonRoundWins;
    public bool RoundWasTie;
    public bool PlayerWonRound;
    public bool MatchOver;
    public bool PlayerWonMatch;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Accepted);
        serializer.SerializeValue(ref RejectReason);
        serializer.SerializeValue(ref PlayerChoice);
        serializer.SerializeValue(ref SkeletonChoice);
        serializer.SerializeValue(ref PlayerRoundWins);
        serializer.SerializeValue(ref SkeletonRoundWins);
        serializer.SerializeValue(ref RoundWasTie);
        serializer.SerializeValue(ref PlayerWonRound);
        serializer.SerializeValue(ref MatchOver);
        serializer.SerializeValue(ref PlayerWonMatch);
    }
}

/// <summary>
/// Rock-paper-scissors against the chained skeleton prop in the jail cell. Best of three (first to two
/// round wins, ties replay): winning unlocks and swings open the jail door exactly like a key would
/// (sealed occupants are released through <see cref="HingeInteractDoor.OnJailUnlockedByPlayerKey"/>);
/// losing leaves the cell sealed. Each player gets ONE match per skeleton — the chance is consumed by
/// the first accepted throw, so walking away mid-game forfeits it (an unfinished match can be resumed
/// while the cell is still sealed). The skeleton only bargains while the door is key-locked.
///
/// Like the jail door itself, this component is built locally on every peer by the deterministic maze
/// build and is NOT Netcode-spawned. The server runs the authoritative match state on its own instance;
/// clients reach it through <see cref="NetworkPlayerInventory.RequestSkeletonRpsThrow"/> keyed by the
/// jail door's <see cref="HingeInteractDoor.DoorId"/> (resolution mirrors DoorNetworkStateStore).
/// </summary>
[DisallowMultipleComponent]
public class SkeletonRpsChallenge : MonoBehaviour
{
    public const int RoundWinsToTakeMatch = 2;

    [Tooltip("The key-locked jail door this skeleton guards. If empty, the nearest HingeInteractDoor in the jail piece is used.")]
    [SerializeField] HingeInteractDoor jailDoor;
    [Tooltip("How close the player must stand to challenge the skeleton (and keep the match open).")]
    [SerializeField] float interactMaxDistance = 3.4f;
    [Tooltip("Server: seconds between the winning reveal and the cell door swinging open, so the banner lands first.")]
    [SerializeField] float winDoorOpenDelaySeconds = 1.15f;

    [Header("Audio")]
    [Tooltip("Bone rattle played at the skeleton when a round is revealed.")]
    [SerializeField] AudioClip roundRevealClip;
    [Tooltip("Alternate rattle so back-to-back reveals don't repeat identically.")]
    [SerializeField] AudioClip roundRevealAltClip;
    [Tooltip("Dull bone knock played when the skeleton takes the match.")]
    [SerializeField] AudioClip matchLossClip;
    [SerializeField, Range(0f, 1f)] float sfxVolume = 0.85f;

    enum LocalMatchProgress : byte
    {
        None,
        InProgress,
        Concluded,
    }

    struct ServerMatchState
    {
        public int PlayerWins;
        public int SkeletonWins;
    }

    static readonly Dictionary<ulong, SkeletonRpsChallenge> s_registered = new();

    /// <summary>Authority-only (server or offline): per-player match scores, keyed by clientId (0 offline).</summary>
    readonly Dictionary<ulong, ServerMatchState> _serverMatches = new();
    /// <summary>Authority-only: players whose one chance is consumed (added at their first accepted throw).</summary>
    readonly HashSet<ulong> _serverPlayed = new();

    LocalMatchProgress _localProgress;
    int _localKnownPlayerWins;
    int _localKnownSkeletonWins;
    bool _registered;
    ulong _registeredId;
    Vector3 _anchorPosition;
    bool _anchorCached;
    bool _revealSfxAlternate;
    AudioSource _sfx;
    Coroutine _winOpenRoutine;

    public HingeInteractDoor JailDoor => jailDoor;
    public ulong ChallengeId => jailDoor != null ? jailDoor.DoorId : 0UL;
    public float InteractMaxDistance => interactMaxDistance;
    /// <summary>Local player already finished (or was refused) their one game against this skeleton.</summary>
    public bool LocalPlayerConcluded => _localProgress == LocalMatchProgress.Concluded;
    /// <summary>Local player started a match that has not concluded (used to resume after an interruption).</summary>
    public bool LocalPlayerHasUnfinishedMatch => _localProgress == LocalMatchProgress.InProgress;
    public int LocalKnownPlayerWins => _localKnownPlayerWins;
    public int LocalKnownSkeletonWins => _localKnownSkeletonWins;

    /// <summary>Centre of the prop's renderers — the skeleton root transform sits off to one side of the bones.</summary>
    public Vector3 AnchorPosition
    {
        get
        {
            if (!_anchorCached)
            {
                _anchorPosition = transform.position;
                Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
                if (renderers != null && renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        bounds.Encapsulate(renderers[i].bounds);
                    _anchorPosition = bounds.center;
                }

                _anchorCached = true;
            }

            return _anchorPosition;
        }
    }

    void Awake()
    {
        if (jailDoor == null)
            jailDoor = JailorAI.FindNearestHingeDoorInLocalPrefabHierarchy(transform, transform.position);

        if (jailDoor == null)
        {
            Debug.LogWarning(
                $"{nameof(SkeletonRpsChallenge)} on '{name}' found no {nameof(HingeInteractDoor)} in its jail piece; the skeleton will not offer a game.",
                this);
        }
    }

    void OnEnable()
    {
        if (jailDoor == null)
            return;

        _registeredId = ChallengeId;
        s_registered[_registeredId] = this;
        _registered = true;
    }

    void OnDisable()
    {
        if (_registered
            && s_registered.TryGetValue(_registeredId, out SkeletonRpsChallenge existing)
            && existing == this)
        {
            s_registered.Remove(_registeredId);
        }

        _registered = false;
    }

    /// <summary>Resolve a challenge from a replicated id + position hint (mirrors <see cref="HingeInteractDoor.TryResolveForSync"/>).</summary>
    public static bool TryResolve(ulong challengeId, Vector3 hintPosition, out SkeletonRpsChallenge challenge)
    {
        if (s_registered.TryGetValue(challengeId, out challenge) && challenge != null)
            return true;

        const float maxMatchDistance = 8f;
        challenge = null;
        float bestSqr = maxMatchDistance * maxMatchDistance;
        foreach (SkeletonRpsChallenge candidate in s_registered.Values)
        {
            if (candidate == null)
                continue;
            float sqr = (candidate.AnchorPosition - hintPosition).sqrMagnitude;
            if (sqr > bestSqr)
                continue;
            bestSqr = sqr;
            challenge = candidate;
        }

        return challenge != null;
    }

    public bool IsInInteractRange(Vector3 worldPosition, float extraSlack = 0f)
    {
        float max = interactMaxDistance + Mathf.Max(0f, extraSlack);
        return (AnchorPosition - worldPosition).sqrMagnitude <= max * max;
    }

    static bool IsAuthority()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;
        return nm.IsServer;
    }

    // ---- Offering / interact entry (local player) ----------------------------------------------

    /// <summary>The skeleton only plays while the cell is sealed and this player still has (or is mid-way through) their one game.</summary>
    public bool CanOfferChallenge(Vector3 viewerPosition)
    {
        if (!isActiveAndEnabled || jailDoor == null)
            return false;
        if (!jailDoor.IsLocked)
            return false;
        if (_localProgress == LocalMatchProgress.Concluded)
            return false;
        if (SkeletonRpsOverlayController.IsInteractive)
            return false;
        return IsInInteractRange(viewerPosition);
    }

    public void RequestChallengeInteract(PlayerController player)
    {
        if (player == null || !CanOfferChallenge(player.transform.position))
            return;

        SkeletonRpsOverlayController.Show(player, this);
    }

    // ---- Throw submission (local player) --------------------------------------------------------

    public void SubmitLocalThrow(PlayerController player, SkeletonRpsChoice choice)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            // Single player: this peer is the authority; resolve synchronously.
            ServerProcessThrow(0UL, choice, out SkeletonRpsThrowResult result);
            NotifyThrowResolved(result);
            return;
        }

        NetworkPlayerInventory inventory = player != null ? player.GetComponent<NetworkPlayerInventory>() : null;
        if (inventory != null)
            inventory.RequestSkeletonRpsThrow(this, choice);
    }

    // ---- Authoritative match core (server or offline) -------------------------------------------

    /// <summary>
    /// Server / offline only. Validates, rolls the skeleton's throw, and advances this player's match.
    /// The player's single chance is consumed by their first accepted throw. A match interrupted because the
    /// cell got unlocked some other way (teammate key) is refunded. On a match win the door open is scheduled.
    /// </summary>
    public void ServerProcessThrow(ulong clientKey, SkeletonRpsChoice playerChoice, out SkeletonRpsThrowResult result)
    {
        result = new SkeletonRpsThrowResult { PlayerChoice = (byte)playerChoice };

        if (!IsAuthority() || jailDoor == null)
        {
            result.RejectReason = (byte)SkeletonRpsRejectReason.Unavailable;
            return;
        }

        if (!jailDoor.IsLocked)
        {
            // The cell opened mid-match (key unlock) — the game is moot. Refund the interrupted chance.
            if (_serverMatches.Remove(clientKey))
                _serverPlayed.Remove(clientKey);
            result.RejectReason = (byte)SkeletonRpsRejectReason.DoorNotLocked;
            return;
        }

        if (!_serverMatches.TryGetValue(clientKey, out ServerMatchState match))
        {
            if (_serverPlayed.Contains(clientKey))
            {
                result.RejectReason = (byte)SkeletonRpsRejectReason.AlreadyPlayed;
                return;
            }

            match = default;
            _serverPlayed.Add(clientKey);
        }

        SkeletonRpsChoice skeletonChoice = (SkeletonRpsChoice)Random.Range(0, 3);
        bool tie = skeletonChoice == playerChoice;
        bool playerWonRound = !tie && PlayerBeats(playerChoice, skeletonChoice);
        if (playerWonRound)
            match.PlayerWins++;
        else if (!tie)
            match.SkeletonWins++;

        bool matchOver = match.PlayerWins >= RoundWinsToTakeMatch || match.SkeletonWins >= RoundWinsToTakeMatch;
        bool playerWonMatch = matchOver && match.PlayerWins >= RoundWinsToTakeMatch;

        if (matchOver)
            _serverMatches.Remove(clientKey);
        else
            _serverMatches[clientKey] = match;

        result.Accepted = true;
        result.SkeletonChoice = (byte)skeletonChoice;
        result.PlayerRoundWins = (byte)match.PlayerWins;
        result.SkeletonRoundWins = (byte)match.SkeletonWins;
        result.RoundWasTie = tie;
        result.PlayerWonRound = playerWonRound;
        result.MatchOver = matchOver;
        result.PlayerWonMatch = playerWonMatch;

        if (playerWonMatch)
            ScheduleWinDoorOpen();
    }

    static bool PlayerBeats(SkeletonRpsChoice player, SkeletonRpsChoice skeleton)
    {
        return (player == SkeletonRpsChoice.Rock && skeleton == SkeletonRpsChoice.Scissors)
            || (player == SkeletonRpsChoice.Paper && skeleton == SkeletonRpsChoice.Rock)
            || (player == SkeletonRpsChoice.Scissors && skeleton == SkeletonRpsChoice.Paper);
    }

    void ScheduleWinDoorOpen()
    {
        if (_winOpenRoutine != null)
            return;
        _winOpenRoutine = StartCoroutine(WinOpenRoutine());
    }

    IEnumerator WinOpenRoutine()
    {
        float wait = Mathf.Max(0f, winDoorOpenDelaySeconds);
        if (wait > 0f)
            yield return new WaitForSecondsRealtime(wait);

        OpenJailDoorAsAuthority();
        _winOpenRoutine = null;
    }

    /// <summary>
    /// Server / offline: unlock exactly like a player key (fires <see cref="HingeInteractDoor.OnJailUnlockedByPlayerKey"/>
    /// so sealed occupants are released), then swing the door open. Uses the same spawned / unspawned-procedural /
    /// offline branches as the key-unlock paths in <see cref="NetworkPlayerInventory"/>.
    /// </summary>
    void OpenJailDoorAsAuthority()
    {
        if (jailDoor == null || !IsAuthority())
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool online = nm != null && nm.IsListening;

        if (online && jailDoor.IsSpawned)
        {
            if (jailDoor.IsLocked)
                jailDoor.ServerUnlockFromKey();
            // Force-open (ignores the post-unlock delay) and publishes the procedural mirror for clients
            // whose local door instance is not spawned.
            jailDoor.ServerJailorOpenForEntry();
            return;
        }

        if (online)
        {
            if (jailDoor.IsLocked)
                jailDoor.ApplyProceduralRemoteUnlock();
            if (!jailDoor.IsOpen)
                jailDoor.ApplyProceduralRemoteOpenState(true);
            NetworkPlayerInventory.ServerBroadcastProceduralDoorUnlockIfNeeded(jailDoor);
            return;
        }

        if (jailDoor.IsLocked)
            jailDoor.ApplyLocalUnlock();
        if (!jailDoor.IsOpen)
            jailDoor.ApplyProceduralRemoteOpenState(true);
    }

    // ---- Local-player result application ---------------------------------------------------------

    /// <summary>
    /// Runs on the throwing player's machine (directly offline / on the host, via the inventory ClientRpc on
    /// clients). Updates the local one-chance bookkeeping and forwards the result to the overlay.
    /// </summary>
    public void NotifyThrowResolved(SkeletonRpsThrowResult result)
    {
        if (result.Accepted)
        {
            _localKnownPlayerWins = result.PlayerRoundWins;
            _localKnownSkeletonWins = result.SkeletonRoundWins;
            _localProgress = result.MatchOver ? LocalMatchProgress.Concluded : LocalMatchProgress.InProgress;
        }
        else
        {
            switch ((SkeletonRpsRejectReason)result.RejectReason)
            {
                case SkeletonRpsRejectReason.AlreadyPlayed:
                    _localProgress = LocalMatchProgress.Concluded;
                    break;
                case SkeletonRpsRejectReason.DoorNotLocked:
                    // Interrupted match was refunded by the authority.
                    _localProgress = LocalMatchProgress.None;
                    _localKnownPlayerWins = 0;
                    _localKnownSkeletonWins = 0;
                    break;
            }
        }

        SkeletonRpsOverlayController.NotifyThrowResult(this, result);
    }

    // ---- Audio -----------------------------------------------------------------------------------

    public void PlayRoundRevealSfx()
    {
        AudioClip clip = _revealSfxAlternate && roundRevealAltClip != null ? roundRevealAltClip : roundRevealClip;
        _revealSfxAlternate = !_revealSfxAlternate;
        PlayClip(clip);
    }

    public void PlayMatchLossSfx()
    {
        PlayClip(matchLossClip != null ? matchLossClip : roundRevealClip);
    }

    void PlayClip(AudioClip clip)
    {
        if (clip == null)
            return;

        EnsureSfxSource();
        if (_sfx == null)
            return;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
        _sfx.PlayOneShot(clip, Mathf.Max(0f, sfxVolume));
    }

    void EnsureSfxSource()
    {
        if (_sfx != null)
            return;
        _sfx = GetComponent<AudioSource>();
        if (_sfx == null)
            _sfx = gameObject.AddComponent<AudioSource>();
        _sfx.playOnAwake = false;
        _sfx.loop = false;
        _sfx.spatialBlend = 1f;
        _sfx.minDistance = 0.8f;
        _sfx.maxDistance = 18f;
        _sfx.rolloffMode = AudioRolloffMode.Linear;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (roundRevealClip == null)
            roundRevealClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonFootstep1.wav");
        if (roundRevealAltClip == null)
            roundRevealAltClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonFootstep2.wav");
        if (matchLossClip == null)
            matchLossClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonHit.wav");
    }
#endif
}
