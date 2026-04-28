using UnityEngine;

/// <summary>
/// Humanoid head look via Animator IK: body stays gameplay-facing while the head follows the view direction.
/// Must live on the same GameObject as the <see cref="Animator"/>. Requires IK Pass on the animator controller.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(50)]
public class PlayerHeadLook : MonoBehaviour
{
    [Header("References")]
    [SerializeField] Animator animator;
    [Tooltip("Transform whose forward is the look direction. If empty, resolves from PlayerController / Camera.main / CameraPitch.")]
    [SerializeField] Transform lookDirectionSource;

    [Header("IK weights")]
    [SerializeField, Range(0f, 1f)] float globalWeight = 1f;
    [SerializeField, Range(0f, 1f)] float bodyWeight = 0.18f;
    [SerializeField, Range(0f, 1f)] float headWeight = 0.92f;
    [SerializeField, Range(0f, 1f)] float eyesWeight = 0f;
    [SerializeField, Range(0f, 1f)] float clampWeight = 0.55f;
    [SerializeField] float lookDistance = 2f;

    [Header("Melee / upper-body layer")]
    [Tooltip("Animator layer that plays masked melee (Upper Body layer on Player). Look-at body IK stacks with punch torso motion and lifts the swing; we disable body contribution while that state is active.")]
    [SerializeField] bool resolveUpperBodyLayerByName = true;
    [SerializeField] string upperBodyLayerName = "Upper Body";
    [SerializeField] int upperBodyLayerIndexFallback = 1;
    [SerializeField] string meleeStateNameOnUpperLayer = "RightHook";

    [Header("Optional")]
    [SerializeField] PlayerRagdollController ragdollController;

    Transform _cachedCameraPitch;
    PlayerController _playerController;

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();
        _playerController = GetComponent<PlayerController>();
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (animator == null || !animator.isHuman || !animator.enabled)
            return;

        if (ragdollController != null && (ragdollController.IsRagdolled || ragdollController.IsGettingUp))
        {
            animator.SetLookAtWeight(0f);
            return;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null)
        {
            animator.SetLookAtWeight(0f);
            return;
        }

        if (IsCameraPitchParentedToHead(head))
        {
            animator.SetLookAtWeight(0f);
            return;
        }

        Transform source = ResolveLookDirectionSource();
        if (source == null)
        {
            animator.SetLookAtWeight(0f);
            return;
        }

        Vector3 lookAt = head.position + source.forward * Mathf.Max(0.25f, lookDistance);

        float bodyW = bodyWeight;
        if (MeleeUpperBodyDominatesLookLayer())
            bodyW = 0f;

        animator.SetLookAtWeight(globalWeight, bodyW, headWeight, eyesWeight, clampWeight);
        animator.SetLookAtPosition(lookAt);
    }

    /// <summary>
    /// True while the upper-body layer is blending to or playing the melee punch (additive spine look would skew the arc vs the clip).
    /// </summary>
    bool MeleeUpperBodyDominatesLookLayer()
    {
        int layer = ResolveUpperBodyPunchLayer();
        if (layer < 0 || layer >= animator.layerCount || string.IsNullOrEmpty(meleeStateNameOnUpperLayer))
            return false;

        if (animator.IsInTransition(layer))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            if (next.IsName(meleeStateNameOnUpperLayer))
                return true;
        }

        return animator.GetCurrentAnimatorStateInfo(layer).IsName(meleeStateNameOnUpperLayer);
    }

    int ResolveUpperBodyPunchLayer()
    {
        if (!resolveUpperBodyLayerByName || string.IsNullOrEmpty(upperBodyLayerName))
            return upperBodyLayerIndexFallback;

        for (int i = 0; i < animator.layerCount; i++)
        {
            if (animator.GetLayerName(i) == upperBodyLayerName)
                return i;
        }

        return upperBodyLayerIndexFallback;
    }

    Transform ResolveLookDirectionSource()
    {
        if (lookDirectionSource != null)
            return lookDirectionSource;

        if (_playerController != null && _playerController.UsesFirstPersonLook)
        {
            Transform cam = _playerController.LookPitchTransform;
            if (cam != null)
                return cam;
        }

        if (Camera.main != null)
            return Camera.main.transform;

        if (_cachedCameraPitch == null)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "CameraPitch")
                {
                    _cachedCameraPitch = t;
                    break;
                }
            }
        }

        return _cachedCameraPitch;
    }

    bool IsCameraPitchParentedToHead(Transform head)
    {
        if (_playerController == null || head == null)
            return false;

        Transform pitch = _playerController.CameraPitchNode;
        if (pitch == null)
            return false;

        return pitch.IsChildOf(head);
    }
}
