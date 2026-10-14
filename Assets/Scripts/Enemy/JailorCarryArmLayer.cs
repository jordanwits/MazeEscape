using UnityEngine;

/// <summary>
/// Blends the Jailor's two arm override layers in and out:
/// - "Grab Reach" (upper body) while its own state machine is playing the Grab reach, so the legs keep running
///   on the base layer while he lunges at the player.
/// - "Carry Hold" (right arm frozen in the grab end-pose) from the replicated <c>Carrying</c> animator bool.
///
/// Runs on EVERY peer (unlike <see cref="JailorAI"/>, which is server-only): the layer weight is a
/// local animator property that <see cref="ServerNetworkAnimator"/> does not replicate, but the
/// <c>Carrying</c> bool and the <c>Grab</c> trigger it follows ARE replicated on the same server-authoritative
/// channel as Speed, so all clients raise the arm in sync without extra network traffic.
///
/// Both must stay regular (non-synced) layers — synced layers NPE NetworkAnimator at spawn.
/// Weight is driven here rather than via an always-on layer + empty passthrough state so the arms
/// animate normally (walk swing) whenever the Jailor is not grabbing or carrying.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public class JailorCarryArmLayer : MonoBehaviour
{
    [SerializeField] Animator animator;
    [Tooltip("Bool animator parameter that is true while a player is being carried.")]
    [SerializeField] string carryingParameter = "Carrying";
    [Tooltip("Override layer holding the right arm in the grab end-pose.")]
    [SerializeField] string carryLayerName = "Carry Hold";
    [Tooltip("Weight units per second when blending the held arm in/out.")]
    [SerializeField, Min(0.1f)] float blendSpeed = 10f;
    [Tooltip("Upper-body override layer that plays the grab reach over the running legs.")]
    [SerializeField] string grabLayerName = "Grab Reach";
    [Tooltip("State on the grab layer that plays the reach; the layer is weighted in only while it plays.")]
    [SerializeField] string grabStateName = "Grab";
    [Tooltip("Weight units per second when blending the grab reach in/out.")]
    [SerializeField, Min(0.1f)] float grabBlendSpeed = 12f;

    int _layerIndex = -1;
    int _grabLayerIndex = -1;
    int _carryingHash;
    int _grabStateHash;
    bool _hasCarryingParameter;
    float _weight;
    float _grabWeight;

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        _carryingHash = Animator.StringToHash(carryingParameter);
        _grabStateHash = Animator.StringToHash(grabStateName);
        ResolveAnimatorState();
    }

    void OnEnable()
    {
        // Controller can be (re)assigned after Awake; re-resolve so the layer/param lookups are valid.
        ResolveAnimatorState();
        _weight = 0f;
        _grabWeight = 0f;
        if (animator != null && _layerIndex >= 0)
            animator.SetLayerWeight(_layerIndex, 0f);
        if (animator != null && _grabLayerIndex >= 0)
            animator.SetLayerWeight(_grabLayerIndex, 0f);
    }

    void ResolveAnimatorState()
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            _layerIndex = -1;
            _grabLayerIndex = -1;
            _hasCarryingParameter = false;
            return;
        }

        _layerIndex = animator.GetLayerIndex(carryLayerName);
        _grabLayerIndex = animator.GetLayerIndex(grabLayerName);

        _hasCarryingParameter = false;
        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].type == AnimatorControllerParameterType.Bool && parameters[i].name == carryingParameter)
            {
                _hasCarryingParameter = true;
                break;
            }
        }
    }

    // Update (not LateUpdate) so the new weight is applied before the animator evaluates this frame;
    // JailorAI.LateUpdate then reads the resulting extended-hand pose for the carried-player anchor.
    void Update()
    {
        if (animator == null || (_layerIndex < 0 && _grabLayerIndex < 0))
        {
            ResolveAnimatorState();
            if (animator == null || (_layerIndex < 0 && _grabLayerIndex < 0))
                return;
        }

        if (_layerIndex >= 0)
        {
            bool carrying = _hasCarryingParameter && animator.GetBool(_carryingHash);
            float target = carrying ? 1f : 0f;
            _weight = Mathf.MoveTowards(_weight, target, blendSpeed * Time.deltaTime);
            animator.SetLayerWeight(_layerIndex, _weight);
        }

        if (_grabLayerIndex >= 0)
        {
            float target = IsGrabReachPlaying() ? 1f : 0f;
            _grabWeight = Mathf.MoveTowards(_grabWeight, target, grabBlendSpeed * Time.deltaTime);
            animator.SetLayerWeight(_grabLayerIndex, _grabWeight);
        }
    }

    /// <summary>
    /// True while the grab layer is in, or crossfading into, the Grab state — and false once it starts
    /// crossfading back out, so the weight ramp and the exit transition fade the reach together.
    /// </summary>
    bool IsGrabReachPlaying()
    {
        bool inTransition = animator.IsInTransition(_grabLayerIndex);
        if (inTransition)
            return animator.GetNextAnimatorStateInfo(_grabLayerIndex).shortNameHash == _grabStateHash;

        return animator.GetCurrentAnimatorStateInfo(_grabLayerIndex).shortNameHash == _grabStateHash;
    }
}
