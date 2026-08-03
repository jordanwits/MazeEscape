using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Maze Escape/Multiplayer Project Settings", fileName = "MultiplayerProjectSettings")]
public class MultiplayerProjectSettings : ScriptableObject
{
    /// <summary>One selectable lobby character. Exactly one player may own each at a time.</summary>
    [Serializable]
    public class LobbyCharacter
    {
        [SerializeField] string displayName = "Survivor";
        [Tooltip("Networked player prefab spawned for the client that picked this character. Must be registered in Resources/DefaultNetworkPrefabs.")]
        [SerializeField] GameObject playerPrefab;
        [Tooltip("Lobby portrait shown on the character select card.")]
        [SerializeField] Sprite portrait;
        [Tooltip("Visual-only rig (no gameplay scripts) that stands in the menu hallway while the local player has this character selected in the lobby.")]
        [SerializeField] GameObject menuPreviewPrefab;

        public string DisplayName => displayName;
        public GameObject PlayerPrefab => playerPrefab;
        public Sprite Portrait => portrait;
        public GameObject MenuPreviewPrefab => menuPreviewPrefab;
    }

    [SerializeField] GameObject playerPrefab;
    [SerializeField] Vector3 levelStartPosition;
    [SerializeField] Vector3 levelStartEulerAngles;
    [SerializeField] float respawnDelaySeconds = 3f;

    [Header("Lobby characters")]
    [Tooltip("Selectable characters in the menu lobby (one owner each). When empty, every player spawns the default Player Prefab.")]
    [SerializeField] LobbyCharacter[] lobbyCharacters = Array.Empty<LobbyCharacter>();
    [Tooltip("Flashlight item prefab the lobby preview character holds, lit, in the menu hallway.")]
    [SerializeField] GameObject menuPreviewFlashlightPrefab;

    [Header("Connection approval")]
    [Tooltip("If left empty, falls back to Application.version (the build version set in PlayerSettings). " +
        "Override only when you need a network-protocol version that's independent from the Unity build version.")]
    [SerializeField] string buildVersionOverride = "";
    [Tooltip("Hard cap including the host. Connection approval rejects further joins once the lobby is full.")]
    [SerializeField, Min(1)] int maxPlayers = 4;

    public GameObject PlayerPrefab => playerPrefab;
    public Vector3 LevelStartPosition => levelStartPosition;
    public Quaternion LevelStartRotation => Quaternion.Euler(levelStartEulerAngles);
    public float RespawnDelaySeconds => respawnDelaySeconds;
    public string BuildVersion => string.IsNullOrWhiteSpace(buildVersionOverride) ? Application.version : buildVersionOverride;
    public int MaxPlayers => maxPlayers;

    public GameObject MenuPreviewFlashlightPrefab => menuPreviewFlashlightPrefab;

    public int LobbyCharacterCount => lobbyCharacters != null ? lobbyCharacters.Length : 0;

    public LobbyCharacter GetLobbyCharacter(int index)
    {
        if (lobbyCharacters == null || index < 0 || index >= lobbyCharacters.Length)
            return null;
        return lobbyCharacters[index];
    }
}
