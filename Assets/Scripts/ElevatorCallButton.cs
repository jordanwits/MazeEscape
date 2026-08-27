using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// A press pad on the exit elevator cab: the one outside the doors calls the elevator (opens), the one inside sends
/// it away (closes, which ends the section). Not networked — the button forwards the press to the spawned
/// <see cref="ElevatorFinishController"/>, which owns and replicates the door state.
/// </summary>
[DisallowMultipleComponent]
public class ElevatorCallButton : MonoBehaviour
{
    public enum ElevatorButtonAction
    {
        OpenDoors,
        CloseDoors,
    }

    [Tooltip("Open Doors = the pad outside the cab. Close Doors = the pad inside it (only pressable from inside, and only with everyone aboard).")]
    [SerializeField] ElevatorButtonAction action = ElevatorButtonAction.OpenDoors;
    [SerializeField] float interactMaxDistance = 3.5f;

    [Header("Press feedback")]
    [Tooltip("Direction the pad sinks when pressed, in the pad's own local space (it is a squashed sphere, so this cannot be read off the mesh).")]
    [SerializeField] Vector3 pressLocalDirection = new Vector3(0f, 0f, -1f);
    [SerializeField, Min(0f)] float pressDepth = 0.018f;
    [SerializeField] AudioClip pressClip;
    [SerializeField, Range(0f, 1f)] float pressVolume = 0.55f;

    const float PressInSeconds = 0.06f;
    const float PressHoldSeconds = 0.05f;
    const float PressOutSeconds = 0.11f;

    ElevatorFinishController _controller;
    ElevatorSlidingDoors _detachedDoors;
    bool _loggedDetachedCab;
    Vector3 _restLocalPosition;
    Vector3 _pressOffsetLocal;
    float _pressTime = -1f;
    AudioSource _sfx;
    bool _warnedMissingController;

    public ElevatorButtonAction Action => action;
    public ElevatorFinishController Controller => _controller;
    public float InteractMaxDistance => interactMaxDistance;

    /// <summary>The inside pad is only reachable from inside the cab, which also keeps it from being pressed through the wall.</summary>
    public bool RequiresInteractorInsideCab => action == ElevatorButtonAction.CloseDoors;

    void Awake()
    {
        _restLocalPosition = transform.localPosition;

        Vector3 local = pressLocalDirection.sqrMagnitude < 1e-6f ? Vector3.back : pressLocalDirection.normalized;
        Vector3 world = transform.TransformDirection(local);
        Transform parent = transform.parent;
        Vector3 parentSpace = parent != null ? parent.InverseTransformDirection(world) : world;
        _pressOffsetLocal = parentSpace.normalized * pressDepth;
    }

    /// <summary>
    /// Called by <see cref="ElevatorFinishController"/> when it binds the finish piece. A controller that is not
    /// Netcode-spawned never replaces a spawned one: on a client the maze piece holds its own inert copy of the sync
    /// object alongside the replicated one, and the replicated one has to win regardless of which bound first.
    /// </summary>
    public void AssignController(ElevatorFinishController controller)
    {
        if (controller == null)
            return;
        if (_controller != null && _controller != controller && _controller.IsSpawned && !controller.IsSpawned)
            return;

        _controller = controller;
    }

    /// <summary>
    /// Occupancy for the close pad's "N/M aboard" prompt. False when this cab has no controller (a detached dev-scene
    /// cab), where there is no gate to report on.
    /// </summary>
    public bool TryGetOccupancyPrompt(Vector3 localPlayerFeetPosition, out int inside, out int required)
    {
        inside = 0;
        required = 0;
        if (_controller == null)
            return false;

        _controller.GetOccupancyForPrompt(localPlayerFeetPosition, out inside, out required);
        return true;
    }

    public bool IsInInteractRange(Vector3 worldPosition) =>
        (transform.position - worldPosition).sqrMagnitude <= interactMaxDistance * interactMaxDistance;

    /// <summary>
    /// A cab dropped into a dev scene on its own (Staging) has no ElevatorFinishSync next to it, so no controller ever
    /// binds and the pads would be dead. With no session running, drive the leaves directly instead so the cab can be
    /// authored and eyeballed in isolation — there is no occupancy gate and closing does not end the run in that mode.
    /// Never active in a session, where a null controller means "not bound yet" and local-only doors would just desync.
    /// </summary>
    bool TryGetDetachedCabDoors(out ElevatorSlidingDoors doors)
    {
        doors = null;
        if (_controller != null)
            return false;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            return false;

        if (_detachedDoors == null)
            _detachedDoors = GetComponentInParent<ElevatorSlidingDoors>();

        doors = _detachedDoors;
        return doors != null;
    }

    /// <summary>True when pressing this pad would actually do something for a player standing at <paramref name="interactorPosition"/>.</summary>
    public bool CanPress(Vector3 interactorPosition)
    {
        if (_controller == null)
        {
            if (!TryGetDetachedCabDoors(out ElevatorSlidingDoors detached))
                return false;

            return action == ElevatorButtonAction.OpenDoors ? !detached.IsOpen : detached.IsOpen;
        }

        if (!_controller.ElevatorButtonsResponsive)
            return false;
        if (RequiresInteractorInsideCab && !_controller.IsPositionInsideInterior(interactorPosition))
            return false;

        return action == ElevatorButtonAction.OpenDoors
            ? _controller.CanRequestDoorsOpen
            : _controller.CanRequestDoorsClose;
    }

    /// <summary>Player interact. Always gives the local press feedback; the door request is validated by the server.</summary>
    public void Press(Vector3 interactorPosition)
    {
        if (_controller == null)
        {
            if (TryGetDetachedCabDoors(out ElevatorSlidingDoors detached))
            {
                PlayPressFeedback();
                detached.SetOpen(action == ElevatorButtonAction.OpenDoors, immediate: false);

                if (!_loggedDetachedCab)
                {
                    _loggedDetachedCab = true;
                    Debug.Log(
                        $"[ElevatorFinish] '{name}' is driving this cab's doors directly — no {nameof(ElevatorFinishController)} in the scene, "
                        + "so there is no \"everyone aboard\" gate and closing will not end the run. Instantiate the finish piece (SeveranceFinish1) to test the full sequence.",
                        this);
                }

                return;
            }

            if (!_warnedMissingController)
            {
                _warnedMissingController = true;
                Debug.LogWarning(
                    $"[ElevatorFinish] '{name}' has no {nameof(ElevatorFinishController)} bound; the finish piece needs the ElevatorFinishSync prefab under its {nameof(ElevatorFinishSpawnMarker)}.",
                    this);
            }

            return;
        }

        PlayPressFeedback();

        if (action == ElevatorButtonAction.OpenDoors)
            _controller.RequestDoorsOpenFromButton(interactorPosition);
        else
            _controller.RequestDoorsCloseFromButton(interactorPosition);
    }

    /// <summary>
    /// The pad sinking plus its click, with no door request attached. The presser runs it immediately from
    /// <see cref="Press"/>; every other peer gets it from the controller's fan-out, so a pad someone else
    /// pushed is seen and heard being pushed rather than silently opening the doors.
    /// </summary>
    public void PlayPressFeedback()
    {
        _pressTime = 0f;
        PlayPressSfx();
    }

    void Update()
    {
        if (_pressTime < 0f)
            return;

        _pressTime += Time.unscaledDeltaTime;
        float total = PressInSeconds + PressHoldSeconds + PressOutSeconds;
        if (_pressTime >= total)
        {
            _pressTime = -1f;
            transform.localPosition = _restLocalPosition;
            return;
        }

        float depth01;
        if (_pressTime <= PressInSeconds)
            depth01 = _pressTime / PressInSeconds;
        else if (_pressTime <= PressInSeconds + PressHoldSeconds)
            depth01 = 1f;
        else
            depth01 = 1f - (_pressTime - PressInSeconds - PressHoldSeconds) / PressOutSeconds;

        transform.localPosition = _restLocalPosition + _pressOffsetLocal * depth01;
    }

    void PlayPressSfx()
    {
        if (pressClip == null)
            return;

        if (_sfx == null)
        {
            _sfx = GetComponent<AudioSource>();
            if (_sfx == null)
                _sfx = gameObject.AddComponent<AudioSource>();
            _sfx.playOnAwake = false;
            _sfx.loop = false;
            _sfx.spatialBlend = 1f;
            _sfx.minDistance = 0.5f;
            _sfx.maxDistance = 15f;
            _sfx.rolloffMode = AudioRolloffMode.Linear;
        }

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
        _sfx.PlayOneShot(pressClip, Mathf.Max(0f, pressVolume));
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (pressClip == null)
            pressClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/General/Click.wav");
    }
#endif
}
