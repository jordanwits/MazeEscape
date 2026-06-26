using System.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Purely-visual, non-networked celebration for the blackjack dealer bot: when a player is dealt a natural
/// blackjack the dealer's head spins all the way around while a stinger SFX plays. Runs on every peer (the
/// <see cref="BlackjackGameController"/> raises <see cref="BlackjackGameController.BlackjackCelebrated"/> via a
/// ClientRpc, so the spin fires for the host and every remote client). The head completes a whole number of
/// revolutions over the clip's duration, so it lands back on its original orientation with no snap.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackDealerSpin : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("The dealer head transform to spin. Defaults to a child named \"DealerHead\" if left unset.")]
    [SerializeField] Transform head;
    [Tooltip("Controller whose blackjack event drives the spin. Defaults to the controller in a parent.")]
    [SerializeField] BlackjackGameController controller;

    [Header("Spin")]
    [Tooltip("SFX played while the head spins; the spin lasts exactly as long as this clip.")]
    [SerializeField] AudioClip spinClip;
    [SerializeField, Range(0f, 1f)] float volume = 1f;
    [Tooltip("Whole revolutions to complete over the clip (rounded, so the head lands back on its start pose).")]
    [SerializeField, Min(1f)] float revolutions = 3f;
    [Tooltip("Fallback spin duration (seconds) if no clip is assigned.")]
    [SerializeField, Min(0.1f)] float fallbackDuration = 1.5f;

    AudioSource _audio;
    Coroutine _spin;
    bool _subscribed;

    void Awake()
    {
        if (head == null)
        {
            Transform t = transform.Find("DealerHead");
            head = t != null ? t : transform;
        }
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
        EnsureAudio();
    }

    void OnEnable()
    {
        TrySubscribe();
    }

    void OnDisable()
    {
        if (controller != null && _subscribed)
        {
            controller.BlackjackCelebrated -= PlaySpin;
            _subscribed = false;
        }
    }

    void Update()
    {
        if (!_subscribed)
            TrySubscribe();
    }

    void TrySubscribe()
    {
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
        if (controller != null && !_subscribed)
        {
            controller.BlackjackCelebrated += PlaySpin;
            _subscribed = true;
        }
    }

    void EnsureAudio()
    {
        if (_audio != null)
            return;
        _audio = GetComponent<AudioSource>();
        if (_audio == null)
            _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.loop = false;
        _audio.spatialBlend = 1f;
        _audio.minDistance = 1f;
        _audio.maxDistance = 45f;
        _audio.rolloffMode = AudioRolloffMode.Linear;
    }

    /// <summary>Spin the dealer's head for the SFX duration. Ignored if a spin is already running.</summary>
    public void PlaySpin()
    {
        if (head == null || !isActiveAndEnabled)
            return;
        if (_spin != null)
            return; // let the current celebration finish its full duration
        _spin = StartCoroutine(SpinRoutine());
    }

    IEnumerator SpinRoutine()
    {
        float duration = (spinClip != null && spinClip.length > 0.01f) ? spinClip.length : fallbackDuration;

        EnsureAudio();
        if (spinClip != null && _audio != null)
            _audio.PlayOneShot(spinClip, Mathf.Clamp01(volume));

        // A whole number of turns over the clip => the head lands back on its starting orientation (no snap).
        float revs = Mathf.Max(1f, Mathf.Round(revolutions));
        float degreesPerSecond = 360f * revs / duration;

        Quaternion start = head.localRotation;
        float t = 0f;
        while (t < duration)
        {
            head.Rotate(Vector3.up, degreesPerSecond * Time.deltaTime, Space.World);
            t += Time.deltaTime;
            yield return null;
        }

        head.localRotation = start;
        _spin = null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (spinClip == null)
            spinClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Carnival/BlackJack.wav");
    }
#endif
}
