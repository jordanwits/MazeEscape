using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One interactable seat on a blackjack table. The player aims at this collider and presses E to sit (if the
/// seat is empty and they aren't already seated) or to leave (if they occupy this seat). Discovered by the
/// player interact raycast via <c>GetComponentInParent</c>, mirroring <see cref="CarnivalGameStartButton"/>.
/// Needs a non-trigger collider on the interact layer; the owning <see cref="BlackjackGameController"/> is
/// resolved from a parent.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackSeat : MonoBehaviour
{
    [SerializeField, Min(0)] int seatIndex;
    [SerializeField, Tooltip("Owning table controller. Auto-resolved from a parent if left empty.")]
    BlackjackGameController controller;

    public int SeatIndex => seatIndex;
    public BlackjackGameController Controller => ResolveController();

    void Awake()
    {
        ResolveController();
    }

    void Reset()
    {
        controller = GetComponentInParent<BlackjackGameController>(true);
    }

    BlackjackGameController ResolveController()
    {
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
        return controller;
    }

    /// <summary>Called by <see cref="PlayerController"/> on E. Decides sit vs leave from replicated seat state.</summary>
    public void RequestSitOrLeave(PlayerController interactor)
    {
        BlackjackGameController c = ResolveController();
        if (c == null || interactor == null)
            return;
        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
            return;

        ulong myId = playerNet.NetworkObjectId;
        int mySeat = c.SeatIndexOfOccupant(myId);

        if (mySeat == seatIndex)
        {
            c.RequestLeave(interactor);
            return;
        }
        if (mySeat >= 0)
            return; // already seated at another seat
        if (c.IsSeatEmpty(seatIndex))
            c.RequestSit(interactor, seatIndex);
    }

    /// <summary>Prompt text for the current aim, or false if this seat shouldn't show a prompt.</summary>
    public bool TryGetPrompt(PlayerController interactor, out string message)
    {
        message = null;
        BlackjackGameController c = ResolveController();
        if (c == null || interactor == null)
            return false;
        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
            return false;

        ulong myId = playerNet.NetworkObjectId;
        int mySeat = c.SeatIndexOfOccupant(myId);

        if (mySeat == seatIndex)
        {
            message = "Press E to leave the table";
            return true;
        }
        if (mySeat >= 0)
            return false; // seated elsewhere; no prompt for this seat
        if (c.IsSeatEmpty(seatIndex))
        {
            message = "Press E to sit";
            return true;
        }
        message = "Seat taken";
        return true;
    }
}
