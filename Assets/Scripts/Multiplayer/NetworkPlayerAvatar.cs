using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkPlayerInventory))]
public class NetworkPlayerAvatar : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] Animator avatarAnimator;
    [SerializeField] PlayerController playerController;
    [SerializeField] PlayerHealth playerHealth;
    [SerializeField] CharacterController characterController;
    [SerializeField] Camera[] localOnlyCameras;
    [SerializeField] AudioListener[] localOnlyAudioListeners;
    [SerializeField] Renderer[] avatarRenderers;
    [SerializeField] NetworkPlayerInventory playerInventory;

    [Header("Flashlight replication")]
    [Tooltip("First-person pitch node (same as PlayerController camera / CameraPitch). Resolved automatically when empty.")]
    [SerializeField] Transform flashlightAimPivot;
    [Tooltip("Local offset for the remote-only light proxy while another player is holding a flashlight.")]
    [SerializeField] Vector3 remoteFlashlightProxyLocalPosition = new Vector3(0f, 0f, 0.08f);

    readonly NetworkVariable<float> _flashlightLookPitchDegrees = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    bool _isDormant;
    bool _isAlive = true;
    OwnerNetworkAnimator _ownerNetworkAnimator;
    Light _remoteFlashlightProxyLight;

    public bool HasHeldFlashlight => playerInventory != null && playerInventory.HasEquippedFlashlight;

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

        bool shouldBeDormant = ShouldBeDormant();

        if (_isDormant == shouldBeDormant)
            return;

        SetDormant(shouldBeDormant);
    }

    public override void OnNetworkSpawn()
    {
        SetDormant(false);
        ApplyOwnershipState();
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
        SetDormant(false);
        ApplyPresentation(true);
        SetRemoteFlashlightProxyEnabled(false);
    }

    public void NotifyFlashlightVisualAttach(FlashlightItem flashlight)
    {
        CopyFlashlightLightSettings(flashlight);
    }

    public bool CanPickupFlashlight(FlashlightItem flashlight)
    {
        return playerInventory != null && playerInventory.CanPickupFlashlight(flashlight);
    }

    public bool TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform)
    {
        holdPoint = null;
        followTransform = null;
        return playerController != null && playerController.TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform);
    }

    public void TryPickupFlashlight(FlashlightItem flashlight)
    {
        playerInventory?.TryPickupFlashlight(flashlight);
    }

    public void TriggerAnimation(string triggerName)
    {
        if (string.IsNullOrWhiteSpace(triggerName))
            return;

        bool useNetworkAnimator = _ownerNetworkAnimator != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && IsSpawned;

        if (useNetworkAnimator)
            _ownerNetworkAnimator.SetTrigger(triggerName);
        else
            avatarAnimator?.SetTrigger(triggerName);
    }

    public void TryToggleHeldFlashlight()
    {
        playerInventory?.TryToggleHeldFlashlight();
    }

    public void TryDropHeldFlashlight(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        playerInventory?.TryDropHeldFlashlight(dropPosition, dropRotation, dropForward);
    }

    void EnsureAnimationSync()
    {
        if (avatarAnimator == null)
            return;

        _ownerNetworkAnimator = avatarAnimator.GetComponent<OwnerNetworkAnimator>();
        if (_ownerNetworkAnimator == null)
            _ownerNetworkAnimator = avatarAnimator.gameObject.AddComponent<OwnerNetworkAnimator>();
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
        bool hasFlashlight = playerInventory != null && playerInventory.HasEquippedFlashlight;
        bool lightOn = playerInventory != null && playerInventory.HeldFlashlightLightOn;
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
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return false;

        return !IsSpawned;
    }

    void ApplyPresentation(bool isLocalOwner)
    {
        if (_isDormant)
            return;

        if (playerController != null)
            playerController.SetLocalControl(isLocalOwner && _isAlive);

        if (playerHealth != null)
            playerHealth.SetHudVisible(isLocalOwner && _isAlive);

        if (characterController != null)
            characterController.enabled = _isAlive;

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
    }

    void SetDormant(bool dormant)
    {
        _isDormant = dormant;

        if (dormant)
        {
            if (playerController != null)
                playerController.SetLocalControl(false);

            if (playerHealth != null)
                playerHealth.SetHudVisible(false);

            if (characterController != null)
                characterController.enabled = false;

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
}
