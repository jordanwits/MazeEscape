using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger volume only the Jailor should trip (layer matrix + <see cref="JailorAI"/> check).
/// A delivery arms the wire — either by <see cref="JailorAI"/> reporting the drop through
/// <see cref="ServerNotifyJailorDeliveryCompleted"/>, or by him crossing the volume while still carrying — after
/// which it waits <see cref="closeDelaySeconds"/> and for the cell to be empty of Jailors, then closes and locks
/// the linked door and seals occupants via <see cref="JailCellSealedReleaseZone"/>.
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
    [Tooltip("Minimum wait before the close can fire (the beat between the Jailor stepping out and the door swinging shut).")]
    [SerializeField] float closeDelaySeconds = 0f;
    [Tooltip(
        "How long the wire and the interior zone must both stay free of Jailors before the door closes. Covers the "
        + "gap between the wire's outer face and the point where his body is clear of the leaf's swing.")]
    [SerializeField] float jailorClearSettleSeconds = 0.75f;
    [Tooltip(
        "Hard cap on the wait: past this the door closes even with a Jailor still in the cell, so an aborted "
        + "delivery can never leave the cell standing open. A sealed-in Jailor walks out through the leaf "
        + "(JailCellSealedReleaseZone door bypass).")]
    [SerializeField] float closeSafetyCapSeconds = 8f;
    [Tooltip("Only start a close sequence when the door is open (avoids repeats while closed).")]
    [SerializeField] bool onlyWhenDoorOpen = true;
    [Tooltip("Match TripwireZone: kinematic Rigidbody improves triggers vs CharacterController.")]
    [SerializeField] bool addKinematicRigidbody = true;

    Coroutine _closeRoutine;
    readonly HashSet<JailorAI> _jailorsOnWire = new();
    readonly List<JailorAI> _jailorScratch = new();
    static readonly List<JailCellDoorTripwire> s_instances = new();
    /// <summary>
    /// True after the Jailor intersected this volume while <see cref="JailorAI.BlocksJailDoorTripwire"/> was true
    /// (carrying / delivery), or after he reported a drop through <see cref="ServerNotifyJailorDeliveryCompleted"/>.
    /// When he stops blocking without ever leaving the collider, <see cref="OnTriggerEnter"/> does not fire again —
    /// <see cref="OnTriggerStay"/> uses this to close the door.
    /// </summary>
    bool _armedAfterBlockedJailorOverlap;
    float _armedAtUnscaledTime;
    /// <summary>
    /// The arm means "a delivery is happening right now", so it has to expire. An aborted delivery (flashbang,
    /// target death or disconnect) leaves it set with nothing left to finish it, and a sticky arm would let an
    /// unrelated crossing minutes later close and lock the cell on whoever happens to be standing in it.
    /// </summary>
    const float ArmedDeliveryFreshnessSeconds = 10f;

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

    void OnEnable()
    {
        if (!s_instances.Contains(this))
            s_instances.Add(this);
    }

    void OnDisable()
    {
        s_instances.Remove(this);
        // Unity kills the coroutine with the component, but the handle would survive re-enable and block every
        // later close via the _closeRoutine != null guard.
        _closeRoutine = null;
    }

    /// <summary>
    /// The wire guarding <paramref name="door"/> — its own leaf, or the mate of a paired pair — or null.
    /// <see cref="JailorAI"/> resolves the wire this way from the door it just delivered through.
    /// </summary>
    public static JailCellDoorTripwire FindForDoor(HingeInteractDoor door)
    {
        if (door == null)
            return null;

        for (int i = 0; i < s_instances.Count; i++)
        {
            JailCellDoorTripwire wire = s_instances[i];
            if (wire == null || wire.jailDoor == null)
                continue;

            if (wire.jailDoor == door || wire.jailDoor == door.PairedLeaf || wire.jailDoor.PairedLeaf == door)
                return wire;
        }

        return null;
    }

    /// <summary>
    /// Server / offline: the Jailor has released a prisoner in this cell. The close cannot hang off the wire
    /// alone — a grab made INSIDE the cell only ever crosses the volume in a non-blocking state, so nothing arms
    /// it and the capture is void. The routine started here still waits for him to clear both the wire and the
    /// interior, so starting it while he is standing over the drop marker is safe.
    /// </summary>
    public void ServerNotifyJailorDeliveryCompleted()
    {
        if (!IsAuthority())
            return;

        ArmForJailorDelivery();
        TryBeginCloseSequenceAfterJailorUnblocked();
    }

    void ArmForJailorDelivery()
    {
        _armedAfterBlockedJailorOverlap = true;
        _armedAtUnscaledTime = Time.unscaledTime;
    }

    /// <summary>Armed by a delivery that is still current; a stale arm is dropped here instead of fired.</summary>
    bool IsArmedByRecentJailorDelivery()
    {
        if (!_armedAfterBlockedJailorOverlap)
            return false;

        if (Time.unscaledTime - _armedAtUnscaledTime > ArmedDeliveryFreshnessSeconds)
        {
            _armedAfterBlockedJailorOverlap = false;
            return false;
        }

        return true;
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

        _jailorsOnWire.Add(jailor);
        if (jailor.BlocksJailDoorTripwire)
        {
            ArmForJailorDelivery();
            return;
        }

        // Only a delivery arms the wire. Any other crossing is a Jailor walking through an open cell —
        // chasing someone in, patrolling past — and closing on that seals him in his own jail.
        if (!IsArmedByRecentJailorDelivery())
            return;

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

        _jailorsOnWire.Add(jailor);
        if (jailor.BlocksJailDoorTripwire)
        {
            // Refreshed every frame he overlaps while blocking, so the freshness window measures time since the
            // delivery was last live rather than since it started.
            ArmForJailorDelivery();
            return;
        }

        if (!IsArmedByRecentJailorDelivery())
            return;

        TryBeginCloseSequenceAfterJailorUnblocked();
    }

    void OnTriggerExit(Collider other)
    {
        if (other == null)
            return;

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
            _jailorsOnWire.Remove(jailor);
    }

    /// <summary>Live Jailors still intersecting the wire (destroyed / deactivated ones are dropped as they are found).</summary>
    bool HasJailorOnWire()
    {
        _jailorScratch.Clear();
        bool any = false;
        foreach (JailorAI j in _jailorsOnWire)
        {
            if (j == null || !j.gameObject.activeInHierarchy)
                _jailorScratch.Add(j);
            else
                any = true;
        }

        for (int i = 0; i < _jailorScratch.Count; i++)
            _jailorsOnWire.Remove(_jailorScratch[i]);
        _jailorScratch.Clear();

        return any;
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
        _closeRoutine = StartCoroutine(CloseWhenCellIsClearRoutine());
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
        {
            occupantZone.ServerSealOccupantsInCell();
            occupantZone.RefreshJailorTrappedInsideDoorBypass();
        }
    }

    /// <summary>
    /// The delay alone is not enough to know he is out: the drop (and with it the end of
    /// <see cref="JailorAI.BlocksJailDoorTripwire"/>) can happen while his body still overlaps the wire, and the leaf
    /// swings shut in 0.4s. So the close waits for the delay AND for the wire and the interior to stay clear of
    /// Jailors for <see cref="jailorClearSettleSeconds"/>, with <see cref="closeSafetyCapSeconds"/> as the backstop.
    /// </summary>
    IEnumerator CloseWhenCellIsClearRoutine()
    {
        float startTime = Time.unscaledTime;
        float earliestCloseTime = startTime + Mathf.Max(0f, closeDelaySeconds);
        float capTime = startTime + Mathf.Max(closeSafetyCapSeconds, closeDelaySeconds);
        float clearSince = -1f;

        while (true)
        {
            float now = Time.unscaledTime;
            bool cellClear = !HasJailorOnWire() && (occupantZone == null || !occupantZone.HasJailorInside);
            if (!cellClear)
                clearSince = -1f;
            else if (clearSince < 0f)
                clearSince = now;

            bool settled = clearSince >= 0f && now >= clearSince + Mathf.Max(0f, jailorClearSettleSeconds);
            if ((settled && now >= earliestCloseTime) || now >= capTime)
                break;

            yield return null;
        }

        CloseDoorAndSealOccupantsNow();
        _closeRoutine = null;
    }
}
