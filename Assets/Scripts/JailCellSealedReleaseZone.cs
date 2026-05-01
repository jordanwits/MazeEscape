using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Place a trigger volume over the jail cell interior. When the linked <see cref="HingeInteractDoor"/> is unlocked with a key,
/// players inside this volume get <see cref="NetworkPlayerAvatar.ServerSetSealedInJailCell"/>(false) so the Jailor can target them again.
/// Also, if the Jailor is still inside when the door is sealed (closed + key-locked), he can walk through the door colliders until he exits
/// (Physics.IgnoreCollision between his <see cref="CharacterController"/> and the hinge mesh colliders).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class JailCellSealedReleaseZone : MonoBehaviour
{
    [Tooltip("The key-locked jail door for this cell. If empty, searches under this object's scene root.")]
    [SerializeField] HingeInteractDoor jailDoor;
    [SerializeField] bool autoFindDoor = true;

    readonly HashSet<PlayerHealth> _occupants = new();
    readonly HashSet<JailorAI> _jailorsInCell = new();
    readonly HashSet<JailorAI> _jailorsIgnoringDoor = new();
    readonly List<JailorAI> _jailorScratch = new();
    readonly List<Collider> _doorColliderScratch = new();

    [Tooltip("How often to reconcile door bypass vs door open/lock state (covers unlock-from-key without triggers).")]
    [SerializeField] float jailorDoorBypassPollSeconds = 0.25f;
    float _nextBypassPollTime;

    void Awake()
    {
        if (jailDoor == null && autoFindDoor)
            jailDoor = JailorAI.FindNearestHingeDoorInLocalPrefabHierarchy(transform, transform.position);
    }

    void OnEnable()
    {
        if (jailDoor != null)
            jailDoor.OnJailUnlockedByPlayerKey += OnJailUnlockedByPlayerKey;
    }

    void OnDisable()
    {
        if (jailDoor != null)
            jailDoor.OnJailUnlockedByPlayerKey -= OnJailUnlockedByPlayerKey;

        StopAllJailorDoorIgnores();
    }

    void OnJailUnlockedByPlayerKey(HingeInteractDoor door)
    {
        if (door != jailDoor || !IsAuthority())
            return;

        foreach (PlayerHealth ph in _occupants)
        {
            if (ph == null || ph.IsDead)
                continue;
            NetworkPlayerAvatar avatar = ph.GetComponent<NetworkPlayerAvatar>();
            if (avatar != null)
                avatar.ServerSetSealedInJailCell(false);
        }

        RefreshJailorTrappedInsideDoorBypass();
    }

    /// <summary>Server / offline host: mark everyone currently inside this zone as sealed in jail (after <see cref="JailCellDoorTripwire"/> closes the door).</summary>
    public void ServerSealOccupantsInCell()
    {
        if (!IsAuthority())
            return;

        foreach (PlayerHealth ph in _occupants)
        {
            if (ph == null || ph.IsDead)
                continue;
            NetworkPlayerAvatar avatar = ph.GetComponent<NetworkPlayerAvatar>();
            if (avatar != null)
                avatar.ServerSetSealedInJailCell(true);
        }
    }

    /// <summary>
    /// When the cell door is sealed (locked + closed) and a Jailor remains inside the interior volume, he ignores physics hits with the door until he leaves or the door opens/unlocks.
    /// </summary>
    public void RefreshJailorTrappedInsideDoorBypass()
    {
        if (!IsAuthority())
            return;

        PruneDestroyedJailorsInCell();

        if (jailDoor == null)
        {
            StopAllJailorDoorIgnores();
            return;
        }

        _doorColliderScratch.Clear();
        jailDoor.AppendSolidDoorColliders(_doorColliderScratch, includePairedLeaf: true);
        bool wantIgnore = jailDoor.IsJailCellStyleEntry
            && jailDoor.IsLocked
            && !jailDoor.IsOpen
            && _doorColliderScratch.Count > 0;

        _jailorScratch.Clear();
        foreach (JailorAI j in _jailorsIgnoringDoor)
        {
            if (!wantIgnore || j == null || !_jailorsInCell.Contains(j))
                _jailorScratch.Add(j);
        }

        for (int i = 0; i < _jailorScratch.Count; i++)
            StopIgnoringDoorCollisions(_jailorScratch[i]);

        if (!wantIgnore)
            return;

        foreach (JailorAI j in _jailorsInCell)
        {
            if (j != null)
                StartIgnoringDoorCollisions(j);
        }
    }

    void Update()
    {
        if (!IsAuthority())
            return;
        if (_jailorsInCell.Count == 0 && _jailorsIgnoringDoor.Count == 0)
            return;

        if (Time.unscaledTime < _nextBypassPollTime)
            return;
        _nextBypassPollTime = Time.unscaledTime + Mathf.Max(0.05f, jailorDoorBypassPollSeconds);

        RefreshJailorTrappedInsideDoorBypass();
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

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
        {
            _jailorsInCell.Add(jailor);
            RefreshJailorTrappedInsideDoorBypass();
        }

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null && !ph.IsDead)
            _occupants.Add(ph);
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsAuthority())
            return;

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
        {
            _jailorsInCell.Remove(jailor);
            RefreshJailorTrappedInsideDoorBypass();
        }

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null)
            _occupants.Remove(ph);
    }

    void PruneDestroyedJailorsInCell()
    {
        _jailorScratch.Clear();
        foreach (JailorAI j in _jailorsInCell)
        {
            if (j == null)
                _jailorScratch.Add(j);
        }

        for (int i = 0; i < _jailorScratch.Count; i++)
            _jailorsInCell.Remove(_jailorScratch[i]);
    }

    void StartIgnoringDoorCollisions(JailorAI jailor)
    {
        if (jailor == null || _jailorsIgnoringDoor.Contains(jailor))
            return;

        CharacterController cc = jailor.GetComponent<CharacterController>();
        if (cc == null)
            return;

        if (jailDoor == null)
            return;

        _doorColliderScratch.Clear();
        jailDoor.AppendSolidDoorColliders(_doorColliderScratch, includePairedLeaf: true);
        for (int i = 0; i < _doorColliderScratch.Count; i++)
        {
            Collider d = _doorColliderScratch[i];
            if (d != null)
                Physics.IgnoreCollision(cc, d, true);
        }

        _jailorsIgnoringDoor.Add(jailor);
    }

    void StopIgnoringDoorCollisions(JailorAI jailor)
    {
        if (jailor == null || !_jailorsIgnoringDoor.Remove(jailor))
            return;

        CharacterController cc = jailor.GetComponent<CharacterController>();
        if (cc == null || jailDoor == null)
            return;

        _doorColliderScratch.Clear();
        jailDoor.AppendSolidDoorColliders(_doorColliderScratch, includePairedLeaf: true);
        for (int i = 0; i < _doorColliderScratch.Count; i++)
        {
            Collider d = _doorColliderScratch[i];
            if (d != null)
                Physics.IgnoreCollision(cc, d, false);
        }
    }

    void StopAllJailorDoorIgnores()
    {
        _jailorScratch.Clear();
        _jailorScratch.AddRange(_jailorsIgnoringDoor);
        for (int i = 0; i < _jailorScratch.Count; i++)
            StopIgnoringDoorCollisions(_jailorScratch[i]);
    }
}
