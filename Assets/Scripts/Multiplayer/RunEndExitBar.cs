using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The exit row at the bottom of both run-end cards (<see cref="RunCompleteSequence"/>, <see cref="TeamWipeSequence"/>).
/// Only the host can move the whole session between scenes, so what it offers depends on who this peer is:
/// <list type="bullet">
/// <item>Host of an online session: BACK TO LOBBY (everyone returns to the lobby still connected, see
/// <see cref="MultiplayerSceneFlow.ReturnToLobby"/>) and LEAVE (ends the session, as LEAVE always has).</item>
/// <item>Anyone else in the session: an inactive WAITING FOR HOST plate where the host's BACK TO LOBBY sits, and LEAVE
/// (quits this player's session alone). The host's return takes this card down with the scene; if the host quits
/// instead, the plate reads HOST LEFT and LEAVE goes to the menu.</item>
/// <item>A solo run, or no session at all: MAIN MENU.</item>
/// </list>
/// LEAVE asks first (a danger confirm, <see cref="MenuModal"/>) while there is still a group to leave; MAIN MENU and a
/// LEAVE after the host has gone do not.
/// </summary>
[DisallowMultipleComponent]
public sealed class RunEndExitBar : MonoBehaviour
{
    enum Mode
    {
        Host,
        Guest,
        Solo,
    }

    const float Height = 62f;
    const float Gap = 26f;
    const float PrimaryWidth = 340f;
    const float LeaveWidth = 220f;
    const float FontSize = 24f;
    /// <summary>The waiting plate is a status, not a greyed-out action: dimmed enough to read as inactive, still legible.</summary>
    const float WaitingPlateAlpha = 0.6f;
    const string LeaveConfirmTitle = "LEAVE GROUP";
    const string LeaveConfirmMessage = "Are you sure you want to leave the group and return to the menu?";
    const string HostLeaveConfirmMessage = LeaveConfirmMessage + " This ends the session for everyone.";

    Mode _mode;
    MenuModal _leaveConfirm;
    Button _primary;
    Button _leave;
    TMP_Text _waitingLabel;
    bool _hostGone;

    /// <summary>The bar's height on the 1920x1080 reference canvas, for laying out what sits above it.</summary>
    public static float BarHeight => Height;

    /// <summary>Where a pad lands first: the main action, or LEAVE when that is all there is.</summary>
    public Button DefaultButton => _primary != null ? _primary : _leave;

    /// <summary>Builds the row centred along the bottom of <paramref name="parent"/>, <paramref name="bottom"/> pixels up.</summary>
    public static RunEndExitBar Create(Transform parent, float bottom)
    {
        RectTransform row = MenuWidgets.CreateRect("ExitBar", parent);
        row.anchorMin = new Vector2(0.5f, 0f);
        row.anchorMax = new Vector2(0.5f, 0f);
        row.pivot = new Vector2(0.5f, 0f);
        row.anchoredPosition = new Vector2(0f, bottom);
        row.sizeDelta = new Vector2(1000f, Height);

        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = Gap;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var bar = row.gameObject.AddComponent<RunEndExitBar>();
        bar.Build(row, ResolveMode());
        return bar;
    }

    void Build(RectTransform row, Mode mode)
    {
        _mode = mode;
        switch (_mode)
        {
            case Mode.Host:
                _primary = CreatePlate(row, "Btn_LOBBY", "BACK TO LOBBY", BackToLobby, MenuWidgets.PlateStyle.Primary, PrimaryWidth);
                // Danger, like every other LEAVE in the game; for the host it ends the session for everyone.
                _leave = CreatePlate(row, "Btn_LEAVE", "LEAVE", Leave, MenuWidgets.PlateStyle.Danger, LeaveWidth);
                break;

            case Mode.Guest:
            {
                // Same plate, same spot as the host's BACK TO LOBBY, but only the host can press it.
                Button waiting = CreatePlate(row, "Btn_WAITING", "WAITING FOR HOST", null, MenuWidgets.PlateStyle.Primary,
                    PrimaryWidth);
                waiting.interactable = false;
                MenuButtonFx waitingFx = waiting.GetComponent<MenuButtonFx>();
                if (waitingFx != null)
                {
                    waitingFx.disabledAlpha = WaitingPlateAlpha;
                    _waitingLabel = waitingFx.label;
                }

                _leave = CreatePlate(row, "Btn_LEAVE", "LEAVE", Leave, MenuWidgets.PlateStyle.Danger, LeaveWidth);
                break;
            }

            default:
                _primary = CreatePlate(row, "Btn_MAINMENU", "MAIN MENU", LeaveNow, MenuWidgets.PlateStyle.Primary, PrimaryWidth);
                break;
        }
    }

    void Update()
    {
        // The host quit instead of bringing everyone back: this card stays up (MultiplayerSceneFlow skips its
        // disconnect-to-menu while a run-end card runs), so say so; LEAVE already goes to the menu either way.
        if (_mode != Mode.Guest || _hostGone)
            return;

        MultiplayerSessionController session = Session();
        if (session != null && session.IsSessionActive)
            return;

        _hostGone = true;
        if (_waitingLabel != null)
            _waitingLabel.text = "HOST LEFT";
    }

    static Mode ResolveMode()
    {
        MultiplayerSessionController session = Session();
        if (session == null || !session.IsSessionActive || session.IsOfflineSession)
            return Mode.Solo;

        return session.IsLobbyHost ? Mode.Host : Mode.Guest;
    }

    static MultiplayerSessionController Session()
    {
        return MultiplayerBootstrap.Instance != null
            ? MultiplayerBootstrap.Instance.GetComponent<MultiplayerSessionController>()
            : FindAnyObjectByType<MultiplayerSessionController>();
    }

    static MultiplayerSceneFlow Flow()
    {
        return MultiplayerBootstrap.Instance != null
            ? MultiplayerBootstrap.Instance.GetComponent<MultiplayerSceneFlow>()
            : FindAnyObjectByType<MultiplayerSceneFlow>();
    }

    void BackToLobby()
    {
        // One press: the load takes a frame or more.
        SetInteractable(false);
        MultiplayerSceneFlow flow = Flow();
        if (flow != null)
            flow.ReturnToLobby();
        else
            ElevatorFinishController.RequestReturnToMainMenuLocal();
    }

    /// <summary>LEAVE: confirm first while there is a group to walk out on.</summary>
    void Leave()
    {
        if (_mode == Mode.Solo || _hostGone)
        {
            LeaveNow();
            return;
        }

        // Built on the card's own canvas, so it sits over the card and takes the pad's focus while open.
        if (_leaveConfirm == null)
            _leaveConfirm = MenuModal.Create(transform.parent);
        _leaveConfirm.Open(LeaveConfirmTitle, _mode == Mode.Host ? HostLeaveConfirmMessage : LeaveConfirmMessage,
            "LEAVE", true, LeaveNow);
    }

    void LeaveNow()
    {
        SetInteractable(false);
        ElevatorFinishController.RequestReturnToMainMenuLocal();
    }

    void SetInteractable(bool interactable)
    {
        if (_primary != null)
            _primary.interactable = interactable;
        if (_leave != null)
            _leave.interactable = interactable;
    }

    static Button CreatePlate(Transform row, string name, string text, System.Action onClick,
        MenuWidgets.PlateStyle style, float width)
    {
        MenuButtonFx fx = MenuWidgets.CreatePlate(row, name, text, onClick, style, Height, FontSize);
        MenuWidgets.SetLayout(fx, minWidth: width, preferredWidth: width, minHeight: Height, preferredHeight: Height);
        return fx.button;
    }
}
