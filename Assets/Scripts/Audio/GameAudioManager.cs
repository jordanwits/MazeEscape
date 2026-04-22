using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Loads the main audio mixer (from Resources unless overridden), applies saved bus levels, and exposes groups for routing.
/// Lives on the same DontDestroyOnLoad object as <see cref="MultiplayerBootstrap"/>.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-20)]
public sealed class GameAudioManager : MonoBehaviour
{
    public const string MixerResourcePath = "GameAudio/MainMixer";
    public const string ExposedMasterVolume = "MasterVolume";
    public const string ExposedMusicVolume = "MusicVolume";
    public const string ExposedSfxVolume = "SfxVolume";

    const string PrefsMaster = "GameAudio.MasterLinear";
    const string PrefsMusic = "GameAudio.MusicLinear";
    const string PrefsSfx = "GameAudio.SfxLinear";

    public static GameAudioManager Instance { get; private set; }

    [Tooltip("If set, used instead of Resources.Load(GameAudio/MainMixer).")]
    [SerializeField] AudioMixer mainMixerOverride;

    AudioMixer _mixer;
    AudioMixerGroup _musicGroup;
    AudioMixerGroup _sfxGroup;

    float _masterLinear = 1f;
    float _musicLinear = 1f;
    float _sfxLinear = 1f;

    public AudioMixer MainMixer => _mixer;
    public AudioMixerGroup MusicGroup => _musicGroup;
    public AudioMixerGroup SfxGroup => _sfxGroup;

    public float MasterVolumeLinear => _masterLinear;
    public float MusicVolumeLinear => _musicLinear;
    public float SfxVolumeLinear => _sfxLinear;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;

        _mixer = mainMixerOverride != null ? mainMixerOverride : Resources.Load<AudioMixer>(MixerResourcePath);
        if (_mixer == null)
        {
            Debug.LogWarning(
                "GameAudioManager: No AudioMixer assigned or found at Resources/GameAudio/MainMixer. " +
                "Use menu: Maze Escape → Audio → Create Game Audio Mixer.");
            return;
        }

        CacheGroups();
        LoadPrefs();
        ApplyAllToMixer();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void CacheGroups()
    {
        _musicGroup = FindGroup("Music");
        _sfxGroup = FindGroup("Sfx");
    }

    AudioMixerGroup FindGroup(string name)
    {
        if (_mixer == null)
            return null;

        var found = _mixer.FindMatchingGroups(name);
        return found != null && found.Length > 0 ? found[0] : null;
    }

    void LoadPrefs()
    {
        _masterLinear = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsMaster, 1f));
        _musicLinear = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsMusic, 1f));
        _sfxLinear = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefsSfx, 1f));
    }

    void SavePrefs()
    {
        PlayerPrefs.SetFloat(PrefsMaster, _masterLinear);
        PlayerPrefs.SetFloat(PrefsMusic, _musicLinear);
        PlayerPrefs.SetFloat(PrefsSfx, _sfxLinear);
        PlayerPrefs.Save();
    }

    public void SetMasterVolumeLinear(float linear01)
    {
        _masterLinear = Mathf.Clamp01(linear01);
        SavePrefs();
        ApplyBus(ExposedMasterVolume, _masterLinear);
    }

    public void SetMusicVolumeLinear(float linear01)
    {
        _musicLinear = Mathf.Clamp01(linear01);
        SavePrefs();
        ApplyBus(ExposedMusicVolume, _musicLinear);
    }

    public void SetSfxVolumeLinear(float linear01)
    {
        _sfxLinear = Mathf.Clamp01(linear01);
        SavePrefs();
        ApplyBus(ExposedSfxVolume, _sfxLinear);
    }

    void ApplyAllToMixer()
    {
        if (_mixer == null)
            return;

        ApplyBus(ExposedMasterVolume, _masterLinear);
        ApplyBus(ExposedMusicVolume, _musicLinear);
        ApplyBus(ExposedSfxVolume, _sfxLinear);
    }

    void ApplyBus(string exposedName, float linear01)
    {
        if (_mixer == null)
            return;

        _mixer.SetFloat(exposedName, LinearToDecibels(linear01));
    }

    /// <summary>
    /// Sends gameplay SFX through the Sfx bus so the SFX slider affects them.
    /// </summary>
    public static void RouteSfxSource(AudioSource source)
    {
        if (source == null || Instance == null || Instance._sfxGroup == null)
            return;

        source.outputAudioMixerGroup = Instance._sfxGroup;
    }

    public static float LinearToDecibels(float linear)
    {
        if (linear <= 0.0001f)
            return -80f;
        return Mathf.Log10(linear) * 20f;
    }
}
