using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shared settings content (audio, display, voice) used by both the main menu and the pause
/// menu. Builds widgets into a vertical-layout parent and binds them to the live managers.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuSettingsPanel : MonoBehaviour
{
    Slider _master;
    Slider _music;
    Slider _sfx;
    Slider _voice;
    Slider _brightness;
    TextMeshProUGUI _masterValue;
    TextMeshProUGUI _musicValue;
    TextMeshProUGUI _sfxValue;
    TextMeshProUGUI _voiceValue;
    TextMeshProUGUI _brightnessValue;
    MenuSegmented _voiceMode;

    public static MenuSettingsPanel Build(Transform parent)
    {
        var go = new GameObject("SettingsContent", typeof(RectTransform));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        MenuWidgets.AddVertical(go, new RectOffset(0, 0, 0, 0), 10f);
        var panel = go.AddComponent<MenuSettingsPanel>();
        panel.BuildContent();
        return panel;
    }

    void BuildContent()
    {
        MenuWidgets.CreateSection(transform, "AUDIO");

        MenuWidgets.LabeledSlider master = MenuWidgets.CreateLabeledSlider(transform, "Master");
        _master = master.Slider;
        _masterValue = master.ValueLabel;
        _master.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetMasterVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider music = MenuWidgets.CreateLabeledSlider(transform, "Music");
        _music = music.Slider;
        _musicValue = music.ValueLabel;
        _music.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetMusicVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider sfx = MenuWidgets.CreateLabeledSlider(transform, "Effects");
        _sfx = sfx.Slider;
        _sfxValue = sfx.ValueLabel;
        _sfx.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetSfxVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider voice = MenuWidgets.CreateLabeledSlider(transform, "Voice Chat");
        _voice = voice.Slider;
        _voiceValue = voice.ValueLabel;
        _voice.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetVoiceVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.CreateSection(transform, "DISPLAY");

        MenuWidgets.LabeledSlider brightness = MenuWidgets.CreateLabeledSlider(transform, "Environment Light");
        _brightness = brightness.Slider;
        _brightnessValue = brightness.ValueLabel;
        _brightness.onValueChanged.AddListener(v =>
        {
            if (GameDisplayBrightness.Instance != null)
                GameDisplayBrightness.Instance.SetBrightnessNormalized(v);
            UpdateValueLabels();
        });

        TextMeshProUGUI brightnessHint = MenuWidgets.CreateText(transform, "BrightnessHint",
            "Midpoint matches the level as authored. Raising it lifts the darkness — and some of the dread.",
            14f, MenuTheme.Faint);
        brightnessHint.lineSpacing = 6f;

        MenuWidgets.CreateSection(transform, "VOICE");

        _voiceMode = MenuWidgets.CreateSegmented(transform, new[] { "OPEN MIC", "PUSH TO TALK" });
        _voiceMode.Changed += index =>
        {
            if (index == 1)
                VoiceUserSettings.SetPushToTalk();
            else
                VoiceUserSettings.SetOpenMic();
        };

        MenuWidgets.CreateText(transform, "VoiceHint",
            "Proximity voice chat. Push to talk is bound to V.", 14f, MenuTheme.Faint);

        SyncFromManagers();
    }

    void OnEnable()
    {
        SyncFromManagers();
    }

    public void SyncFromManagers()
    {
        if (_master == null)
            return;

        if (GameAudioManager.Instance != null)
        {
            _master.SetValueWithoutNotify(GameAudioManager.Instance.MasterVolumeLinear);
            _music.SetValueWithoutNotify(GameAudioManager.Instance.MusicVolumeLinear);
            _sfx.SetValueWithoutNotify(GameAudioManager.Instance.SfxVolumeLinear);
            _voice.SetValueWithoutNotify(GameAudioManager.Instance.VoiceVolumeLinear);
        }

        if (GameDisplayBrightness.Instance != null)
            _brightness.SetValueWithoutNotify(GameDisplayBrightness.Instance.BrightnessNormalized);

        _voiceMode.Set(VoiceUserSettings.IsPushToTalk ? 1 : 0, false);
        UpdateValueLabels();
    }

    void UpdateValueLabels()
    {
        SetPercent(_masterValue, _master);
        SetPercent(_musicValue, _music);
        SetPercent(_sfxValue, _sfx);
        SetPercent(_voiceValue, _voice);
        SetPercent(_brightnessValue, _brightness);
    }

    static void SetPercent(TextMeshProUGUI label, Slider slider)
    {
        if (label != null && slider != null)
            label.text = Mathf.RoundToInt(slider.value * 100f) + "%";
    }
}
