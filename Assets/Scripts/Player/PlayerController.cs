using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;
using Unity.Netcode;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(AudioSource))]
[DefaultExecutionOrder(100)]
public partial class PlayerController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] CharacterController characterController;
    [SerializeField] Animator animator;
    [SerializeField] InputActionAsset inputActions;
    [Tooltip("What should rotate to face movement. Leave empty to auto-pick a child visual transform when possible.")]
    [SerializeField] Transform facingTransform;
    [Tooltip("Trigger sphere used to stop the upper body pushing farther into walls. Leave empty to auto-pick a child trigger sphere.")]
    [SerializeField] SphereCollider upperBodyWallTrigger;

    [Header("First-person look")]
    [SerializeField] bool firstPersonLook = true;
    [Tooltip("Mouse pointer delta scale. Gamepad uses degrees per second below.")]
    [SerializeField] float mouseLookSensitivity = 0.08f;
    [SerializeField] float gamepadLookSensitivityDegrees = 140f;
    [SerializeField] float minPitchDegrees = -89f;
    [SerializeField] float maxPitchDegrees = 89f;
    [SerializeField] bool lockCursor = true;

    [Header("Movement")]
    [SerializeField] bool moveRelativeToCamera = true;
    [Tooltip("If null, uses Camera.main for facing and movement.")]
    [SerializeField] Transform cameraTransform;
    [Tooltip("First-person yaw node (child of mesh, usually named CameraPitch). Auto-found under the player if empty.")]
    [SerializeField] Transform cameraPitchTransform;
    [Tooltip("While ragdolled or standing up, parent CameraPitch to the head so the view follows the body. Otherwise CameraPitch stays on its prefab parent (e.g. mesh root) so it does not bob with the head.")]
    [FormerlySerializedAs("parentCameraPitchToHead")]
    [SerializeField] bool attachCameraPitchToHeadDuringRagdollRecovery = true;
    [Header("Interaction")]
    [Tooltip("Where held items should attach. Assign your flashlight hold point here.")]
    [SerializeField] Transform flashlightHoldPoint;
    [Tooltip("If enabled, held flashlights follow the full camera rotation, including pitch.")]
    [SerializeField] bool flashlightFollowsCameraPitch = true;
    [SerializeField] float interactDistance = 5f;
    [Tooltip("Radius for aim-forgiving interaction checks. 0 uses a thin line raycast.")]
    [SerializeField] float interactSphereRadius = 0.25f;
    [SerializeField] float dropForce = 0.65f;
    /// <summary>Same scalar used for inventory toss impulse (<see cref="dropForce"/>).</summary>
    public float DropItemImpulse => dropForce;
    [Tooltip("Optional UI root (e.g. a Panel) shown when you look at something you can pick up.")]
    [SerializeField] GameObject pickupPromptRoot;
    [Tooltip("Optional UI Text for the pickup prompt. If empty, tries to find a Text under pickupPromptRoot.")]
    [SerializeField] Text pickupPromptText;
    [Tooltip("Optional icon shown next to the elevator occupancy prompt (e.g. player silhouette).")]
    [SerializeField] Image pickupPromptPlayerIcon;
    [SerializeField] string pickupPromptMessage = "Press E to pick up";
    [SerializeField] string chestPromptMessage = "Press E to open";
    [SerializeField] string doorUnlockPromptMessage = "Press E to unlock";
    [SerializeField] string doorOpenPromptMessage = "Press E to open";
    [Tooltip("Optional mask for interactable items. If empty, Unity default raycast layers are used.")]
    [SerializeField] LayerMask interactMask;
    [NonSerialized] RaycastHit[] _interactCastHitBuffer = new RaycastHit[32];
    [Tooltip("Optional mask for upper-body wall blocking. If empty, Unity default raycast layers are used.")]
    [SerializeField] LayerMask upperBodyWallMask;
    [SerializeField] float walkSpeed = 2.4f;
    [SerializeField] float runSpeed = 4.8f;
    [SerializeField] float acceleration = 10f;
    [SerializeField] float deceleration = 14f;
    [Tooltip("Extra braking used when movement is released or reversed. Higher values reduce the slippery feeling.")]
    [SerializeField] float brakingDeceleration = 26f;
    [SerializeField] float turnSpeedDegrees = 720f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float jumpHeight = 1.0f;
    [SerializeField] float groundedStickDown = 2f;

    [Header("Physics props")]
    [Tooltip("How much of this controller's horizontal speed is imparted to rigidbodies that add PlayerPhysicsPushReceiver.")]
    [SerializeField, Min(0f)] float rigidbodyHorizontalPushStrength = 0.85f;
    [Tooltip("Caps the VelocityChange impulse (m/s) so sprinting into props does not spike unrealistically.")]
    [SerializeField, Min(0.1f)] float rigidbodyHorizontalPushMaxDelta = 3.5f;

    [Header("Footsteps")]
    [SerializeField] AudioSource footstepAudioSource;
    [SerializeField] AudioClip footstepClip1;
    [SerializeField] AudioClip footstepClip2;
    [SerializeField] float walkFootstepInterval = 0.48f;
    [SerializeField] float runFootstepInterval = 0.34f;
    [SerializeField] float footstepVolume = 0.5f;
    [SerializeField] float minimumFootstepSpeed = 0.15f;
    [SerializeField] AudioClip flashlightClickClip;
    [SerializeField, Range(0f, 1f)] float flashlightClickVolume = 0.65f;
    [SerializeField] AudioClip bandageUseClip;
    [SerializeField, Range(0f, 1f)] float bandageUseVolume = 0.75f;

    [Header("Stamina")]
    [SerializeField] float maxStamina = 100f;
    [Tooltip("Stamina drained per second while sprinting.")]
    [SerializeField] float staminaDrainRate = 20f;
    [Tooltip("Stamina recovered per second while not sprinting.")]
    [SerializeField] float staminaRegenRate = 15f;
    [Tooltip("Seconds after releasing sprint before stamina begins regenerating.")]
    [SerializeField] float staminaRegenDelay = 1.5f;
    [Tooltip("Stamina spent when the player jumps.")]
    [SerializeField] float jumpStaminaCost = 8f;
    [Tooltip("Stamina spent when the player throws a punch.")]
    [SerializeField] float punchStaminaCost = 6f;
    [Tooltip("Optional UI Image (set to Filled) to display the stamina bar. If empty, one is created automatically.")]
    [SerializeField] Image staminaBarImage;
    [Tooltip("Auto-create a HUD stamina bar if none is assigned.")]
    [SerializeField] bool autoCreateStaminaBar = true;

    [Header("Heavy Throwable Charge")]
    [Tooltip("Seconds of holding Attack (left click) to reach a full-power, max-distance throw. The charge bar fills over this duration.")]
    [SerializeField, Min(0.05f)] float chargeSecondsToFull = 1.25f;
    [Tooltip("Auto-create the HUD throw-charge bar (shown only while charging a throw).")]
    [SerializeField] bool autoCreateThrowChargeBar = true;

    [Header("Melee")]
    [Tooltip("Range of the melee attack.")]
    [SerializeField] float meleeRange = 2f;
    [Tooltip("Angle in degrees for the melee cone (half-angle from center).")]
    [SerializeField] float meleeAngle = 60f;
    [Tooltip("Layer mask for detecting enemies. Defaults to 'Enemy' layer if empty.")]
    [SerializeField] LayerMask enemyMask;
    [Tooltip("Delay in seconds before damage is applied (sync with animation hit frame).")]
    [SerializeField] float meleeHitDelay = 0.25f;
    [Tooltip("Cooldown between melee attacks in seconds.")]
    [SerializeField] float meleeCooldown = 0.8f;

    /// <summary>Exposed so <see cref="NetworkPlayerCombat"/> can enforce the same cooldown server-side.</summary>
    public float MeleeCooldown => meleeCooldown;
    [Tooltip("Trigger parameter name in Animator for melee attack.")]
    [SerializeField] string meleeTrigger = "RightHook";
    [SerializeField] AudioClip meleeSwooshClip;
    [SerializeField, Range(0f, 1f)] float meleeSwooshVolume = 0.65f;
    [SerializeField] AudioClip meleeHitPunch1;
    [SerializeField] AudioClip meleeHitPunch2;
    [SerializeField] AudioClip meleeHitPunch3;
    [SerializeField, Range(0f, 1f)] float meleeHitPunchVolume = 0.7f;
    [Tooltip("Played instead of the punch impact when the melee connects with a Skeleton.")]
    [SerializeField] AudioClip skeletonHitClip;
    [SerializeField] AudioClip zombieHitClip;
    [SerializeField, Range(0f, 1f)] float zombieHitVolume = 0.75f;

    [Header("Animator")]
    [SerializeField] bool driveAnimator = true;
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
    [SerializeField] string strafeDirectionParameter = "StrafeDirection";
    [SerializeField] string moveXParameter = "MoveX";
    [SerializeField] string moveYParameter = "MoveY";
    [SerializeField] string animSpeedParameter = "AnimSpeed";
    [Tooltip("Damping time for animator locomotion parameters, in seconds.")]
    [SerializeField] float locomotionBlendDampTime = 0.12f;
    [Tooltip("Smoothing factor for strafe direction transitions (higher = faster).")]
    [SerializeField] float strafeDirectionSmoothSpeed = 8f;
    [Tooltip("After ragdoll/get-up, keep animator locomotion Speed at 0 for this long (covers GettingUp→Idle blend ~0.15s + margin).")]
    [SerializeField] float ragdollRecoverAnimatorSuppressSeconds = 0.28f;
    [Tooltip("If true, cross-fade base layer to Idle when ragdoll recovery ends so locomotion cannot flash during the transition.")]
    [SerializeField] bool snapAnimatorToIdleAfterRagdollRecover = true;
    [SerializeField] string baseLayerIdleStateName = "Idle";
    [Tooltip("Fixed-time crossfade duration into Idle (seconds).")]
    [SerializeField] float ragdollIdleCrossFadeSeconds = 0.08f;
    [Tooltip("Animator Speed value used for walking. Keep this below the run threshold in the controller.")]
    [SerializeField] float walkAnimationSpeed = 0.35f;
    [Tooltip("Animator Speed value used for sprinting. Keep this above the run threshold in the controller.")]
    [SerializeField] float runAnimationSpeed = 1f;

    [Header("Crouch")]
    [Tooltip("Hold this action (Crouch input, default C / gamepad East) to crouch while standing still. Moving stands the player back up (no crouch-walk). Lowers the capsule while crouched.")]
    [SerializeField] bool enableCrouch = true;
    [Tooltip("Animator bool set true while crouched. Drives the Crouch (CrouchIdle) state.")]
    [SerializeField] string crouchParameter = "Crouching";
    [Tooltip("CharacterController height while crouched. Standing height is read from the controller at Awake.")]
    [SerializeField] float crouchHeight = 1.15f;
    [Tooltip("Layers that block standing back up (a low ceiling). Leave empty to auto-build everything except this player and Ignore Raycast/Enemy.")]
    [SerializeField] LayerMask standUpBlockingMask;

    bool _isCrouching;
    /// <summary>
    /// Server-safe crouch state. Reads the replicated <c>Crouching</c> animator bool so it is valid on the
    /// server's copy of a remote player (where the owner-local <see cref="_isCrouching"/> input field is never
    /// set). Falls back to the local field when the animator/param is unavailable (e.g. offline play).
    /// </summary>
    public bool IsCrouching =>
        animator != null && !string.IsNullOrEmpty(crouchParameter)
            ? animator.GetBool(crouchParameter)
            : _isCrouching;
    float _standingHeight;
    Vector3 _standingCenter;
    int _standUpMaskFallback = Physics.DefaultRaycastLayers;

    InputActionMap _playerMap;
    InputAction _moveAction;
    InputAction _lookAction;
    InputAction _jumpAction;
    InputAction _sprintAction;
    InputAction _interactAction;
    InputAction _dropAction;
    InputAction _flashlightAction;
    InputAction _attackAction;
    InputAction _crouchAction;
    InputActionAsset _runtimeInputActions;

    float _lookYawDegrees;
    float _lookPitchDegrees;

    Vector3 _verticalVelocity;
    Vector3 _horizontalVelocity;
    Vector2 _moveInput;
    Vector3 _groundMoveThisFrame;
    float _currentHorizontalSpeed;

    // External knockback (e.g. a swinging axe trap). Non-ragdoll: integrated into the CharacterController
    // move and briefly suppresses movement input so the external force takes priority over the player.
    Vector3 _externalPushVelocity;          // horizontal world velocity; bleeds off over time
    float _externalPushControlLockTimer;    // seconds remaining where movement input is ignored
    const float ExternalPushDecayPerSecond = 40f;

    readonly Collider[] _upperBodyWallHits = new Collider[16];
    bool _pickupPromptVisible;

    float _currentStamina;
    float _staminaRegenTimer;
    bool _isSprinting;
    bool _audiblySprintingForAi;
    RectTransform _staminaFillRect;
    GameObject _staminaBarRoot;
    bool _isChargingThrow;
    float _throwChargeTimer;
    GameObject _throwChargeBarRoot;
    RectTransform _throwChargeFillRect;
    GameObject _crosshairRoot;
    GameObject _inventorySlotsRoot;
    Image[] _inventorySlotBorderImages;
    Image[] _inventorySlotIconImages;
    Color _inventoryDefaultBorderColor;
    Color _inventoryDefaultFillColor;
    Color _inventorySelectedBorderColor;
    NetworkPlayerInventory _networkPlayerInventory;
    [Tooltip("Items not in the active hotbar slot are parented here. Auto-created as child of the player if empty.")]
    [SerializeField] Transform inventoryStashRoot;
    GrabbableInventoryItem[] _localInventorySlots = new GrabbableInventoryItem[3];
    /// <summary>Parallel to <see cref="_localInventorySlots"/>; 0 for empty, 1 for flashlight, 1–5 for glowstick.</summary>
    int[] _localSlotStacks = new int[3];
    int _localSelectedSlot;
    TMP_Text[] _inventorySlotCountTexts;
    HudPrompt _hudPrompt;
    Image[] _inventorySlotFlashlightBatteryFillImages;
    RectTransform[] _inventorySlotFlashlightBatteryFillRects;
    GameObject[] _inventorySlotFlashlightBatteryBarRoots;
    static readonly Color FlashlightBatteryBarFill = new Color(0.85f, 0.65f, 0.17f, 0.95f);
    float _footstepTimer;
    bool _playFootstep1Next = true;
    bool _hasLocalControl = true;
    bool _allowLookWhileMovementLocked;
    float _smoothedStrafeDirection;
    int _lastRigidbodyPushFrame = -1;
    int _lastRigidbodyPushBodyId = int.MinValue;

    float _nextMeleeTime;
    readonly Collider[] _meleeHits = new Collider[16];
    readonly HashSet<ZombieHealth> _meleeHitZombies = new();
    readonly HashSet<SkeletonHealth> _meleeHitSkeletons = new();
    bool _meleeHitSkeletonThisSwing;
    const string EnemyLayerName = "Enemy";
    NetworkPlayerCombat _networkPlayerCombat;
    NetworkPlayerAvatar _networkPlayerAvatar;
    PlayerRagdollController _ragdollController;
    RagdollCameraCollision _ragdollCameraCollision;
    RagdollCameraDamper _ragdollCameraDamper;
    PlayerHealth _playerHealth;

    float _ragdollRecoverAnimatorSuppressUntil;
    float _postJailMovementLockEndTime;

    bool _cameraPitchParentedToHead;
    bool _hasSavedCameraPitchPrefabPose;
    Transform _savedCameraPitchParent;
    Vector3 _savedCameraPitchLocalPosition;
    Quaternion _savedCameraPitchLocalRotation;

    public float StaminaNormalized => maxStamina > 0f ? _currentStamina / maxStamina : 0f;
    public float ThrowChargeNormalized => _isChargingThrow ? Mathf.Clamp01(_throwChargeTimer / Mathf.Max(0.0001f, chargeSecondsToFull)) : 0f;
    public bool HasLocalControl => _hasLocalControl;
    public bool IsAudiblySprintingForAi => _audiblySprintingForAi;
    bool IsPostJailMovementLocked => Time.time < _postJailMovementLockEndTime;

    /// <summary>Local player only: blocks walk/sprint/jump for a short time (e.g. after Jailor seals the cell).</summary>
    public void BeginPostJailMovementLockout(float durationSeconds)
    {
        if (durationSeconds <= 0f)
            return;
        _postJailMovementLockEndTime = Time.time + durationSeconds;
    }
    public Transform LookPitchTransform => cameraTransform;
    public bool UsesFirstPersonLook => firstPersonLook;
    public Transform CameraPitchNode => cameraPitchTransform;

    public void RestoreFullStamina()
    {
        _currentStamina = maxStamina;
        _staminaRegenTimer = 0f;
        RefreshStaminaUI();
    }

    void HandlePlayerHealthStaminaReset()
    {
        RestoreFullStamina();
    }

    /// <summary>
    /// Clears movement state and animator locomotion parameters. Call when ragdoll/get-up ends so
    /// pre-ragdoll horizontal velocity does not briefly drive Walk/Run after returning to Idle.
    /// </summary>
    public void ResetLocomotionAfterRagdollRecover()
    {
        _horizontalVelocity = Vector3.zero;
        _currentHorizontalSpeed = 0f;
        _groundMoveThisFrame = Vector3.zero;
        _isSprinting = false;
        _audiblySprintingForAi = false;
        if (_networkPlayerAvatar != null && _networkPlayerAvatar.IsSpawned && _networkPlayerAvatar.IsOwner)
            _networkPlayerAvatar.PublishAudiblySprinting(false);
        _smoothedStrafeDirection = 0f;
        if (_isCrouching)
        {
            _isCrouching = false;
            ApplyCrouchCollider(false);
        }
        _verticalVelocity.y = characterController != null && characterController.isGrounded
            ? -groundedStickDown
            : 0f;

        if (!driveAnimator || animator == null)
            return;

        if (snapAnimatorToIdleAfterRagdollRecover && !string.IsNullOrEmpty(baseLayerIdleStateName))
        {
            int idleHash = Animator.StringToHash(baseLayerIdleStateName);
            if (animator.HasState(0, idleHash))
                animator.CrossFadeInFixedTime(baseLayerIdleStateName, ragdollIdleCrossFadeSeconds, 0, 0f);
        }

        animator.SetFloat(speedParameter, 0f);
        animator.SetBool(groundedParameter, true);
        animator.SetFloat(verticalVelocityParameter, _verticalVelocity.y);
        animator.SetFloat(strafeDirectionParameter, 0f);
        animator.SetFloat(moveXParameter, 0f);
        animator.SetFloat(moveYParameter, 0f);
        animator.SetFloat(animSpeedParameter, 1f);
        if (!string.IsNullOrEmpty(crouchParameter))
            animator.SetBool(crouchParameter, false);

        _ragdollRecoverAnimatorSuppressUntil = Time.time + Mathf.Max(0f, ragdollRecoverAnimatorSuppressSeconds);
    }

    /// <summary>
    /// Apply a non-ragdoll knockback that moves the CharacterController and briefly overrides movement input
    /// (so an external force such as a swinging trap "wins" over the player's own walking). World space.
    /// Call this on the player that owns this controller (movement is owner-authoritative).
    /// </summary>
    public void ApplyExternalPush(Vector3 horizontalVelocity, float upwardVelocity, float controlLockSeconds)
    {
        horizontalVelocity.y = 0f;
        _externalPushVelocity = horizontalVelocity;
        _externalPushControlLockTimer = Mathf.Max(_externalPushControlLockTimer, Mathf.Max(0f, controlLockSeconds));
        // Cancel current input momentum so the shove starts clean and isn't fought by leftover walk velocity.
        _horizontalVelocity = Vector3.zero;
        if (upwardVelocity > 0f)
            _verticalVelocity.y = Mathf.Max(_verticalVelocity.y, upwardVelocity);
    }

    void Awake()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (characterController != null)
        {
            _standingHeight = characterController.height;
            _standingCenter = characterController.center;
        }
        BuildStandUpMaskFallback();
        if (upperBodyWallTrigger == null)
            upperBodyWallTrigger = FindUpperBodyWallTrigger();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (animator != null)
            animator.applyRootMotion = false;

        if (facingTransform == null)
        {
            if (animator != null && animator.transform != transform)
                facingTransform = animator.transform;
            else
                facingTransform = transform;
        }

        if (firstPersonLook)
            SyncLookAnglesFromTransforms();

        // the authored prompt root is retired: prompts render through the runtime HudPrompt chip
        if (pickupPromptRoot != null)
            pickupPromptRoot.SetActive(false);

        SetPickupPromptVisible(false);
        _currentStamina = maxStamina;

        if (staminaBarImage == null && autoCreateStaminaBar)
            staminaBarImage = CreateStaminaBarUI();

        if (_throwChargeBarRoot == null && autoCreateThrowChargeBar)
            CreateThrowChargeBarUI();
        if (_throwChargeBarRoot != null)
            _throwChargeBarRoot.SetActive(false);

        CreateCrosshairUI();
        CreateTicketCounterUI();

        RefreshStaminaUI();
        RefreshInventorySlotHud();

        if (footstepAudioSource == null)
            footstepAudioSource = GetComponent<AudioSource>();
        if (footstepAudioSource == null)
            footstepAudioSource = gameObject.AddComponent<AudioSource>();

        ConfigureFootstepAudioSource();
        _networkPlayerCombat = GetComponent<NetworkPlayerCombat>();
        _networkPlayerAvatar = GetComponent<NetworkPlayerAvatar>();
        _networkPlayerInventory = GetComponent<NetworkPlayerInventory>();
        _ragdollController = GetComponent<PlayerRagdollController>();
        _ragdollCameraCollision = GetComponent<RagdollCameraCollision>();
        _ragdollCameraDamper = GetComponent<RagdollCameraDamper>();
        _playerHealth = GetComponent<PlayerHealth>();
        HookupCarnivalTickets();
        EnsureInventoryStashRoot();

        if (firstPersonLook && _ragdollController != null && _ragdollCameraCollision == null)
            _ragdollCameraCollision = gameObject.AddComponent<RagdollCameraCollision>();

        if (firstPersonLook && _ragdollController != null && _ragdollCameraDamper == null)
            _ragdollCameraDamper = gameObject.AddComponent<RagdollCameraDamper>();

        if (cameraPitchTransform == null)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "CameraPitch")
                {
                    cameraPitchTransform = t;
                    break;
                }
            }
        }

        if (cameraTransform == null)
        {
            foreach (Camera c in GetComponentsInChildren<Camera>(true))
            {
                if (c != null && c.transform.IsChildOf(transform))
                {
                    cameraTransform = c.transform;
                    break;
                }
            }
        }

        if (cameraTransform == null && Camera.main != null && Camera.main.transform.IsChildOf(transform))
            cameraTransform = Camera.main.transform;

        if (firstPersonLook && GetComponent<FirstPersonViewHeadSync>() == null)
            gameObject.AddComponent<FirstPersonViewHeadSync>();

        if (enemyMask == 0)
        {
            int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
            if (enemyLayer >= 0)
                enemyMask = 1 << enemyLayer;
        }

#if UNITY_EDITOR
        AutoAssignFootstepClipsInEditor();
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (footstepAudioSource == null)
            footstepAudioSource = GetComponent<AudioSource>();

        AutoAssignFootstepClipsInEditor();
    }
#endif

    void OnEnable()
    {
        if (_playerHealth != null)
        {
            _playerHealth.Died += HandlePlayerHealthStaminaReset;
            _playerHealth.Restored += HandlePlayerHealthStaminaReset;
        }

        ApplyLocalControlState();
    }

    void OnDisable()
    {
        if (_playerHealth != null)
        {
            _playerHealth.Died -= HandlePlayerHealthStaminaReset;
            _playerHealth.Restored -= HandlePlayerHealthStaminaReset;
        }

        DetachCameraPitchFromHead();
        DisableInputActions();
        ReleaseCursor();
    }

    void OnDestroy()
    {
        if (_runtimeInputActions != null)
            Destroy(_runtimeInputActions);
        UnhookCarnivalTickets();
    }

    void Update()
    {
        if (!_hasLocalControl && !ShouldRunDeadRagdollCameraUpdate())
        {
            if (_allowLookWhileMovementLocked && firstPersonLook)
                UpdateLookOnlyWhileMovementLocked();
            return;
        }

        if (_hasLocalControl && !IsUsingNetworkedInventory)
            TickLocalFlashlightBatteries();

        EnsureCameraPitchParentedToHead();

        if (_ragdollController != null && (_ragdollController.IsRagdolled || _ragdollController.IsHeld))
        {
            CancelThrowCharge();
            if (PauseMenuController.BlocksGameplayInput)
            {
                _moveInput = Vector2.zero;
                return;
            }

            Vector2 lookInputR = _playerMap != null && _playerMap.enabled && _lookAction != null
                ? _lookAction.ReadValue<Vector2>()
                : ReadLookFallback();

            if (firstPersonLook && lockCursor && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
                && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            if (firstPersonLook)
            {
                ApplyRagdollFirstPersonLook(lookInputR);
                if (UseNetworkedFlashlightFlow && _networkPlayerAvatar != null && _networkPlayerAvatar.IsOwner)
                    _networkPlayerAvatar.PublishFlashlightLookPitch(_lookPitchDegrees);
            }

            return;
        }

        if (_ragdollController != null && _ragdollController.IsGettingUp)
            return;

        if (PauseMenuController.BlocksGameplayInput || BlackjackOverlayController.IsInteractive)
        {
            CancelThrowCharge();
            _moveInput = Vector2.zero;
            _horizontalVelocity = Vector3.zero;
            SetPickupPromptVisible(false);
            // While seated at blackjack the game keeps running (time isn't paused), so the locomotion
            // animator would otherwise loop the last walk pose ("walking in place"). Force idle.
            if (BlackjackOverlayController.IsInteractive && driveAnimator && animator != null)
            {
                animator.SetFloat(speedParameter, 0f);
                animator.SetBool(groundedParameter, true);
                animator.SetFloat(verticalVelocityParameter, 0f);
            }
            return;
        }

        _moveInput = _moveAction != null ? _moveAction.ReadValue<Vector2>() : ReadMoveFallback();
        Vector2 lookInput = _lookAction != null ? _lookAction.ReadValue<Vector2>() : ReadLookFallback();
        bool jumpPressed = _jumpAction != null ? _jumpAction.WasPressedThisFrame() : WasJumpPressedFallback();
        bool sprintHeld = _sprintAction != null ? _sprintAction.IsPressed() : IsSprintHeldFallback();
        bool interactPressed = _interactAction != null
            ? _interactAction.WasPressedThisFrame()
            : WasInteractPressedFallback();
        bool dropPressed = _dropAction != null
            ? _dropAction.WasPressedThisFrame()
            : WasDropPressedFallback();
        bool flashlightPressed = _flashlightAction != null
            ? _flashlightAction.WasPressedThisFrame()
            : WasFlashlightPressedFallback();
        bool attackPressed = _attackAction != null
            ? _attackAction.WasPressedThisFrame()
            : WasAttackPressedFallback();
        bool attackReleased = _attackAction != null
            ? _attackAction.WasReleasedThisFrame()
            : WasAttackReleasedFallback();
        bool attackHeld = _attackAction != null
            ? _attackAction.IsPressed()
            : IsAttackHeldFallback();
        bool crouchHeld = enableCrouch && (_crouchAction != null
            ? _crouchAction.IsPressed()
            : IsCrouchHeldFallback());

        if (IsPostJailMovementLocked)
        {
            _moveInput = Vector2.zero;
            jumpPressed = false;
            sprintHeld = false;
        }

        if (firstPersonLook && lockCursor && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
            && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (firstPersonLook)
            ApplyFirstPersonLook(lookInput);

        if (firstPersonLook && UseNetworkedFlashlightFlow && _networkPlayerAvatar != null && _networkPlayerAvatar.IsOwner)
            _networkPlayerAvatar.PublishFlashlightLookPitch(_lookPitchDegrees);

        if (ProceduralMazeCoordinator.ShouldBlockLocalPlayerUntilMazeReady())
        {
            CancelThrowCharge();
            _moveInput = Vector2.zero;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = Vector3.zero;
            _groundMoveThisFrame = Vector3.zero;
            if (driveAnimator && animator != null)
            {
                animator.SetFloat(speedParameter, 0f, locomotionBlendDampTime, Time.deltaTime);
                // Stay visually grounded while waiting for replicated maze colliders. grounded=false
                // drives an in-air pose and lowers the head; first-person camera follows the head, so
                // other players (already standing) looked taller until the maze finished building.
                animator.SetBool(groundedParameter, true);
                animator.SetFloat(verticalVelocityParameter, 0f);
            }
            return;
        }

        HandleInventoryScrollInUpdate();

        if (interactPressed)
            HandlePickupInput();

        if (dropPressed)
            HandleDropInput();

        if (flashlightPressed)
            HandleFlashlightToggleInput();

        HandleAttackInput(attackPressed, attackReleased, attackHeld);
        RefreshThrowChargeUI();

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        Vector3 move = BuildGroundMoveDirection(_moveInput);
        float inputMagnitude = Mathf.Clamp01(_moveInput.magnitude);

        // While an external push is asserting movement priority (e.g. a trap shove), ignore walk/sprint/jump
        // input so the push wins instead of the player walking out of it.
        if (_externalPushControlLockTimer > 0f)
        {
            _externalPushControlLockTimer -= Time.deltaTime;
            inputMagnitude = 0f;
            sprintHeld = false;
            jumpPressed = false;
        }

        UpdateCrouchState(crouchHeld, inputMagnitude > 0.01f);
        if (_isCrouching)
            jumpPressed = false; // crouch is a stationary pose; moving uncrouches instead of jumping

        _isSprinting = sprintHeld && _currentStamina > 0f && inputMagnitude > 0.01f;
        UpdateStamina(sprintHeld, _isSprinting);

        float targetSpeed = inputMagnitude > 0.01f
            ? inputMagnitude * (_isSprinting ? runSpeed : walkSpeed)
            : 0f;
        Vector3 desiredHorizontalVelocity = move * targetSpeed;
        float speedChangeRate = GetHorizontalSpeedChangeRate(desiredHorizontalVelocity, targetSpeed);
        _horizontalVelocity = Vector3.MoveTowards(
            _horizontalVelocity,
            desiredHorizontalVelocity,
            speedChangeRate * Time.deltaTime);

        if (jumpPressed && grounded && _currentStamina > 0f)
        {
            _verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            SpendStamina(jumpStaminaCost);
        }

        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 horizontal = ApplyUpperBodyWallBlock(_horizontalVelocity);
        _horizontalVelocity = horizontal;
        _currentHorizontalSpeed = horizontal.magnitude;
        Vector3 motion = horizontal * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;

        // External knockback (trap push): add its displacement on top of normal movement and bleed it off.
        // Combined with the input lock above, this lets the axe shove the player without ragdolling them.
        if (_externalPushVelocity.sqrMagnitude > 0.0001f)
        {
            motion += _externalPushVelocity * Time.deltaTime;
            _externalPushVelocity = Vector3.MoveTowards(
                _externalPushVelocity, Vector3.zero, ExternalPushDecayPerSecond * Time.deltaTime);
        }

        bool wasGroundedBeforeMove = characterController.isGrounded;
        characterController.Move(motion);

        _groundMoveThisFrame = horizontal.sqrMagnitude > 1e-6f ? horizontal.normalized : move;

        TryPlayLandFootstep(wasGroundedBeforeMove, characterController.isGrounded);
        UpdateFootsteps(characterController.isGrounded);

        bool audibleSprint = _isSprinting
            && characterController.isGrounded
            && _currentHorizontalSpeed >= minimumFootstepSpeed;
        _audiblySprintingForAi = audibleSprint;
        if (_networkPlayerAvatar != null && _networkPlayerAvatar.IsSpawned && _networkPlayerAvatar.IsOwner)
            _networkPlayerAvatar.PublishAudiblySprinting(audibleSprint);

        if (driveAnimator && animator != null)
        {
            float speedForAnimator = 0f;
            if (_currentHorizontalSpeed > 0.01f)
                speedForAnimator = _isSprinting ? runAnimationSpeed : walkAnimationSpeed;

            speedForAnimator = Mathf.Clamp01(speedForAnimator);
            if (Time.time < _ragdollRecoverAnimatorSuppressUntil)
                speedForAnimator = 0f;
            if (IsPostJailMovementLocked)
                speedForAnimator = 0f;

            float targetStrafeDirection = ComputeStrafeDirection(_moveInput);
            _smoothedStrafeDirection = Mathf.MoveTowards(
                _smoothedStrafeDirection,
                targetStrafeDirection,
                strafeDirectionSmoothSpeed * Time.deltaTime);
            Vector2 targetLocomotionBlend = ComputeLocomotionBlend(_moveInput);

            float animSpeed = ComputeAnimationSpeed(_currentHorizontalSpeed, _isSprinting);

            animator.SetFloat(speedParameter, speedForAnimator, locomotionBlendDampTime, Time.deltaTime);
            animator.SetBool(groundedParameter, characterController.isGrounded);
            animator.SetFloat(verticalVelocityParameter, _verticalVelocity.y);
            animator.SetFloat(strafeDirectionParameter, _smoothedStrafeDirection);
            animator.SetFloat(moveXParameter, targetLocomotionBlend.x, locomotionBlendDampTime, Time.deltaTime);
            animator.SetFloat(moveYParameter, targetLocomotionBlend.y, locomotionBlendDampTime, Time.deltaTime);
            animator.SetFloat(animSpeedParameter, animSpeed);
            if (!string.IsNullOrEmpty(crouchParameter))
                animator.SetBool(crouchParameter, _isCrouching);
        }

        UpdatePickupPrompt();
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!isActiveAndEnabled || characterController == null || !characterController.enabled)
            return;

        if (ProceduralMazeCoordinator.ShouldBlockLocalPlayerUntilMazeReady())
            return;

        PlayerPhysicsPushReceiver receiver =
            hit.collider.GetComponent<PlayerPhysicsPushReceiver>()
            ?? hit.collider.GetComponentInParent<PlayerPhysicsPushReceiver>();
        if (receiver == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool networkedListening = nm != null && nm.IsListening;
        NetworkedPhysicsPropPush netPush =
            receiver.GetComponent<NetworkedPhysicsPropPush>() ?? hit.collider.GetComponentInParent<NetworkedPhysicsPropPush>();

        if (networkedListening && netPush != null && netPush.IsSpawned)
        {
            int bodyOrNetId = netPush.GetInstanceID();

            if (_lastRigidbodyPushFrame == Time.frameCount && _lastRigidbodyPushBodyId == bodyOrNetId)
                return;
            _lastRigidbodyPushFrame = Time.frameCount;
            _lastRigidbodyPushBodyId = bodyOrNetId;

            Vector3 planar = characterController.velocity;
            planar.y = 0f;
            float speed = planar.magnitude;
            if (speed < 0.04f)
                return;

            Vector3 md = hit.moveDirection;
            Vector3 pushDir = new Vector3(md.x, 0f, md.z);
            if (pushDir.sqrMagnitude < 1e-5f)
                return;

            pushDir.Normalize();

            if (md.y < -0.35f && hit.normal.y > 0.2f)
                return;

            netPush.RequestCharacterBumpServerRpc(pushDir, speed);
            return;
        }

        Rigidbody body = hit.collider.attachedRigidbody;
        if (body == null || body.isKinematic || !body.detectCollisions)
            return;

        int bodyId = body.GetInstanceID();
        if (_lastRigidbodyPushFrame == Time.frameCount && _lastRigidbodyPushBodyId == bodyId)
            return;
        _lastRigidbodyPushFrame = Time.frameCount;
        _lastRigidbodyPushBodyId = bodyId;

        Vector3 planarVel = characterController.velocity;
        planarVel.y = 0f;
        float spd = planarVel.magnitude;
        if (spd < 0.04f)
            return;

        Vector3 moveDir = hit.moveDirection;
        Vector3 dir = new Vector3(moveDir.x, 0f, moveDir.z);
        if (dir.sqrMagnitude < 1e-5f)
            return;
        dir.Normalize();

        // Avoid shoving when mostly stepping down onto the obstacle.
        if (moveDir.y < -0.35f && hit.normal.y > 0.2f)
            return;

        float transfer = Mathf.Min(
            spd * rigidbodyHorizontalPushStrength * receiver.PushGainMultiplier,
            rigidbodyHorizontalPushMaxDelta);

        body.AddForce(dir * transfer, ForceMode.VelocityChange);
        if (body.TryGetComponent<RigidbodyImpactSfx>(out var impactBumpSfx))
            impactBumpSfx.NotifyCharacterControllerBump(spd);
    }

    void UpdateStamina(bool sprintHeld, bool isSprinting)
    {
        if (isSprinting)
        {
            _currentStamina = Mathf.Max(0f, _currentStamina - staminaDrainRate * Time.deltaTime);
            _staminaRegenTimer = staminaRegenDelay;
        }
        else if (sprintHeld)
        {
            // Holding sprint at empty stamina should not let the player bounce
            // between walk and run as tiny amounts of stamina regenerate.
        }
        else
        {
            _staminaRegenTimer -= Time.deltaTime;
            if (_staminaRegenTimer <= 0f)
                _currentStamina = Mathf.Min(maxStamina, _currentStamina + staminaRegenRate * Time.deltaTime);
        }

        RefreshStaminaUI();
    }

    void SpendStamina(float amount)
    {
        if (amount <= 0f)
            return;

        _currentStamina = Mathf.Max(0f, _currentStamina - amount);
        _staminaRegenTimer = staminaRegenDelay;
        RefreshStaminaUI();
    }

    float GetHorizontalSpeedChangeRate(Vector3 desiredHorizontalVelocity, float targetSpeed)
    {
        if (targetSpeed <= 0.01f)
            return brakingDeceleration;

        if (_horizontalVelocity.sqrMagnitude > 1e-6f && desiredHorizontalVelocity.sqrMagnitude > 1e-6f)
        {
            float alignment = Vector3.Dot(_horizontalVelocity.normalized, desiredHorizontalVelocity.normalized);
            if (alignment < 0.35f)
                return brakingDeceleration;
        }

        return targetSpeed > _currentHorizontalSpeed ? acceleration : deceleration;
    }

    /// <summary>
    /// Crouch is a stationary pose: hold the crouch input while still to crouch; any movement input stands
    /// the player back up so they walk normally (there is no crouch-walk). When standing still, releasing
    /// under a low ceiling keeps the player crouched until there is room to stand.
    /// </summary>
    void UpdateCrouchState(bool crouchHeld, bool isMoving)
    {
        bool wantCrouch = crouchHeld && !isMoving;

        // Only block standing on headroom while stationary; moving always uncrouches so the player can
        // walk out from under a low ceiling instead of getting stuck crouched in place.
        if (_isCrouching && !wantCrouch && !isMoving && !HasHeadroomToStand())
            wantCrouch = true;

        if (wantCrouch == _isCrouching)
            return;

        _isCrouching = wantCrouch;
        ApplyCrouchCollider(_isCrouching);
    }

    void ApplyCrouchCollider(bool crouched)
    {
        if (characterController == null)
            return;

        if (crouched)
        {
            float feetY = _standingCenter.y - _standingHeight * 0.5f;
            float height = Mathf.Min(crouchHeight, _standingHeight);
            characterController.height = height;
            Vector3 center = _standingCenter;
            center.y = feetY + height * 0.5f; // keep the feet planted; shrink from the top down
            characterController.center = center;
        }
        else
        {
            characterController.height = _standingHeight;
            characterController.center = _standingCenter;
        }
    }

    /// <summary>True when the full standing capsule has clearance above the current crouched capsule.</summary>
    bool HasHeadroomToStand()
    {
        if (characterController == null)
            return true;

        float radius = Mathf.Max(0.01f, characterController.radius * 0.95f);
        float standTopY = _standingCenter.y + _standingHeight * 0.5f - characterController.radius;
        float crouchTopY = characterController.center.y + characterController.height * 0.5f - characterController.radius;
        float distance = standTopY - crouchTopY;
        if (distance <= 0.001f)
            return true;

        Vector3 origin = transform.TransformPoint(new Vector3(characterController.center.x, crouchTopY, characterController.center.z));
        int mask = standUpBlockingMask.value != 0 ? standUpBlockingMask.value : _standUpMaskFallback;
        return !Physics.SphereCast(origin, radius, Vector3.up, out _, distance, mask, QueryTriggerInteraction.Ignore);
    }

    void BuildStandUpMaskFallback()
    {
        int mask = Physics.AllLayers;
        mask &= ~(1 << gameObject.layer);
        int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
        if (ignoreRaycast >= 0)
            mask &= ~(1 << ignoreRaycast);
        int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
        if (enemyLayer >= 0)
            mask &= ~(1 << enemyLayer);
        _standUpMaskFallback = mask;
    }

    void RefreshStaminaUI()
    {
        if (_staminaFillRect != null)
            _staminaFillRect.anchorMax = new Vector2(StaminaNormalized, 1f);
        else if (staminaBarImage != null)
            staminaBarImage.fillAmount = StaminaNormalized;
    }

    void RefreshThrowChargeUI()
    {
        if (_throwChargeBarRoot == null)
            return;

        bool show = _isChargingThrow;
        if (_throwChargeBarRoot.activeSelf != show)
            _throwChargeBarRoot.SetActive(show);

        if (show && _throwChargeFillRect != null)
            _throwChargeFillRect.anchorMax = new Vector2(ThrowChargeNormalized, 1f);
    }

    void UpdateFootsteps(bool grounded)
    {
        if (footstepAudioSource == null)
            return;

        if (!grounded || _currentHorizontalSpeed < minimumFootstepSpeed)
        {
            _footstepTimer = 0f;
            return;
        }

        float interval = Mathf.Max(0.05f, _isSprinting ? runFootstepInterval : walkFootstepInterval);
        _footstepTimer -= Time.deltaTime;
        if (_footstepTimer > 0f)
            return;

        PlayFootstepOneShot();
        _footstepTimer = interval;
    }

    void TryPlayLandFootstep(bool wasGroundedBeforeMove, bool groundedAfterMove)
    {
        if (footstepAudioSource == null || !groundedAfterMove || wasGroundedBeforeMove)
            return;

        PlayFootstepOneShot();

        if (_currentHorizontalSpeed >= minimumFootstepSpeed)
            _footstepTimer = Mathf.Max(0.05f, _isSprinting ? runFootstepInterval : walkFootstepInterval);
    }

    void PlayFootstepOneShot()
    {
        if (footstepAudioSource == null)
            return;

        AudioClip clipToPlay = _playFootstep1Next ? footstepClip1 : footstepClip2;
        if (clipToPlay == null)
            clipToPlay = footstepClip1 != null ? footstepClip1 : footstepClip2;

        if (clipToPlay == null)
            return;

        footstepAudioSource.PlayOneShot(clipToPlay, Mathf.Max(0f, footstepVolume));
        _playFootstep1Next = !_playFootstep1Next;
    }

    void ConfigureFootstepAudioSource()
    {
        if (footstepAudioSource == null)
            return;

        footstepAudioSource.playOnAwake = false;
        footstepAudioSource.loop = false;
        footstepAudioSource.spatialBlend = 0f;
        footstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(footstepAudioSource);
    }

    /// <summary>
    /// When false, normal movement/input is off but first-person look can still run (e.g. Jailor carry).
    /// Must be set before <see cref="SetLocalControl"/> when entering that state so input bindings apply correctly.
    /// </summary>
    public void SetAllowLookWhileMovementLocked(bool allow)
    {
        if (_allowLookWhileMovementLocked == allow)
            return;

        _allowLookWhileMovementLocked = allow;
        if (!_hasLocalControl)
            ApplyLocalControlState();
    }

    public void SetLocalControl(bool hasLocalControl)
    {
        if (_hasLocalControl == hasLocalControl)
            return;

        _hasLocalControl = hasLocalControl;
        ApplyLocalControlState();
    }

    public void OnClientMazeCollidersBecameReady()
    {
        if (isActiveAndEnabled)
            _verticalVelocity = new Vector3(0f, -groundedStickDown, 0f);
    }

    public void SetHudVisible(bool visible)
    {
        if (!visible)
            CancelThrowCharge();

        if (_staminaBarRoot != null)
            _staminaBarRoot.SetActive(visible);
        else if (staminaBarImage != null)
            staminaBarImage.enabled = visible;

        if (_inventorySlotsRoot != null)
            _inventorySlotsRoot.SetActive(visible);

        if (_crosshairRoot != null)
            _crosshairRoot.SetActive(visible);

        if (_ticketCounterRoot != null)
            _ticketCounterRoot.SetActive(visible);

        if (!visible)
            SetPickupPromptVisible(false);
    }

    void ApplyLocalControlState()
    {
        if (_hasLocalControl)
        {
            AcquireInputActions();
            _playerMap?.Enable();
            ApplyCursorLock();
        }
        else
        {
            if (_allowLookWhileMovementLocked && firstPersonLook)
                ApplyLookOnlyInputMode();
            else
            {
                DisableInputActions();
                ReleaseCursor();
            }

            _moveInput = Vector2.zero;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = Vector3.zero;
            SetPickupPromptVisible(false);
        }

        SetHudVisible(_hasLocalControl);
    }

    void ApplyLookOnlyInputMode()
    {
        AcquireInputActions();
        _moveAction?.Disable();
        _jumpAction?.Disable();
        _sprintAction?.Disable();
        _interactAction?.Disable();
        _dropAction?.Disable();
        _flashlightAction?.Enable();
        _attackAction?.Disable();
        _crouchAction?.Disable();
        _lookAction?.Enable();
        ApplyCursorLock();
    }

    void UpdateLookOnlyWhileMovementLocked()
    {
        if (_ragdollController != null && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld))
            return;

        EnsureCameraPitchParentedToHead();

        if (PauseMenuController.BlocksGameplayInput)
            return;

        Vector2 lookInput = _lookAction != null && _lookAction.enabled
            ? _lookAction.ReadValue<Vector2>()
            : ReadLookFallback();

        if (firstPersonLook && lockCursor && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
            && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (!firstPersonLook)
            return;

        ApplyFirstPersonLook(lookInput);

        if (UseNetworkedFlashlightFlow && _networkPlayerAvatar != null && _networkPlayerAvatar.IsOwner)
            _networkPlayerAvatar.PublishFlashlightLookPitch(_lookPitchDegrees);

        bool flashlightPressed = _flashlightAction != null
            ? _flashlightAction.WasPressedThisFrame()
            : WasFlashlightPressedFallback();
        if (flashlightPressed)
            HandleFlashlightToggleInput();
    }

    void AcquireInputActions()
    {
        if (inputActions == null)
        {
            Debug.LogWarning($"{nameof(PlayerController)}: Assign the Input Actions asset (e.g. InputSystem_Actions). Falling back to direct device input.", this);
            return;
        }

        if (_runtimeInputActions == null)
            _runtimeInputActions = Instantiate(inputActions);

        _playerMap ??= _runtimeInputActions.FindActionMap("Player");
        if (_playerMap == null)
        {
            Debug.LogWarning($"{nameof(PlayerController)}: No 'Player' action map on the assigned asset. Falling back to direct device input.", this);
            return;
        }

        _moveAction ??= _playerMap.FindAction("Move");
        _lookAction ??= _playerMap.FindAction("Look");
        _jumpAction ??= _playerMap.FindAction("Jump");
        _sprintAction ??= _playerMap.FindAction("Sprint");
        _interactAction ??= _playerMap.FindAction("Interact");
        _dropAction ??= _playerMap.FindAction("Drop");
        _flashlightAction ??= _playerMap.FindAction("Flashlight");
        _attackAction ??= _playerMap.FindAction("Attack");
        _crouchAction ??= _playerMap.FindAction("Crouch");

        if (!_playerMap.enabled)
            _playerMap.Enable();
    }

    void DisableInputActions()
    {
        _playerMap?.Disable();
    }

    void ApplyCursorLock()
    {
        if (!firstPersonLook || !lockCursor || PauseMenuController.BlocksGameplayInput || BlackjackOverlayController.IsInteractive)
            return;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void ReleaseCursor()
    {
        if (!firstPersonLook || !lockCursor)
            return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

#if UNITY_EDITOR
    void AutoAssignFootstepClipsInEditor()
    {
        if (footstepClip1 == null)
            footstepClip1 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep1.mp3");

        if (footstepClip2 == null)
            footstepClip2 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep2.mp3");

        if (flashlightClickClip == null)
            flashlightClickClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/FlashLightClick.wav");

        if (bandageUseClip == null)
            bandageUseClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Bandage.mp3");

        if (meleeSwooshClip == null)
            meleeSwooshClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Swoosh.wav");

        if (meleeHitPunch1 == null)
            meleeHitPunch1 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Punch1.wav");
        if (meleeHitPunch2 == null)
            meleeHitPunch2 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Punch2.wav");
        if (meleeHitPunch3 == null)
            meleeHitPunch3 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Punch3.wav");
        if (skeletonHitClip == null)
            skeletonHitClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonHit.wav");
        if (zombieHitClip == null)
            zombieHitClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/ZombieHit.wav");
    }
#endif

    void CreateCrosshairUI()
    {
        Canvas canvas = HudKit.EnsureHudCanvas();

        GameObject dot = new GameObject("Crosshair");
        dot.layer = 5;
        dot.transform.SetParent(canvas.transform, false);
        RectTransform rt = dot.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(6f, 6f);
        Image img = dot.AddComponent<Image>();
        img.sprite = MenuTheme.Circle();
        img.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.42f);
        img.raycastTarget = false;
        _crosshairRoot = dot;
    }

    Image CreateStaminaBarUI()
    {
        const float invSlotRowHeight = 96f;
        const float invSlotBorder = 2.5f;
        const float invSlotBottomPad = 6f;
        const float invSlotStaminaGap = 8f;
        float staminaBarBottomY = invSlotBottomPad + invSlotRowHeight + invSlotStaminaGap;

        Canvas canvas = HudKit.EnsureHudCanvas();

        GameObject invRow = new GameObject("InventorySlotRow");
        invRow.layer = 5;
        invRow.transform.SetParent(canvas.transform, false);
        RectTransform invRowRect = invRow.AddComponent<RectTransform>();
        invRowRect.anchorMin = new Vector2(0.5f, 0f);
        invRowRect.anchorMax = new Vector2(0.5f, 0f);
        invRowRect.pivot = new Vector2(0.5f, 0f);
        invRowRect.anchoredPosition = new Vector2(0f, invSlotBottomPad);
        invRowRect.sizeDelta = new Vector2(304f, invSlotRowHeight);
        HorizontalLayoutGroup invLayout = invRow.AddComponent<HorizontalLayoutGroup>();
        invLayout.spacing = 8f;
        invLayout.childAlignment = TextAnchor.MiddleCenter;
        invLayout.childControlWidth = true;
        invLayout.childControlHeight = true;
        invLayout.childForceExpandWidth = true;
        invLayout.childForceExpandHeight = true;
        // dark plate slots with a bone frame; the selected slot flips its frame mustard
        _inventoryDefaultBorderColor = MenuTheme.WithAlpha(MenuTheme.Bone, 0.28f);
        _inventoryDefaultFillColor = MenuTheme.WithAlpha(MenuTheme.Ink, 0.60f);
        _inventorySelectedBorderColor = MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f);
        _inventorySlotBorderImages = new Image[3];
        _inventorySlotIconImages = new Image[3];
        _inventorySlotCountTexts = new TMP_Text[3];
        _inventorySlotFlashlightBatteryFillImages = new Image[3];
        _inventorySlotFlashlightBatteryFillRects = new RectTransform[3];
        _inventorySlotFlashlightBatteryBarRoots = new GameObject[3];
        for (int i = 0; i < 3; i++)
        {
            GameObject slot = new GameObject("InventorySlot" + (i + 1));
            slot.layer = 5;
            slot.transform.SetParent(invRow.transform, false);
            // the slot root draws only the frame ring; the dark plate is the inset fill child
            Image border = slot.AddComponent<Image>();
            border.sprite = MenuTheme.RoundedOutline(2, 2f);
            border.type = Image.Type.Sliced;
            border.color = _inventoryDefaultBorderColor;
            border.raycastTarget = false;
            _inventorySlotBorderImages[i] = border;
            LayoutElement le = slot.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minHeight = invSlotRowHeight;

            GameObject invSlotFillGo = new GameObject("Fill");
            invSlotFillGo.layer = 5;
            invSlotFillGo.transform.SetParent(slot.transform, false);
            invSlotFillGo.transform.SetAsFirstSibling();
            Image invSlotFillImage = invSlotFillGo.AddComponent<Image>();
            invSlotFillImage.sprite = MenuTheme.RoundedRect(2);
            invSlotFillImage.type = Image.Type.Sliced;
            invSlotFillImage.color = _inventoryDefaultFillColor;
            invSlotFillImage.raycastTarget = false;
            RectTransform invSlotFillRect = invSlotFillGo.GetComponent<RectTransform>();
            invSlotFillRect.anchorMin = Vector2.zero;
            invSlotFillRect.anchorMax = Vector2.one;
            invSlotFillRect.pivot = new Vector2(0.5f, 0.5f);
            invSlotFillRect.offsetMin = new Vector2(invSlotBorder, invSlotBorder);
            invSlotFillRect.offsetMax = new Vector2(-invSlotBorder, -invSlotBorder);

            GameObject iconGo = new GameObject("Icon");
            iconGo.layer = 5;
            iconGo.transform.SetParent(invSlotFillGo.transform, false);
            Image icon = iconGo.AddComponent<Image>();
            icon.color = Color.white;
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            icon.enabled = false;
            RectTransform iconRect = icon.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.1f, 0.17f);
            iconRect.anchorMax = new Vector2(0.9f, 0.9f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;
            _inventorySlotIconImages[i] = icon;

            GameObject batteryBarGo = new GameObject("FlashlightBattery");
            batteryBarGo.layer = 5;
            batteryBarGo.transform.SetParent(invSlotFillGo.transform, false);
            _inventorySlotFlashlightBatteryBarRoots[i] = batteryBarGo;
            RectTransform batteryBarRt = batteryBarGo.AddComponent<RectTransform>();
            batteryBarRt.anchorMin = new Vector2(0.1f, 0.04f);
            batteryBarRt.anchorMax = new Vector2(0.9f, 0.12f);
            batteryBarRt.pivot = new Vector2(0.5f, 0.5f);
            batteryBarRt.offsetMin = Vector2.zero;
            batteryBarRt.offsetMax = Vector2.zero;
            GameObject trackGo = new GameObject("Track");
            trackGo.layer = 5;
            trackGo.transform.SetParent(batteryBarGo.transform, false);
            Image trackImg = trackGo.AddComponent<Image>();
            trackImg.raycastTarget = false;
            trackImg.color = MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f);
            RectTransform trackRect = trackGo.GetComponent<RectTransform>();
            trackRect.anchorMin = Vector2.zero;
            trackRect.anchorMax = Vector2.one;
            trackRect.pivot = new Vector2(0.5f, 0.5f);
            trackRect.offsetMin = Vector2.zero;
            trackRect.offsetMax = Vector2.zero;
            GameObject batteryFillGo = new GameObject("Fill");
            batteryFillGo.layer = 5;
            batteryFillGo.transform.SetParent(batteryBarGo.transform, false);
            Image batteryFill = batteryFillGo.AddComponent<Image>();
            batteryFill.raycastTarget = false;
            batteryFill.color = FlashlightBatteryBarFill;
            _inventorySlotFlashlightBatteryFillImages[i] = batteryFill;
            RectTransform batteryFillRect = batteryFill.GetComponent<RectTransform>();
            batteryFillRect.anchorMin = new Vector2(0f, 0f);
            batteryFillRect.anchorMax = new Vector2(1f, 1f);
            batteryFillRect.pivot = new Vector2(0f, 0.5f);
            batteryFillRect.offsetMin = new Vector2(1.5f, 1.5f);
            batteryFillRect.offsetMax = new Vector2(-1.5f, -1.5f);
            _inventorySlotFlashlightBatteryFillRects[i] = batteryFillRect;
            batteryBarGo.SetActive(false);

            GameObject countGo = new GameObject("StackCount");
            countGo.layer = 5;
            countGo.transform.SetParent(invSlotFillGo.transform, false);
            TextMeshProUGUI countText = countGo.AddComponent<TextMeshProUGUI>();
            countText.font = MenuTheme.DisplayFont;
            countText.alignment = TextAlignmentOptions.BottomRight;
            countText.fontSize = 17f;
            countText.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.95f);
            countText.raycastTarget = false;
            countText.text = string.Empty;
            _inventorySlotCountTexts[i] = countText;
            RectTransform countRect = countText.rectTransform;
            countRect.anchorMin = new Vector2(0.55f, 0f);
            countRect.anchorMax = new Vector2(1f, 0.45f);
            countRect.pivot = new Vector2(1f, 0f);
            countRect.offsetMin = new Vector2(2f, 2f);
            countRect.offsetMax = new Vector2(-5f, 0f);
        }
        _inventorySlotsRoot = invRow;

        GameObject bg = new GameObject("StaminaBarBG");
        bg.layer = 5;
        bg.transform.SetParent(canvas.transform, false);
        Image bgImage = bg.AddComponent<Image>();
        bgImage.sprite = MenuTheme.RoundedRect(2);
        bgImage.type = Image.Type.Sliced;
        bgImage.color = MenuTheme.WithAlpha(MenuTheme.Ink, 0.72f);
        bgImage.raycastTarget = false;
        RectTransform bgRect = bg.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0.5f, 0f);
        bgRect.anchorMax = new Vector2(0.5f, 0f);
        bgRect.pivot = new Vector2(0.5f, 0f);
        bgRect.anchoredPosition = new Vector2(0f, staminaBarBottomY);
        bgRect.sizeDelta = new Vector2(304f, 24f);
        _staminaBarRoot = bg;

        GameObject fill = new GameObject("StaminaBarFill");
        fill.layer = 5;
        fill.transform.SetParent(bg.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.62f);
        fillImage.raycastTarget = false;
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.offsetMin = new Vector2(2.5f, 2.5f);
        fillRect.offsetMax = new Vector2(-2.5f, -2.5f);
        _staminaFillRect = fillRect;

        GameObject frameGo = new GameObject("Frame");
        frameGo.layer = 5;
        frameGo.transform.SetParent(bg.transform, false);
        Image frame = frameGo.AddComponent<Image>();
        frame.sprite = MenuTheme.RoundedOutline(2, 1.6f);
        frame.type = Image.Type.Sliced;
        frame.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.20f);
        frame.raycastTarget = false;
        RectTransform frameRect = frame.rectTransform;
        frameRect.anchorMin = Vector2.zero;
        frameRect.anchorMax = Vector2.one;
        frameRect.offsetMin = Vector2.zero;
        frameRect.offsetMax = Vector2.zero;

        return fillImage;
    }

    void CreateThrowChargeBarUI()
    {
        // Sit directly above the stamina bar (which sits above the inventory row), matching its
        // width and styling. Mirrors the constants used by CreateStaminaBarUI for placement.
        const float invSlotRowHeight = 96f;
        const float invSlotBottomPad = 6f;
        const float invSlotStaminaGap = 8f;
        const float staminaBarHeight = 24f;
        const float chargeBarHeight = 18f;
        const float chargeBarGap = 6f;
        float staminaBarBottomY = invSlotBottomPad + invSlotRowHeight + invSlotStaminaGap;
        float chargeBarBottomY = staminaBarBottomY + staminaBarHeight + chargeBarGap;

        Canvas canvas = HudKit.EnsureHudCanvas();

        GameObject bg = new GameObject("ThrowChargeBarBG");
        bg.layer = 5;
        bg.transform.SetParent(canvas.transform, false);
        Image bgImage = bg.AddComponent<Image>();
        bgImage.sprite = MenuTheme.RoundedRect(2);
        bgImage.type = Image.Type.Sliced;
        bgImage.color = MenuTheme.WithAlpha(MenuTheme.Ink, 0.72f);
        bgImage.raycastTarget = false;
        RectTransform bgRect = bg.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0.5f, 0f);
        bgRect.anchorMax = new Vector2(0.5f, 0f);
        bgRect.pivot = new Vector2(0.5f, 0f);
        bgRect.anchoredPosition = new Vector2(0f, chargeBarBottomY);
        bgRect.sizeDelta = new Vector2(304f, chargeBarHeight);
        _throwChargeBarRoot = bg;

        GameObject fill = new GameObject("ThrowChargeBarFill");
        fill.layer = 5;
        fill.transform.SetParent(bg.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f);
        fillImage.raycastTarget = false;
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.offsetMin = new Vector2(2.5f, 2.5f);
        fillRect.offsetMax = new Vector2(-2.5f, -2.5f);
        _throwChargeFillRect = fillRect;

        GameObject frameGo = new GameObject("Frame");
        frameGo.layer = 5;
        frameGo.transform.SetParent(bg.transform, false);
        Image frame = frameGo.AddComponent<Image>();
        frame.sprite = MenuTheme.RoundedOutline(2, 1.6f);
        frame.type = Image.Type.Sliced;
        frame.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.20f);
        frame.raycastTarget = false;
        RectTransform frameRect = frame.rectTransform;
        frameRect.anchorMin = Vector2.zero;
        frameRect.anchorMax = Vector2.one;
        frameRect.offsetMin = Vector2.zero;
        frameRect.offsetMax = Vector2.zero;
    }

    void LateUpdate()
    {
        if (_hasLocalControl)
            UpdateInventoryFlashlightBatteryHud();

        if (!_hasLocalControl)
            return;

        if (_ragdollController != null && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp))
            return;

        if (firstPersonLook)
            return;

        Transform cam = CameraTransformForFacing;
        if (facingTransform == null || cam == null)
            return;

        Vector3 faceDir = GetFacingDirection(cam, _groundMoveThisFrame);
        if (faceDir.sqrMagnitude < 0.0001f)
            return;

        Quaternion target = Quaternion.LookRotation(faceDir);
        facingTransform.rotation = Quaternion.RotateTowards(
            facingTransform.rotation,
            target,
            turnSpeedDegrees * Time.deltaTime);
    }

    public Transform CameraTransformForFacing => cameraTransform != null ? cameraTransform : Camera.main != null ? Camera.main.transform : null;
    bool UseNetworkedFlashlightFlow => NetworkManager.Singleton != null
        && NetworkManager.Singleton.IsListening
        && _networkPlayerAvatar != null
        && _networkPlayerAvatar.IsSpawned;

    SphereCollider FindUpperBodyWallTrigger()
    {
        SphereCollider[] spheres = GetComponentsInChildren<SphereCollider>(true);
        foreach (SphereCollider sphere in spheres)
        {
            if (sphere == null || !sphere.isTrigger)
                continue;

            if (sphere.transform == transform)
                continue;

            return sphere;
        }

        return null;
    }

    Vector3 ApplyUpperBodyWallBlock(Vector3 horizontalVelocity)
    {
        if (upperBodyWallTrigger == null || !upperBodyWallTrigger.enabled || !upperBodyWallTrigger.isTrigger)
            return horizontalVelocity;

        GetWorldSphere(upperBodyWallTrigger, out Vector3 center, out float radius);
        int mask = upperBodyWallMask.value == 0 ? Physics.DefaultRaycastLayers : upperBodyWallMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(center, radius, _upperBodyWallHits, mask, QueryTriggerInteraction.Ignore);
        if (hitCount <= 0)
            return horizontalVelocity;

        Vector3 filtered = horizontalVelocity;
        Transform root = transform.root;
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _upperBodyWallHits[i];
            if (hit == null)
                continue;

            _upperBodyWallHits[i] = null;

            if (hit.isTrigger || hit.transform.root == root)
                continue;

            if (!Physics.ComputePenetration(
                    upperBodyWallTrigger,
                    upperBodyWallTrigger.transform.position,
                    upperBodyWallTrigger.transform.rotation,
                    hit,
                    hit.transform.position,
                    hit.transform.rotation,
                    out Vector3 separationDirection,
                    out _))
            {
                continue;
            }

            filtered = RemoveIntoWallComponent(filtered, separationDirection);
            if (filtered.sqrMagnitude < 1e-6f)
                return Vector3.zero;
        }

        return filtered;
    }

    static void GetWorldSphere(SphereCollider sphere, out Vector3 center, out float radius)
    {
        center = sphere.transform.TransformPoint(sphere.center);
        Vector3 lossy = sphere.transform.lossyScale;
        float maxScale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z));
        radius = sphere.radius * maxScale;
    }

    static Vector3 RemoveIntoWallComponent(Vector3 velocity, Vector3 separationDirection)
    {
        float pushIntoWall = Vector3.Dot(velocity, separationDirection);
        if (pushIntoWall >= 0f)
            return velocity;

        return velocity - separationDirection * pushIntoWall;
    }

    Vector3 BuildGroundMoveDirection(Vector2 input)
    {
        Vector3 raw = new Vector3(input.x, 0f, input.y);
        if (raw.sqrMagnitude > 1f)
            raw.Normalize();

        if (firstPersonLook)
            return FlattenBasisFromTransform(transform, input);

        Transform cam = CameraTransformForFacing;
        if (!moveRelativeToCamera || cam == null)
            return raw;

        Vector3 forward = cam.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 right = cam.right;
        right.y = 0f;
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.right;
        right.Normalize();

        Vector3 onGround = forward * input.y + right * input.x;
        if (onGround.sqrMagnitude > 1f)
            onGround.Normalize();
        return onGround;
    }

    static Vector3 FlattenBasisFromTransform(Transform basis, Vector2 input)
    {
        Vector3 forward = basis.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 right = basis.right;
        right.y = 0f;
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.right;
        right.Normalize();

        Vector3 onGround = forward * input.y + right * input.x;
        if (onGround.sqrMagnitude > 1f)
            onGround.Normalize();
        return onGround;
    }

    void AccumulateFirstPersonLookDeltas(Vector2 look)
    {
        if (look.sqrMagnitude > 1e-8f)
        {
            InputDevice activeDevice = _lookAction != null ? _lookAction.activeControl?.device : null;
            bool fromMouse = activeDevice is Mouse || activeDevice is Pointer
                || (activeDevice == null && Mouse.current != null);

            float yawDelta;
            float pitchDelta;
            if (fromMouse)
            {
                yawDelta = look.x * mouseLookSensitivity;
                pitchDelta = look.y * mouseLookSensitivity;
            }
            else
            {
                float rate = gamepadLookSensitivityDegrees * Time.deltaTime;
                yawDelta = look.x * rate;
                pitchDelta = look.y * rate;
            }

            _lookYawDegrees += yawDelta;
            _lookPitchDegrees -= pitchDelta;
        }

        _lookPitchDegrees = Mathf.Clamp(_lookPitchDegrees, minPitchDegrees, maxPitchDegrees);
    }

    void ApplyFirstPersonLook(Vector2 look)
    {
        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        AccumulateFirstPersonLookDeltas(look);
        transform.rotation = Quaternion.Euler(0f, _lookYawDegrees, 0f);

        if (cam.IsChildOf(transform))
            cam.localRotation = Quaternion.Euler(_lookPitchDegrees, 0f, 0f);
        else
            cam.rotation = transform.rotation * Quaternion.Euler(_lookPitchDegrees, 0f, 0f);
    }

    /// <summary>
    /// When dead, <see cref="NetworkPlayerAvatar"/> clears local control so movement stops, but we still need
    /// Update (camera parented to head + ragdoll look) while death ragdoll is active for the owning player.
    /// </summary>
    bool ShouldRunDeadRagdollCameraUpdate()
    {
        if (_playerHealth == null || !_playerHealth.IsDead)
            return false;
        if (_ragdollController == null || !_ragdollController.IsRagdolled)
            return false;
        if (_networkPlayerAvatar != null && _networkPlayerAvatar.IsSpawned && !_networkPlayerAvatar.IsOwner)
            return false;
        return true;
    }

    void EnsureCameraPitchParentedToHead()
    {
        if (!attachCameraPitchToHeadDuringRagdollRecovery || cameraPitchTransform == null || animator == null || !animator.isHuman)
        {
            if (_cameraPitchParentedToHead)
                DetachCameraPitchFromHead();
            return;
        }

        bool wantAttach = _ragdollController != null && firstPersonLook
            && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld);

        if (wantAttach && !_cameraPitchParentedToHead)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null)
                return;

            if (!_hasSavedCameraPitchPrefabPose)
            {
                _savedCameraPitchParent = cameraPitchTransform.parent;
                _savedCameraPitchLocalPosition = cameraPitchTransform.localPosition;
                _savedCameraPitchLocalRotation = cameraPitchTransform.localRotation;
                _hasSavedCameraPitchPrefabPose = true;
            }

            // Parent to the damper's smoothed proxy (which chases the head) rather than the head bone directly,
            // so the view inherits a dampened pose instead of the head's raw physics flailing. Falls back to the
            // head bone if no damper is present.
            Transform followParent = head;
            if (_ragdollCameraDamper != null)
            {
                _ragdollCameraDamper.BeginFollow(head);
                followParent = _ragdollCameraDamper.Proxy;
            }

            cameraPitchTransform.SetParent(followParent, true);
            _cameraPitchParentedToHead = true;
        }
        else if (!wantAttach && _cameraPitchParentedToHead)
            DetachCameraPitchFromHead();
    }

    void DetachCameraPitchFromHead()
    {
        if (!_cameraPitchParentedToHead || cameraPitchTransform == null)
            return;

        if (_hasSavedCameraPitchPrefabPose && _savedCameraPitchParent != null)
        {
            cameraPitchTransform.SetParent(_savedCameraPitchParent, false);
            cameraPitchTransform.localPosition = _savedCameraPitchLocalPosition;
            cameraPitchTransform.localRotation = _savedCameraPitchLocalRotation;
        }

        if (_ragdollCameraDamper != null)
            _ragdollCameraDamper.EndFollow();

        _cameraPitchParentedToHead = false;
        if (firstPersonLook)
            SyncLookAnglesFromTransforms();
    }

    void ApplyRagdollFirstPersonLook(Vector2 look)
    {
        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        AccumulateFirstPersonLookDeltas(look);

        if (cameraPitchTransform != null && _cameraPitchParentedToHead)
        {
            cameraPitchTransform.localRotation = Quaternion.Euler(0f, _lookYawDegrees, 0f);
            cam.localRotation = Quaternion.Euler(_lookPitchDegrees, 0f, 0f);
        }
        else
            cam.rotation = Quaternion.Euler(_lookPitchDegrees, _lookYawDegrees, 0f);
    }

    void SyncLookAnglesFromTransforms()
    {
        _lookYawDegrees = transform.eulerAngles.y;
        Transform cam = CameraTransformForFacing;
        if (cam == null)
        {
            _lookPitchDegrees = 0f;
            return;
        }

        if (cam.IsChildOf(transform))
        {
            Vector3 e = cam.localEulerAngles;
            _lookPitchDegrees = NormalizeEulerPitch(e.x);
            return;
        }

        Quaternion yawOnly = Quaternion.Euler(0f, _lookYawDegrees, 0f);
        Vector3 rel = (Quaternion.Inverse(yawOnly) * cam.rotation).eulerAngles;
        _lookPitchDegrees = NormalizeEulerPitch(rel.x);
    }

    static float NormalizeEulerPitch(float x)
    {
        if (x > 180f)
            x -= 360f;
        return x;
    }

    float ComputeStrafeDirection(Vector2 input)
    {
        if (input.sqrMagnitude < 0.01f)
            return 0f;

        float magnitude = input.magnitude;
        if (magnitude < 0.01f)
            return 0f;

        Vector2 normalized = input / magnitude;
        return Mathf.Clamp(normalized.x, -1f, 1f);
    }

    Vector2 ComputeLocomotionBlend(Vector2 input)
    {
        if (input.sqrMagnitude < 0.01f)
            return Vector2.zero;

        return new Vector2(
            Mathf.Clamp(input.x, -1f, 1f),
            Mathf.Clamp(input.y, -1f, 1f));
    }

    float ComputeAnimationSpeed(float currentSpeed, bool sprinting)
    {
        if (currentSpeed < 0.01f)
            return 1f;

        float referenceSpeed = sprinting ? runSpeed : walkSpeed;
        if (referenceSpeed < 0.01f)
            return 1f;

        float ratio = currentSpeed / referenceSpeed;
        return Mathf.Clamp(ratio, 0.5f, 2f);
    }

    public bool TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform)
    {
        holdPoint = flashlightHoldPoint;
        followTransform = flashlightHoldPoint;

        if (flashlightHoldPoint == null)
            return false;

        Transform cam = CameraTransformForFacing;
        // Local owner applies pitch on this transform; remote peers get the same pitch via NetworkPlayerAvatar.
        bool useCameraPitch = flashlightFollowsCameraPitch && cam != null;
        followTransform = useCameraPitch ? cam : flashlightHoldPoint;
        return true;
    }

    void UpdatePickupPrompt()
    {
        if (pickupPromptRoot == null)
            return;

        Transform cam = CameraTransformForFacing;
        if (cam != null && TryFindInteractableMazeChest(cam, out _))
        {
            SetPickupPromptVisible(true, chestPromptMessage);
            return;
        }

        // All three door prompts key off the same aimed door; cast once and evaluate the conditions on that
        // single result rather than repeating the spherecast + sort three times per frame.
        if (cam != null && TryFindInteractableHingeDoor(cam, out HingeInteractDoor aimedDoor) && aimedDoor != null)
        {
            if (aimedDoor.UseKeyToUnlock && aimedDoor.IsLocked && PlayerHasKeyInInventory())
            {
                SetPickupPromptVisible(true, doorUnlockPromptMessage);
                return;
            }

            if (!aimedDoor.IsLocked && !aimedDoor.IsOpen && !aimedDoor.IsPostUnlockOpenDelayActive && aimedDoor.ShowOpenInteractionPrompt)
            {
                SetPickupPromptVisible(true, doorOpenPromptMessage);
                return;
            }

            if (!aimedDoor.IsLocked
                && aimedDoor.IsOpen
                && !aimedDoor.IsBusy
                && aimedDoor.TryGetElevatorFinishController(out ElevatorFinishController elevatorFinish))
            {
                SetElevatorClosePromptVisible(
                    true,
                    elevatorFinish.LivingInsideDisplay,
                    elevatorFinish.LivingRequiredDisplay);
                return;
            }
        }

        if (cam != null && TryGetCarnivalPromptForCurrentAim(cam, out string carnivalMessage))
        {
            SetPickupPromptVisible(true, carnivalMessage);
            return;
        }

        bool shouldShow = ShouldShowPickupPrompt();
        SetPickupPromptVisible(shouldShow, pickupPromptMessage);
    }

    bool TryFindInteractableMazeChest(Transform cam, out MazeChest chest)
    {
        chest = null;

        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);

        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            if (InteractHitBelongsToOpenedChest(h))
                continue;

            MazeChest found = h.collider.GetComponentInParent<MazeChest>();
            if (found != null && !found.IsOpened)
            {
                chest = found;
                return true;
            }
        }

        return false;
    }

    bool TryFindInteractableHingeDoor(Transform cam, out HingeInteractDoor door)
    {
        door = null;

        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);

        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            if (InteractHitBelongsToOpenedChest(h))
                continue;

            HingeInteractDoor found = h.collider.GetComponentInParent<HingeInteractDoor>();
            if (found == null || found.IsBusy || !found.IsInInteractRange(cam.position))
                continue;

            door = found;
            return true;
        }

        return false;
    }

    bool PlayerHasKeyInInventory()
    {
        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory == null)
                return false;
            for (int i = 0; i < 3; i++)
            {
                ulong id = _networkPlayerInventory.GetSlotItemId(i);
                if (_networkPlayerInventory.GetSlotItemTypeId(i) == GrabbableInventoryItem.TypeIdKey)
                    return true;
                if (id == 0UL)
                    continue;
                if (GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) && g is KeyItem)
                    return true;
            }
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] is KeyItem)
                return true;
        }
        return false;
    }

    static bool InteractHitBelongsToOpenedChest(RaycastHit hit)
    {
        MazeChest chest = hit.collider.GetComponentInParent<MazeChest>();
        return chest != null && chest.IsOpened;
    }

    int TryInteractCastNonAlloc(Transform cam, int mask, QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore)
    {
        Vector3 origin = cam.position;
        Vector3 direction = cam.forward;
        float distance = interactDistance;
        float radius = interactSphereRadius;

        if (radius > 0.0001f)
        {
            float backOffset = Mathf.Min(radius * 0.25f, 0.1f);
            origin -= direction * backOffset;
            distance += backOffset;
            return Physics.SphereCastNonAlloc(
                origin,
                radius,
                direction,
                _interactCastHitBuffer,
                distance,
                mask,
                triggerInteraction);
        }

        return Physics.RaycastNonAlloc(
            origin,
            direction,
            _interactCastHitBuffer,
            distance,
            mask,
            triggerInteraction);
    }

    static readonly IComparer<RaycastHit> s_InteractHitDistanceComparer =
        Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

    void SortInteractHitsByDistance(int count)
    {
        if (count <= 1)
            return;

        // Hoisted comparer: Comparer.Create allocated a new wrapper on every sort, and this runs several
        // times per frame across the chest/door/grabbable prompt casts.
        Array.Sort(_interactCastHitBuffer, 0, count, s_InteractHitDistanceComparer);
    }

    /// <summary>
    /// When <see cref="Physics.SphereCastNonAlloc"/> fills its buffer, Unity does not guarantee the closest hits
    /// (docs: arbitrary subset up to buffer size). Small floor pickups like glowsticks are often missing from the
    /// hit list in dense mazes. This scans registered grabbables in a view cylinder and uses a short LOS ray toward
    /// each item's closest collider point so the ground does not spuriously block.
    /// </summary>
    bool TryFindInteractableGrabbableInViewFallback(Transform cam, int mask, out GrabbableInventoryItem grabbable)
    {
        grabbable = null;
        if (cam == null)
            return false;

        Vector3 o = cam.position;
        Vector3 d = cam.forward;
        if (d.sqrMagnitude < 1e-6f)
            return false;
        d.Normalize();

        float maxAlong = interactDistance + Mathf.Max(0.15f, interactSphereRadius) + 0.45f;
        float lateral = Mathf.Max(0.4f, interactSphereRadius + 0.4f);
        float lateralSq = lateral * lateral;

        GrabbableInventoryItem best = null;
        float bestAlong = float.MaxValue;

        foreach (GrabbableInventoryItem g in GrabbableInventoryItem.GetRegisteredItems())
        {
            if (g == null || !g.gameObject.activeInHierarchy || g.IsHeld)
                continue;

            if (IsUsingNetworkedInventory)
            {
                if (_networkPlayerInventory == null
                    || !_networkPlayerInventory.CanPickup(g))
                    continue;
            }
            else if (!CanPickupLocal(g))
                continue;

            if (!PassHeavyThrowableInteractPromptHint(g))
                continue;

            // Broad-phase reject on the item's transform before the precise, collider-scanning aim point.
            // The margin generously covers item size + any collider offset, so an in-range item is never
            // wrongly dropped; distant items (the common case) bail here without touching their colliders.
            Vector3 toItem = g.transform.position - o;
            float itemAlong = Vector3.Dot(toItem, d);
            if (itemAlong < -2f || itemAlong > maxAlong + 2f)
                continue;

            Vector3 aim = GetFallbackAimPointForGrabbable(g, o);

            float t = Vector3.Dot(aim - o, d);
            if (t < -0.12f || t > maxAlong)
                continue;

            Vector3 closestOnRay = o + d * t;
            float candidateLateralSq = lateralSq;
            if (g is HeavyThrowableHoldItem && TryGetRendererBounds(g, out Bounds heavyBounds))
            {
                float heavyLateral = Mathf.Max(lateral, heavyBounds.extents.magnitude + 0.35f);
                candidateLateralSq = heavyLateral * heavyLateral;
            }

            if ((aim - closestOnRay).sqrMagnitude > candidateLateralSq)
                continue;

            Vector3 toAim = aim - o;
            float dist = toAim.magnitude;
            if (dist > 0.04f)
            {
                int lineHitCount = Physics.RaycastNonAlloc(
                    o,
                    toAim / dist,
                    _interactCastHitBuffer,
                    dist - 0.03f,
                    mask,
                    QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int i = 0; i < lineHitCount; i++)
                {
                    RaycastHit rh = _interactCastHitBuffer[i];
                    if (rh.collider.GetComponentInParent<GrabbableInventoryItem>() == g)
                        continue;
                    if (InteractHitBelongsToOpenedChest(rh))
                        continue;

                    blocked = true;
                    break;
                }

                if (blocked)
                    continue;
            }

            if (t < bestAlong)
            {
                bestAlong = t;
                best = g;
            }
        }

        if (best == null)
            return false;

        grabbable = best;
        return true;
    }

    Vector3 GetFallbackAimPointForGrabbable(GrabbableInventoryItem item, Vector3 observer)
    {
        if (item is HeavyThrowableHoldItem && TryGetRendererBounds(item, out Bounds bounds))
            return bounds.ClosestPoint(observer);

        return item.GetInteractAimPointClosestTo(observer);
    }

    static bool TryGetRendererBounds(GrabbableInventoryItem item, out Bounds bounds)
    {
        bounds = default;
        if (item == null)
            return false;

        Renderer[] renderers = item.GetComponentsInChildren<Renderer>(true);
        bool found = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.enabled || r.forceRenderingOff)
                continue;

            if (!found)
            {
                bounds = r.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        return found;
    }

    void SetPickupPromptVisible(bool visible, string messageForText = null)
    {
        _pickupPromptVisible = visible;

        if (!visible)
        {
            if (_hudPrompt != null)
                _hudPrompt.Hide();
            return;
        }

        if (!_hasLocalControl)
            return;

        EnsureHudPrompt().ShowMessage(messageForText ?? pickupPromptMessage);
    }

    void SetElevatorClosePromptVisible(bool visible, int insideLiving, int requiredLiving)
    {
        _pickupPromptVisible = visible;

        if (!visible)
        {
            if (_hudPrompt != null)
                _hudPrompt.Hide();
            return;
        }

        if (!_hasLocalControl)
            return;

        Sprite icon = pickupPromptPlayerIcon != null ? pickupPromptPlayerIcon.sprite : null;
        EnsureHudPrompt().ShowCount(icon, $"{insideLiving}/{requiredLiving}");
    }

    HudPrompt EnsureHudPrompt()
    {
        if (_hudPrompt == null)
            _hudPrompt = HudPrompt.Create(HudKit.EnsureHudCanvas().transform);
        return _hudPrompt;
    }

    Vector3 GetFacingDirection(Transform cam, Vector3 groundMove)
    {
        if (cam != null)
        {
            Vector3 forward = cam.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        if (groundMove.sqrMagnitude > 0.0001f)
            return groundMove;
        return Vector3.zero;
    }

    static Vector2 ReadMoveFallback()
    {
        Vector2 move = Vector2.zero;
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1f;
        }

        Gamepad pad = Gamepad.current;
        if (pad != null)
            move += pad.leftStick.ReadValue();

        if (move.sqrMagnitude > 1f)
            move.Normalize();
        return move;
    }

    static Vector2 ReadLookFallback()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null)
            return mouse.delta.ReadValue();

        Gamepad pad = Gamepad.current;
        return pad != null ? pad.rightStick.ReadValue() : Vector2.zero;
    }

    static bool WasJumpPressedFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonSouth.wasPressedThisFrame;
    }

    static bool IsSprintHeldFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.leftShiftKey.isPressed)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.leftStickButton.isPressed;
    }

    static bool IsCrouchHeldFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && (keyboard.cKey.isPressed || keyboard.leftCtrlKey.isPressed))
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonEast.isPressed;
    }

    static bool WasInteractPressedFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonWest.wasPressedThisFrame;
    }

    static bool WasDropPressedFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.gKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonEast.wasPressedThisFrame;
    }

    static bool WasFlashlightPressedFallback()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.dpad.up.wasPressedThisFrame;
    }

    static bool WasAttackPressedFallback()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            return true;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.vKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonWest.wasPressedThisFrame;
    }

    static bool WasAttackReleasedFallback()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasReleasedThisFrame)
            return true;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.vKey.wasReleasedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonWest.wasReleasedThisFrame;
    }

    static bool IsAttackHeldFallback()
    {
        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.isPressed)
            return true;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.vKey.isPressed)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null && pad.buttonWest.isPressed;
    }

    // Attack (left click) behaves two ways:
    //  - Holding a heavy throwable: press starts charging, holding fills the charge, release throws
    //    with distance scaled by how long it was held. A charged release never falls through to melee.
    //  - Otherwise: a press performs a melee, exactly as before.
    void HandleAttackInput(bool pressed, bool released, bool held)
    {
        bool holdingThrowable = IsHoldingHeavyThrowable();

        if (pressed && holdingThrowable)
        {
            _isChargingThrow = true;
            _throwChargeTimer = 0f;
        }
        else if (pressed && !_isChargingThrow)
        {
            if (_currentStamina > 0f)
                TryMelee();
            return;
        }

        if (!_isChargingThrow)
            return;

        // Throwable was dropped, stolen, or otherwise lost mid-charge: abort cleanly, no throw.
        if (!holdingThrowable)
        {
            CancelThrowCharge();
            return;
        }

        if (held)
            _throwChargeTimer += Time.deltaTime;

        if (released)
        {
            float charge01 = ThrowChargeNormalized;
            _isChargingThrow = false;
            _throwChargeTimer = 0f;
            TryShootHeldHeavyThrowable(charge01);
        }
    }

    void CancelThrowCharge()
    {
        if (!_isChargingThrow && _throwChargeTimer == 0f)
            return;
        _isChargingThrow = false;
        _throwChargeTimer = 0f;
        if (_throwChargeBarRoot != null && _throwChargeBarRoot.activeSelf)
            _throwChargeBarRoot.SetActive(false);
    }

    void TryMelee()
    {
        if (_currentStamina <= 0f)
            return;

        if (Time.time < _nextMeleeTime)
            return;

        _nextMeleeTime = Time.time + meleeCooldown;
        SpendStamina(punchStaminaCost);

        if (_networkPlayerAvatar != null)
            _networkPlayerAvatar.TriggerAnimation(meleeTrigger);
        else if (animator != null)
            animator.SetTrigger(meleeTrigger);

        PlayMeleeSwooshSfx();
        StartCoroutine(ApplyMeleeDamageAfterDelay());
    }

    public void PlayMeleeSwooshSfx()
    {
        if (meleeSwooshClip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(meleeSwooshClip, Mathf.Max(0f, meleeSwooshVolume));
    }

    IEnumerator ApplyMeleeDamageAfterDelay()
    {
        if (meleeHitDelay > 0f)
            yield return new WaitForSeconds(meleeHitDelay);

        ApplyMeleeDamage();
    }

    void ApplyMeleeDamage()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && _networkPlayerCombat != null)
        {
            _networkPlayerCombat.RequestMeleeAttack();
            return;
        }

        if (ApplyMeleeDamageLocally())
        {
            if (_meleeHitSkeletonThisSwing)
                PlaySkeletonHitSfx();
            else
                PlayMeleeHitSfx();
        }
    }

    public void ApplyServerAuthoritativeMeleeDamage()
    {
        bool hit = ApplyMeleeDamageLocally();
        if (!hit)
            return;

        if (_networkPlayerCombat != null && _networkPlayerCombat.IsSpawned)
        {
            if (_meleeHitSkeletonThisSwing)
                _networkPlayerCombat.NotifyObserversSkeletonHit();
            else
                _networkPlayerCombat.NotifyObserversMeleeHit(PickRandomMeleeHitClipIndex());
        }
        else if (_meleeHitSkeletonThisSwing)
        {
            PlaySkeletonHitSfx();
        }
        else
        {
            PlayMeleeHitSfx();
        }
    }

    bool ApplyMeleeDamageLocally()
    {
        Vector3 origin = transform.position;
        Vector3 forward = transform.forward;

        int mask = enemyMask.value == 0 ? Physics.DefaultRaycastLayers : enemyMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(origin, meleeRange, _meleeHits, mask, QueryTriggerInteraction.Ignore);

        bool damagedAny = false;
        _meleeHitZombies.Clear();
        _meleeHitSkeletons.Clear();
        _meleeHitSkeletonThisSwing = false;
        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _meleeHits[i];
            if (col == null)
                continue;

            _meleeHits[i] = null;

            // Wind-up monkey: a short collider near the floor. Handle it BEFORE the shared cone gate below —
            // that gate measures the full 3D angle to the target, which is steep for such a low object and can
            // reject it. Use a horizontal-plane facing test instead, and require crouch so a standing punch
            // (too high to reach the toy) passes over it. A crouched hit tips it over, silencing the Clown lure.
            WindupMonkeyAI monkey = col.GetComponentInParent<WindupMonkeyAI>();
            if (monkey != null)
            {
                if (monkey.IsKnockedOver || !IsCrouching)
                    continue;
                Vector3 flatDir = col.transform.position - origin;
                flatDir.y = 0f;
                Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
                if (flatDir.sqrMagnitude > 0.0001f && Vector3.Angle(flatForward, flatDir) > meleeAngle)
                    continue;
                // Topple it away from the punch (fall direction = player -> monkey, else the player's facing).
                Vector3 knockDir = flatDir.sqrMagnitude > 0.0001f ? flatDir : flatForward;
                monkey.ServerKnockOver(knockDir);
                damagedAny = true;   // plays the regular punch impact thud
                continue;
            }

            Vector3 dirToTarget = (col.transform.position - origin).normalized;
            float angle = Vector3.Angle(forward, dirToTarget);
            if (angle > meleeAngle)
                continue;

            ZombieHealth zombieHealth = col.GetComponentInParent<ZombieHealth>();
            if (zombieHealth != null && !zombieHealth.IsDead)
            {
                if (!_meleeHitZombies.Add(zombieHealth))
                    continue;

                float damage = zombieHealth.MaxHealth * 0.25f;
                if (zombieHealth.TakeDamage(damage, fromPlayerMelee: true, attacker: transform, attackerHealth: _playerHealth))
                    damagedAny = true;
                continue;
            }

            SkeletonHealth skeletonHealth = col.GetComponentInParent<SkeletonHealth>();
            if (skeletonHealth != null && !skeletonHealth.IsDead)
            {
                if (!_meleeHitSkeletons.Add(skeletonHealth))
                    continue;

                float damage = skeletonHealth.MaxHealth * 0.25f;
                if (skeletonHealth.TakeDamage(damage, fromPlayerMelee: true, attacker: transform, attackerHealth: _playerHealth))
                {
                    damagedAny = true;
                    _meleeHitSkeletonThisSwing = true;
                }
                continue;
            }

        }

        return damagedAny;
    }

    public void PlayMeleeHitSfx()
    {
        if (footstepAudioSource == null)
            return;

        AudioClip clip = PickRandomMeleeHitClip();
        if (clip == null)
            return;

        footstepAudioSource.PlayOneShot(clip, Mathf.Max(0f, meleeHitPunchVolume));
    }

    /// <summary>Impact sound when the melee connects with a Skeleton (replaces the punch impact for that hit).</summary>
    public void PlaySkeletonHitSfx()
    {
        if (footstepAudioSource == null || skeletonHitClip == null)
            return;

        footstepAudioSource.PlayOneShot(skeletonHitClip, Mathf.Max(0f, meleeHitPunchVolume));
    }

    /// <summary>Which punch clip slot (0–2) to play; same value must be used on all clients for a given hit.</summary>
    public void PlayMeleeHitSfxWithIndex(byte clipSlot0To2)
    {
        if (footstepAudioSource == null)
            return;

        AudioClip c = clipSlot0To2 == 0
            ? meleeHitPunch1
            : clipSlot0To2 == 1
                ? meleeHitPunch2
                : meleeHitPunch3;
        if (c == null)
            return;

        footstepAudioSource.PlayOneShot(c, Mathf.Max(0f, meleeHitPunchVolume));
    }

    public byte PickRandomMeleeHitClipIndex()
    {
        AudioClip c0 = meleeHitPunch1, c1 = meleeHitPunch2, c2 = meleeHitPunch3;
        int n = (c0 != null ? 1 : 0) + (c1 != null ? 1 : 0) + (c2 != null ? 1 : 0);
        if (n == 0)
            return 0;

        int r = UnityEngine.Random.Range(0, n);
        if (c0 != null)
        {
            if (r == 0)
                return 0;
            r--;
        }

        if (c1 != null)
        {
            if (r == 0)
                return 1;
            r--;
        }

        return 2;
    }

    public void PlayZombieHitSfx()
    {
        if (zombieHitClip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(zombieHitClip, Mathf.Max(0f, zombieHitVolume));
    }

    AudioClip PickRandomMeleeHitClip()
    {
        AudioClip c0 = meleeHitPunch1, c1 = meleeHitPunch2, c2 = meleeHitPunch3;
        int n = (c0 != null ? 1 : 0) + (c1 != null ? 1 : 0) + (c2 != null ? 1 : 0);
        if (n == 0)
            return null;

        int r = UnityEngine.Random.Range(0, n);
        if (c0 != null)
        {
            if (r == 0)
                return c0;
            r--;
        }

        if (c1 != null)
        {
            if (r == 0)
                return c1;
            r--;
        }

        return c2;
    }
}
