using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Place a trigger volume over the jail cell interior. When the linked <see cref="HingeInteractDoor"/> is unlocked with a key,
/// players inside this volume get <see cref="NetworkPlayerAvatar.ServerSetSealedInJailCell"/>(false) so the Jailor can target them again.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class JailCellSealedReleaseZone : MonoBehaviour
{
    [Tooltip("The key-locked jail door for this cell. If empty, searches under this object's scene root.")]
    [SerializeField] HingeInteractDoor jailDoor;
    [SerializeField] bool autoFindDoor = true;

    readonly HashSet<PlayerHealth> _occupants = new();

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

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null && !ph.IsDead)
            _occupants.Add(ph);
    }

    void OnTriggerExit(Collider other)
    {
        if (!IsAuthority())
            return;

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null)
            _occupants.Remove(ph);
    }
}
