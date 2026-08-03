using UnityEngine;

/// <summary>
/// Stands the local player's picked survivor in the menu hallway while the online lobby screen is
/// up: offset toward screen-left ahead of the menu camera, body turned a few degrees so the right
/// (flashlight) arm faces the viewer, sweeping a lit flashlight slowly down the corridor. Purely
/// local dressing — it mirrors <see cref="MultiplayerSessionController.LocalCharacterIndex"/> only
/// (never other players' picks) and despawns the moment the lobby screen hides, the selection is
/// lost, or the session dies. Driven every frame by <see cref="MainMenuController"/> via
/// <see cref="Apply"/>; prefab data comes from <see cref="MultiplayerProjectSettings"/>.
/// </summary>
[DisallowMultipleComponent]
public sealed class LobbyCharacterPreview : MonoBehaviour
{
    // Stage pose, hand-placed against the Menu.unity hallway camera (y=3.0, pitched 9° down,
    // looking down -Z): right up close so the survivor fills the frame between the lobby panels,
    // and hoisted ~1m off the floor so the body rides high enough in the tilted frame — the floor
    // this near the camera is below the bottom frame edge, so the lift never reads as floating.
    static readonly Vector3 StagePosition = new Vector3(0.76f, 0.98f, -1.18f);
    /// <summary>180 faces straight down the hall; a touch less turns the flashlight side to the camera.</summary>
    const float StageYawDegrees = 174.5f;

    // Warm key spot hung high behind the survivor's shoulder, raking down their back. The stage
    // sits in an unlit stretch of hallway, so without it the survivor is a black cutout; a spot
    // (not a point) keeps the throw off the corridor walls. Hand-placed in a live session.
    static readonly Vector3 KeyLightPosition = new Vector3(0.35f, 3.48f, 0.5f);
    static readonly Vector3 KeyLightEulerAngles = new Vector3(39f, 166.3f, 0f);
    static readonly Color KeyLightColor = new Color(1f, 0.86f, 0.66f);
    const float KeyLightIntensity = 7f;
    const float KeyLightRange = 3.5f;
    const float KeyLightSpotAngle = 72f;
    const float KeyLightInnerSpotAngle = 40f;

    // Beam rest direction in world space: down the hallway, drifted back toward the centre line and
    // dipped so the spot pools on the floor partway down — the in-game carry angle.
    static readonly Vector3 BeamRestDirection = new Vector3(-0.04f, -0.12f, -1f);

    // Slow Perlin drift on the beam so the light reads as held, not bolted on. Degrees, half-amplitude.
    const float BeamWanderYawDegrees = 6f;
    const float BeamWanderPitchDegrees = 3f;
    const float BeamWanderSpeed = 0.07f;

    /// <summary>Animator value for the one-hand fist grip (see <see cref="GrabbableInventoryItem.HeldPoseIndex"/>).</summary>
    const int FistHoldPose = 1;

    static readonly int HoldPoseHash = Animator.StringToHash("HoldPose");

    MultiplayerProjectSettings _settings;
    GameObject _character;
    Animator _characterAnimator;
    GameObject _flashlightGo;
    FlashlightItem _flashlightItem;
    GameObject _keyLightGo;
    Transform _handSocket;
    Transform _spotLight;
    int _shownCharacterIndex = -1;
    float _wanderSeed;
    bool _warnedMissingData;

    void Awake()
    {
        _settings = Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
        _wanderSeed = Random.value * 83f;
    }

    /// <summary>Idempotent per-frame drive: show the given character, or nothing when hidden or unassigned.</summary>
    public void Apply(bool visible, int characterIndex)
    {
        int wanted = visible && characterIndex >= 0 ? characterIndex : -1;
        if (wanted == _shownCharacterIndex)
            return;

        Despawn();
        if (wanted >= 0)
            Spawn(wanted);
        _shownCharacterIndex = wanted;
    }

    void OnDestroy()
    {
        Despawn();
    }

    void LateUpdate()
    {
        // After the Animator has posed the hand for this frame, same as HeldItemHandSocketFollow in-game.
        SeatFlashlight();
    }

    void Spawn(int characterIndex)
    {
        MultiplayerProjectSettings.LobbyCharacter character = _settings != null ? _settings.GetLobbyCharacter(characterIndex) : null;
        GameObject characterPrefab = character != null ? character.MenuPreviewPrefab : null;
        if (characterPrefab == null)
        {
            WarnMissingDataOnce();
            return;
        }

        _character = Instantiate(characterPrefab, transform);
        _character.name = "Preview_" + characterPrefab.name;
        _character.transform.SetPositionAndRotation(StagePosition, Quaternion.Euler(0f, StageYawDegrees, 0f));

        // The character rigs bundle the first-person arm meshes alongside the third-person body;
        // seen from behind both arm sets overlap and z-fight, so only the body renders here.
        foreach (Renderer meshRenderer in _character.GetComponentsInChildren<Renderer>(true))
        {
            if (meshRenderer.gameObject.name.Contains("FPS"))
                meshRenderer.enabled = false;
        }

        // Carve the menu navmesh around the survivor so the patrolling Jailor walks around them
        // instead of clipping through (the visual rig has no colliders of its own). The capsule is
        // centred on the hoisted root and tall enough to reach down through the floor navmesh.
        var navObstacle = _character.AddComponent<UnityEngine.AI.NavMeshObstacle>();
        navObstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Capsule;
        navObstacle.radius = 0.55f;
        navObstacle.height = 3f;
        navObstacle.center = Vector3.zero;
        navObstacle.carving = true;

        _characterAnimator = _character.GetComponentInChildren<Animator>(true);
        if (_characterAnimator != null)
        {
            _characterAnimator.applyRootMotion = false;
            _characterAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (_characterAnimator.runtimeAnimatorController != null)
                _characterAnimator.SetInteger(HoldPoseHash, FistHoldPose);
            else
                Debug.LogWarning($"[{nameof(LobbyCharacterPreview)}] Preview prefab '{characterPrefab.name}' has no animator controller — it will T-pose.", this);
        }

        SpawnFlashlight(character);
        SpawnKeyLight();
    }

    void SpawnKeyLight()
    {
        _keyLightGo = new GameObject("Preview_KeyLight");
        _keyLightGo.transform.SetParent(transform, false);
        _keyLightGo.transform.SetPositionAndRotation(KeyLightPosition, Quaternion.Euler(KeyLightEulerAngles));

        var keyLight = _keyLightGo.AddComponent<Light>();
        keyLight.type = LightType.Spot;
        keyLight.spotAngle = KeyLightSpotAngle;
        keyLight.innerSpotAngle = KeyLightInnerSpotAngle;
        keyLight.range = KeyLightRange;
        keyLight.intensity = KeyLightIntensity;
        keyLight.color = KeyLightColor;
        keyLight.shadows = LightShadows.None;
        keyLight.renderMode = LightRenderMode.ForcePixel;
    }

    void SpawnFlashlight(MultiplayerProjectSettings.LobbyCharacter character)
    {
        GameObject flashlightPrefab = _settings != null ? _settings.MenuPreviewFlashlightPrefab : null;
        if (flashlightPrefab == null)
        {
            WarnMissingDataOnce();
            return;
        }

        _handSocket = ResolveHandSocket(character);
        if (_handSocket == null)
            return;

        _flashlightGo = Instantiate(flashlightPrefab, transform);
        _flashlightGo.name = "Preview_Flashlight";

        // Held items never collide or simulate — mirror that so the prop cannot fall or shove anything.
        foreach (Collider itemCollider in _flashlightGo.GetComponentsInChildren<Collider>(true))
            itemCollider.enabled = false;
        if (_flashlightGo.TryGetComponent(out Rigidbody body))
        {
            body.isKinematic = true;
            body.useGravity = false;
        }

        _flashlightItem = _flashlightGo.GetComponent<FlashlightItem>();
        if (_flashlightItem != null)
            _flashlightItem.SetLightEnabled(true);

        _spotLight = null;
        foreach (Light light in _flashlightGo.GetComponentsInChildren<Light>(true))
        {
            if (light.type == LightType.Spot)
            {
                _spotLight = light.transform;
                break;
            }
        }

        // Seat immediately so the very first rendered frame is already in-hand, not at the prefab origin.
        SeatFlashlight();
    }

    /// <summary>
    /// The visual rigs don't carry the hand grip socket — recreate it from this character's playable
    /// prefab (same skeleton, socket authored under the right-hand bone), falling back to the bare bone.
    /// </summary>
    Transform ResolveHandSocket(MultiplayerProjectSettings.LobbyCharacter character)
    {
        if (_characterAnimator == null || !_characterAnimator.isHuman)
            return null;

        Transform hand = _characterAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null)
            return null;

        Transform socket = hand.Find("GripSocket_R");
        if (socket != null)
            return socket;

        socket = new GameObject("GripSocket_R (Preview)").transform;
        socket.SetParent(hand, false);

        Transform authored = character != null && character.PlayerPrefab != null
            ? FindDeep(character.PlayerPrefab.transform, "GripSocket_R")
            : null;
        if (authored != null)
        {
            socket.localPosition = authored.localPosition;
            socket.localRotation = authored.localRotation;
        }
        return socket;
    }

    void SeatFlashlight()
    {
        if (_flashlightItem == null || _handSocket == null || _character == null)
            return;

        float t = Time.time * BeamWanderSpeed;
        float yaw = (Mathf.PerlinNoise(_wanderSeed, t) - 0.5f) * 2f * BeamWanderYawDegrees;
        float pitch = (Mathf.PerlinNoise(_wanderSeed + 31.7f, t * 0.8f) - 0.5f) * 2f * BeamWanderPitchDegrees;
        Quaternion aim = Quaternion.LookRotation(BeamRestDirection.normalized) * Quaternion.Euler(pitch, yaw, 0f);

        // The in-game seat: grip pinned onto the animated fist, mesh and beam turned to the aim.
        _flashlightItem.ApplyHandSocketHeldPoseAim(_handSocket, aim);
        if (_spotLight != null)
            _spotLight.rotation = aim;
    }

    void Despawn()
    {
        if (_character != null)
            Destroy(_character);
        if (_flashlightGo != null)
            Destroy(_flashlightGo);
        if (_keyLightGo != null)
            Destroy(_keyLightGo);
        _character = null;
        _characterAnimator = null;
        _flashlightGo = null;
        _flashlightItem = null;
        _keyLightGo = null;
        _handSocket = null;
        _spotLight = null;
    }

    static Transform FindDeep(Transform root, string childName)
    {
        if (root == null)
            return null;
        if (root.name == childName)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), childName);
            if (found != null)
                return found;
        }
        return null;
    }

    void WarnMissingDataOnce()
    {
        if (_warnedMissingData)
            return;
        _warnedMissingData = true;
        Debug.LogWarning($"[{nameof(LobbyCharacterPreview)}] MultiplayerProjectSettings is missing menu preview prefabs (character or flashlight) — the lobby hallway preview stays empty.", this);
    }
}
