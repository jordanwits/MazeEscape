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

        animator.SetLookAtWeight(globalWeight, bodyWeight, headWeight, eyesWeight, clampWeight);
        animator.SetLookAtPosition(lookAt);
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
