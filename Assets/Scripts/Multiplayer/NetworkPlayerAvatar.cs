using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkPlayerInventory))]
public class NetworkPlayerAvatar : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] Animator avatarAnimator;
    [Tooltip("Animator bool on Player controller; true while Jailor is carrying this avatar (owner writes; NetworkAnimator replicates).")]
    [SerializeField] string carriedByJailorAnimatorParameter = "CarriedByJailor";
    [Tooltip("After the server marks this player sealed in a jail cell, the owning client cannot move for this long (look still works).")]
    [SerializeField] float postJailMovementLockSeconds = 2f;
    [SerializeField] PlayerController playerController;
    [SerializeField] PlayerHealth playerHealth;
    [SerializeField] CharacterController characterController;
    [SerializeField] Camera[] localOnlyCameras;
    [SerializeField] AudioListener[] localOnlyAudioListeners;
    [SerializeField] Renderer[] avatarRenderers;
    [Tooltip("Hidden from the owner's first-person view via ShadowsOnly (e.g. the head, so the camera never shows its interior). Remote players still see these normally; shadows stay correct locally.")]
    [SerializeField] Renderer[] localViewShadowOnlyRenderers;
    [SerializeField] NetworkPlayerInventory playerInventory;

    [Header("Carried-by-Jailor own-body hide")]
    [Tooltip("While the Jailor carries this player, hide their own body (everything but the arms) from their first-person view via ShadowsOnly. Local only — other players still see the whole body.")]
    [SerializeField] bool hideOwnBodyWhileCarriedByJailor = true;
    [Tooltip("Body renderers that stay visible while carried. Left empty by design: the name fragments below already match the arm meshes on every character.")]
    [SerializeField] Renderer[] carriedVisibleRenderers;
    [Tooltip("Name fragments (case-insensitive) marking a body renderer as an arm/hand, which stays visible while carried.")]
    [SerializeField] string[] carriedVisibleNameFragments = { "arm", "glove", "hand" };

    [Header("Flashlight replication")]
    [Tooltip("First-person pitch node (same as PlayerController camera / CameraPitch). Resolved automatically when empty.")]
    [SerializeField] Transform flashlightAimPivot;
    [Tooltip("Local offset for the remote-only light proxy while another player is holding a flashlight.")]
    [SerializeField] Vector3 remoteFlashlightProxyLocalPosition = new Vector3(0f, 0f, 0.08f);

    readonly NetworkVariable<float> _flashlightLookPitchDegrees = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    // Item id the owner is currently reaching for during a gated pickup (0 = none). Remote peers replay the
    // reach arm-extension in PlayerItemHoldIK toward the registered item; a stale id merely plays a short
    // extend-and-retract, so a netvar (self-correcting for late joiners) beats an RPC here.
    readonly NetworkVariable<ulong> _reachTargetItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    readonly NetworkVariable<bool> _audiblySprintingForAi = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    readonly NetworkVariable<bool> _carriedByJailor = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _sealedInJailCell = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    bool _offlineCarriedByJailor;
    bool _offlineSealedInJailCell;
    bool _isDormant;
    bool _isAlive = true;
    SkinnedMeshRenderer[] _skinnedRenderers;
    bool _skinnedRenderersOffscreenForced;
    bool _localViewHeadHidden;
    bool _carriedBodyHidden;
    Renderer[] _carriedHiddenRenderers;
    UnityEngine.Rendering.ShadowCastingMode[] _carriedHiddenPreviousModes;
    NetworkManager _networkManager;
    OwnerNetworkAnimator _ownerNetworkAnimator;
    Light _remoteFlashlightProxyLight;
    Transform _blockingProxyRoot;

    public bool HasHeldFlashlight => playerInventory != null
        && playerInventory.IsSpawned
        && playerInventory.HasItemInSelectedSlot;

    /// <summary>Replicated from owner: sprinting on foot loud enough for enemy AI (e.g. Jailor hearing).</summary>
    public bool AudiblySprintingForAi => _audiblySprintingForAi.Value;

    /// <summary>Server-authoritative: player is grabbed and carried by the Jailor.</summary>
    public bool IsCarriedByJailor => IsSpawned ? _carriedByJailor.Value : _offlineCarriedByJailor;

    /// <summary>Replicated from owner: item id of an in-flight gated-pickup reach (0 = none).</summary>
    public ulong ReachTargetItemId => IsSpawned ? _reachTargetItemId.Value : 0UL;

    /// <summary>Owner-side publish of the gated-pickup reach target (no-op offline or off-owner).</summary>
    public void PublishReachTarget(ulong itemId)
    {
        if (!IsSpawned || !IsOwner)
            return;
        if (_reachTargetItemId.Value != itemId)
            _reachTargetItemId.Value = itemId;
    }

    /// <summary>
    /// Server-authoritative: Jailor finished locking this player in a key-locked jail cell.
    /// Cleared when the cell is unlocked with a key (see <see cref="JailCellSealedReleaseZone"/>) or on death/restore.
    /// </summary>
    public bool IsSealedInJailCell => IsSpawned ? _sealedInJailCell.Value : _offlineSealedInJailCell;

    public void PublishAudiblySprinting(bool value)
    {
        if (!IsSpawned || !IsOwner)
            return;
        if (_audiblySprintingForAi.Value == value)
            return;
        _audiblySprintingForAi.Value = value;
    }

    /// <summary>Called on the server by <see cref="JailorAI"/> when parenting / releasing the carry.</summary>
    public void ServerSetCarriedByJailor(bool carried)
    {
        if (IsSpawned)
        {
            if (!IsServer)
                return;
            _carriedByJailor.Value = carried;
            return;
        }

        _offlineCarriedByJailor = carried;
        ApplyPresentation(IsOwner);
    }

    /// <summary>Server / offline host: mark player as locked in a jail cell for Jailor AI ignore rules.</summary>
    public void ServerSetSealedInJailCell(bool sealedInCell)
    {
        if (IsSpawned)
        {
            if (!IsServer)
                return;
            _sealedInJailCell.Value = sealedInCell;
            return;
        }

        bool wasSealed = _offlineSealedInJailCell;
        _offlineSealedInJailCell = sealedInCell;
        if (sealedInCell && !wasSealed)
            TryBeginPostJailMovementLockOnOwner();
    }

    void OnCarriedByJailorChanged(bool previousValue, bool newValue)
    {
        if (_isDormant)
            return;
        if (TryGetComponent(out OwnerNetworkTransform ownerNetworkTransform))
            ownerNetworkTransform.RefreshAuthorityAfterCarryStateChanged();
        ApplyPresentation(IsOwner);
    }

    void OnSealedInJailCellChanged(bool previousValue, bool newValue)
    {
        if (_isDormant || !newValue || previousValue)
            return;
        TryBeginPostJailMovementLockOnOwner();
    }

    void TryBeginPostJailMovementLockOnOwner()
    {
        if (!IsOwner || playerController == null || postJailMovementLockSeconds <= 0f)
            return;
        playerController.BeginPostJailMovementLockout(postJailMovementLockSeconds);
    }

    void Awake()
    {
        if (avatarAnimator == null)
            avatarAnimator = GetComponentInChildren<Animator>();
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (localOnlyCameras == null || localOnlyCameras.Length == 0)
            localOnlyCameras = GetComponentsInChildren<Camera>(true);
        if (localOnlyAudioListeners == null || localOnlyAudioListeners.Length == 0)
            localOnlyAudioListeners = GetComponentsInChildren<AudioListener>(true);
        if (avatarRenderers == null || avatarRenderers.Length == 0)
            avatarRenderers = GetComponentsInChildren<Renderer>(true);
        if (playerInventory == null)
            playerInventory = GetComponent<NetworkPlayerInventory>();

        ResolveFlashlightAimPivot();
        EnsureAnimationSync();
        EnsureRemoteBlockingProxyObject();
        // Before any presentation/ownership callback, so an offline standalone player is covered too.
        ForceSkinnedBoundsAlwaysUpdate();

        if (playerHealth != null)
        {
            playerHealth.Died += ClearSealedInJailCellIfAuthoritative;
            playerHealth.Restored += ClearSealedInJailCellIfAuthoritative;
        }
    }

    public override void OnDestroy()
    {
        if (playerHealth != null)
        {
            playerHealth.Died -= ClearSealedInJailCellIfAuthoritative;
            playerHealth.Restored -= ClearSealedInJailCellIfAuthoritative;
        }

        base.OnDestroy();
    }

    void ClearSealedInJailCellIfAuthoritative()
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsSpawned && IsServer)
        {
            if (_sealedInJailCell.Value)
                _sealedInJailCell.Value = false;
            return;
        }

        _offlineSealedInJailCell = false;
    }

    void ResolveFlashlightAimPivot()
    {
        if (flashlightAimPivot != null)
            return;

        if (playerController != null)
            flashlightAimPivot = playerController.LookPitchTransform;

        if (flashlightAimPivot != null)
            return;

        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "CameraPitch")
            {
                flashlightAimPivot = t;
                return;
            }
        }
    }

    public void PublishFlashlightLookPitch(float pitchDegrees)
    {
        if (!IsSpawned || !IsOwner)
            return;

        if (Mathf.Abs(pitchDegrees - _flashlightLookPitchDegrees.Value) < 0.05f)
            return;

        _flashlightLookPitchDegrees.Value = pitchDegrees;
    }

    void Update()
    {
        if (IsSpawned && !IsOwner && !_isDormant && flashlightAimPivot != null)
            flashlightAimPivot.localRotation = Quaternion.Euler(_flashlightLookPitchDegrees.Value, 0f, 0f);

        UpdateRemoteFlashlightProxy();

        EnforceUnitScaleWhenUnparented();

        bool shouldBeDormant = ShouldBeDormant();

        if (_isDormant == shouldBeDormant)
            return;

        SetDormant(shouldBeDormant);
    }

    /// <summary>
    /// Players are authored and simulated at root scale 1; the only legitimate exception is while parented
    /// under the scaled Jailor root during a carry, where localScale compensates so lossy scale stays 1.
    /// On client machines the carry release could strand a shrunken root scale (the ParentSync message,
    /// NetworkTransform scale interpolation and the carried-NetworkVariable authority flip race each other)
    /// and nothing ever restored it — the first-person camera then rides the shrunken head bone, which reads
    /// as "the POV sank to my chest". Scale sync is disabled on the player NetworkTransform now; this
    /// per-frame invariant repairs any residue, including corruption from before the fix.
    /// </summary>
    void EnforceUnitScaleWhenUnparented()
    {
        if (transform.parent != null || IsCarriedByJailor)
            return;

        Vector3 scale = transform.localScale;
        if (Mathf.Abs(scale.x - 1f) < 0.001f && Mathf.Abs(scale.y - 1f) < 0.001f && Mathf.Abs(scale.z - 1f) < 0.001f)
            return;

        transform.localScale = Vector3.one;
        Debug.LogWarning(
            $"[{nameof(NetworkPlayerAvatar)}] Repaired non-unit player root scale {scale} -> (1,1,1) on '{name}'.",
            this);
    }

    public override void OnNetworkSpawn()
    {
        _networkManager = NetworkManager.Singleton;
        SetDormant(false);
        _carriedByJailor.OnValueChanged += OnCarriedByJailorChanged;
        _sealedInJailCell.OnValueChanged += OnSealedInJailCellChanged;
        PlanarMirror.ReflectionPass += OnMirrorReflectionPass;
        ApplyOwnershipState();

        if (IsOwner
            && !IsServer
            && MultiplayerSceneFlow.IsMazeGameplayScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene().name))
        {
            RequestMazeSeedFromHostServerRpc();
        }
    }

    public override void OnGainedOwnership()
    {
        ApplyOwnershipState();
    }

    public override void OnLostOwnership()
    {
        ApplyOwnershipState();
    }

    public override void OnNetworkDespawn()
    {
        _carriedByJailor.OnValueChanged -= OnCarriedByJailorChanged;
        _sealedInJailCell.OnValueChanged -= OnSealedInJailCellChanged;
        PlanarMirror.ReflectionPass -= OnMirrorReflectionPass;
        _networkManager = null;
        SetDormant(false);
        ApplyPresentation(true);
        SetRemoteFlashlightProxyEnabled(false);
    }

    public void NotifyFlashlightVisualAttach(FlashlightItem flashlight)
    {
        CopyFlashlightLightSettings(flashlight);
    }

    public bool CanPickupItem(GrabbableInventoryItem item)
    {
        return playerInventory != null && item != null && playerInventory.CanPickup(item);
    }

    public bool CanPickupFlashlight(FlashlightItem flashlight)
    {
        return CanPickupItem(flashlight);
    }

    public bool TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform)
    {
        holdPoint = null;
        followTransform = null;
        return playerController != null && playerController.TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform);
    }

    public bool TryGetInventoryAttachmentTargets(out Transform holdPoint, out Transform followTransform, out Transform stash)
    {
        holdPoint = null;
        followTransform = null;
        stash = null;
        return playerController != null && playerController.TryGetInventoryAttachmentTargets(out holdPoint, out followTransform, out stash);
    }

    public void TryPickupItem(GrabbableInventoryItem item)
    {
        playerInventory?.TryPickupItem(item);
    }

    public void TryPickupFlashlight(FlashlightItem flashlight)
    {
        TryPickupItem(flashlight);
    }

    public     void TriggerAnimation(string triggerName)
    {
        if (string.IsNullOrWhiteSpace(triggerName))
            return;

        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        bool useNetworkAnimator = _ownerNetworkAnimator != null
            && nm != null
            && nm.IsListening
            && IsSpawned;

        if (useNetworkAnimator)
            _ownerNetworkAnimator.SetTrigger(triggerName);
        else
            avatarAnimator?.SetTrigger(triggerName);
    }

    public void TryToggleHeldFlashlight()
    {
        playerInventory?.TryToggleSelectedFlashlight();
    }

    public void TryDropHeldFlashlight(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        playerInventory?.TryDropSelectedItem(dropPosition, dropRotation, dropForward);
    }

    void EnsureAnimationSync()
    {
        if (avatarAnimator == null)
            return;

        _ownerNetworkAnimator = avatarAnimator.GetComponent<OwnerNetworkAnimator>();
        if (_ownerNetworkAnimator == null)
            _ownerNetworkAnimator = avatarAnimator.gameObject.AddComponent<OwnerNetworkAnimator>();
    }

    const string BlockingProxyObjectName = "NetworkPlayerBlockingProxy";

    void EnsureRemoteBlockingProxyObject()
    {
        if (characterController == null)
            return;

        int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");

        Transform existing = transform.Find(BlockingProxyObjectName);
        if (existing != null)
        {
            _blockingProxyRoot = existing;
            if (ignoreRaycastLayer >= 0)
                existing.gameObject.layer = ignoreRaycastLayer;
            SyncBlockingProxyCapsuleToCharacterController();
            SetBlockingProxyActive(false);
            return;
        }

        var proxyObject = new GameObject(BlockingProxyObjectName);
        proxyObject.transform.SetParent(transform, false);
        proxyObject.transform.localPosition = Vector3.zero;
        proxyObject.transform.localRotation = Quaternion.identity;
        proxyObject.transform.localScale = Vector3.one;
        _blockingProxyRoot = proxyObject.transform;

        Rigidbody rb = proxyObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        CapsuleCollider capsule = proxyObject.AddComponent<CapsuleCollider>();
        capsule.isTrigger = false;

        if (ignoreRaycastLayer >= 0)
            proxyObject.layer = ignoreRaycastLayer;

        SyncBlockingProxyCapsuleToCharacterController();
        SetBlockingProxyActive(false);
    }

    void SyncBlockingProxyCapsuleToCharacterController()
    {
        if (characterController == null || _blockingProxyRoot == null)
            return;

        CapsuleCollider capsule = _blockingProxyRoot.GetComponent<CapsuleCollider>();
        if (capsule == null)
            return;

        capsule.center = characterController.center;
        capsule.radius = characterController.radius;
        float minHeight = characterController.radius * 2f;
        capsule.height = characterController.height < minHeight ? minHeight : characterController.height;
        capsule.direction = 1;
    }

    void SetBlockingProxyActive(bool active)
    {
        if (_blockingProxyRoot != null)
            _blockingProxyRoot.gameObject.SetActive(active);
    }

    void UpdateRemoteBlockingProxyEnabled(bool inNetSession, bool isLocalOwner, bool jailorCarried)
    {
        if (_blockingProxyRoot == null)
            return;

        SyncBlockingProxyCapsuleToCharacterController();

        bool useProxy = _isAlive && inNetSession && !isLocalOwner && !jailorCarried
                        && !_blockingProxySuppressedForRagdoll;
        SetBlockingProxyActive(useProxy);
    }

    bool _blockingProxySuppressedForRagdoll;

    /// <summary>
    /// While a non-owner copy of this player runs its short local ragdoll launch (dynamic bones covering
    /// the pose-stream warm-up — see <see cref="NetworkPlayerRagdoll"/>), the blocking proxy capsule must
    /// be off: the bones start inside it and PhysX would eject them violently. Releasing suppression
    /// re-evaluates the normal proxy rules. No-op when the state doesn't change.
    /// </summary>
    public void SetBlockingProxySuppressedForRagdoll(bool suppressed)
    {
        if (_blockingProxySuppressedForRagdoll == suppressed)
            return;

        _blockingProxySuppressedForRagdoll = suppressed;

        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        bool inNetSession = nm != null && nm.IsListening;
        UpdateRemoteBlockingProxyEnabled(inNetSession, IsOwner, IsCarriedByJailor);
    }

    void EnsureRemoteFlashlightProxy()
    {
        if (_remoteFlashlightProxyLight != null)
            return;

        GameObject proxy = new GameObject("RemoteFlashlightLightProxy");
        proxy.transform.SetParent(transform, false);
        proxy.SetActive(true);

        Light lightComponent = proxy.AddComponent<Light>();
        lightComponent.type = LightType.Spot;
        lightComponent.range = 20f;
        lightComponent.spotAngle = 62f;
        lightComponent.innerSpotAngle = 46.715996f;
        lightComponent.intensity = 15f;
        lightComponent.color = new Color(1f, 0.9820902f, 0.7877358f, 1f);
        lightComponent.renderMode = LightRenderMode.ForcePixel;
        lightComponent.shadows = LightShadows.None;
        lightComponent.cookie = null;
        lightComponent.enabled = false;

        _remoteFlashlightProxyLight = lightComponent;
    }

    void CopyFlashlightLightSettings(FlashlightItem flashlight)
    {
        if (flashlight == null)
            return;

        EnsureRemoteFlashlightProxy();
        if (_remoteFlashlightProxyLight == null)
            return;

        Light sourceLight = flashlight.GetComponentInChildren<Light>(true);
        if (sourceLight == null)
            return;

        _remoteFlashlightProxyLight.range = sourceLight.range;
        _remoteFlashlightProxyLight.spotAngle = sourceLight.spotAngle;
        _remoteFlashlightProxyLight.innerSpotAngle = sourceLight.innerSpotAngle;
        _remoteFlashlightProxyLight.intensity = sourceLight.intensity;
        _remoteFlashlightProxyLight.color = sourceLight.color;
        _remoteFlashlightProxyLight.cookie = sourceLight.cookie;
        _remoteFlashlightProxyLight.cullingMask = sourceLight.cullingMask;
        _remoteFlashlightProxyLight.renderingLayerMask = sourceLight.renderingLayerMask;
        _remoteFlashlightProxyLight.shadowStrength = sourceLight.shadowStrength;
    }

    void UpdateRemoteFlashlightProxy()
    {
        Transform holdPoint = null;
        Transform followTransform = null;
        bool hasFlashlight = playerInventory != null
            && playerInventory.IsSpawned
            && playerInventory.IsSelectedItemFlashlight
            && playerInventory.HasItemInSelectedSlot;
        bool lightOn = playerInventory != null
            && playerInventory.IsSpawned
            && playerInventory.SelectedFlashlightLightOn;
        bool shouldEnable = IsSpawned
            && !IsOwner
            && !_isDormant
            && hasFlashlight
            && lightOn
            && TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform)
            && holdPoint != null;

        if (!shouldEnable)
        {
            SetRemoteFlashlightProxyEnabled(false);
            return;
        }

        EnsureRemoteFlashlightProxy();
        if (_remoteFlashlightProxyLight == null)
            return;

        Transform rotationSource = followTransform != null ? followTransform : holdPoint;
        _remoteFlashlightProxyLight.transform.SetPositionAndRotation(
            holdPoint.TransformPoint(remoteFlashlightProxyLocalPosition),
            rotationSource.rotation);
        SetRemoteFlashlightProxyEnabled(true);
    }

    void SetRemoteFlashlightProxyEnabled(bool enabled)
    {
        if (_remoteFlashlightProxyLight != null && _remoteFlashlightProxyLight.enabled != enabled)
            _remoteFlashlightProxyLight.enabled = enabled;
    }

    void ApplyOwnershipState()
    {
        ApplyPresentation(IsOwner);
    }

    bool ShouldBeDormant()
    {
        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return false;

        return !IsSpawned;
    }

    void ApplyJailorCarryAnimatorState(bool carried)
    {
        if (avatarAnimator == null || string.IsNullOrEmpty(carriedByJailorAnimatorParameter))
            return;

        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        bool inNetSession = nm != null && nm.IsListening;
        if (inNetSession && IsSpawned && !IsOwner)
            return;

        avatarAnimator.SetBool(carriedByJailorAnimatorParameter, carried);
    }

    void ApplyPresentation(bool isLocalOwner)
    {
        if (_isDormant)
            return;

        bool jailorCarried = IsCarriedByJailor;

        ApplyJailorCarryAnimatorState(jailorCarried);

        if (playerController != null)
        {
            bool lookOnlyWhileCarried = isLocalOwner && _isAlive && jailorCarried;
            playerController.SetAllowLookWhileMovementLocked(lookOnlyWhileCarried);
            playerController.SetLocalControl(isLocalOwner && _isAlive && !jailorCarried);
        }

        if (playerHealth != null)
            playerHealth.SetHudVisible(isLocalOwner && _isAlive);

        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        bool inNetSession = nm != null && nm.IsListening;

        if (characterController != null)
        {
            // Client-authoritative transform: remote player proxies should not run CharacterController
            // physics (no Move on host/client observer); that wastes work and can fight NetworkTransform.
            bool enableCc = _isAlive && (!inNetSession || isLocalOwner) && !jailorCarried;
            characterController.enabled = enableCc;
        }

        // CharacterControllers do not collide with each other in Unity. Remotes also have CC off.
        // A kinematic capsule on a child gives other players' CharacterControllers something solid to slide against.
        UpdateRemoteBlockingProxyEnabled(inNetSession, isLocalOwner, jailorCarried);

        if (localOnlyCameras != null)
        {
            foreach (Camera cameraComponent in localOnlyCameras)
            {
                if (cameraComponent != null)
                    cameraComponent.enabled = isLocalOwner;
            }
        }

        if (localOnlyAudioListeners != null)
        {
            foreach (AudioListener audioListener in localOnlyAudioListeners)
            {
                if (audioListener != null)
                    audioListener.enabled = isLocalOwner;
            }
        }

        if (localViewShadowOnlyRenderers != null)
        {
            foreach (Renderer rendererComponent in localViewShadowOnlyRenderers)
            {
                if (rendererComponent != null)
                    rendererComponent.shadowCastingMode = isLocalOwner
                        ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                        : UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }
        // Only the local owner's head is ShadowsOnly (hidden from the FP camera) — that is the state a
        // mirror needs to temporarily undo so the reflection isn't headless. See OnMirrorReflectionPass.
        _localViewHeadHidden = isLocalOwner
            && localViewShadowOnlyRenderers != null
            && localViewShadowOnlyRenderers.Length > 0;

        ApplyCarriedBodyHide(hideOwnBodyWhileCarriedByJailor && isLocalOwner && jailorCarried);

        ForceSkinnedBoundsAlwaysUpdate();
    }

    /// <summary>
    /// In first person the camera is embedded at the head, so a SkinnedMeshRenderer's precomputed bind-pose
    /// bounds frequently sit partly behind/beside the camera. When a pose throws a limb outside those stale
    /// bounds (a punch reaching the fist toward the camera, or a hold pose raising the arm), Unity
    /// frustum-culls the whole renderer and the body vanishes at particular view angles. Forcing
    /// <c>updateWhenOffscreen</c> recomputes bounds from the live pose each frame, so on-screen limbs are
    /// never culled.
    ///
    /// Applied unconditionally from <see cref="Awake"/>, NOT gated on ownership: gating it meant the flag
    /// only landed once <c>OnNetworkSpawn</c> ran, so a player running standalone in an offline scene
    /// (Dev_IKTest / Staging, no NetworkManager session) never got it and the bug came straight back. The
    /// flag is a pure rendering-correctness setting with no ownership semantics, and recomputing bounds for
    /// at most four avatars is negligible next to skinning them.
    /// </summary>
    void ForceSkinnedBoundsAlwaysUpdate()
    {
        // Once set true we never revert.
        if (_skinnedRenderersOffscreenForced)
            return;

        if (_skinnedRenderers == null)
            _skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

        foreach (SkinnedMeshRenderer skinned in _skinnedRenderers)
        {
            if (skinned != null)
                skinned.updateWhenOffscreen = true;
        }
        _skinnedRenderersOffscreenForced = true;
    }

    /// <summary>
    /// Owner-only body hide for the Jailor carry: hanging off the Jailor puts the first-person camera inside
    /// the player's own torso and legs, so every body renderer except the arms is switched to
    /// <see cref="UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly"/> for the duration of the carry — the
    /// mesh stops drawing but still casts its shadow. Purely local: remote peers keep seeing the whole body,
    /// because only the owner's <see cref="ApplyPresentation"/> ever passes true here.
    ///
    /// Only skinned (character) renderers are touched, so a held item stays in view, and the work happens on
    /// state transitions only — each renderer is restored to the exact mode it had when the carry began, so
    /// this never fights the other writers of <c>shadowCastingMode</c> (the head in
    /// <see cref="localViewShadowOnlyRenderers"/>, <see cref="RagdollCameraCollision"/>'s close-camera hide).
    /// </summary>
    void ApplyCarriedBodyHide(bool hide)
    {
        if (hide == _carriedBodyHidden)
            return;

        if (hide)
        {
            if (_skinnedRenderers == null)
                _skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

            int hideCount = 0;
            foreach (SkinnedMeshRenderer skinned in _skinnedRenderers)
            {
                if (skinned != null && !IsKeptVisibleWhileCarried(skinned))
                    hideCount++;
            }

            _carriedHiddenRenderers = new Renderer[hideCount];
            _carriedHiddenPreviousModes = new UnityEngine.Rendering.ShadowCastingMode[hideCount];

            int index = 0;
            foreach (SkinnedMeshRenderer skinned in _skinnedRenderers)
            {
                if (skinned == null || IsKeptVisibleWhileCarried(skinned))
                    continue;

                _carriedHiddenRenderers[index] = skinned;
                _carriedHiddenPreviousModes[index] = skinned.shadowCastingMode;
                skinned.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                index++;
            }

            _carriedBodyHidden = true;
            return;
        }

        if (_carriedHiddenRenderers != null)
        {
            for (int i = 0; i < _carriedHiddenRenderers.Length; i++)
            {
                if (_carriedHiddenRenderers[i] != null)
                    _carriedHiddenRenderers[i].shadowCastingMode = _carriedHiddenPreviousModes[i];
            }
        }

        _carriedHiddenRenderers = null;
        _carriedHiddenPreviousModes = null;
        _carriedBodyHidden = false;
    }

    /// <summary>Arms / hands stay drawn while the body is hidden during a Jailor carry.</summary>
    bool IsKeptVisibleWhileCarried(Renderer candidate)
    {
        if (carriedVisibleRenderers != null)
        {
            foreach (Renderer keepVisible in carriedVisibleRenderers)
            {
                if (keepVisible == candidate)
                    return true;
            }
        }

        if (carriedVisibleNameFragments == null)
            return false;

        string rendererName = candidate.name;
        foreach (string fragment in carriedVisibleNameFragments)
        {
            if (!string.IsNullOrEmpty(fragment)
                && rendererName.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// While a mirror renders its reflection, temporarily draw the local owner's first-person-hidden
    /// renderers (the head — normally <see cref="UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly"/>
    /// so the FP camera never shows the inside of the skull) so the player sees a complete reflection
    /// instead of a headless body. Restored the instant the reflection pass ends, so the main
    /// first-person view is unchanged. No-op for remote avatars — their head is already drawn — and
    /// when nothing is hidden. Driven by <see cref="PlanarMirror.ReflectionPass"/>.
    /// </summary>
    void OnMirrorReflectionPass(bool revealing)
    {
        // Carry hide first, head second: the head is in both sets, and its own state must win.
        if (_carriedBodyHidden && _carriedHiddenRenderers != null)
        {
            for (int i = 0; i < _carriedHiddenRenderers.Length; i++)
            {
                if (_carriedHiddenRenderers[i] != null)
                {
                    _carriedHiddenRenderers[i].shadowCastingMode = revealing
                        ? _carriedHiddenPreviousModes[i]
                        : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
            }
        }

        if (!_localViewHeadHidden || localViewShadowOnlyRenderers == null)
            return;

        UnityEngine.Rendering.ShadowCastingMode mode = revealing
            ? UnityEngine.Rendering.ShadowCastingMode.On
            : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

        foreach (Renderer rendererComponent in localViewShadowOnlyRenderers)
        {
            if (rendererComponent != null)
                rendererComponent.shadowCastingMode = mode;
        }
    }

    void SetDormant(bool dormant)
    {
        _isDormant = dormant;

        if (dormant)
        {
            if (playerController != null)
            {
                playerController.SetAllowLookWhileMovementLocked(false);
                playerController.SetLocalControl(false);
            }

            if (playerHealth != null)
                playerHealth.SetHudVisible(false);

            if (characterController != null)
                characterController.enabled = false;

            SetBlockingProxyActive(false);

            if (localOnlyCameras != null)
            {
                foreach (Camera cameraComponent in localOnlyCameras)
                {
                    if (cameraComponent != null)
                        cameraComponent.enabled = false;
                }
            }

            if (localOnlyAudioListeners != null)
            {
                foreach (AudioListener audioListener in localOnlyAudioListeners)
                {
                    if (audioListener != null)
                        audioListener.enabled = false;
                }
            }

            if (avatarRenderers != null)
            {
                foreach (Renderer rendererComponent in avatarRenderers)
                {
                    if (rendererComponent != null)
                        rendererComponent.enabled = false;
                }
            }

            return;
        }

        if (avatarRenderers != null)
        {
            foreach (Renderer rendererComponent in avatarRenderers)
            {
                if (rendererComponent != null)
                    rendererComponent.enabled = true;
            }
        }

        ApplyPresentation(!IsSpawned || IsOwner);
    }

    public void SetLifeState(bool isAlive)
    {
        if (_isAlive == isAlive)
            return;

        _isAlive = isAlive;

        if (_isDormant)
            return;

        ApplyPresentation(IsOwner);
    }

    /// <summary>Called on the <b>server</b> on this avatar instance to push the maze seed to that player's client. Uses ClientRpc (reliable) instead of custom named messages, which do not work reliably to the Steam host in practice.</summary>
    public void DeliverMazeSeedToOwnerFromServer(int seed)
    {
        if (!IsServer)
            return;
        DeliverMazeSeedToOwnerClientRpc(seed);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void RequestMazeSeedFromHostServerRpc()
    {
        if (NetworkManager.Singleton == null
            || !NetworkManager.Singleton.TryGetComponent(out ProceduralMazeCoordinator coordinator)
            || coordinator == null
            || !coordinator.TryGetServerMazeSeed(out int seed))
        {
            return;
        }
        DeliverMazeSeedToOwnerClientRpc(seed);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void RequestProceduralJailDoorSnapshotsServerRpc()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
            return;

        NetworkPlayerInventory.ServerSendProceduralJailDoorSnapshotsToClient(OwnerClientId);
    }

    [ClientRpc]
    void DeliverMazeSeedToOwnerClientRpc(int seed, ClientRpcParams clientRpcParams = default)
    {
        if (IsServer)
            return;
        if (!IsOwner)
            return;
        ProceduralMazeCoordinator.TryApplyMazeSeedAsClientFromRpc(seed);
        RequestProceduralJailDoorSnapshotsServerRpc();
    }
}
