using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shared settings content (audio, display, graphics, voice) used by both the main menu and the pause
/// menu. Builds widgets into a scrollable vertical layout and binds them to the live managers
/// (<see cref="GameAudioManager"/>, <see cref="GameDisplayBrightness"/>, <see cref="GameGraphicsSettings"/>).
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuSettingsPanel : MonoBehaviour
{
    Transform _content;

    Slider _master;
    Slider _music;
    Slider _sfx;
    Slider _clownBreathing;
    Slider _voice;
    Slider _brightness;
    TextMeshProUGUI _masterValue;
    TextMeshProUGUI _musicValue;
    TextMeshProUGUI _sfxValue;
    TextMeshProUGUI _clownBreathingValue;
    TextMeshProUGUI _voiceValue;
    TextMeshProUGUI _brightnessValue;
    MenuSegmented _voiceMode;

    // Graphics
    MenuSegmented _quality;
    Slider _renderScale;
    TextMeshProUGUI _renderScaleValue;
    MenuStepper _resolution;
    MenuSegmented _displayMode;
    MenuSegmented _vsync;
    MenuSegmented _fpsCap;

    public static MenuSettingsPanel Build(Transform parent)
    {
        var go = new GameObject("SettingsContent", typeof(RectTransform));
        go.layer = 5;
        go.transform.SetParent(parent, false);
        MenuWidgets.AddVertical(go, new RectOffset(0, 0, 0, 0), 0f);
        var panel = go.AddComponent<MenuSettingsPanel>();
        panel._content = MenuWidgets.CreateScrollView(go.transform, 560f);
        panel.BuildContent();
        return panel;
    }

    void BuildContent()
    {
        MenuWidgets.CreateSection(_content, "AUDIO");

        MenuWidgets.LabeledSlider master = MenuWidgets.CreateLabeledSlider(_content, "Master");
        _master = master.Slider;
        _masterValue = master.ValueLabel;
        _master.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetMasterVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider music = MenuWidgets.CreateLabeledSlider(_content, "Music");
        _music = music.Slider;
        _musicValue = music.ValueLabel;
        _music.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetMusicVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider sfx = MenuWidgets.CreateLabeledSlider(_content, "Effects");
        _sfx = sfx.Slider;
        _sfxValue = sfx.ValueLabel;
        _sfx.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetSfxVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider clownBreathing = MenuWidgets.CreateLabeledSlider(_content, "Clown Breathing");
        _clownBreathing = clownBreathing.Slider;
        _clownBreathingValue = clownBreathing.ValueLabel;
        _clownBreathing.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetClownBreathingVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.LabeledSlider voice = MenuWidgets.CreateLabeledSlider(_content, "Voice Chat");
        _voice = voice.Slider;
        _voiceValue = voice.ValueLabel;
        _voice.onValueChanged.AddListener(v =>
        {
            if (GameAudioManager.Instance != null)
                GameAudioManager.Instance.SetVoiceVolumeLinear(v);
            UpdateValueLabels();
        });

        MenuWidgets.CreateSection(_content, "DISPLAY");

        MenuWidgets.LabeledSlider brightness = MenuWidgets.CreateLabeledSlider(_content, "Environment Light");
        _brightness = brightness.Slider;
        _brightnessValue = brightness.ValueLabel;
        _brightness.onValueChanged.AddListener(v =>
        {
            if (GameDisplayBrightness.Instance != null)
                GameDisplayBrightness.Instance.SetBrightnessNormalized(v);
            UpdateValueLabels();
        });

        TextMeshProUGUI brightnessHint = MenuWidgets.CreateText(_content, "BrightnessHint",
            "Midpoint matches the level as authored. Raising it lifts the darkness — and some of the dread.",
            14f, MenuTheme.Faint);
        brightnessHint.lineSpacing = 6f;

        BuildGraphicsSection();

        MenuWidgets.CreateSection(_content, "VOICE");

        _voiceMode = MenuWidgets.CreateSegmented(_content, new[] { "OPEN MIC", "PUSH TO TALK" });
        _voiceMode.Changed += index =>
        {
            if (index == 1)
                VoiceUserSettings.SetPushToTalk();
            else
                VoiceUserSettings.SetOpenMic();
        };

        MenuWidgets.CreateText(_content, "VoiceHint",
            "Proximity voice chat. Push to talk is bound to V.", 14f, MenuTheme.Faint);

        SyncFromManagers();
    }

    void BuildGraphicsSection()
    {
        MenuWidgets.CreateSection(_content, "GRAPHICS");

        MenuWidgets.CreateText(_content, "QualityCaption", "Quality Preset", 15f, MenuTheme.Bone);
        _quality = MenuWidgets.CreateSegmented(_content, GameGraphicsSettings.TierNames());
        _quality.Changed += index =>
        {
            if (GameGraphicsSettings.Instance == null)
                return;
            GameGraphicsSettings.Instance.SetTier(index);
            // A preset resets render scale to its default — reflect that on the slider.
            if (_renderScale != null)
                _renderScale.SetValueWithoutNotify(GameGraphicsSettings.Instance.RenderScale);
            UpdateValueLabels();
        };

        MenuWidgets.LabeledSlider renderScale = MenuWidgets.CreateLabeledSlider(_content, "Render Scale");
        _renderScale = renderScale.Slider;
        _renderScaleValue = renderScale.ValueLabel;
        _renderScale.minValue = 0.5f;
        _renderScale.maxValue = 1f;
        _renderScale.onValueChanged.AddListener(v =>
        {
            if (GameGraphicsSettings.Instance != null)
                GameGraphicsSettings.Instance.SetRenderScale(v);
            UpdateValueLabels();
        });

        MenuWidgets.CreateText(_content, "ResolutionCaption", "Resolution", 15f, MenuTheme.Bone);
        _resolution = MenuWidgets.CreateStepper(_content);
        _resolution.Changed += index =>
        {
            if (GameGraphicsSettings.Instance != null)
                GameGraphicsSettings.Instance.SetResolutionIndex(index);
        };

        MenuWidgets.CreateText(_content, "DisplayModeCaption", "Display Mode", 15f, MenuTheme.Bone);
        _displayMode = MenuWidgets.CreateSegmented(_content, GameGraphicsSettings.DisplayModeNames());
        _displayMode.Changed += index =>
        {
            if (GameGraphicsSettings.Instance != null)
                GameGraphicsSettings.Instance.SetDisplayModeIndex(index);
        };

        MenuWidgets.CreateText(_content, "VSyncCaption", "V-Sync", 15f, MenuTheme.Bone);
        _vsync = MenuWidgets.CreateSegmented(_content, GameGraphicsSettings.VSyncNames());
        _vsync.Changed += index =>
        {
            if (GameGraphicsSettings.Instance != null)
                GameGraphicsSettings.Instance.SetVSync(index); // 0 = off, 1 = on
        };

        MenuWidgets.CreateText(_content, "FpsCaption", "Frame Rate Limit", 15f, MenuTheme.Bone);
        _fpsCap = MenuWidgets.CreateSegmented(_content, GameGraphicsSettings.FpsCapNames());
        _fpsCap.Changed += index =>
        {
            if (GameGraphicsSettings.Instance != null)
                GameGraphicsSettings.Instance.SetFpsCapOptionIndex(index);
        };

        MenuWidgets.CreateText(_content, "GraphicsHint",
            "If the game runs slowly, lower the Quality Preset or Render Scale — render scale is the strongest "
                + "performance lever. V-Sync (when on) overrides the frame-rate limit.",
            14f, MenuTheme.Faint).lineSpacing = 6f;
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
            _clownBreathing.SetValueWithoutNotify(GameAudioManager.Instance.ClownBreathingVolumeLinear);
            _voice.SetValueWithoutNotify(GameAudioManager.Instance.VoiceVolumeLinear);
        }

        if (GameDisplayBrightness.Instance != null)
            _brightness.SetValueWithoutNotify(GameDisplayBrightness.Instance.BrightnessNormalized);

        if (GameGraphicsSettings.Instance != null && _quality != null)
        {
            GameGraphicsSettings g = GameGraphicsSettings.Instance;
            _quality.Set(g.CurrentTier, false);
            _renderScale.SetValueWithoutNotify(g.RenderScale);

            int resCount = g.Resolutions.Count;
            var resNames = new string[resCount];
            for (int i = 0; i < resCount; i++)
                resNames[i] = g.ResolutionLabel(i);
            _resolution.SetOptions(resNames, g.CurrentResolutionIndex, false);

            _displayMode.Set(g.DisplayModeIndex, false);
            _vsync.Set(Mathf.Clamp(g.VSync, 0, 1), false);
            _fpsCap.Set(g.FpsCapOptionIndex, false);
        }

        _voiceMode.Set(VoiceUserSettings.IsPushToTalk ? 1 : 0, false);
        UpdateValueLabels();
    }

    void UpdateValueLabels()
    {
        SetPercent(_masterValue, _master);
        SetPercent(_musicValue, _music);
        SetPercent(_sfxValue, _sfx);
        SetPercent(_clownBreathingValue, _clownBreathing);
        SetPercent(_voiceValue, _voice);
        SetPercent(_brightnessValue, _brightness);
        SetPercent(_renderScaleValue, _renderScale);
    }

    static void SetPercent(TextMeshProUGUI label, Slider slider)
    {
        if (label != null && slider != null)
            label.text = Mathf.RoundToInt(slider.value * 100f) + "%";
    }
}
