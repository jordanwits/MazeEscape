using UnityEngine;

/// <summary>
/// Blends the Jailor's "Carry Hold" override layer (right arm frozen in the grab end-pose) in and
/// out based on the replicated <c>Carrying</c> animator bool.
///
/// Runs on EVERY peer (unlike <see cref="JailorAI"/>, which is server-only): the layer weight is a
/// local animator property that <see cref="ServerNetworkAnimator"/> does not replicate, but the
/// <c>Carrying</c> bool it reads IS replicated on the same server-authoritative channel as Speed/Grab,
/// so all clients raise the held arm in sync without extra network traffic.
///
/// The layer must stay a regular (non-synced) layer — synced layers NPE NetworkAnimator at spawn.
/// Weight is driven here rather than via an always-on layer + empty passthrough state so the right
/// arm animates normally (walk swing, grab reach) whenever the Jailor is not carrying.
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

    int _layerIndex = -1;
    int _carryingHash;
    bool _hasCarryingParameter;
    float _weight;

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        _carryingHash = Animator.StringToHash(carryingParameter);
        ResolveAnimatorState();
    }

    void OnEnable()
    {
        // Controller can be (re)assigned after Awake; re-resolve so the layer/param lookups are valid.
        ResolveAnimatorState();
        _weight = 0f;
        if (animator != null && _layerIndex >= 0)
            animator.SetLayerWeight(_layerIndex, 0f);
    }

    void ResolveAnimatorState()
    {
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            _layerIndex = -1;
            _hasCarryingParameter = false;
            return;
        }

        _layerIndex = animator.GetLayerIndex(carryLayerName);

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
        if (animator == null || _layerIndex < 0)
        {
            ResolveAnimatorState();
            if (animator == null || _layerIndex < 0)
                return;
        }

        bool carrying = _hasCarryingParameter && animator.GetBool(_carryingHash);
        float target = carrying ? 1f : 0f;
        _weight = Mathf.MoveTowards(_weight, target, blendSpeed * Time.deltaTime);
        animator.SetLayerWeight(_layerIndex, _weight);
    }
}
