using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger volume placed just under a bottle-booth shelf. On the server, any
/// <see cref="CarnivalBottleKnockdown"/> bottle that falls into it is reported to the owning
/// <see cref="BottleBoothGameController"/>, which tallies it as a knocked-off bottle (once per bottle).
/// <para>
/// Detection is server-authoritative: bottles are server-owned rigidbodies, so the host's physics is
/// the single source of truth for when a bottle drops through. Clients never score locally. Position
/// the volume's top just below the resting bottles so a standing (un-knocked) bottle never overlaps it.
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public sealed class CarnivalBottleKnockoffZone : MonoBehaviour
{
    [SerializeField, Tooltip("Controller notified when a bottle falls through. Auto-resolved from a parent if left empty.")]
    BottleBoothGameController controller;

    void Reset()
    {
        controller = GetComponentInParent<BottleBoothGameController>(true);
        Collider col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    void Awake()
    {
        if (controller == null)
            controller = GetComponentInParent<BottleBoothGameController>(true);
    }

    void OnTriggerEnter(Collider other)
    {
        if (controller == null)
            return;

        // Server (or offline single-player) is the only authority that scores.
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer)
            return;

        // The bottle's collider sits on the bottle root alongside the knockdown + NetworkObject.
        CarnivalBottleKnockdown bottle = other.GetComponentInParent<CarnivalBottleKnockdown>();
        if (bottle == null)
            return;

        // Only count bottles that were actually knocked down (become dynamic and fall). Guards against a
        // standing bottle that happens to clip the volume from ever being mis-scored.
        if (!bottle.IsKnockedDown)
            return;

        NetworkObject bottleNet = bottle.GetComponent<NetworkObject>();
        if (bottleNet == null)
            return;

        controller.ServerOnBottleKnockedOff(bottleNet);
    }
}
