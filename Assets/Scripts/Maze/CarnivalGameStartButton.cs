using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Contract a minigame controller implements so a <see cref="CarnivalGameStartButton"/> can drive it.
/// Implemented by <see cref="BasketballGameController"/> and any future carnival minigame controller
/// that wants to expose a "Press E to start" button.
/// </summary>
public interface ICarnivalGameStart
{
    /// <summary>True if a start request would begin a fresh round right now.</summary>
    bool CanStartNow { get; }

    /// <summary>Called by the button on E press. Implementor handles ServerRpc routing as needed.</summary>
    void ProcessStartRequest(PlayerController interactor);
}

/// <summary>
/// Generic interactable "Start" prop for any carnival minigame. <see cref="PlayerController"/>
/// raycasts find this via the interact layer mask and calls <see cref="RequestStart"/> on E.
/// The button forwards to the assigned host (any MonoBehaviour implementing <see cref="ICarnivalGameStart"/>).
/// Per-prefab prompt strings let each minigame display its own "Press E to start ..." text.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalGameStartButton : MonoBehaviour
{
    [FormerlySerializedAs("controller")]
    [SerializeField, Tooltip("Any MonoBehaviour implementing ICarnivalGameStart (e.g. BasketballGameController). Auto-resolved from a parent if left empty.")]
    MonoBehaviour controllerHost;

    [SerializeField] string startPromptMessage = "Press E to start";
    [SerializeField] string inProgressPromptMessage = "Game in progress";

    ICarnivalGameStart _resolvedHost;

    public string StartPromptMessage => startPromptMessage;
    public string InProgressPromptMessage => inProgressPromptMessage;
    public bool CanStart
    {
        get
        {
            ICarnivalGameStart h = ResolveHost();
            return h != null && h.CanStartNow;
        }
    }

    void Reset()
    {
        if (GetComponentInParent<ICarnivalGameStart>(true) is MonoBehaviour mb)
            controllerHost = mb;
    }

    void Awake()
    {
        ResolveHost();
    }

    ICarnivalGameStart ResolveHost()
    {
        if (_resolvedHost != null)
            return _resolvedHost;
        _resolvedHost = controllerHost as ICarnivalGameStart;
        if (_resolvedHost == null)
            _resolvedHost = GetComponentInParent<ICarnivalGameStart>(true);
        return _resolvedHost;
    }

    public void RequestStart(PlayerController interactor)
    {
        Debug.Log($"[CarnivalGameStartButton] RequestStart called on {name}", this);
        if (interactor == null)
        {
            Debug.LogWarning($"[CarnivalGameStartButton] interactor is null", this);
            return;
        }
        ICarnivalGameStart host = ResolveHost();
        if (host == null)
        {
            Debug.LogWarning($"[CarnivalGameStartButton] no ICarnivalGameStart host resolved (controllerHost field or parent chain). controllerHost = {(controllerHost != null ? controllerHost.GetType().Name : "null")}", this);
            return;
        }
        if (!host.CanStartNow)
        {
            Debug.Log($"[CarnivalGameStartButton] host says CanStartNow = false (round already active)", this);
            return;
        }
        host.ProcessStartRequest(interactor);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (controllerHost != null && controllerHost is not ICarnivalGameStart)
            Debug.LogError(
                $"{name}: '{controllerHost.GetType().Name}' is assigned to controllerHost but does not implement ICarnivalGameStart.",
                this);
    }
#endif
}
