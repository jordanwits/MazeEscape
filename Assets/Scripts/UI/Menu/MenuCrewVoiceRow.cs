using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The local voice mix for one other player, as a crew row shows it: a VOICE slider along the bottom of the row
/// and a bare mic glyph in its top-right corner that mutes them. Both write <see cref="VoicePlayerMixSettings"/>,
/// which is listener-side only — nothing is replicated, so the person you turn down is never told.
///
/// Shared by the lobby roster (<see cref="MainMenuController"/>) and the pause menu's crew box
/// (<see cref="PauseMenuController"/>); each lays out its own name line above it and must leave
/// <see cref="MicSize"/> + 8 free on that line's right.
/// </summary>
public sealed class MenuCrewVoiceRow
{
    /// <summary>Square hit area of the microphone toggle.</summary>
    public const float MicSize = 26f;

    /// <summary>Height of a row for another player: name line over the voice slider.</summary>
    public const float RowHeight = 68f;

    readonly ulong _clientId;
    readonly Slider _volume;
    readonly TextMeshProUGUI _volumeValue;
    readonly Button _micButton;
    readonly Image _micIcon;

    /// <summary>
    /// Adds the voice controls to <paramref name="row"/> (a <see cref="RowHeight"/>-tall rect); the caption and
    /// slider start <paramref name="leftInset"/> in from its left edge.
    /// </summary>
    public MenuCrewVoiceRow(RectTransform row, ulong clientId, float leftInset)
    {
        _clientId = clientId;

        TextMeshProUGUI caption = MenuWidgets.CreateText(row, "VoiceCaption", "VOICE", 10.5f,
            MenuTheme.WithAlpha(MenuTheme.Faint, 0.9f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.MidlineLeft, 3f);
        RectTransform captionRt = caption.rectTransform;
        captionRt.anchorMin = new Vector2(0f, 0f);
        captionRt.anchorMax = new Vector2(0f, 0f);
        captionRt.pivot = new Vector2(0f, 0f);
        captionRt.anchoredPosition = new Vector2(leftInset, 6f);
        captionRt.sizeDelta = new Vector2(46f, 24f);

        _volume = MenuWidgets.CreateSlider(row);
        var volumeRt = (RectTransform)_volume.transform;
        volumeRt.anchorMin = new Vector2(0f, 0f);
        volumeRt.anchorMax = new Vector2(1f, 0f);
        volumeRt.pivot = new Vector2(0.5f, 0f);
        volumeRt.offsetMin = new Vector2(leftInset + 50f, 7f);
        volumeRt.offsetMax = new Vector2(-56f, 29f);

        _volumeValue = MenuWidgets.CreateText(row, "VolumeValue", string.Empty, 12.5f,
            MenuTheme.Mist, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 1f);
        RectTransform volumeValueRt = _volumeValue.rectTransform;
        volumeValueRt.anchorMin = new Vector2(1f, 0f);
        volumeValueRt.anchorMax = new Vector2(1f, 0f);
        volumeValueRt.pivot = new Vector2(1f, 0f);
        volumeValueRt.anchoredPosition = new Vector2(0f, 6f);
        volumeValueRt.sizeDelta = new Vector2(50f, 24f);

        _volume.onValueChanged.AddListener(v =>
        {
            VoicePlayerMixSettings.SetVolume(_clientId, v);
            _volumeValue.text = Mathf.RoundToInt(v * 100f) + "%";
        });

        // Bare glyph, no plate: the icon itself is the control, and the struck-through variant
        // carries the state (see MenuTheme.MicIcon).
        _micIcon = MenuWidgets.CreateImage(row, "Mic", MenuTheme.MicIcon(false), MenuTheme.Bone, true);
        _micIcon.preserveAspect = true;
        RectTransform micRt = _micIcon.rectTransform;
        micRt.anchorMin = new Vector2(1f, 1f);
        micRt.anchorMax = new Vector2(1f, 1f);
        micRt.pivot = new Vector2(1f, 1f);
        micRt.anchoredPosition = new Vector2(0f, -1f);
        micRt.sizeDelta = new Vector2(MicSize, MicSize);

        _micButton = _micIcon.gameObject.AddComponent<Button>();
        _micButton.targetGraphic = _micIcon;
        _micButton.transition = Selectable.Transition.ColorTint;
        // A pad reaches it with up from the row's slider (UiEventSystem's navigation).
        _micButton.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        _micButton.onClick.AddListener(() =>
        {
            MenuUiAudio.PlayClick();
            VoicePlayerMixSettings.ToggleMuted(_clientId);
            ApplyMuteVisual();
        });

        SyncFromSettings();
    }

    /// <summary>
    /// Re-reads this player's volume and mute from <see cref="VoicePlayerMixSettings"/>. For a row that outlives
    /// a session: the mix is cleared when the session ends, and NGO hands the same client ids out again.
    /// </summary>
    public void SyncFromSettings()
    {
        float volume = VoicePlayerMixSettings.GetVolume(_clientId);
        _volume.SetValueWithoutNotify(volume);
        _volumeValue.text = Mathf.RoundToInt(volume * 100f) + "%";
        ApplyMuteVisual();
    }

    /// <summary>Pushes the stored mute state onto the row — rows are not rebuilt on a mute click.</summary>
    void ApplyMuteVisual()
    {
        bool muted = VoicePlayerMixSettings.IsMuted(_clientId);

        _micIcon.sprite = MenuTheme.MicIcon(muted);

        // ColorTint overwrites the Image colour every state change, so the mute state has to live in
        // the button's palette rather than on the Image itself.
        ColorBlock colors = _micButton.colors;
        colors.normalColor = muted ? MenuTheme.BloodBright : MenuTheme.WithAlpha(MenuTheme.Bone, 0.85f);
        colors.highlightedColor = muted ? MenuTheme.Bone : MenuTheme.AmberBright;
        colors.pressedColor = muted ? MenuTheme.Blood : MenuTheme.Amber;
        colors.selectedColor = colors.highlightedColor;   // pad focus reads as hover
        colors.disabledColor = MenuTheme.Faint;
        _micButton.colors = colors;

        _volume.interactable = !muted;
        _volumeValue.color = muted ? MenuTheme.Faint : MenuTheme.Mist;
    }
}
