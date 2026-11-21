using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The carnival main room's battery-powered security gates: one control panel (Close / Open / Recharge) drives a
/// portcullis in each doorway, and one shared battery powers both.
///
/// <para><b>Rules.</b> The battery lasts <see cref="batteryCapacitySeconds"/> of CLOSED time and is shown as
/// <see cref="batteryBars"/> bars. Every manual press of Close or Open costs <see cref="pressCostBars"/> of a bar.
/// Closing needs more charge than one press costs (so the gates never drop just to bounce straight back up);
/// opening is always allowed while closed, even when the press costs more than is left, so nobody can be sealed in.
/// When the charge reaches zero with the gates closed they open on their own (no press cost). Recharge refills the
/// battery to full for <see cref="rechargeTicketCost"/> tickets from the presser's own wallet, any time it is not
/// already full, whether the gates are open or closed. Every level starts with the gates open and a full battery.</para>
///
/// <para><b>Networking.</b> This component is built locally on every peer as part of the room prefab. The shared
/// state rides <see cref="CarnivalGateNetworkStore"/>; clients request presses through it, the server validates them
/// here (<see cref="ServerHandleRequest"/>), and every peer drives its own gates/indicators from the replicated
/// state each frame. Offline (editor dev scenes, no session) this peer is the authority and recharge is free,
/// matching how <see cref="CarnivalStore"/> behaves offline.</para>
///
/// <para>The level holds at most one carnival main room, so the store carries a single state and requests resolve
/// to the one active system.</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalGateSystem : MonoBehaviour
{
    public enum GateAction : byte
    {
        Close = 0,
        Open = 1,
        Recharge = 2,
    }

    enum PressResult
    {
        Ok,
        AlreadyClosed,
        AlreadyOpen,
        BatteryTooLow,
        BatteryFull,
        NotEnoughTickets,
    }

    [Header("Parts")]
    [SerializeField] CarnivalGate[] gates;
    [SerializeField] CarnivalGateBatteryIndicator[] indicators;
    [Tooltip("The control panel. The server measures a presser's distance from it before accepting a press.")]
    [SerializeField] Transform controlPanel;

    [Header("Battery")]
    [Tooltip("Seconds of CLOSED time a full battery lasts.")]
    [SerializeField, Min(1f)] float batteryCapacitySeconds = 180f;
    [Tooltip("Segments shown on the indicators. Press costs are measured in these.")]
    [SerializeField, Min(1)] int batteryBars = 6;
    [Tooltip("Bars each manual Close or Open press costs.")]
    [SerializeField, Min(0f)] float pressCostBars = 0.5f;
    [Tooltip("Tickets a recharge (always back to full) costs the presser.")]
    [SerializeField, Min(0)] int rechargeTicketCost = 60;

    [Header("Server checks")]
    [Tooltip("How far from the control panel the server still accepts a press (the client interact reach is ~1.5 m).")]
    [SerializeField, Min(0.5f)] float serverPanelReach = 4f;

    [Header("Room interior (local to this transform)")]
    [Tooltip("Box that counts as 'inside the room' while sealed — enemy respawns are kept out of it.")]
    [SerializeField] Vector3 roomInteriorCenter = new Vector3(0f, 6f, 0f);
    [SerializeField] Vector3 roomInteriorSize = new Vector3(22f, 12f, 22f);

    [Header("Audio")]
    [SerializeField] AudioClip buttonClip;
    [SerializeField] AudioClip deniedClip;
    [SerializeField, Range(0f, 1f)] float buttonVolume = 0.8f;
    [SerializeField, Range(0f, 1f)] float beepVolume = 0.55f;
    [Tooltip("Remaining bars at which the indicators start beeping while the gates are closed.")]
    [SerializeField, Min(0f)] float lowBatteryBeepBars = 1f;
    [Tooltip("Remaining seconds at which the beeping speeds up.")]
    [SerializeField, Min(0f)] float urgentBeepSeconds = 10f;

    static readonly List<CarnivalGateSystem> s_active = new List<CarnivalGateSystem>();
    static AudioClip s_beepClip;
    static AudioClip s_chargeClip;

    CarnivalGateNetworkStore.GateState _offlineState = CarnivalGateNetworkStore.GateState.Initial;
    bool _hasApplied;
    bool _appliedClosed;
    float _lastCharge = -1f;
    double _nextBeepTime;

    // ---- Public read side --------------------------------------------------------------------------------------

    public int BatteryBars => Mathf.Max(1, batteryBars);
    public float BatteryCapacitySeconds => Mathf.Max(1f, batteryCapacitySeconds);
    public int RechargeTicketCost => rechargeTicketCost;

    /// <summary>Charge one manual press costs, as a 0..1 fraction of the battery.</summary>
    float PressCost01 => Mathf.Clamp01(pressCostBars / BatteryBars);

    /// <summary>True while the gates are closed or closing (they count as sealed from the moment Close is accepted).</summary>
    public bool IsSealed => ReadState().IsClosed;

    /// <summary>Live battery charge 0..1 on this peer.</summary>
    public float CurrentCharge01 => ChargeAt(ReadState(), CurrentTime(), BatteryCapacitySeconds);

    /// <summary>The one live system on this peer (the level holds at most one carnival main room), or null.</summary>
    public static CarnivalGateSystem Active => s_active.Count > 0 ? s_active[0] : null;

    /// <summary>
    /// True when <paramref name="worldPoint"/> lies inside a carnival main room whose gates are closed. Enemy
    /// respawns use this so a replacement hunter never appears inside a sealed room.
    /// </summary>
    public static bool IsPointInsideSealedRoom(Vector3 worldPoint)
    {
        for (int i = 0; i < s_active.Count; i++)
        {
            CarnivalGateSystem system = s_active[i];
            if (system != null && system.IsSealed && system.ContainsPoint(worldPoint))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The battery charge at <paramref name="now"/>: frozen while open, draining one battery per
    /// <paramref name="capacitySeconds"/> of closed time since the state's stamp.
    /// </summary>
    public static float ChargeAt(in CarnivalGateNetworkStore.GateState state, double now, float capacitySeconds)
    {
        if (!state.IsClosed)
            return Mathf.Clamp01(state.Charge);

        // A client's estimate of server time can trail the stamp by a frame or two; never show charge going up.
        double elapsed = System.Math.Max(0d, now - state.Stamp);
        return Mathf.Clamp01(state.Charge - (float)(elapsed / Mathf.Max(1f, capacitySeconds)));
    }

    bool ContainsPoint(Vector3 worldPoint)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint) - roomInteriorCenter;
        Vector3 half = roomInteriorSize * 0.5f;
        return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------------------

    void OnEnable()
    {
        if (!s_active.Contains(this))
            s_active.Add(this);
    }

    void OnDisable()
    {
        s_active.Remove(this);
    }

    void Update()
    {
        double now = CurrentTime();
        CarnivalGateNetworkStore.GateState state = ReadState();

        // The authority opens the gates the moment the battery runs flat. No press cost: the battery is empty.
        if (HasAuthority() && state.IsClosed && ChargeAt(state, now, BatteryCapacitySeconds) <= 0f)
        {
            state = new CarnivalGateNetworkStore.GateState { Closed = 0, Charge = 0f, Stamp = now };
            WriteState(state);
        }

        float charge = ChargeAt(state, now, BatteryCapacitySeconds);
        bool closed = state.IsClosed;

        if (!_hasApplied || closed != _appliedClosed)
        {
            // The first state this peer sees (level start, or a late join mid-seal) is shown as-is, not animated.
            bool snap = !_hasApplied;
            if (gates != null)
            {
                for (int i = 0; i < gates.Length; i++)
                {
                    if (gates[i] != null)
                        gates[i].SetClosed(closed, snap);
                }
            }

            _appliedClosed = closed;
            _hasApplied = true;
        }

        // A jump UP in charge can only be a recharge; every peer hears it at the panel.
        if (_lastCharge >= 0f && charge > _lastCharge + 0.02f && controlPanel != null)
            GameAudioManager.PlaySfxAtPoint(ChargeClip, controlPanel.position, 0.7f, 1.5f, 18f);
        _lastCharge = charge;

        float secondsLeft = charge * BatteryCapacitySeconds;
        bool canClose = !closed && charge > PressCost01 + 0.0001f;
        bool blinkOn = Mathf.Repeat(Time.unscaledTime, 0.5f) < 0.3f;
        if (indicators != null)
        {
            for (int i = 0; i < indicators.Length; i++)
            {
                if (indicators[i] != null)
                    indicators[i].Show(charge, closed, secondsLeft, canClose, blinkOn);
            }
        }

        TickLowBatteryBeep(closed, charge, secondsLeft, now);
    }

    void TickLowBatteryBeep(bool closed, float charge, float secondsLeft, double now)
    {
        if (!closed || charge <= 0f || charge * BatteryBars > lowBatteryBeepBars)
        {
            _nextBeepTime = 0d;
            return;
        }

        if (now < _nextBeepTime)
            return;

        _nextBeepTime = now + (secondsLeft <= urgentBeepSeconds ? 0.5d : 1.5d);
        if (indicators == null)
            return;

        for (int i = 0; i < indicators.Length; i++)
        {
            if (indicators[i] != null)
                GameAudioManager.PlaySfxAtPoint(BeepClip, indicators[i].transform.position, beepVolume, 2f, 26f);
        }
    }

    // ---- Local player entry points -----------------------------------------------------------------------------

    /// <summary>Local player pressed E on a panel button. Refusals are answered locally; the prompt says why.</summary>
    public void LocalPress(GateAction action, PlayerController presser, Vector3 buttonPosition)
    {
        if (presser == null)
            return;

        PressResult preview = Evaluate(action, ReadState(), CurrentTime(), ViewerTickets(presser), out _);
        if (preview != PressResult.Ok)
        {
            GameAudioManager.PlaySfxAtPoint(deniedClip, buttonPosition, buttonVolume * 0.7f, 1f, 10f);
            return;
        }

        GameAudioManager.PlaySfxAtPoint(buttonClip, buttonPosition, buttonVolume, 1f, 10f);

        if (IsNetworkSession())
        {
            CarnivalGateNetworkStore.RequestAction(action);
            return;
        }

        // Offline: this peer is the authority and there is no wallet to debit.
        if (Evaluate(action, _offlineState, CurrentTime(), int.MaxValue, out CarnivalGateNetworkStore.GateState next)
            == PressResult.Ok)
        {
            _offlineState = next;
        }
    }

    /// <summary>The HUD prompt for aiming at <paramref name="action"/>'s button.</summary>
    public string GetPrompt(GateAction action, PlayerController viewer)
    {
        int tickets = ViewerTickets(viewer);
        PressResult result = Evaluate(action, ReadState(), CurrentTime(), tickets, out _);
        switch (result)
        {
            case PressResult.Ok:
                switch (action)
                {
                    case GateAction.Close: return "Press E to close the gates";
                    case GateAction.Open: return "Press E to open the gates";
                    default:
                        return tickets == int.MaxValue
                            ? "Press E to charge the battery"
                            : $"Press E to charge the battery ({rechargeTicketCost} tickets)";
                }
            case PressResult.AlreadyClosed: return "The gates are closed";
            case PressResult.AlreadyOpen: return "The gates are open";
            case PressResult.BatteryTooLow: return "Battery too low to close the gates";
            case PressResult.BatteryFull: return "The battery is full";
            case PressResult.NotEnoughTickets:
                return $"Charging costs {rechargeTicketCost} tickets (you have {Mathf.Max(0, tickets)})";
            default: return string.Empty;
        }
    }

    /// <summary>Whether a press would currently do something, ignoring the presser's wallet (button lamps use this).</summary>
    public bool IsActionLit(GateAction action)
    {
        return Evaluate(action, ReadState(), CurrentTime(), int.MaxValue, out _) == PressResult.Ok;
    }

    /// <summary>The viewer's ticket balance, or int.MaxValue offline where recharge is free.</summary>
    static int ViewerTickets(PlayerController viewer)
    {
        if (!IsNetworkSession())
            return int.MaxValue;
        if (viewer == null)
            return 0;
        NetworkPlayerCarnivalTickets wallet = viewer.GetComponent<NetworkPlayerCarnivalTickets>();
        return wallet != null ? wallet.TicketCount : 0;
    }

    // ---- Server ------------------------------------------------------------------------------------------------

    /// <summary>Server-only: a client pressed a panel button. Validates range, battery and tickets, then publishes.</summary>
    public static void ServerHandleRequest(GateAction action, ulong senderClientId)
    {
        CarnivalGateSystem system = Active;
        if (system != null)
            system.ServerHandle(action, senderClientId);
    }

    void ServerHandle(GateAction action, ulong senderClientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || !CarnivalGateNetworkStore.IsLive)
            return;

        if (!nm.ConnectedClients.TryGetValue(senderClientId, out NetworkClient client) || client.PlayerObject == null)
            return;

        // Range is re-checked here so a client cannot work the panel from across the map.
        Vector3 presserPosition = client.PlayerObject.transform.position;
        Vector3 panelPosition = controlPanel != null ? controlPanel.position : transform.position;
        if ((presserPosition - panelPosition).sqrMagnitude > serverPanelReach * serverPanelReach)
            return;

        PlayerHealth health = client.PlayerObject.GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;

        NetworkPlayerCarnivalTickets wallet = client.PlayerObject.GetComponent<NetworkPlayerCarnivalTickets>();
        int tickets = wallet != null ? wallet.TicketCount : 0;

        double now = CurrentTime();
        PressResult result = Evaluate(action, ReadState(), now, tickets, out CarnivalGateNetworkStore.GateState next);
        if (result != PressResult.Ok)
            return;

        // Spend last, after every other check passed, so a refused press never costs anything.
        if (action == GateAction.Recharge && rechargeTicketCost > 0
            && (wallet == null || !wallet.ServerTrySpend(rechargeTicketCost)))
        {
            return;
        }

        WriteState(next);
    }

    /// <summary>The whole rule set, shared by the server, the offline path, prompts and button lamps.</summary>
    PressResult Evaluate(
        GateAction action,
        CarnivalGateNetworkStore.GateState state,
        double now,
        int tickets,
        out CarnivalGateNetworkStore.GateState next)
    {
        next = state;
        float charge = ChargeAt(state, now, BatteryCapacitySeconds);
        float cost = PressCost01;

        switch (action)
        {
            case GateAction.Close:
                if (state.IsClosed)
                    return PressResult.AlreadyClosed;
                if (charge <= cost + 0.0001f)
                    return PressResult.BatteryTooLow;
                next = new CarnivalGateNetworkStore.GateState { Closed = 1, Charge = charge - cost, Stamp = now };
                return PressResult.Ok;

            case GateAction.Open:
                if (!state.IsClosed)
                    return PressResult.AlreadyOpen;
                // Always allowed: an almost-flat battery must never seal anyone in.
                next = new CarnivalGateNetworkStore.GateState { Closed = 0, Charge = Mathf.Max(0f, charge - cost), Stamp = now };
                return PressResult.Ok;

            case GateAction.Recharge:
                if (charge >= 0.999f)
                    return PressResult.BatteryFull;
                if (tickets < rechargeTicketCost)
                    return PressResult.NotEnoughTickets;
                next = new CarnivalGateNetworkStore.GateState { Closed = state.Closed, Charge = 1f, Stamp = now };
                return PressResult.Ok;
        }

        return PressResult.AlreadyOpen;
    }

    // ---- State plumbing ----------------------------------------------------------------------------------------

    static bool IsNetworkSession()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening;
    }

    bool HasAuthority()
    {
        if (!IsNetworkSession())
            return true;
        NetworkManager nm = NetworkManager.Singleton;
        return nm.IsServer && CarnivalGateNetworkStore.IsLive;
    }

    CarnivalGateNetworkStore.GateState ReadState()
    {
        if (CarnivalGateNetworkStore.IsLive)
            return CarnivalGateNetworkStore.CurrentState;
        // In a session whose store has not synced yet, show the level-start state rather than stale offline data.
        return IsNetworkSession() ? CarnivalGateNetworkStore.GateState.Initial : _offlineState;
    }

    void WriteState(CarnivalGateNetworkStore.GateState state)
    {
        if (IsNetworkSession())
            CarnivalGateNetworkStore.ServerWrite(state);
        else
            _offlineState = state;
    }

    static double CurrentTime()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening ? nm.ServerTime.Time : Time.timeAsDouble;
    }

    // ---- Procedural cues ---------------------------------------------------------------------------------------

    static AudioClip BeepClip => s_beepClip != null ? s_beepClip : (s_beepClip = BuildTone("CarnivalGateBeep", 0.11f, 1650f, 1650f));
    static AudioClip ChargeClip => s_chargeClip != null ? s_chargeClip : (s_chargeClip = BuildTone("CarnivalGateCharge", 0.7f, 320f, 1500f));

    /// <summary>A short sine tone sweeping <paramref name="fromHz"/> → <paramref name="toHz"/> with soft edges.</summary>
    static AudioClip BuildTone(string clipName, float seconds, float fromHz, float toHz)
    {
        const int rate = 44100;
        int samples = Mathf.Max(1, Mathf.RoundToInt(seconds * rate));
        float[] data = new float[samples];
        double phase = 0d;
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)samples;
            float hz = Mathf.Lerp(fromHz, toHz, t);
            phase += 2d * System.Math.PI * hz / rate;
            float envelope = Mathf.Min(1f, t * 25f) * Mathf.Min(1f, (1f - t) * 12f);
            data[i] = (float)System.Math.Sin(phase) * 0.6f * envelope;
        }

        AudioClip clip = AudioClip.Create(clipName, samples, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(roomInteriorCenter, roomInteriorSize);
    }
#endif
}
