using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger volume only the Jailor should trip (layer matrix + <see cref="JailorAI"/> check).
/// When he crosses it leaving the cell after a drop, waits <see cref="closeDelaySeconds"/>, then closes and locks the linked door and seals occupants via <see cref="JailCellSealedReleaseZone"/>.
/// Place the wire across the doorway or exit path so his CharacterController intersects it once on the way out.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class JailCellDoorTripwire : MonoBehaviour
{
    [Tooltip("Door to close and lock after the delay.")]
    [SerializeField] HingeInteractDoor jailDoor;
    [Tooltip("Interior zone listing prisoners for sealing. If empty, searched under the same jail root.")]
    [SerializeField] JailCellSealedReleaseZone occupantZone;
    [Tooltip("0 = close and seal on the same frame as the trigger (after delay, occupant zone seal is redundant if JailorAI already sealed).")]
    [SerializeField] float closeDelaySeconds = 0f;
    [Tooltip("Only start a close sequence when the door is open (avoids repeats while closed).")]
    [SerializeField] bool onlyWhenDoorOpen = true;
    [Tooltip("Match TripwireZone: kinematic Rigidbody improves triggers vs CharacterController.")]
    [SerializeField] bool addKinematicRigidbody = true;

    Coroutine _closeRoutine;
    /// <summary>
    /// True after the Jailor intersected this volume while <see cref="JailorAI.BlocksJailDoorTripwire"/> was true
    /// (carrying / delivery). When he stops blocking without ever leaving the collider, <see cref="OnTriggerEnter"/>
    /// does not fire again — <see cref="OnTriggerStay"/> uses this to close the door.
    /// </summary>
    bool _armedAfterBlockedJailorOverlap;

    void Awake()
    {
        if (jailDoor == null)
            jailDoor = JailorAI.FindNearestHingeDoorInLocalPrefabHierarchy(transform, transform.position);
        if (occupantZone == null)
            occupantZone = FindNearestSealedReleaseZoneInHierarchy();

        Collider c = GetComponent<Collider>();
        if (c != null)
            c.isTrigger = true;

        if (addKinematicRigidbody && GetComponent<Rigidbody>() == null)
        {
            Rigidbody rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    JailCellSealedReleaseZone FindNearestSealedReleaseZoneInHierarchy()
    {
        const int maxParentSteps = 14;
        Transform t = transform;
        for (int depth = 0; depth < maxParentSteps && t != null; depth++)
        {
            JailCellSealedReleaseZone[] zones = t.GetComponentsInChildren<JailCellSealedReleaseZone>(true);
            if (zones != null && zones.Length > 0)
            {
                JailCellSealedReleaseZone best = zones[0];
                float bestSqr = (best.transform.position - transform.position).sqrMagnitude;
                for (int i = 1; i < zones.Length; i++)
                {
                    if (zones[i] == null)
                        continue;
                    float sqr = (zones[i].transform.position - transform.position).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = zones[i];
                    }
                }

                return best;
            }

            t = t.parent;
        }

        return null;
    }

    static bool IsAuthority()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;
        return nm.IsServer;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!IsAuthority())
            return;
        if (other == null)
            return;

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor == null)
            return;
        if (jailor.BlocksJailDoorTripwire)
        {
            _armedAfterBlockedJailorOverlap = true;
            return;
        }

        TryBeginCloseSequenceAfterJailorUnblocked();
    }

    void OnTriggerStay(Collider other)
    {
        if (!IsAuthority())
            return;
        if (other == null)
            return;

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor == null)
            return;

        if (jailor.BlocksJailDoorTripwire)
        {
            _armedAfterBlockedJailorOverlap = true;
            return;
        }

        if (!_armedAfterBlockedJailorOverlap)
            return;

        TryBeginCloseSequenceAfterJailorUnblocked();
    }

    void TryBeginCloseSequenceAfterJailorUnblocked()
    {
        if (jailDoor == null)
            return;
        if (onlyWhenDoorOpen && !jailDoor.IsOpen)
        {
            _armedAfterBlockedJailorOverlap = false;
            return;
        }

        if (_closeRoutine != null)
            return;

        _armedAfterBlockedJailorOverlap = false;

        if (closeDelaySeconds <= 0f)
        {
            CloseDoorAndSealOccupantsNow();
            return;
        }

        _closeRoutine = StartCoroutine(CloseAfterDelayRoutine());
    }

    void CloseDoorAndSealOccupantsNow()
    {
        if (jailDoor != null)
        {
            if (!jailDoor.UseKeyToUnlock)
            {
                Debug.LogWarning(
                    $"{nameof(JailCellDoorTripwire)} on '{name}' needs a door with Use Key To Unlock enabled "
                    + $"so the Jailor tripwire can seal the cell (assign '{jailDoor.name}' or enable the flag on its {nameof(HingeInteractDoor)}).",
                    this);
            }

            jailDoor.ServerJailorCloseAndLock();
        }

        if (occupantZone != null)
            occupantZone.ServerSealOccupantsInCell();
    }

    IEnumerator CloseAfterDelayRoutine()
    {
        float wait = Mathf.Max(0f, closeDelaySeconds);
        if (wait > 0f)
            yield return new WaitForSecondsRealtime(wait);

        CloseDoorAndSealOccupantsNow();
        _closeRoutine = null;
    }
}
