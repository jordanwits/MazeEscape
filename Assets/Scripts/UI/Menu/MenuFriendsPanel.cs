using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Invite overlay for the lobby: the local player's logged-in friends, one invite button each.
/// Invites go through the matchmaking API rather than the game overlay, so this works in the Unity
/// Editor too — the overlay is only injected into processes Steam itself launched and never appears
/// in the editor. Built in the shared plate language (<see cref="MenuWidgets"/>), same as
/// <see cref="MenuModal"/>.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuFriendsPanel : MonoBehaviour
{
    const float CardWidth = 560f;
    const float ListMaxHeight = 360f;
    const float RefreshInterval = 2f;

    sealed class FriendRow
    {
        public ulong UserId;
        public Button Invite;
        public TextMeshProUGUI InviteLabel;
    }

    MultiplayerSessionController _session;
    CanvasGroup _group;
    RectTransform _listContent;
    TextMeshProUGUI _emptyLabel;
    bool _shown;
    float _refreshTimer;
    long _renderedSignature = -1;

    readonly List<OnlineFriend> _friends = new();
    readonly List<FriendRow> _rows = new();
    readonly HashSet<ulong> _invited = new();

    public bool IsOpen => _shown;

    public static MenuFriendsPanel Create(Transform canvasRoot, MultiplayerSessionController session)
    {
        RectTransform root = MenuWidgets.CreateStretched("FriendsPanel", canvasRoot);
        var panel = root.gameObject.AddComponent<MenuFriendsPanel>();
        panel._session = session;
        panel.Build(root);
        return panel;
    }

    void Build(RectTransform root)
    {
        _group = root.gameObject.AddComponent<CanvasGroup>();

        Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.82f), true);
        scrim.rectTransform.SetStretch();

        RectTransform card = MenuWidgets.CreateRect("Card", root);
        card.sizeDelta = new Vector2(CardWidth, 100f);

        Image shadow = MenuWidgets.CreateImage(card, "Shadow", MenuTheme.RoundedShadow(MenuWidgets.CardRadius, 26), MenuTheme.WithAlpha(MenuTheme.Ink, 0.7f));
        shadow.rectTransform.SetStretch();
        shadow.rectTransform.offsetMin = new Vector2(-16f, -24f);
        shadow.rectTransform.offsetMax = new Vector2(16f, 10f);

        Image bg = MenuWidgets.CreateRoundedMaskedFill(card, "Bg", MenuWidgets.CardRadius, MenuTheme.PanelRaised, true);
        MenuWidgets.CreateGrunge(bg.transform, MenuTheme.WithAlpha(Color.white, 0.035f));
        Image outline = MenuWidgets.CreateImage(card, "Outline", MenuTheme.RoundedOutline(MenuWidgets.CardRadius, 1.8f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.22f));
        outline.rectTransform.SetStretch();

        RectTransform content = MenuWidgets.CreateStretched("Content", card);
        MenuWidgets.AddVertical(content.gameObject, new RectOffset(40, 40, 34, 34), 12f);
        var mirror = card.gameObject.AddComponent<CardHeightMirror>();
        mirror.content = content;

        TextMeshProUGUI title = MenuWidgets.CreateText(content, "Title", "INVITE FRIENDS", 28f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 5f);
        MenuWidgets.SetLayout(title, preferredHeight: 38f);

        _emptyLabel = MenuWidgets.CreateText(content, "Empty", string.Empty, 14.5f, MenuTheme.Faint,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.Left);

        _listContent = MenuWidgets.CreateScrollView(content, ListMaxHeight);

        MenuWidgets.CreateSpacer(content, 6f);
        MenuWidgets.CreateGhostButton(content, "DONE", Close, false, 50f);

        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;
        gameObject.SetActive(false);
    }

    public void Open()
    {
        // No SetAsLastSibling here: the panel is built before the toast, and invite feedback
        // ("Invite sent.") arrives as a toast that has to stay readable on top of it.
        gameObject.SetActive(true);
        _shown = true;
        _group.blocksRaycasts = true;
        _group.interactable = true;
        _refreshTimer = 0f;
        Refresh();
    }

    public void Close()
    {
        _shown = false;
        _group.blocksRaycasts = false;
        _group.interactable = false;
    }

    void Update()
    {
        float target = _shown ? 1f : 0f;
        _group.alpha = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * 10f);
        if (!_shown)
        {
            if (_group.alpha <= 0.001f)
                gameObject.SetActive(false);
            return;
        }

        // The lobby can die under us (host left, session shut down) — don't leave a dead panel up.
        // Sent-invite state belongs to that lobby, so it goes with it.
        if (_session == null || !_session.IsSessionActive)
        {
            _invited.Clear();
            Close();
            return;
        }

        _refreshTimer -= Time.unscaledDeltaTime;
        if (_refreshTimer <= 0f)
            Refresh();
    }

    void Refresh()
    {
        _refreshTimer = RefreshInterval;

        bool canInvite = _session != null && _session.CanInviteFriends;
        bool hasList = canInvite && _session.TryGetFriends(_friends);
        if (!hasList)
            _friends.Clear();

        _emptyLabel.text = !canInvite
            ? "Online play is unavailable right now, so nobody can be invited."
            : (_friends.Count == 0 ? "None of your friends are online right now." : string.Empty);
        bool showEmpty = _emptyLabel.text.Length > 0;
        if (_emptyLabel.gameObject.activeSelf != showEmpty)
            _emptyLabel.gameObject.SetActive(showEmpty);

        long signature = BuildSignature(_friends);
        if (signature != _renderedSignature)
        {
            _renderedSignature = signature;
            RebuildRows();
        }

        for (int i = 0; i < _rows.Count; i++)
            ApplyInvitedState(_rows[i]);
    }

    void RebuildRows()
    {
        for (int i = _listContent.childCount - 1; i >= 0; i--)
        {
            // Detach before Destroy: destruction is deferred to end of frame, and a still-parented
            // row would double up in the layout alongside its replacement for that frame.
            Transform stale = _listContent.GetChild(i);
            stale.SetParent(null, false);
            Destroy(stale.gameObject);
        }
        _rows.Clear();

        for (int i = 0; i < _friends.Count; i++)
        {
            OnlineFriend friend = _friends[i];
            ulong userId = friend.UserId;

            RectTransform row = MenuWidgets.CreateRow(_listContent, "Friend_" + userId, 46f, 10f);

            TextMeshProUGUI name = MenuWidgets.CreateText(row, "Name", friend.Name, 16f, MenuTheme.Bone,
                MenuWidgets.FontKind.Body, TextAlignmentOptions.MidlineLeft);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            name.overflowMode = TextOverflowModes.Ellipsis;
            MenuWidgets.SetLayout(name, flexibleWidth: 1f);

            if (friend.InThisGame)
            {
                TextMeshProUGUI tag = MenuWidgets.CreateText(row, "Tag", "IN GAME", 12f,
                    MenuTheme.WithAlpha(MenuTheme.Amber, 0.9f), MenuWidgets.FontKind.Display,
                    TextAlignmentOptions.MidlineRight, 3f);
                MenuWidgets.SetLayout(tag, minWidth: 92f, preferredWidth: 92f);
            }

            Button invite = MenuWidgets.CreateMiniButton(row, "INVITE", () => OnInviteClicked(userId), 108f);

            _rows.Add(new FriendRow
            {
                UserId = userId,
                Invite = invite,
                InviteLabel = invite.GetComponentInChildren<TextMeshProUGUI>(),
            });
        }
    }

    void OnInviteClicked(ulong userId)
    {
        if (_session == null || !_session.InviteFriend(userId))
            return;

        _invited.Add(userId);
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].UserId == userId)
                ApplyInvitedState(_rows[i]);
        }
    }

    void ApplyInvitedState(FriendRow row)
    {
        bool invited = _invited.Contains(row.UserId);
        if (row.Invite != null)
            row.Invite.interactable = !invited;
        if (row.InviteLabel != null)
            row.InviteLabel.text = invited ? "SENT" : "INVITE";
    }

    static long BuildSignature(List<OnlineFriend> friends)
    {
        long signature = friends.Count;
        for (int i = 0; i < friends.Count; i++)
            signature = signature * 31 + (long)friends[i].UserId + (friends[i].InThisGame ? 1 : 0);
        return signature;
    }
}
