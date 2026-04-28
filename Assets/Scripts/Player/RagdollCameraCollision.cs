using UnityEngine;

/// <summary>
/// Prevents the camera from clipping through walls during ragdoll mode.
/// Attach to the same GameObject as PlayerController.
/// Offsets the camera pivot (CameraPitch) to keep the camera out of geometry.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(601)]
public class RagdollCameraCollision : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The CameraPitch transform (parent of the camera). If empty, auto-finds.")]
    [SerializeField] Transform cameraPitchTransform;
    [Tooltip("The actual camera transform. If empty, uses Camera.main.")]
    [SerializeField] Transform cameraTransform;
    [Tooltip("The ragdoll controller. If empty, auto-finds.")]
    [SerializeField] PlayerRagdollController ragdollController;

    [Header("Collision Settings")]
    [Tooltip("Radius for collision detection.")]
    [SerializeField] float collisionRadius = 0.2f;
    [Tooltip("Minimum clearance from surfaces.")]
    [SerializeField] float minClearance = 0.3f;
    [Tooltip("Layers to check for collision.")]
    [SerializeField] LayerMask collisionLayers = ~0;

    Vector3 _originalPivotLocalPos;
    /// <summary>Parent of CameraPitch when <see cref="_originalPivotLocalPos"/> was captured. If <see cref="PlayerController"/> reparents the pivot (head ↔ mesh) in Update, LateUpdate must not apply stale locals meant for another parent.</summary>
    Transform _originalPivotParent;
    bool _isActive;
    bool _hasSavedOriginal;
    Collider[] _playerColliders;

    void Awake()
    {
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();

        // Cache all colliders on this player to ignore them
        _playerColliders = GetComponentsInChildren<Collider>(true);

        // Exclude player layer
        int playerLayer = gameObject.layer;
        if (playerLayer >= 0 && playerLayer < 32)
            collisionLayers &= ~(1 << playerLayer);
    }

    void Start()
    {
        FindCameraReferences();
    }

    void FindCameraReferences()
    {
        if (cameraTransform == null)
        {
            PlayerController playerController = GetComponent<PlayerController>();
            if (playerController != null && playerController.LookPitchTransform != null
                && playerController.LookPitchTransform.IsChildOf(transform))
            {
                cameraTransform = playerController.LookPitchTransform;
            }
            else if (Camera.main != null && Camera.main.transform.IsChildOf(transform))
            {
                cameraTransform = Camera.main.transform;
            }
        }

        if (cameraPitchTransform == null && cameraTransform != null)
        {
            // CameraPitch is typically the parent of the camera
            Transform parent = cameraTransform.parent;
            while (parent != null)
            {
                if (parent.name.Contains("CameraPitch") || parent.name.Contains("Pitch"))
                {
                    cameraPitchTransform = parent;
                    break;
                }
                // Stop if we reach the player root
                if (parent.GetComponent<CharacterController>() != null)
                    break;
                parent = parent.parent;
            }

            // If not found, use the camera's direct parent
            if (cameraPitchTransform == null && cameraTransform.parent != null)
                cameraPitchTransform = cameraTransform.parent;
        }
    }

    void LateUpdate()
    {
        if (ragdollController == null)
            return;

        bool shouldBeActive = ragdollController.IsRagdolled || ragdollController.IsGettingUp;

        if (!shouldBeActive)
        {
            if (_isActive && _hasSavedOriginal && cameraPitchTransform != null
                && _originalPivotParent != null && cameraPitchTransform.parent == _originalPivotParent)
            {
                // Only if parent unchanged: PlayerController often reparents CameraPitch head→mesh earlier this frame — applying stale locals would misplace the pivot.
                cameraPitchTransform.localPosition = _originalPivotLocalPos;
            }
            _isActive = false;
            _hasSavedOriginal = false;
            _originalPivotParent = null;
            return;
        }

        if (cameraPitchTransform == null || cameraTransform == null)
        {
            FindCameraReferences();
            if (cameraPitchTransform == null || cameraTransform == null)
                return;
        }

        // Save original on first active frame (locals are only valid relative to current parent).
        if (!_isActive)
        {
            _originalPivotParent = cameraPitchTransform.parent;
            _originalPivotLocalPos = cameraPitchTransform.localPosition;
            _hasSavedOriginal = true;
        }
        _isActive = true;

        ApplyCollisionOffset();
    }

    void ApplyCollisionOffset()
    {
        // Reset to original first
        cameraPitchTransform.localPosition = _originalPivotLocalPos;

        // Get the camera's world position
        Vector3 camWorldPos = cameraTransform.position;

        // Check if we need to adjust
        if (!NeedsAdjustment(camWorldPos, out Vector3 validPos))
            return;

        // Calculate world offset needed
        Vector3 worldOffset = validPos - camWorldPos;

        // Convert to local offset for the pivot
        Vector3 localOffset;
        if (cameraPitchTransform.parent != null)
            localOffset = cameraPitchTransform.parent.InverseTransformDirection(worldOffset);
        else
            localOffset = worldOffset;

        // Apply offset to pivot
        cameraPitchTransform.localPosition = _originalPivotLocalPos + localOffset;
    }

    bool NeedsAdjustment(Vector3 pos, out Vector3 validPos)
    {
        validPos = pos;

        bool needsAdjust = false;
        Vector3 adjustment = Vector3.zero;

        // Floor check - cast from well above to find the floor reliably
        // even if camera is already below floor level
        float floorY = FindFloorBelow(pos);
        // Keep the full collision sphere above the floor, then add extra visual clearance
        // so the camera near clip plane does not skim into the ground during ragdoll.
        float requiredY = floorY + collisionRadius + minClearance;
        
        if (pos.y < requiredY)
        {
            adjustment.y = requiredY - pos.y;
            needsAdjust = true;
        }

        // Apply floor adjustment before checking walls
        Vector3 adjustedPos = pos + adjustment;

        // Check for overlapping geometry (walls, objects)
        Collider[] overlaps = Physics.OverlapSphere(adjustedPos, collisionRadius, collisionLayers, QueryTriggerInteraction.Ignore);

        foreach (Collider col in overlaps)
        {
            if (col.isTrigger || IsPlayerCollider(col))
                continue;

            needsAdjust = true;

            Vector3 closestPoint = col.ClosestPoint(adjustedPos);
            Vector3 awayDir = adjustedPos - closestPoint;
            float dist = awayDir.magnitude;

            if (dist < 0.001f)
            {
                // Inside collider - push toward open space
                Vector3 escape = FindEscapeVector(col, adjustedPos);
                adjustment += escape;
            }
            else
            {
                // Overlapping - push out
                float penetration = collisionRadius - dist + 0.05f;
                adjustment += (awayDir / dist) * penetration;
            }
        }

        if (needsAdjust)
        {
            validPos = pos + adjustment;

            // Verify the new position is valid, if not try harder
            int attempts = 0;
            while (IsInsideGeometry(validPos) && attempts < 5)
            {
                validPos += Vector3.up * 0.15f;
                attempts++;
            }
        }

        return needsAdjust;
    }

    bool IsPlayerCollider(Collider col)
    {
        if (_playerColliders == null)
            return false;
            
        for (int i = 0; i < _playerColliders.Length; i++)
        {
            if (_playerColliders[i] == col)
                return true;
        }
        return false;
    }

    Vector3 FindEscapeVector(Collider col, Vector3 pos)
    {
        // Cast rays in multiple directions to find nearest exit
        Vector3[] dirs = {
            Vector3.up,
            (Vector3.up + Vector3.forward).normalized,
            (Vector3.up + Vector3.back).normalized,
            (Vector3.up + Vector3.left).normalized,
            (Vector3.up + Vector3.right).normalized,
            Vector3.forward,
            Vector3.back,
            Vector3.left,
            Vector3.right
        };

        float bestDist = float.MaxValue;
        Vector3 bestDir = Vector3.up;

        foreach (Vector3 dir in dirs)
        {
            // Cast from inside outward, find first non-player hit
            RaycastHit[] hits = Physics.RaycastAll(pos, dir, 2f, collisionLayers, QueryTriggerInteraction.Ignore);
            
            bool foundValidHit = false;
            foreach (RaycastHit hit in hits)
            {
                if (!IsPlayerCollider(hit.collider))
                {
                    if (hit.distance < bestDist)
                    {
                        bestDist = hit.distance;
                        bestDir = hit.normal;
                    }
                    foundValidHit = true;
                    break;
                }
            }
            
            if (!foundValidHit)
            {
                // No valid hit - clear path, prefer this (especially upward)
                float score = 0.1f + (dir.y > 0 ? 0 : 5f);
                if (score < bestDist)
                {
                    bestDist = score;
                    bestDir = dir;
                }
            }
        }

        return bestDir * (collisionRadius + minClearance);
    }

    bool IsInsideGeometry(Vector3 pos)
    {
        Collider[] hits = Physics.OverlapSphere(pos, collisionRadius * 0.7f, collisionLayers, QueryTriggerInteraction.Ignore);
        foreach (Collider col in hits)
        {
            if (!col.isTrigger && !IsPlayerCollider(col))
                return true;
        }
        return false;
    }

    float FindFloorBelow(Vector3 pos)
    {
        // Cast from high above to find the floor, even if camera is below it
        float castHeight = 3f;
        Vector3 castOrigin = new Vector3(pos.x, pos.y + castHeight, pos.z);
        float castDistance = castHeight + 2f;

        // Try a direct raycast first
        RaycastHit[] hits = Physics.RaycastAll(castOrigin, Vector3.down, castDistance, collisionLayers, QueryTriggerInteraction.Ignore);
        
        float highestFloor = pos.y - 10f; // Default to far below
        
        foreach (RaycastHit hit in hits)
        {
            if (IsPlayerCollider(hit.collider))
                continue;
                
            // Check if this is a floor-like surface (normal pointing up)
            if (hit.normal.y > 0.5f && hit.point.y > highestFloor)
            {
                highestFloor = hit.point.y;
            }
        }

        // Also do a SphereCast for better coverage on edges/slopes
        if (Physics.SphereCast(castOrigin, collisionRadius * 0.5f, Vector3.down, out RaycastHit sphereHit, castDistance, collisionLayers, QueryTriggerInteraction.Ignore))
        {
            if (!IsPlayerCollider(sphereHit.collider) && sphereHit.normal.y > 0.5f)
            {
                float sphereFloorY = sphereHit.point.y;
                if (sphereFloorY > highestFloor)
                    highestFloor = sphereFloorY;
            }
        }

        // Check surrounding area for nearby floors the camera might clip through
        Vector3[] offsets = {
            Vector3.zero,
            Vector3.forward * 0.2f,
            Vector3.back * 0.2f,
            Vector3.left * 0.2f,
            Vector3.right * 0.2f
        };

        foreach (Vector3 offset in offsets)
        {
            Vector3 checkOrigin = castOrigin + offset;
            if (Physics.Raycast(checkOrigin, Vector3.down, out RaycastHit offsetHit, castDistance, collisionLayers, QueryTriggerInteraction.Ignore))
            {
                if (!IsPlayerCollider(offsetHit.collider) && offsetHit.normal.y > 0.5f)
                {
                    if (offsetHit.point.y > highestFloor)
                        highestFloor = offsetHit.point.y;
                }
            }
        }

        return highestFloor;
    }
}

