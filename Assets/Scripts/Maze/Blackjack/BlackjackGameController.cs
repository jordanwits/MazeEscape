using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked controller for a single multi-seat blackjack table. Server-authoritative: the server owns the shoe,
/// the dealer's hidden hole card, and the round state machine; clients read replicated <see cref="SeatState"/> /
/// <see cref="DealerState"/> for rendering and the control overlay, and submit actions (sit/bet/ready/hit/stand)
/// through a single validated ServerRpc.
/// <para>
/// Round flow: players sit (<see cref="BlackjackSeat"/>), adjust bets and ready up during <see cref="BlackjackPhase.Betting"/>;
/// once at least one seat is ready (and the betting window elapses, or everyone is ready) the server stakes each bet
/// (<see cref="NetworkPlayerCarnivalTickets.ServerTrySpend"/>), deals, runs player turns in seat order, plays the
/// dealer (hits to 17, stands on all 17), then credits winnings (<see cref="NetworkPlayerCarnivalTickets.ServerAdd"/>)
/// at real-casino payouts (blackjack 3:2, win 1:1, push returns the stake).
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class BlackjackGameController : NetworkBehaviour, ICarnivalScoreSource
{
    enum SeatAction : byte { Sit, Leave, BetDown, BetUp, Ready, Unready, Hit, Stand }

    [Header("Seats")]
    [SerializeField, Range(1, BlackjackConfig.SeatCount)]
    int activeSeatCount = BlackjackConfig.SeatCount;

    [Header("View")]
    [Tooltip("Fallback camera pose while seated (angled top-down over the felt) if no per-seat anchor is set.")]
    [SerializeField] Transform tableCameraAnchor;
    [Tooltip("Per-seat camera poses, zoomed in on that seat's cards + the dealer. Index = seat index.")]
    [SerializeField] Transform[] seatCameraAnchors = new Transform[BlackjackConfig.SeatCount];
    public Transform TableCameraAnchor => tableCameraAnchor;

    [Header("Seating (sit-down pose)")]
    [Tooltip("Per-seat anchor placed at the top-center of each stool. The seated player is positioned relative to " +
        "this (see seatSitOffset), so it stays correct even if the table is scaled. Index = seat index.")]
    [SerializeField] Transform[] seatSitAnchors = new Transform[BlackjackConfig.SeatCount];
    [Tooltip("Player-root offset from the stool-top anchor, in the seated player's local space (x=right, y=up, " +
        "z=forward/toward dealer). y lowers the feet-origin so the seated hips rest on the seat; z nudges the body " +
        "so the buttocks land on the seat center. Applied in WORLD via the anchor's yaw, so it is NOT scaled by the table.")]
    [SerializeField] Vector3 seatSitOffset = new Vector3(0f, -0.45f, 0.11f);

    /// <summary>Camera pose for the given seat (per-seat if authored, else the shared table anchor).</summary>
    public Transform GetSeatCameraAnchor(int seatIndex)
    {
        if (seatCameraAnchors != null && seatIndex >= 0 && seatIndex < seatCameraAnchors.Length && seatCameraAnchors[seatIndex] != null)
            return seatCameraAnchors[seatIndex];
        return tableCameraAnchor;
    }

    /// <summary>Stool-top anchor for the given seat (or null if none authored).</summary>
    public Transform GetSeatSitAnchor(int seatIndex)
    {
        if (seatSitAnchors != null && seatIndex >= 0 && seatIndex < seatSitAnchors.Length)
            return seatSitAnchors[seatIndex];
        return null;
    }

    /// <summary>World pose to place a seated player's root so the avatar sits naturally on that seat's stool.</summary>
    public bool TryGetSeatSitPose(int seatIndex, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        Transform a = GetSeatSitAnchor(seatIndex);
        if (a == null)
            return false;
        rotation = Quaternion.Euler(0f, a.eulerAngles.y, 0f); // yaw only — player faces the dealer
        position = a.position + rotation * seatSitOffset;
        return true;
    }

    [Header("Timing (seconds)")]
    [SerializeField, Min(2f)] float bettingWindowSeconds = 12f;
    [SerializeField, Min(3f)] float turnTimeoutSeconds = 15f;
    [SerializeField, Min(0.1f)] float dealerDrawInterval = 0.8f;
    [SerializeField, Min(1f)] float resultHoldSeconds = 5f;

    [Header("Anti-cheat range gate (vs table root)")]
    [SerializeField, Min(1f)] float maxInteractHorizontal = 9f;
    [SerializeField, Min(1f)] float maxInteractVertical = 3f;

    [Header("Shoe")]
    [Tooltip("Reshuffle the shoe at the start of a round when fewer than this many cards remain.")]
    [SerializeField, Min(20)] int reshuffleRemainingThreshold = 78;

    // --- Replicated state (server writes only) ---
    readonly NetworkVariable<BlackjackPhase> _phase = new(
        BlackjackPhase.Idle, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<int> _actingSeatIndex = new(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _phaseTimer = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<DealerState> _dealer = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SeatState> _seat0 = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SeatState> _seat1 = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SeatState> _seat2 = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<SeatState> _seat3 = new(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- Server-only state ---
    readonly List<byte> _shoe = new(BlackjackConfig.DeckCount * BlackjackCard.DeckSize);
    int _shoeCursor;
    System.Random _rng;
    readonly List<byte> _dealerCards = new(16); // full dealer hand incl. hole (server truth)

    readonly byte[] _evalBuf = new byte[32];

    /// <summary>Fired on every peer whenever any replicated state changes; the view + overlay rebuild from it.</summary>
    public event Action StateChanged;

    /// <summary>
    /// Fired on every peer (host + clients) when a player is dealt a natural blackjack — drives the dealer-bot
    /// spin/stinger celebration (<see cref="BlackjackDealerSpin"/>). Server-raised via a ClientRpc.
    /// </summary>
    public event Action BlackjackCelebrated;

    public int ActiveSeatCount => Mathf.Clamp(activeSeatCount, 1, BlackjackConfig.SeatCount);
    public BlackjackPhase Phase => _phase.Value;
    public int ActingSeatIndex => _actingSeatIndex.Value;
    public float PhaseTimer => _phaseTimer.Value;
    public DealerState Dealer => _dealer.Value;

    // ICarnivalScoreSource (keeps an optional world-space CarnivalWorldNumberDisplay countdown working).
    public bool IsActive => _phase.Value >= BlackjackPhase.Dealing && _phase.Value <= BlackjackPhase.Payout;
    public int Score => 0;
    public int LastFinishedScore => 0;
    public float TimeRemaining =>
        (_phase.Value == BlackjackPhase.Betting || _phase.Value == BlackjackPhase.PlayerTurns) ? _phaseTimer.Value : 0f;

    public SeatState GetSeat(int i) => i switch
    {
        0 => _seat0.Value,
        1 => _seat1.Value,
        2 => _seat2.Value,
        3 => _seat3.Value,
        _ => default,
    };

    void SetSeat(int i, SeatState s)
    {
        switch (i)
        {
            case 0: _seat0.Value = s; break;
            case 1: _seat1.Value = s; break;
            case 2: _seat2.Value = s; break;
            case 3: _seat3.Value = s; break;
        }
    }

    public bool IsSeatEmpty(int i) => i >= 0 && i < ActiveSeatCount && !GetSeat(i).IsOccupied;

    public int SeatIndexOfOccupant(ulong netObjId)
    {
        if (netObjId == 0UL)
            return -1;
        for (int i = 0; i < ActiveSeatCount; i++)
            if (GetSeat(i).OccupantNetObjId == netObjId)
                return i;
        return -1;
    }

    /// <summary>Total of a seat's (fully visible) hand for the overlay.</summary>
    public int SeatTotal(int seatIndex, out bool isSoft, out bool isBlackjack)
    {
        SeatState s = GetSeat(seatIndex);
        return EvalTotal(s.Cards, out isSoft, out isBlackjack);
    }

    /// <summary>Total of the dealer's currently visible cards (up-card only during player turns).</summary>
    public int DealerVisibleTotal()
    {
        DealerState d = _dealer.Value;
        return EvalTotal(d.Cards, out _, out _);
    }

    int EvalTotal(FixedList32Bytes<byte> cards, out bool isSoft, out bool isBlackjack)
    {
        int n = Mathf.Min(cards.Length, _evalBuf.Length);
        for (int i = 0; i < n; i++)
            _evalBuf[i] = cards[i];
        BlackjackCard.Evaluate(_evalBuf, n, out int total, out isSoft, out isBlackjack);
        return total;
    }

    public override void OnNetworkSpawn()
    {
        _phase.OnValueChanged += OnPhaseChanged;
        _actingSeatIndex.OnValueChanged += OnIntChanged;
        _phaseTimer.OnValueChanged += OnFloatChanged;
        _dealer.OnValueChanged += OnDealerChanged;
        _seat0.OnValueChanged += OnSeatChanged;
        _seat1.OnValueChanged += OnSeatChanged;
        _seat2.OnValueChanged += OnSeatChanged;
        _seat3.OnValueChanged += OnSeatChanged;

        if (IsServer)
        {
            _rng = new System.Random();
            _phase.Value = BlackjackPhase.Idle;
            _actingSeatIndex.Value = -1;
        }

        StateChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        _phase.OnValueChanged -= OnPhaseChanged;
        _actingSeatIndex.OnValueChanged -= OnIntChanged;
        _phaseTimer.OnValueChanged -= OnFloatChanged;
        _dealer.OnValueChanged -= OnDealerChanged;
        _seat0.OnValueChanged -= OnSeatChanged;
        _seat1.OnValueChanged -= OnSeatChanged;
        _seat2.OnValueChanged -= OnSeatChanged;
        _seat3.OnValueChanged -= OnSeatChanged;
    }

    void OnPhaseChanged(BlackjackPhase _, BlackjackPhase __) => StateChanged?.Invoke();
    void OnIntChanged(int _, int __) => StateChanged?.Invoke();
    // Phase timer changes every tick; don't spam StateChanged for it (the overlay reads PhaseTimer live).
    void OnFloatChanged(float _, float __) { }
    void OnDealerChanged(DealerState _, DealerState __) => StateChanged?.Invoke();
    void OnSeatChanged(SeatState _, SeatState __) => StateChanged?.Invoke();

    // =========================================================================================
    // Public action entry points (called on the LOCAL player by BlackjackSeat / the overlay).
    // =========================================================================================

    public void RequestSit(PlayerController interactor, int seatIndex) => Submit(interactor, SeatAction.Sit, seatIndex);
    public void RequestLeave(PlayerController interactor) => Submit(interactor, SeatAction.Leave, 0);
    public void RequestAdjustBet(PlayerController interactor, int delta) =>
        Submit(interactor, delta < 0 ? SeatAction.BetDown : SeatAction.BetUp, 0);
    public void RequestReady(PlayerController interactor, bool ready) =>
        Submit(interactor, ready ? SeatAction.Ready : SeatAction.Unready, 0);
    public void RequestHit(PlayerController interactor) => Submit(interactor, SeatAction.Hit, 0);
    public void RequestStand(PlayerController interactor) => Submit(interactor, SeatAction.Stand, 0);

    void Submit(PlayerController interactor, SeatAction action, int arg)
    {
        if (interactor == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !IsSpawned)
            return;
        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
            return;

        if (nm.IsServer)
            ServerHandleAction(action, arg, playerNet.NetworkObjectId, playerNet.OwnerClientId);
        else
            SubmitActionServerRpc((byte)action, arg, playerNet.NetworkObjectId);
    }

    [ServerRpc(RequireOwnership = false)]
    void SubmitActionServerRpc(byte action, int arg, ulong playerNetObjId, ServerRpcParams rpcParams = default)
    {
        ServerHandleAction((SeatAction)action, arg, playerNetObjId, rpcParams.Receive.SenderClientId);
    }

    [ClientRpc]
    void BlackjackCelebrationClientRpc()
    {
        BlackjackCelebrated?.Invoke();
    }

    void ServerHandleAction(SeatAction action, int arg, ulong playerNetObjId, ulong expectedOwnerClientId)
    {
        if (!IsServer)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetObjId, out NetworkObject po)
            || po == null)
            return;
        if (po.OwnerClientId != expectedOwnerClientId)
            return;
        // Leave is always allowed regardless of range. A player killed while seated respawns at level start
        // (far from the table) but the overlay stays up and freezes their movement until the seat is freed; if
        // the range gate rejected their Leave they'd be stranded. Every other action still requires proximity.
        if (action != SeatAction.Leave && !WithinInteractRange(po.transform.position))
            return;

        switch (action)
        {
            case SeatAction.Sit: ServerSit(po, arg); break;
            case SeatAction.Leave: ServerLeave(playerNetObjId); break;
            case SeatAction.BetDown: ServerAdjustBet(playerNetObjId, -BlackjackConfig.BetStep); break;
            case SeatAction.BetUp: ServerAdjustBet(playerNetObjId, BlackjackConfig.BetStep); break;
            case SeatAction.Ready: ServerSetReady(playerNetObjId, true); break;
            case SeatAction.Unready: ServerSetReady(playerNetObjId, false); break;
            case SeatAction.Hit: ServerHit(playerNetObjId); break;
            case SeatAction.Stand: ServerStand(playerNetObjId); break;
        }
    }

    bool WithinInteractRange(Vector3 playerPos)
    {
        Vector3 here = transform.position;
        Vector3 flat = new(playerPos.x - here.x, 0f, playerPos.z - here.z);
        if (flat.sqrMagnitude > maxInteractHorizontal * maxInteractHorizontal)
            return false;
        return Mathf.Abs(playerPos.y - here.y) <= maxInteractVertical;
    }

    // =========================================================================================
    // Server action handlers
    // =========================================================================================

    void ServerSit(NetworkObject po, int seatIndex)
    {
        if (seatIndex < 0 || seatIndex >= ActiveSeatCount)
            return;
        ulong id = po.NetworkObjectId;
        if (SeatIndexOfOccupant(id) >= 0)
            return; // already seated somewhere
        SeatState s = GetSeat(seatIndex);
        if (s.IsOccupied)
            return;
        if (po.GetComponent<NetworkPlayerCarnivalTickets>() == null)
            return;

        s = default;
        s.OccupantNetObjId = id;
        s.Bet = BlackjackConfig.MinBet;
        s.Status = (byte)BlackjackHandStatus.Empty;
        s.LastResult = (byte)BlackjackSeatResult.None;
        SetSeat(seatIndex, s);

        if (_phase.Value == BlackjackPhase.Idle)
        {
            _phase.Value = BlackjackPhase.Betting;
            _phaseTimer.Value = 0f; // window starts when the first player readies
        }
    }

    void ServerLeave(ulong playerNetObjId)
    {
        int seat = SeatIndexOfOccupant(playerNetObjId);
        if (seat < 0)
            return;
        ServerVacateSeat(seat);
    }

    void ServerVacateSeat(int seat)
    {
        bool wasActing = _actingSeatIndex.Value == seat;
        // Mid-round (stake already taken, not yet resolved) => forfeit the stake, no refund.
        SetSeat(seat, default);

        if (wasActing && _phase.Value == BlackjackPhase.PlayerTurns)
            ServerAdvanceTurn();

        if (CountOccupied() == 0)
        {
            // Everyone left: abandon any in-progress round and reset to idle.
            ServerResetTable();
        }
    }

    void ServerAdjustBet(ulong playerNetObjId, int delta)
    {
        if (_phase.Value != BlackjackPhase.Betting)
            return;
        int seat = SeatIndexOfOccupant(playerNetObjId);
        if (seat < 0)
            return;
        SeatState s = GetSeat(seat);
        int balance = ServerBalanceOf(playerNetObjId);
        int maxBet = Mathf.Max(BlackjackConfig.MinBet, (balance / BlackjackConfig.BetStep) * BlackjackConfig.BetStep);
        int newBet = Mathf.Clamp(s.Bet + delta, BlackjackConfig.MinBet, maxBet);
        if (newBet == s.Bet)
            return;
        s.Bet = newBet;
        SetSeat(seat, s);
    }

    void ServerSetReady(ulong playerNetObjId, bool ready)
    {
        if (_phase.Value != BlackjackPhase.Betting)
            return;
        int seat = SeatIndexOfOccupant(playerNetObjId);
        if (seat < 0)
            return;
        SeatState s = GetSeat(seat);
        if (ready)
        {
            int balance = ServerBalanceOf(playerNetObjId);
            if (s.Bet < BlackjackConfig.MinBet || s.Bet > balance)
                return; // can't afford the staked bet
            s.IsReady = 1;
        }
        else
        {
            s.IsReady = 0;
        }
        SetSeat(seat, s);

        if (ready)
        {
            if (_phaseTimer.Value <= 0f)
                _phaseTimer.Value = bettingWindowSeconds; // open the window on first ready
            if (AllOccupiedReady())
                ServerBeginDealing();
        }
    }

    void ServerHit(ulong playerNetObjId)
    {
        if (_phase.Value != BlackjackPhase.PlayerTurns)
            return;
        int seat = SeatIndexOfOccupant(playerNetObjId);
        if (seat < 0 || seat != _actingSeatIndex.Value)
            return;
        SeatState s = GetSeat(seat);
        if (s.Status != (byte)BlackjackHandStatus.Playing)
            return;

        s.Cards.Add(ServerDraw());
        int total = EvalTotal(s.Cards, out _, out _);
        if (total > 21)
            s.Status = (byte)BlackjackHandStatus.Bust;
        else if (total == 21 || s.Cards.Length >= BlackjackConfig.MaxHandCards)
            s.Status = (byte)BlackjackHandStatus.Stand;
        SetSeat(seat, s);

        if (s.Status != (byte)BlackjackHandStatus.Playing)
            ServerAdvanceTurn();
        else
            _phaseTimer.Value = turnTimeoutSeconds; // reset AFK clock after a successful hit
    }

    void ServerStand(ulong playerNetObjId)
    {
        if (_phase.Value != BlackjackPhase.PlayerTurns)
            return;
        int seat = SeatIndexOfOccupant(playerNetObjId);
        if (seat < 0 || seat != _actingSeatIndex.Value)
            return;
        SeatState s = GetSeat(seat);
        if (s.Status != (byte)BlackjackHandStatus.Playing)
            return;
        s.Status = (byte)BlackjackHandStatus.Stand;
        SetSeat(seat, s);
        ServerAdvanceTurn();
    }

    // =========================================================================================
    // Server round flow
    // =========================================================================================

    void ServerBeginDealing()
    {
        _phase.Value = BlackjackPhase.Dealing;
        EnsureShoe();

        // Stake bets; only seats that can pay are dealt in.
        int inRoundCount = 0;
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            s.Cards.Clear();
            s.LastResult = (byte)BlackjackSeatResult.None;
            s.LastPayout = 0;
            if (s.IsOccupied && s.IsReady == 1 && ServerTrySpendBet(s.OccupantNetObjId, s.Bet))
            {
                s.InRound = 1;
                s.Status = (byte)BlackjackHandStatus.Playing;
                inRoundCount++;
            }
            else
            {
                s.InRound = 0;
                s.IsReady = 0;
                s.Status = (byte)BlackjackHandStatus.Empty;
            }
            SetSeat(i, s);
        }

        if (inRoundCount == 0)
        {
            // Nobody could pay (rare race): go back to waiting.
            _phase.Value = BlackjackPhase.Betting;
            _phaseTimer.Value = 0f;
            return;
        }

        // Deal: round 1 to each in-round seat, dealer up-card; round 2 to each seat, dealer hole card.
        for (int i = 0; i < ActiveSeatCount; i++)
            DealToSeatIfInRound(i);
        byte dealerUp = ServerDraw();
        for (int i = 0; i < ActiveSeatCount; i++)
            DealToSeatIfInRound(i);
        byte dealerHole = ServerDraw();

        _dealerCards.Clear();
        _dealerCards.Add(dealerUp);
        _dealerCards.Add(dealerHole);

        DealerState d = default;
        d.Cards.Clear();
        d.Cards.Add(dealerUp);
        d.HoleHidden = 1;
        d.Status = (byte)BlackjackHandStatus.Playing;
        _dealer.Value = d;

        // Flag player naturals.
        bool anyPlayerBlackjack = false;
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (s.InRound != 1)
                continue;
            EvalTotal(s.Cards, out _, out bool bj);
            if (bj)
            {
                s.Status = (byte)BlackjackHandStatus.Blackjack;
                SetSeat(i, s);
                anyPlayerBlackjack = true;
            }
        }

        // Celebrate a player natural on every peer (dealer head spins + stinger SFX).
        if (anyPlayerBlackjack)
            BlackjackCelebrationClientRpc();

        // Dealer peek on an Ace / ten-value up-card.
        bool dealerNatural = false;
        if (BlackjackCard.CardValue(dealerUp) >= 10)
        {
            for (int i = 0; i < _dealerCards.Count; i++)
                _evalBuf[i] = _dealerCards[i];
            BlackjackCard.Evaluate(_evalBuf, _dealerCards.Count, out int dt, out _, out bool dbj);
            dealerNatural = dbj && dt == 21;
        }

        if (dealerNatural)
        {
            ServerRevealDealerHole(BlackjackHandStatus.Blackjack);
            ServerResolveRound();
            return;
        }

        // Begin player turns at the first in-round, still-playing seat; if none (all naturals), go to dealer.
        _phase.Value = BlackjackPhase.PlayerTurns;
        _actingSeatIndex.Value = -1;
        _phaseTimer.Value = 0f;
        ServerAdvanceTurn();
    }

    void DealToSeatIfInRound(int i)
    {
        SeatState s = GetSeat(i);
        if (s.InRound != 1)
            return;
        if (s.Status != (byte)BlackjackHandStatus.Playing)
            return;
        s.Cards.Add(ServerDraw());
        SetSeat(i, s);
    }

    /// <summary>Move to the next in-round seat that may still act. If none remain, the dealer plays.</summary>
    void ServerAdvanceTurn()
    {
        int start = _actingSeatIndex.Value;
        for (int i = start + 1; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (s.InRound == 1 && s.Status == (byte)BlackjackHandStatus.Playing)
            {
                _actingSeatIndex.Value = i;
                _phaseTimer.Value = turnTimeoutSeconds;
                return;
            }
        }

        // No one left to act: dealer's turn.
        _actingSeatIndex.Value = -1;
        ServerBeginDealerTurn();
    }

    void ServerBeginDealerTurn()
    {
        _phase.Value = BlackjackPhase.DealerTurn;
        ServerRevealDealerHole(BlackjackHandStatus.Playing);
        _phaseTimer.Value = dealerDrawInterval;
    }

    void ServerTickDealer()
    {
        _phaseTimer.Value -= Time.fixedDeltaTime;
        if (_phaseTimer.Value > 0f)
            return;

        int dealerTotal = DealerServerTotal(out _);
        if (dealerTotal < 17)
        {
            _dealerCards.Add(ServerDraw());
            ServerSyncDealerRevealed(BlackjackHandStatus.Playing);
            _phaseTimer.Value = dealerDrawInterval;
            return;
        }

        BlackjackHandStatus dealerStatus = dealerTotal > 21 ? BlackjackHandStatus.Bust : BlackjackHandStatus.Stand;
        ServerSyncDealerRevealed(dealerStatus);
        ServerResolveRound();
    }

    void ServerResolveRound()
    {
        _phase.Value = BlackjackPhase.Resolve;

        int dealerTotal = DealerServerTotal(out int dealerCount);
        bool dealerBust = dealerTotal > 21;
        bool dealerBlackjack = dealerCount == 2 && dealerTotal == 21;

        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (s.InRound != 1)
                continue;

            int seatTotal = EvalTotal(s.Cards, out _, out _);
            bool seatBlackjack = s.Status == (byte)BlackjackHandStatus.Blackjack;
            bool seatBust = s.Status == (byte)BlackjackHandStatus.Bust || seatTotal > 21;

            BlackjackSeatResult result;
            int credit; // tickets returned to the player (0 if they lose)

            if (s.Status == (byte)BlackjackHandStatus.Forfeit)
            {
                result = BlackjackSeatResult.Forfeit;
                credit = 0;
            }
            else if (seatBust)
            {
                result = BlackjackSeatResult.Bust;
                credit = 0;
            }
            else if (seatBlackjack)
            {
                if (dealerBlackjack)
                {
                    result = BlackjackSeatResult.Push;
                    credit = s.Bet;
                }
                else
                {
                    result = BlackjackSeatResult.Blackjack;
                    credit = s.Bet + (s.Bet * 3) / 2; // 3:2, floored to whole tickets
                }
            }
            else if (dealerBlackjack)
            {
                result = BlackjackSeatResult.Lose;
                credit = 0;
            }
            else if (dealerBust || seatTotal > dealerTotal)
            {
                result = BlackjackSeatResult.Win;
                credit = s.Bet * 2;
            }
            else if (seatTotal == dealerTotal)
            {
                result = BlackjackSeatResult.Push;
                credit = s.Bet;
            }
            else
            {
                result = BlackjackSeatResult.Lose;
                credit = 0;
            }

            if (credit > 0)
                ServerCredit(s.OccupantNetObjId, credit);

            s.LastResult = (byte)result;
            s.LastPayout = credit - s.Bet;
            SetSeat(i, s);
        }

        _phase.Value = BlackjackPhase.Payout;
        _phaseTimer.Value = resultHoldSeconds;
    }

    void ServerEndPayout()
    {
        // Clear hands; keep occupants and their last bet for the next round.
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (!s.IsOccupied)
            {
                SetSeat(i, default);
                continue;
            }
            s.Cards.Clear();
            s.InRound = 0;
            s.IsReady = 0;
            s.Status = (byte)BlackjackHandStatus.Empty;
            // Keep s.Bet; clamp later when they re-ready. Keep LastResult/LastPayout so the overlay can show it.
            SetSeat(i, s);
        }

        _dealerCards.Clear();
        _dealer.Value = default;
        _actingSeatIndex.Value = -1;

        _phase.Value = CountOccupied() > 0 ? BlackjackPhase.Betting : BlackjackPhase.Idle;
        _phaseTimer.Value = 0f;
    }

    void ServerResetTable()
    {
        for (int i = 0; i < ActiveSeatCount; i++)
            SetSeat(i, default);
        _dealerCards.Clear();
        _dealer.Value = default;
        _actingSeatIndex.Value = -1;
        _phase.Value = BlackjackPhase.Idle;
        _phaseTimer.Value = 0f;
    }

    // =========================================================================================
    // Server tick
    // =========================================================================================

    void FixedUpdate()
    {
        if (!IsServer || !IsSpawned)
            return;

        ServerScanForLostOccupants();

        switch (_phase.Value)
        {
            case BlackjackPhase.Betting:
                ServerTickBetting();
                break;
            case BlackjackPhase.PlayerTurns:
                ServerTickPlayerTurn();
                break;
            case BlackjackPhase.DealerTurn:
                ServerTickDealer();
                break;
            case BlackjackPhase.Payout:
                _phaseTimer.Value -= Time.fixedDeltaTime;
                if (_phaseTimer.Value <= 0f)
                    ServerEndPayout();
                break;
        }
    }

    void ServerTickBetting()
    {
        if (CountOccupied() == 0)
        {
            ServerResetTable();
            return;
        }
        if (_phaseTimer.Value <= 0f)
            return; // window not open yet (no one ready)

        _phaseTimer.Value -= Time.fixedDeltaTime;
        if (_phaseTimer.Value <= 0f)
        {
            _phaseTimer.Value = 0f;
            if (AnyReady())
                ServerBeginDealing();
            // else: stay in Betting, window closed, wait for a fresh ready to reopen it.
        }
    }

    void ServerTickPlayerTurn()
    {
        int seat = _actingSeatIndex.Value;
        if (seat < 0 || seat >= ActiveSeatCount)
        {
            ServerAdvanceTurn();
            return;
        }
        SeatState s = GetSeat(seat);
        if (s.InRound != 1 || s.Status != (byte)BlackjackHandStatus.Playing)
        {
            ServerAdvanceTurn();
            return;
        }

        _phaseTimer.Value -= Time.fixedDeltaTime;
        if (_phaseTimer.Value <= 0f)
        {
            // AFK auto-stand.
            s.Status = (byte)BlackjackHandStatus.Stand;
            SetSeat(seat, s);
            ServerAdvanceTurn();
        }
    }

    /// <summary>
    /// Vacate (and mid-round forfeit) seats whose occupant is no longer able to hold the seat: the
    /// NetworkObject no longer exists (disconnect), or the occupant has died (killed by the Clown/a trap
    /// while seated). A dead occupant that kept its seat would strand the respawned player — the overlay
    /// stays interactive and freezes their movement (see <see cref="BlackjackOverlayController.IsInteractive"/>)
    /// — and would let a ragdolled player keep hitting/standing. Vacating auto-forfeits any live stake.
    /// </summary>
    void ServerScanForLostOccupants()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (!s.IsOccupied)
                continue;
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(s.OccupantNetObjId, out NetworkObject po) || po == null)
            {
                // Occupant is gone (disconnect). If mid-round their stake is already forfeit; just vacate.
                ServerVacateSeat(i);
                continue;
            }
            PlayerHealth health = po.GetComponent<PlayerHealth>();
            if (health != null && health.IsDead)
            {
                // Occupant died while seated. Free the seat so their respawn is mobile and others can sit.
                ServerVacateSeat(i);
            }
        }
    }

    // =========================================================================================
    // Dealer / shoe / wallet helpers (server only)
    // =========================================================================================

    void ServerRevealDealerHole(BlackjackHandStatus status) => ServerSyncDealerRevealed(status);

    void ServerSyncDealerRevealed(BlackjackHandStatus status)
    {
        DealerState d = default;
        d.Cards.Clear();
        for (int i = 0; i < _dealerCards.Count && i < 28; i++)
            d.Cards.Add(_dealerCards[i]);
        d.HoleHidden = 0;
        d.Status = (byte)status;
        _dealer.Value = d;
    }

    int DealerServerTotal(out int count)
    {
        count = _dealerCards.Count;
        int n = Mathf.Min(count, _evalBuf.Length);
        for (int i = 0; i < n; i++)
            _evalBuf[i] = _dealerCards[i];
        BlackjackCard.Evaluate(_evalBuf, n, out int total, out _, out _);
        return total;
    }

    void EnsureShoe()
    {
        if (_shoe.Count == 0 || _shoe.Count - _shoeCursor < reshuffleRemainingThreshold)
            BuildAndShuffleShoe();
    }

    void BuildAndShuffleShoe()
    {
        _shoe.Clear();
        for (int d = 0; d < BlackjackConfig.DeckCount; d++)
            for (byte c = 0; c < BlackjackCard.DeckSize; c++)
                _shoe.Add(c);
        // Fisher-Yates.
        for (int i = _shoe.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_shoe[i], _shoe[j]) = (_shoe[j], _shoe[i]);
        }
        _shoeCursor = 0;
    }

    byte ServerDraw()
    {
        if (_shoeCursor >= _shoe.Count)
            BuildAndShuffleShoe();
        return _shoe[_shoeCursor++];
    }

    int ServerBalanceOf(ulong playerNetObjId)
    {
        NetworkPlayerCarnivalTickets w = ServerWalletOf(playerNetObjId);
        return w != null ? w.TicketCount : 0;
    }

    bool ServerTrySpendBet(ulong playerNetObjId, int bet)
    {
        NetworkPlayerCarnivalTickets w = ServerWalletOf(playerNetObjId);
        return w != null && w.ServerTrySpend(bet);
    }

    void ServerCredit(ulong playerNetObjId, int amount)
    {
        ServerWalletOf(playerNetObjId)?.ServerAdd(amount);
    }

    NetworkPlayerCarnivalTickets ServerWalletOf(ulong playerNetObjId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return null;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetObjId, out NetworkObject po) || po == null)
            return null;
        return po.GetComponent<NetworkPlayerCarnivalTickets>();
    }

    // =========================================================================================
    // Small server-side queries
    // =========================================================================================

    int CountOccupied()
    {
        int n = 0;
        for (int i = 0; i < ActiveSeatCount; i++)
            if (GetSeat(i).IsOccupied)
                n++;
        return n;
    }

    bool AnyReady()
    {
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (s.IsOccupied && s.IsReady == 1)
                return true;
        }
        return false;
    }

    bool AllOccupiedReady()
    {
        bool any = false;
        for (int i = 0; i < ActiveSeatCount; i++)
        {
            SeatState s = GetSeat(i);
            if (!s.IsOccupied)
                continue;
            any = true;
            if (s.IsReady != 1)
                return false;
        }
        return any;
    }
}
