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
    readonly HashSet<NetworkPlayerAvatar> _sealedByThisZone = new();
    readonly Dictionary<NetworkPlayerAvatar, float> _sealedOutsideSince = new();
    readonly List<NetworkPlayerAvatar> _avatarScratch = new();

    const float SealedOutsideClearSeconds = 1f;

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
        _sealedByThisZone.Clear();
        _sealedOutsideSince.Clear();
    }

    /// <summary>True while a live Jailor stands in the cell interior — the tripwire waits on this before sealing.</summary>
    public bool HasJailorInside
    {
        get
        {
            foreach (JailorAI j in _jailorsInCell)
            {
                if (IsLiveJailor(j))
                    return true;
            }

            return false;
        }
    }

    static bool IsLiveJailor(JailorAI jailor) => jailor != null && jailor.gameObject.activeInHierarchy;

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

        // A key unlock frees this cell's prisoners wherever they stand — including one whose flag stuck after
        // being relocated out of the volume (death respawn, leaf depenetration).
        foreach (NetworkPlayerAvatar avatar in _sealedByThisZone)
        {
            if (avatar != null)
                avatar.ServerSetSealedInJailCell(false);
        }
        _sealedByThisZone.Clear();
        _sealedOutsideSince.Clear();

        RefreshJailorTrappedInsideDoorBypass();
    }

    /// <summary>Server / offline host: mark everyone currently inside this zone as sealed in jail (after <see cref="JailCellDoorTripwire"/> closes the door).</summary>
    public void ServerSealOccupantsInCell()
    {
        if (!IsAuthority())
            return;

        PruneDestroyedJailorsInCell();

        foreach (PlayerHealth ph in _occupants)
        {
            if (ph == null || ph.IsDead)
                continue;
            NetworkPlayerAvatar avatar = ph.GetComponent<NetworkPlayerAvatar>();
            if (avatar != null)
            {
                avatar.ServerSetSealedInJailCell(true);
                _sealedByThisZone.Add(avatar);
            }
        }
    }

    /// <summary>
    /// When the cell door is sealed (locked + closed) and a Jailor remains inside the interior volume, he ignores physics hits with the door until he leaves or the door opens/unlocks.
    /// The grant is a live condition, not a snapshot taken at seal time: a Jailor shoved back into a cell that
    /// is already sealed has to be able to walk out too, or he is trapped in there for the rest of the run.
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
            if (!wantIgnore || !IsLiveJailor(j) || !_jailorsInCell.Contains(j))
                _jailorScratch.Add(j);
        }

        for (int i = 0; i < _jailorScratch.Count; i++)
            StopIgnoringDoorCollisions(_jailorScratch[i]);

        if (!wantIgnore)
            return;

        foreach (JailorAI j in _jailorsInCell)
        {
            if (IsLiveJailor(j))
                StartIgnoringDoorCollisions(j);
        }
    }

    void Update()
    {
        if (!IsAuthority())
            return;
        if (_jailorsInCell.Count == 0 && _jailorsIgnoringDoor.Count == 0 && _sealedByThisZone.Count == 0)
            return;

        if (Time.unscaledTime < _nextBypassPollTime)
            return;
        _nextBypassPollTime = Time.unscaledTime + Mathf.Max(0.05f, jailorDoorBypassPollSeconds);

        RefreshJailorTrappedInsideDoorBypass();
        ReconcileSealedFlagsAgainstOccupancy();
    }

    /// <summary>
    /// A player this zone sealed must actually be inside it: the closing leaf can depenetrate someone to the
    /// outside, and a death respawn relocates the avatar with no OnTriggerExit while the door is shut — either way
    /// the sealed flag (and Jailor untargetability) would stick for the rest of the level. The grace period covers
    /// the physics-step lag between the drop teleport into the cell and its OnTriggerEnter.
    /// </summary>
    void ReconcileSealedFlagsAgainstOccupancy()
    {
        if (_sealedByThisZone.Count == 0)
            return;

        _avatarScratch.Clear();
        foreach (NetworkPlayerAvatar avatar in _sealedByThisZone)
        {
            if (avatar == null || !avatar.IsSealedInJailCell)
            {
                _avatarScratch.Add(avatar);
                continue;
            }

            PlayerHealth ph = avatar.GetComponent<PlayerHealth>();
            if (ph != null && _occupants.Contains(ph))
            {
                _sealedOutsideSince.Remove(avatar);
                continue;
            }

            if (!_sealedOutsideSince.TryGetValue(avatar, out float outsideSince))
            {
                _sealedOutsideSince[avatar] = Time.unscaledTime;
                continue;
            }

            if (Time.unscaledTime - outsideSince < SealedOutsideClearSeconds)
                continue;

            avatar.ServerSetSealedInJailCell(false);
            _avatarScratch.Add(avatar);
        }

        for (int i = 0; i < _avatarScratch.Count; i++)
        {
            _sealedByThisZone.Remove(_avatarScratch[i]);
            _sealedOutsideSince.Remove(_avatarScratch[i]);
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

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
        {
            _jailorsInCell.Add(jailor);
            RefreshJailorTrappedInsideDoorBypass();
        }

        PlayerHealth ph = other.GetComponentInParent<PlayerHealth>();
        if (ph != null && !ph.IsDead)
        {
            _occupants.Add(ph);
            // Adopt prisoners sealed at the drop itself (JailorAI seals before the door-close sweep runs), so the
            // occupancy reconcile covers them too.
            NetworkPlayerAvatar avatar = ph.GetComponent<NetworkPlayerAvatar>();
            if (avatar != null && avatar.IsSealedInJailCell)
            {
                _sealedByThisZone.Add(avatar);
                _sealedOutsideSince.Remove(avatar);
            }
        }
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
        {
            _occupants.Remove(ph);
            ClearSealedFlagOnEscapeThroughOpenDoor(ph);
        }
    }

    /// <summary>
    /// A prisoner who walks out while the door still stands open (or unlocked) has escaped, so the sealed flag goes
    /// with them. Without this the flag only ever clears on a key unlock, and slipping out mid-delivery leaves them
    /// permanently invisible to every Jailor. A closed and locked door is a real seal — never cleared here.
    /// </summary>
    void ClearSealedFlagOnEscapeThroughOpenDoor(PlayerHealth playerHealth)
    {
        if (playerHealth == null || jailDoor == null)
            return;
        if (!jailDoor.IsOpen && jailDoor.IsLocked)
            return;

        NetworkPlayerAvatar avatar = playerHealth.GetComponent<NetworkPlayerAvatar>();
        if (avatar != null && avatar.IsSealedInJailCell)
        {
            avatar.ServerSetSealedInJailCell(false);
            _sealedByThisZone.Remove(avatar);
            _sealedOutsideSince.Remove(avatar);
        }
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

    /// <summary>
    /// Re-applied on every reconcile, never once per Jailor: disabling and re-enabling a
    /// <see cref="CharacterController"/> (NavMesh warps, pit rescues) drops all of its IgnoreCollision pairs and
    /// fires no trigger callbacks, so a one-shot grant silently leaves a sealed-in Jailor solid against the leaf
    /// with nothing left to notice it. <see cref="Physics.IgnoreCollision(Collider,Collider,bool)"/> is idempotent
    /// and cheap; the set is only revocation bookkeeping.
    /// </summary>
    void StartIgnoringDoorCollisions(JailorAI jailor)
    {
        if (jailor == null)
            return;

        CharacterController cc = jailor.GetComponent<CharacterController>();
        if (!CanPairIgnoreCollision(cc))
            return;

        if (jailDoor == null)
            return;

        _doorColliderScratch.Clear();
        jailDoor.AppendSolidDoorColliders(_doorColliderScratch, includePairedLeaf: true);
        for (int i = 0; i < _doorColliderScratch.Count; i++)
        {
            Collider d = _doorColliderScratch[i];
            if (CanPairIgnoreCollision(d))
                Physics.IgnoreCollision(cc, d, true);
        }

        _jailorsIgnoringDoor.Add(jailor);
    }

    /// <summary>
    /// <see cref="Physics.IgnoreCollision(Collider,Collider,bool)"/> is only legal between two active, enabled
    /// colliders and logs an error otherwise; the door subtree is gathered with inactive children included, and a
    /// CharacterController is briefly disabled around warps. Skipping costs nothing — a disabled collider cannot
    /// collide, and re-enabling one clears its ignore pairs anyway, which is what the reconcile re-applies.
    /// </summary>
    static bool CanPairIgnoreCollision(Collider collider) =>
        collider != null && collider.enabled && collider.gameObject.activeInHierarchy;

    void StopIgnoringDoorCollisions(JailorAI jailor)
    {
        // The Remove comes first so a destroyed Jailor still leaves the set: short-circuiting on the Unity-null
        // check ahead of it kept dead entries forever, and every reconcile walked them again.
        if (!_jailorsIgnoringDoor.Remove(jailor))
            return;

        if (jailor == null)
            return;

        CharacterController cc = jailor.GetComponent<CharacterController>();
        if (!CanPairIgnoreCollision(cc) || jailDoor == null)
            return;

        _doorColliderScratch.Clear();
        jailDoor.AppendSolidDoorColliders(_doorColliderScratch, includePairedLeaf: true);
        for (int i = 0; i < _doorColliderScratch.Count; i++)
        {
            Collider d = _doorColliderScratch[i];
            if (CanPairIgnoreCollision(d))
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
