using UnityEngine;

/// <summary>
/// Keeps the first-person <see cref="PlayerController.CameraPitchNode"/> at the animated head.
/// The camera is usually parented to the skinned mesh object (e.g. body mesh root), not the head bone, so
/// run/crouch/punch/head-bob and skinning move the head while the view pivot does not, which can clip
/// into the back of the skull. This runs after <see cref="MovementViewBob"/> to match the final pose.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(600)]
public class FirstPersonViewHeadSync : MonoBehaviour
{
    [SerializeField] PlayerController playerController;
    [SerializeField] Animator animator;
    [SerializeField] PlayerRagdollController ragdoll;

    [Tooltip("Where the view sits relative to the head bone: X=right, Y=up, Z=forward of the head. Tune if you still see the hairline or interior.")]
    [SerializeField] Vector3 viewOffsetInHeadLocalSpace = new Vector3(0.0112f, 0.12f, 0.1f);

    [Tooltip("How quickly the view catches the head (seconds). Slightly higher = less bob from footsteps; 0 = instant (old behavior).")]
    [SerializeField] float positionFollowSmoothTime = 0.055f;

    Vector3 _positionSmoothVelocity;
    bool _wasFollowingHead;

    void Awake()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (ragdoll == null)
            ragdoll = GetComponent<PlayerRagdollController>();
    }

    void LateUpdate()
    {
        if (playerController == null || !playerController.UsesFirstPersonLook || !playerController.HasLocalControl)
        {
            _wasFollowingHead = false;
            return;
        }
        if (animator == null || !animator.isHuman)
        {
            _wasFollowingHead = false;
            return;
        }
        if (ragdoll != null && (ragdoll.IsRagdolled || ragdoll.IsGettingUp))
        {
            _wasFollowingHead = false;
            return;
        }

        Transform pitch = playerController.CameraPitchNode;
        if (pitch == null)
        {
            _wasFollowingHead = false;
            return;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null)
        {
            _wasFollowingHead = false;
            return;
        }
        if (pitch.IsChildOf(head))
        {
            _wasFollowingHead = false;
            return;
        }

        Vector3 target = head.position + head.rotation * viewOffsetInHeadLocalSpace;

        if (positionFollowSmoothTime <= 0f)
        {
            pitch.position = target;
            _positionSmoothVelocity = Vector3.zero;
            _wasFollowingHead = true;
            return;
        }

        if (!_wasFollowingHead)
        {
            pitch.position = target;
            _positionSmoothVelocity = Vector3.zero;
            _wasFollowingHead = true;
            return;
        }

        pitch.position = Vector3.SmoothDamp(pitch.position, target, ref _positionSmoothVelocity, positionFollowSmoothTime);
    }
}
