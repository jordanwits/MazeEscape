using System.Collections;
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
public class PlayerController : MonoBehaviour
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
    [Tooltip("Optional UI root (e.g. a Panel) shown when you look at something you can pick up.")]
    [SerializeField] GameObject pickupPromptRoot;
    [Tooltip("Optional UI Text for the pickup prompt. If empty, tries to find a Text under pickupPromptRoot.")]
    [SerializeField] Text pickupPromptText;
    [SerializeField] string pickupPromptMessage = "Press E to pick up";
    [Tooltip("Optional mask for interactable items. If empty, Unity default raycast layers are used.")]
    [SerializeField] LayerMask interactMask;
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

    [Header("Footsteps")]
    [SerializeField] AudioSource footstepAudioSource;
    [SerializeField] AudioClip footstepClip1;
    [SerializeField] AudioClip footstepClip2;
    [SerializeField] float walkFootstepInterval = 0.48f;
    [SerializeField] float runFootstepInterval = 0.34f;
    [SerializeField] float footstepVolume = 0.5f;
    [SerializeField] float minimumFootstepSpeed = 0.15f;

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
    [Tooltip("Trigger parameter name in Animator for melee attack.")]
    [SerializeField] string meleeTrigger = "RightHook";

    [Header("Animator")]
    [SerializeField] bool driveAnimator = true;
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
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

    InputActionMap _playerMap;
    InputAction _moveAction;
    InputAction _lookAction;
    InputAction _jumpAction;
    InputAction _sprintAction;
    InputAction _interactAction;
    InputAction _dropAction;
    InputAction _flashlightAction;
    InputAction _attackAction;
    InputActionAsset _runtimeInputActions;

    float _lookYawDegrees;
    float _lookPitchDegrees;

    Vector3 _verticalVelocity;
    Vector3 _horizontalVelocity;
    Vector2 _moveInput;
    Vector3 _groundMoveThisFrame;
    float _currentHorizontalSpeed;
    FlashlightItem _heldFlashlight;
    readonly Collider[] _upperBodyWallHits = new Collider[16];
    bool _pickupPromptVisible;

    float _currentStamina;
    float _staminaRegenTimer;
    bool _isSprinting;
    RectTransform _staminaFillRect;
    GameObject _staminaBarRoot;
    float _footstepTimer;
    bool _playFootstep1Next = true;
    bool _hasLocalControl = true;

    float _nextMeleeTime;
    readonly Collider[] _meleeHits = new Collider[16];
    const string EnemyLayerName = "Enemy";
    NetworkPlayerCombat _networkPlayerCombat;
    NetworkPlayerAvatar _networkPlayerAvatar;
    PlayerRagdollController _ragdollController;
    PlayerHealth _playerHealth;

    float _ragdollRecoverAnimatorSuppressUntil;

    bool _cameraPitchParentedToHead;
    bool _hasSavedCameraPitchPrefabPose;
    Transform _savedCameraPitchParent;
    Vector3 _savedCameraPitchLocalPosition;
    Quaternion _savedCameraPitchLocalRotation;

    public float StaminaNormalized => maxStamina > 0f ? _currentStamina / maxStamina : 0f;
    public bool HasLocalControl => _hasLocalControl;
    public Transform LookPitchTransform => cameraTransform;

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

        _ragdollRecoverAnimatorSuppressUntil = Time.time + Mathf.Max(0f, ragdollRecoverAnimatorSuppressSeconds);
    }

    void Awake()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (upperBodyWallTrigger == null)
            upperBodyWallTrigger = FindUpperBodyWallTrigger();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (animator != null)
            animator.applyRootMotion = false;
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
        if (facingTransform == null)
        {
            if (animator != null && animator.transform != transform)
                facingTransform = animator.transform;
            else
                facingTransform = transform;
        }

        if (firstPersonLook)
            SyncLookAnglesFromTransforms();

        if (pickupPromptText == null && pickupPromptRoot != null)
            pickupPromptText = pickupPromptRoot.GetComponentInChildren<Text>(true);

        SetPickupPromptVisible(false);
        _currentStamina = maxStamina;

        if (staminaBarImage == null && autoCreateStaminaBar)
            staminaBarImage = CreateStaminaBarUI();

        RefreshStaminaUI();

        if (footstepAudioSource == null)
            footstepAudioSource = GetComponent<AudioSource>();
        if (footstepAudioSource == null)
            footstepAudioSource = gameObject.AddComponent<AudioSource>();

        ConfigureFootstepAudioSource();
        _networkPlayerCombat = GetComponent<NetworkPlayerCombat>();
        _networkPlayerAvatar = GetComponent<NetworkPlayerAvatar>();
        _ragdollController = GetComponent<PlayerRagdollController>();
        _playerHealth = GetComponent<PlayerHealth>();

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
    }

    void Update()
    {
        if (!_hasLocalControl && !ShouldRunDeadRagdollCameraUpdate())
            return;

        EnsureCameraPitchParentedToHead();

        if (_ragdollController != null && _ragdollController.IsRagdolled)
        {
            if (MultiplayerMenuOverlay.BlocksGameplayInput)
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

        if (MultiplayerMenuOverlay.BlocksGameplayInput)
        {
            _moveInput = Vector2.zero;
            _horizontalVelocity = Vector3.zero;
            SetPickupPromptVisible(false);
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

        if (interactPressed)
            HandlePickupInput();

        if (dropPressed)
            HandleDropInput();

        if (flashlightPressed)
            HandleFlashlightToggleInput();

        if (attackPressed && _currentStamina > 0f)
            TryMelee();

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        Vector3 move = BuildGroundMoveDirection(_moveInput);
        float inputMagnitude = Mathf.Clamp01(_moveInput.magnitude);

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
        characterController.Move(motion);

        _groundMoveThisFrame = horizontal.sqrMagnitude > 1e-6f ? horizontal.normalized : move;

        UpdateFootsteps(characterController.isGrounded);

        if (driveAnimator && animator != null)
        {
            float speedForAnimator = 0f;
            if (_currentHorizontalSpeed > 0.01f)
                speedForAnimator = _isSprinting ? runAnimationSpeed : walkAnimationSpeed;

            speedForAnimator = Mathf.Clamp01(speedForAnimator);
            if (Time.time < _ragdollRecoverAnimatorSuppressUntil)
                speedForAnimator = 0f;

            animator.SetFloat(speedParameter, speedForAnimator);
            animator.SetBool(groundedParameter, characterController.isGrounded);
            animator.SetFloat(verticalVelocityParameter, _verticalVelocity.y);
        }

        UpdatePickupPrompt();
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

    void RefreshStaminaUI()
    {
        if (_staminaFillRect != null)
            _staminaFillRect.anchorMax = new Vector2(StaminaNormalized, 1f);
        else if (staminaBarImage != null)
            staminaBarImage.fillAmount = StaminaNormalized;
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

        AudioClip clipToPlay = _playFootstep1Next ? footstepClip1 : footstepClip2;
        if (clipToPlay == null)
            clipToPlay = footstepClip1 != null ? footstepClip1 : footstepClip2;

        if (clipToPlay != null)
        {
            footstepAudioSource.PlayOneShot(clipToPlay, Mathf.Max(0f, footstepVolume));
            _playFootstep1Next = !_playFootstep1Next;
        }

        _footstepTimer = interval;
    }

    void ConfigureFootstepAudioSource()
    {
        if (footstepAudioSource == null)
            return;

        footstepAudioSource.playOnAwake = false;
        footstepAudioSource.loop = false;
        footstepAudioSource.spatialBlend = 0f;
        footstepAudioSource.dopplerLevel = 0f;
    }

    public void SetLocalControl(bool hasLocalControl)
    {
        if (_hasLocalControl == hasLocalControl)
            return;

        _hasLocalControl = hasLocalControl;
        ApplyLocalControlState();
    }

    public void SetHudVisible(bool visible)
    {
        if (_staminaBarRoot != null)
            _staminaBarRoot.SetActive(visible);
        else if (staminaBarImage != null)
            staminaBarImage.enabled = visible;

        if (!visible)
            SetPickupPromptVisible(false);
    }

    void ApplyLocalControlState()
    {
        if (_hasLocalControl)
        {
            AcquireInputActions();
            ApplyCursorLock();
        }
        else
        {
            DisableInputActions();
            ReleaseCursor();
            _moveInput = Vector2.zero;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = Vector3.zero;
            SetPickupPromptVisible(false);
        }

        SetHudVisible(_hasLocalControl);
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

        if (!_playerMap.enabled)
            _playerMap.Enable();
    }

    void DisableInputActions()
    {
        _playerMap?.Disable();
    }

    void ApplyCursorLock()
    {
        if (!firstPersonLook || !lockCursor || MultiplayerMenuOverlay.BlocksGameplayInput)
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
    }
#endif

    Image CreateStaminaBarUI()
    {
        Canvas canvas = FindAnyObjectByType<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGo = new GameObject("StaminaCanvas");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();
        }

        GameObject bg = new GameObject("StaminaBarBG");
        bg.transform.SetParent(canvas.transform, false);
        Image bgImage = bg.AddComponent<Image>();
        bgImage.color = new Color(0f, 0f, 0f, 0.6f);
        RectTransform bgRect = bg.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0.5f, 0f);
        bgRect.anchorMax = new Vector2(0.5f, 0f);
        bgRect.pivot = new Vector2(0.5f, 0f);
        bgRect.anchoredPosition = new Vector2(0f, 30f);
        bgRect.sizeDelta = new Vector2(304f, 24f);
        _staminaBarRoot = bg;

        GameObject fill = new GameObject("StaminaBarFill");
        fill.transform.SetParent(bg.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.2f, 0.75f, 1f, 0.9f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.offsetMin = new Vector2(2f, 2f);
        fillRect.offsetMax = new Vector2(-2f, -2f);
        _staminaFillRect = fillRect;

        return fillImage;
    }

    void LateUpdate()
    {
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

    Transform CameraTransformForFacing => cameraTransform != null ? cameraTransform : Camera.main != null ? Camera.main.transform : null;
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
            && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp);

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

            cameraPitchTransform.SetParent(head, true);
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

    void HandlePickupInput()
    {
        if (UseNetworkedFlashlightFlow)
        {
            TryPickupNetworkFlashlight();
            return;
        }

        TryPickupFlashlight();
    }

    void HandleDropInput()
    {
        if (UseNetworkedFlashlightFlow)
        {
            TryDropNetworkFlashlight();
            return;
        }

        DropFlashlight();
    }

    void HandleFlashlightToggleInput()
    {
        if (UseNetworkedFlashlightFlow)
        {
            _networkPlayerAvatar?.TryToggleHeldFlashlight();
            return;
        }

        if (_heldFlashlight != null)
            _heldFlashlight.ToggleLight();
    }

    void TryPickupNetworkFlashlight()
    {
        if (_networkPlayerAvatar == null || !TryFindInteractableFlashlight(out FlashlightItem flashlight))
            return;

        _networkPlayerAvatar.TryPickupFlashlight(flashlight);
    }

    void TryDropNetworkFlashlight()
    {
        if (_networkPlayerAvatar == null || !_networkPlayerAvatar.HasHeldFlashlight)
            return;

        Transform cam = CameraTransformForFacing;
        Vector3 forward = cam != null ? cam.forward : transform.forward;
        Vector3 dropPosition = flashlightHoldPoint != null ? flashlightHoldPoint.position : transform.position + forward * 0.75f;
        Quaternion dropRotation = flashlightHoldPoint != null ? flashlightHoldPoint.rotation : transform.rotation;
        _networkPlayerAvatar.TryDropHeldFlashlight(dropPosition, dropRotation, forward);
    }

    void TryPickupFlashlight()
    {
        if (_heldFlashlight != null || flashlightHoldPoint == null)
            return;

        if (!TryFindInteractableFlashlight(out FlashlightItem flashlight))
            return;

        _heldFlashlight = flashlight;
        Transform holdFollowTransform = flashlightFollowsCameraPitch ? CameraTransformForFacing : flashlightHoldPoint;
        _heldFlashlight.Pickup(flashlightHoldPoint, holdFollowTransform);
        SetPickupPromptVisible(false);
    }

    void DropFlashlight()
    {
        if (_heldFlashlight == null)
            return;

        Transform cam = CameraTransformForFacing;
        Vector3 impulse = (cam != null ? cam.forward : transform.forward) * dropForce;
        _heldFlashlight.Drop(impulse);
        _heldFlashlight = null;
    }

    void UpdatePickupPrompt()
    {
        if (pickupPromptRoot == null)
            return;

        bool shouldShow = ShouldShowPickupPrompt();
        SetPickupPromptVisible(shouldShow);
    }

    bool ShouldShowPickupPrompt()
    {
        if ((UseNetworkedFlashlightFlow ? _networkPlayerAvatar != null && _networkPlayerAvatar.HasHeldFlashlight : _heldFlashlight != null)
            || flashlightHoldPoint == null)
        {
            return false;
        }

        return TryFindInteractableFlashlight(out _);
    }

    bool TryFindInteractableFlashlight(out FlashlightItem flashlight)
    {
        flashlight = null;

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        if (!TryInteractCast(cam, mask, out RaycastHit hit))
            return false;

        flashlight = hit.collider.GetComponentInParent<FlashlightItem>();
        if (flashlight == null)
            return false;

        if (UseNetworkedFlashlightFlow)
            return _networkPlayerAvatar != null && _networkPlayerAvatar.CanPickupFlashlight(flashlight);

        return !flashlight.IsHeld;
    }

    bool TryInteractCast(Transform cam, int mask, out RaycastHit hit)
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
            return Physics.SphereCast(
                origin,
                radius,
                direction,
                out hit,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);
        }

        return Physics.Raycast(
            origin,
            direction,
            out hit,
            distance,
            mask,
            QueryTriggerInteraction.Ignore);
    }

    void SetPickupPromptVisible(bool visible)
    {
        if (pickupPromptRoot == null || _pickupPromptVisible == visible)
            return;

        _pickupPromptVisible = visible;
        pickupPromptRoot.SetActive(visible);

        if (!visible || pickupPromptText == null)
            return;

        if (!string.IsNullOrEmpty(pickupPromptMessage))
            pickupPromptText.text = pickupPromptMessage;
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

        StartCoroutine(ApplyMeleeDamageAfterDelay());
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

        ApplyMeleeDamageLocally();
    }

    public void ApplyServerAuthoritativeMeleeDamage()
    {
        ApplyMeleeDamageLocally();
    }

    void ApplyMeleeDamageLocally()
    {
        Vector3 origin = transform.position;
        Vector3 forward = transform.forward;

        int mask = enemyMask.value == 0 ? Physics.DefaultRaycastLayers : enemyMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(origin, meleeRange, _meleeHits, mask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _meleeHits[i];
            if (col == null)
                continue;

            _meleeHits[i] = null;

            Vector3 dirToTarget = (col.transform.position - origin).normalized;
            float angle = Vector3.Angle(forward, dirToTarget);
            if (angle > meleeAngle)
                continue;

            ZombieHealth zombieHealth = col.GetComponentInParent<ZombieHealth>();
            if (zombieHealth == null || zombieHealth.IsDead)
                continue;

            float damage = zombieHealth.MaxHealth * 0.25f;
            zombieHealth.TakeDamage(damage);
        }
    }
}
