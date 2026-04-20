using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Enables/disables ragdoll on a humanoid (Rigidbodies + joints under this hierarchy).
/// Add ragdoll parts with GameObject &gt; 3D Object &gt; Ragdoll… on your skinned mesh, then tune colliders/masses.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(500)]
public class PlayerRagdollController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] CharacterController characterController;
    [SerializeField] Animator animator;
    [Tooltip("If empty, uses HumanBodyBones.Hips from the animator.")]
    [SerializeField] Rigidbody hipsRigidbody;

    [Header("Recovery")]
    [Tooltip("When exiting ragdoll, root Y is set from a raycast down from the hips; max ray length.")]
    [SerializeField] float groundSnapRayLength = 4f;
    [SerializeField] LayerMask groundLayers = ~0;
    [Tooltip("When ragdoll ends because the body settled on the ground (see land recovery).")]
    [SerializeField] bool recoverWhenLandedAndStill = true;
    [Tooltip("Land recovery only starts after the ragdoll has clearly reacted (airborne or moving fast). Stops instant stand-up while still grounded.")]
    [SerializeField] float landRecoverPrimeSpeedThreshold = 2.1f;
    [Tooltip("If still not \"primed\" after this many seconds, allow land recovery anyway (mild bumps).")]
    [SerializeField] float landRecoverPrimeFallbackSeconds = 0.9f;
    [Tooltip("No land recovery until ragdoll has been active this long (after primed).")]
    [SerializeField] float minRagdollTimeBeforeLandRecover = 0.55f;
    [Tooltip("Hips speed must stay below this (m/s) to count as settled.")]
    [SerializeField] float landedSpeedThreshold = 1.25f;
    [Tooltip("Must be grounded and under the speed threshold continuously for this long to stand up.")]
    [SerializeField] float groundedStillTimeToRecover = 0.5f;
    [Tooltip("Short ray from hips to detect ground under the ragdoll.")]
    [SerializeField] float landGroundCheckDistance = 0.45f;
    [Tooltip("Failsafe stand-up if land logic never finishes (stuck ragdoll).")]
    [SerializeField] float recoverFallbackAfterSeconds = 8f;

    [Header("Get-Up Animation")]
    [Tooltip("Animator trigger for getting up from back (hips up points toward sky).")]
    [SerializeField] string getUpBackTrigger = "GetUpBack";
    [Tooltip("Animator trigger for getting up from stomach (hips up points toward ground).")]
    [SerializeField] string getUpFrontTrigger = "GetUpFront";
    [Tooltip("If true, play a get-up animation instead of snapping to idle.")]
    [SerializeField] bool useGetUpAnimation = true;
    [Tooltip("Height offset above ground for get-up animation root. Usually 0 for Mixamo. Adjust if feet/hands clip.")]
    [SerializeField] float getUpGroundOffset = 0f;
    [Tooltip("Fallback only (no foot bones): added to CC snap in SnapToStandingHeight. Foot-based get-up ignores this in favor of sole alignment.")]
    [SerializeField] float standingGroundYOffset = 0f;
    [Tooltip("Extra Y added to the foot target plane during get-up (negative pulls the whole character down if he still hovers).")]
    [SerializeField] float getUpFootVerticalBias = 0f;

    [Header("Optional")]
    [Tooltip("If true, bone colliders are disabled while not ragdolled (avoids physics fighting CharacterController).")]
    [SerializeField] bool disableBoneCollidersWhileAnimated = true;
    [Tooltip("If greater than zero, DeactivateRagdoll runs automatically after this many seconds.")]
    [SerializeField] float autoRecoverAfterSeconds = 0f;
    [Tooltip("Caps knockback when using ForceMode.Impulse. 0 = no cap.")]
    [SerializeField] float maxImpulseMagnitude = 18f;
    [Tooltip("Caps knockback when using ForceMode.VelocityChange (delta-V in m/s). 0 = no cap.")]
    [SerializeField] float maxVelocityChangeMagnitude = 16f;

    readonly List<Rigidbody> _ragdollBodies = new List<Rigidbody>();
    readonly List<Collider> _ragdollColliders = new List<Collider>();
    MovementViewBob _movementViewBob;

    Coroutine _autoRecoverRoutine;
    Coroutine _landRecoverRoutine;
    Coroutine _getUpRoutine;

    int _getUpBackHash;
    int _getUpFrontHash;

    Transform _getUpLeftFoot;
    Transform _getUpRightFoot;
    float _getUpLeftSole;
    float _getUpRightSole;

    public bool IsRagdolled { get; private set; }
    public bool IsGettingUp { get; private set; }

    void Awake()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (animator == null)
            animator = GetComponent<Animator>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        _movementViewBob = GetComponent<MovementViewBob>();

        _getUpBackHash = Animator.StringToHash(getUpBackTrigger);
        _getUpFrontHash = Animator.StringToHash(getUpFrontTrigger);

        CacheRagdollParts();
        ResolveHips();

        if (hipsRigidbody != null)
            SetRagdollPhysicsActive(false);
    }

    void CacheRagdollParts()
    {
        _ragdollBodies.Clear();
        _ragdollColliders.Clear();

        Rigidbody[] bodies = GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody rb = bodies[i];
            if (rb == null || rb.gameObject == gameObject)
                continue;

            _ragdollBodies.Add(rb);
            Collider[] cols = rb.GetComponents<Collider>();
            for (int c = 0; c < cols.Length; c++)
            {
                if (cols[c] != null)
                    _ragdollColliders.Add(cols[c]);
            }
        }

        if (_ragdollBodies.Count == 0)
        {
            Debug.LogWarning(
                $"{nameof(PlayerRagdollController)}: No child Rigidbodies found. Use the Ragdoll wizard on your character mesh (bones need Rigidbodies + joints).",
                this);
        }
    }

    void ResolveHips()
    {
        if (hipsRigidbody != null)
            return;

        if (animator != null && animator.isHuman)
        {
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null)
                hipsRigidbody = hips.GetComponent<Rigidbody>();
        }

        if (hipsRigidbody == null && _ragdollBodies.Count > 0)
            hipsRigidbody = _ragdollBodies[0];
    }

    /// <summary>
    /// Turns on ragdoll, disables CharacterController + animator control, and applies an optional impulse.
    /// </summary>
    public void ActivateRagdoll(Vector3 worldForce, Vector3 worldForcePosition, ForceMode forceMode = ForceMode.Impulse)
    {
        if (IsRagdolled)
            return;

        if (_ragdollBodies.Count == 0)
            return;

        ResolveHips();
        if (hipsRigidbody == null)
        {
            Debug.LogWarning($"{nameof(PlayerRagdollController)}: No hips Rigidbody; cannot ragdoll.", this);
            return;
        }

        IsRagdolled = true;

        if (characterController != null)
            characterController.enabled = false;

        Physics.SyncTransforms();

        if (animator != null)
            animator.enabled = false;

        if (_movementViewBob != null)
            _movementViewBob.enabled = false;

        SetRagdollPhysicsActive(true);

        Vector3 appliedForce = worldForce;
        if (forceMode == ForceMode.Impulse && maxImpulseMagnitude > 0f && appliedForce.sqrMagnitude > maxImpulseMagnitude * maxImpulseMagnitude)
            appliedForce = appliedForce.normalized * maxImpulseMagnitude;
        if (forceMode == ForceMode.VelocityChange && maxVelocityChangeMagnitude > 0f
            && appliedForce.sqrMagnitude > maxVelocityChangeMagnitude * maxVelocityChangeMagnitude)
            appliedForce = appliedForce.normalized * maxVelocityChangeMagnitude;

        // Apply to ALL ragdoll bodies so the whole body launches (joints absorb single-body impulse).
        if (appliedForce.sqrMagnitude > 1e-6f)
        {
            for (int i = 0; i < _ragdollBodies.Count; i++)
            {
                Rigidbody rb = _ragdollBodies[i];
                if (rb != null)
                    rb.AddForce(appliedForce, forceMode);
            }
        }

        StopRecoveryCoroutines();

        if (autoRecoverAfterSeconds > 0f)
            _autoRecoverRoutine = StartCoroutine(AutoRecoverAfterDelay());

        if (recoverWhenLandedAndStill)
            _landRecoverRoutine = StartCoroutine(LandRecoverWhenStillRoutine());
    }

    IEnumerator AutoRecoverAfterDelay()
    {
        yield return new WaitForSeconds(autoRecoverAfterSeconds);
        _autoRecoverRoutine = null;
        DeactivateRagdoll();
    }

    IEnumerator LandRecoverWhenStillRoutine()
    {
        float ragdollStart = Time.time;
        float stable = 0f;
        bool primed = false;
        float primedAt = -1f;

        while (IsRagdolled)
        {
            yield return new WaitForFixedUpdate();

            if (!IsRagdolled)
                yield break;

            ResolveHips();
            if (hipsRigidbody == null)
                yield break;

            float elapsed = Time.time - ragdollStart;
            if (recoverFallbackAfterSeconds > 0f && elapsed >= recoverFallbackAfterSeconds)
            {
                _landRecoverRoutine = null;
                DeactivateRagdoll();
                yield break;
            }

            Vector3 hipsPos = hipsRigidbody.position;
            bool grounded = Physics.Raycast(
                hipsPos,
                Vector3.down,
                landGroundCheckDistance,
                groundLayers,
                QueryTriggerInteraction.Ignore);

            float speed = hipsRigidbody.linearVelocity.magnitude;

            if (!primed)
            {
                if (!grounded
                    || speed >= landRecoverPrimeSpeedThreshold
                    || elapsed >= landRecoverPrimeFallbackSeconds)
                {
                    primed = true;
                    primedAt = Time.time;
                }

                stable = 0f;
                continue;
            }

            if (Time.time - primedAt < minRagdollTimeBeforeLandRecover)
            {
                stable = 0f;
                continue;
            }

            if (grounded && speed < landedSpeedThreshold)
                stable += Time.fixedDeltaTime;
            else
                stable = 0f;

            if (stable >= groundedStillTimeToRecover)
            {
                _landRecoverRoutine = null;
                DeactivateRagdoll();
                yield break;
            }
        }

        _landRecoverRoutine = null;
    }

    /// <summary>
    /// Stops ragdoll, snaps the player upright near the ground under the hips, and re-enables control.
    /// </summary>
    public void DeactivateRagdoll()
    {
        if (!IsRagdolled)
            return;

        StopRecoveryCoroutines();
        ResolveHips();

        bool onBack = IsLyingOnBack();

        SetRagdollPhysicsActive(false);

        if (useGetUpAnimation && animator != null)
        {
            SnapToGroundForGetUp();
            _getUpRoutine = StartCoroutine(GetUpAnimationRoutine(onBack));
            return;
        }

        SnapToStandingHeight();
        FinishStandUp();
    }

    void SnapToGroundForGetUp()
    {
        if (hipsRigidbody == null)
            return;

        // Find where the ground is under the hips
        Vector3 sample = hipsRigidbody.position + Vector3.up * 0.5f;
        float groundY = hipsRigidbody.position.y - 0.5f;
        if (Physics.Raycast(sample, Vector3.down, out RaycastHit hit, groundSnapRayLength, groundLayers, QueryTriggerInteraction.Ignore))
        {
            groundY = hit.point.y;
        }

        // For Mixamo-style animations: the animation root is at Y=0 (floor level)
        // The animation itself handles the body being above the floor
        // So we place the transform root at the ground Y + offset
        transform.position = new Vector3(hipsRigidbody.position.x, groundY + getUpGroundOffset, hipsRigidbody.position.z);

        // Face the direction the hips are pointing
        Vector3 hipsForward = hipsRigidbody.transform.forward;
        hipsForward.y = 0f;
        if (hipsForward.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(hipsForward.normalized, Vector3.up);
    }

    void SnapToStandingHeight()
    {
        if (characterController == null)
            return;

        Vector3 sample = transform.position + Vector3.up * 0.5f;
        if (Physics.Raycast(sample, Vector3.down, out RaycastHit hit, groundSnapRayLength, groundLayers, QueryTriggerInteraction.Ignore))
        {
            float halfHeight = characterController.height * 0.5f;
            float centerY = characterController.center.y;
            // Capsule bottom = transform.y + centerY - halfHeight; align that to hit surface (no skinWidth — that was lifting the mesh visibly).
            float targetY = hit.point.y + halfHeight - centerY + standingGroundYOffset;
            transform.position = new Vector3(transform.position.x, targetY, transform.position.z);
        }
    }

    bool IsLyingOnBack()
    {
        if (hipsRigidbody == null)
            return true;

        Vector3 hipsUp = hipsRigidbody.transform.up;
        return Vector3.Dot(hipsUp, Vector3.up) > 0f;
    }

    /// <summary>
    /// Ground height under the character (ray down from above transform).
    /// </summary>
    bool TrySampleGroundBelow(out float groundHitY)
    {
        Vector3 sample = transform.position + Vector3.up * 0.5f;
        if (Physics.Raycast(sample, Vector3.down, out RaycastHit hit, groundSnapRayLength, groundLayers, QueryTriggerInteraction.Ignore))
        {
            groundHitY = hit.point.y;
            return true;
        }

        groundHitY = transform.position.y;
        return false;
    }

    float GetGroundYUnderWorldXZ(Vector3 worldPos)
    {
        float rayTop = Mathf.Max(worldPos.y + 2.5f, transform.position.y + 4f);
        Vector3 origin = new Vector3(worldPos.x, rayTop, worldPos.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 40f, groundLayers, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return TrySampleGroundBelow(out float gy) ? gy : transform.position.y;
    }

    void ApplyGetUpFootPlanting()
    {
        if (_getUpLeftFoot == null && _getUpRightFoot == null)
            return;

        float planeY;
        if (_getUpLeftFoot != null && _getUpRightFoot != null)
            planeY = Mathf.Min(GetGroundYUnderWorldXZ(_getUpLeftFoot.position), GetGroundYUnderWorldXZ(_getUpRightFoot.position));
        else if (_getUpLeftFoot != null)
            planeY = GetGroundYUnderWorldXZ(_getUpLeftFoot.position);
        else
            planeY = GetGroundYUnderWorldXZ(_getUpRightFoot.position);

        float targetSoleY = planeY + getUpGroundOffset + getUpFootVerticalBias;

        float lowestSoleY = float.PositiveInfinity;
        if (_getUpLeftFoot != null)
        {
            float s = _getUpLeftSole > 1e-4f ? _getUpLeftSole : 0.03f;
            Vector3 sole = _getUpLeftFoot.position - _getUpLeftFoot.up * s;
            lowestSoleY = Mathf.Min(lowestSoleY, sole.y);
        }

        if (_getUpRightFoot != null)
        {
            float s = _getUpRightSole > 1e-4f ? _getUpRightSole : 0.03f;
            Vector3 sole = _getUpRightFoot.position - _getUpRightFoot.up * s;
            lowestSoleY = Mathf.Min(lowestSoleY, sole.y);
        }

        if (lowestSoleY >= float.PositiveInfinity)
            return;

        float delta = targetSoleY - lowestSoleY;
        if (Mathf.Abs(delta) > 1e-5f)
            transform.position += new Vector3(0f, delta, 0f);
    }

    void LateUpdate()
    {
        if (!IsGettingUp || IsRagdolled)
            return;
        if (characterController != null && characterController.enabled)
            return;

        ApplyGetUpFootPlanting();
    }

    IEnumerator GetUpAnimationRoutine(bool onBack)
    {
        IsGettingUp = true;
        IsRagdolled = false;

        float groundY = transform.position.y;
        Vector3 sample = hipsRigidbody != null ? hipsRigidbody.position + Vector3.up * 0.5f : transform.position + Vector3.up * 0.5f;
        if (Physics.Raycast(sample, Vector3.down, out RaycastHit hit, groundSnapRayLength, groundLayers, QueryTriggerInteraction.Ignore))
            groundY = hit.point.y;

        // Position at ground level for animation start
        transform.position = new Vector3(transform.position.x, groundY + getUpGroundOffset, transform.position.z);

        if (animator != null)
        {
            animator.enabled = true;
            animator.Rebind();
            animator.Update(0f);

            int triggerHash = onBack ? _getUpBackHash : _getUpFrontHash;
            animator.SetTrigger(triggerHash);
        }

        // Foot planting runs in LateUpdate (after Animator) so bone poses match the clip.
        _getUpLeftFoot = null;
        _getUpRightFoot = null;
        if (animator != null && animator.isHuman)
        {
            _getUpLeftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            _getUpRightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            _getUpLeftSole = animator.leftFeetBottomHeight > 1e-4f ? animator.leftFeetBottomHeight : 0.03f;
            _getUpRightSole = animator.rightFeetBottomHeight > 1e-4f ? animator.rightFeetBottomHeight : 0.03f;
        }

        yield return null;
        yield return null;

        float clipLength = 2f;
        if (animator != null)
        {
            AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            if (stateInfo.length > 0.1f)
                clipLength = stateInfo.length;
        }

        float endTime = Time.time + clipLength + 0.1f;
        while (Time.time < endTime)
            yield return null;

        ApplyGetUpFootPlanting();

        if (_getUpLeftFoot == null && _getUpRightFoot == null && characterController != null)
        {
            float halfHeight = characterController.height * 0.5f;
            float centerY = characterController.center.y;
            float gy = groundY;
            if (TrySampleGroundBelow(out float g))
                gy = g;
            transform.position = new Vector3(
                transform.position.x,
                gy + halfHeight - centerY + standingGroundYOffset,
                transform.position.z);
        }

        _getUpLeftFoot = null;
        _getUpRightFoot = null;

        FinishStandUp();
        _getUpRoutine = null;
    }

    void FinishStandUp()
    {
        IsGettingUp = false;
        IsRagdolled = false;

        if (animator != null)
        {
            animator.enabled = true;
            animator.ResetTrigger(getUpBackTrigger);
            animator.ResetTrigger(getUpFrontTrigger);
        }

        if (_movementViewBob != null)
            _movementViewBob.enabled = true;

        if (characterController != null)
            characterController.enabled = true;

        PlayerController playerController = GetComponent<PlayerController>();
        if (playerController != null)
            playerController.ResetLocomotionAfterRagdollRecover();
    }

    /// <summary>
    /// Called before teleport/respawn so the CharacterController can move the root without fighting ragdoll bodies.
    /// </summary>
    public void ForceExitRagdollWithoutGroundSnap()
    {
        if (!IsRagdolled && !IsGettingUp)
            return;

        StopRecoveryCoroutines();
        SetRagdollPhysicsActive(false);

        if (animator != null)
        {
            animator.enabled = true;
            animator.Rebind();
            animator.Update(0f);
        }

        if (_movementViewBob != null)
            _movementViewBob.enabled = true;

        if (characterController != null)
            characterController.enabled = true;

        IsRagdolled = false;
        IsGettingUp = false;
        _getUpLeftFoot = null;
        _getUpRightFoot = null;

        PlayerController playerController = GetComponent<PlayerController>();
        if (playerController != null)
            playerController.ResetLocomotionAfterRagdollRecover();
    }

    void StopRecoveryCoroutines()
    {
        if (_autoRecoverRoutine != null)
        {
            StopCoroutine(_autoRecoverRoutine);
            _autoRecoverRoutine = null;
        }

        if (_landRecoverRoutine != null)
        {
            StopCoroutine(_landRecoverRoutine);
            _landRecoverRoutine = null;
        }

        if (_getUpRoutine != null)
        {
            StopCoroutine(_getUpRoutine);
            _getUpRoutine = null;
            _getUpLeftFoot = null;
            _getUpRightFoot = null;
        }
    }

    void SetRagdollPhysicsActive(bool ragdollOn)
    {
        for (int i = 0; i < _ragdollBodies.Count; i++)
        {
            Rigidbody rb = _ragdollBodies[i];
            if (rb == null)
                continue;

            if (!ragdollOn)
            {
                // Clear velocity while still dynamic; Unity does not allow setting velocity on kinematic bodies.
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                rb.isKinematic = true;
                rb.useGravity = false;
            }
            else
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }
        }

        if (disableBoneCollidersWhileAnimated)
        {
            for (int i = 0; i < _ragdollColliders.Count; i++)
            {
                Collider col = _ragdollColliders[i];
                if (col != null)
                    col.enabled = ragdollOn;
            }
        }
    }
}
