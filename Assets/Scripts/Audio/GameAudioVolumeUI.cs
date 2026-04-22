using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Optional UI: wire three sliders (0–1) to Master / Music / SFX. Assign references or name children MasterSlider, MusicSlider, SfxSlider.
/// </summary>
[DisallowMultipleComponent]
public sealed class GameAudioVolumeUI : MonoBehaviour
{
    [SerializeField] Slider masterSlider;
    [SerializeField] Slider musicSlider;
    [SerializeField] Slider sfxSlider;

    bool _wired;

    void Awake()
    {
        ResolveSlidersIfNeeded();
    }

    void OnEnable()
    {
        WireIfNeeded();
        PushValuesFromManagerToSliders();
    }

    void OnDisable()
    {
        Unwire();
    }

    void ResolveSlidersIfNeeded()
    {
        if (masterSlider != null && musicSlider != null && sfxSlider != null)
            return;

        foreach (Slider s in GetComponentsInChildren<Slider>(true))
        {
            if (s == null)
                continue;
            string n = s.gameObject.name;
            if (masterSlider == null && n == "MasterSlider")
                masterSlider = s;
            else if (musicSlider == null && n == "MusicSlider")
                musicSlider = s;
            else if (sfxSlider == null && n == "SfxSlider")
                sfxSlider = s;
        }
    }

    void WireIfNeeded()
    {
        if (_wired)
            return;

        if (masterSlider != null)
            masterSlider.onValueChanged.AddListener(OnMasterChanged);
        if (musicSlider != null)
            musicSlider.onValueChanged.AddListener(OnMusicChanged);
        if (sfxSlider != null)
            sfxSlider.onValueChanged.AddListener(OnSfxChanged);

        _wired = true;
    }

    void Unwire()
    {
        if (!_wired)
            return;

        if (masterSlider != null)
            masterSlider.onValueChanged.RemoveListener(OnMasterChanged);
        if (musicSlider != null)
            musicSlider.onValueChanged.RemoveListener(OnMusicChanged);
        if (sfxSlider != null)
            sfxSlider.onValueChanged.RemoveListener(OnSfxChanged);

        _wired = false;
    }

    void PushValuesFromManagerToSliders()
    {
        if (GameAudioManager.Instance == null)
            return;

        if (masterSlider != null)
            masterSlider.SetValueWithoutNotify(GameAudioManager.Instance.MasterVolumeLinear);
        if (musicSlider != null)
            musicSlider.SetValueWithoutNotify(GameAudioManager.Instance.MusicVolumeLinear);
        if (sfxSlider != null)
            sfxSlider.SetValueWithoutNotify(GameAudioManager.Instance.SfxVolumeLinear);
    }

    void OnMasterChanged(float v)
    {
        if (GameAudioManager.Instance != null)
            GameAudioManager.Instance.SetMasterVolumeLinear(v);
    }

    void OnMusicChanged(float v)
    {
        if (GameAudioManager.Instance != null)
            GameAudioManager.Instance.SetMusicVolumeLinear(v);
    }

    void OnSfxChanged(float v)
    {
        if (GameAudioManager.Instance != null)
            GameAudioManager.Instance.SetSfxVolumeLinear(v);
    }
}
