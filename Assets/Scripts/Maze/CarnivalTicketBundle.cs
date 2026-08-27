using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Physical ticket roll spawned by a carnival minigame on round end. Carries a ticket value;
/// pressing E on it credits the full <see cref="Value"/> to the picker's
/// <see cref="NetworkPlayerCarnivalTickets"/> and despawns. Whoever grabs it first gets the entire payout
/// — same as a real arcade ticket dispenser. Not an inventory item, not stackable, not droppable.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class CarnivalTicketBundle : NetworkBehaviour
{
    const string DefaultPrintClipPath = "Assets/Audio/SFX/Carnival/TicketPrint.wav";

    [Header("Print SFX")]
    [Tooltip("One-shot played on every peer when the bundle spawns (the ticket-dispenser print sound).")]
    [SerializeField] AudioClip printClip;
    [SerializeField, Range(0f, 1f)] float printVolume = 0.25f;
    [SerializeField, Min(0.5f)] float printSpatialMinDistance = 1.5f;
    [SerializeField, Min(1f)] float printSpatialMaxDistance = 22f;

    readonly NetworkVariable<int> _value = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    AudioSource _printAudio;

    public int Value => _value.Value;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (printClip == null)
            printClip = AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultPrintClipPath);
    }
#endif

    [ClientRpc]
    void PlayPrintSfxClientRpc() => PlayPrintSfx();

    void PlayPrintSfx()
    {
        if (printClip == null)
            return;

        if (_printAudio == null)
        {
            _printAudio = GetComponent<AudioSource>();
            if (_printAudio == null)
                _printAudio = gameObject.AddComponent<AudioSource>();
        }

        _printAudio.playOnAwake = false;
        _printAudio.loop = false;
        _printAudio.spatialBlend = 1f;
        _printAudio.dopplerLevel = 0f;
        _printAudio.rolloffMode = AudioRolloffMode.Linear;
        _printAudio.minDistance = printSpatialMinDistance;
        _printAudio.maxDistance = printSpatialMaxDistance;
        GameAudioManager.RouteSfxSource(_printAudio);
        _printAudio.PlayOneShot(printClip, Mathf.Clamp01(printVolume));
    }

    /// <summary>
    /// Server-only. Call after <see cref="NetworkObject.Spawn"/> so the change replicates to all connected clients.
    /// Doubles as the "just printed" signal: the booth calls it once, on a bundle it created this instant, so the
    /// print one-shot rides here instead of <c>OnNetworkSpawn</c>. A ClientRpc reaches only the peers connected when
    /// it is sent, which is the point — a joining client spawns every bundle already lying around the midway and
    /// would otherwise hear all of them print at once on connect.
    /// </summary>
    public void ServerSetValue(int v)
    {
        if (!IsServer || !IsSpawned)
            return;
        _value.Value = Mathf.Max(0, v);
        PlayPrintSfxClientRpc();
    }

    /// <summary>Called from <see cref="PlayerController"/> when the player presses E while aiming at this bundle.</summary>
    public void RequestPickup(PlayerController interactor)
    {
        if (interactor == null)
            return;
        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !IsSpawned)
            return;

        if (nm.IsServer)
            ServerApplyPickup(playerNet.NetworkObjectId, playerNet.OwnerClientId);
        else
            RequestPickupServerRpc(playerNet.NetworkObjectId);
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestPickupServerRpc(ulong playerNetworkObjectId, ServerRpcParams rpcParams = default)
    {
        ServerApplyPickup(playerNetworkObjectId, rpcParams.Receive.SenderClientId);
    }

    void ServerApplyPickup(ulong playerNetworkObjectId, ulong expectedOwnerClientId)
    {
        if (!IsServer || !IsSpawned)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObj)
            || playerObj == null)
            return;

        if (playerObj.OwnerClientId != expectedOwnerClientId)
            return;

        // Server-side range gate: without this, any client could claim any ticket bundle on the map from
        // anywhere ("whoever grabs it first gets the entire payout"). Validate against the server's known
        // player position — never a client-supplied hint.
        const float ServerMaxPickupHorizontal = 4f;
        const float ServerMaxPickupVertical = 3f;
        Vector3 bundlePos = transform.position;
        Vector3 playerPos = playerObj.transform.position;
        Vector3 flatDelta = new Vector3(bundlePos.x - playerPos.x, 0f, bundlePos.z - playerPos.z);
        if (flatDelta.sqrMagnitude > ServerMaxPickupHorizontal * ServerMaxPickupHorizontal)
            return;
        if (Mathf.Abs(bundlePos.y - playerPos.y) > ServerMaxPickupVertical)
            return;

        NetworkPlayerCarnivalTickets wallet = playerObj.GetComponent<NetworkPlayerCarnivalTickets>();
        if (wallet == null)
            return;

        wallet.ServerAdd(_value.Value);

        NetworkObject self = GetComponent<NetworkObject>();
        if (self != null && self.IsSpawned)
            self.Despawn(true);
    }
}
