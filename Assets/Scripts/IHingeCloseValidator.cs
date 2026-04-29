/// <summary>Server-only hooks for doors that must block close until gameplay conditions are met (e.g. maze exit elevator).</summary>
public interface IHingeCloseValidator
{
    /// <summary>Return true if the door may transition from open to closed on the server.</summary>
    bool ServerValidateClose(HingeInteractDoor door, ulong senderClientId);

    /// <summary>Called on the server after <see cref="ServerValidateClose"/> returned true and the door is about to close.</summary>
    void ServerOnCloseAuthorized(HingeInteractDoor door, ulong senderClientId);
}
