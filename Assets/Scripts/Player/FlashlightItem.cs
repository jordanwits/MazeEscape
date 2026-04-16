using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class FlashlightItem : MonoBehaviour
{
    static readonly Dictionary<ulong, FlashlightItem> RegisteredFlashlights = new();

    [SerializeField] Light flashlightLight;
    [SerializeField] Rigidbody itemRigidbody;
    [SerializeField] Collider[] itemColliders;
    [SerializeField] Vector3 heldLocalPosition;
    [Tooltip("If enabled, the flashlight rotates so its Light points the same way as the hold point.")]
    [SerializeField] bool alignHeldRotationToLight = true;
    [SerializeField] Vector3 heldLocalEulerAngles;

    public bool IsHeld { get; private set; }
    public bool IsLightOn => _isLightOn;
    public Vector3 IdentityHintPosition => _identityHintPosition;
    public Quaternion IdentityHintRotation => _identityHintRotation;
    public ulong ItemId
    {
        get
        {
            if (!_hasCachedItemId)
            {
                _cachedItemId = ComputeStableItemId();
                _hasCachedItemId = true;
            }

            return _cachedItemId;
        }
    }
    public ulong HolderNetworkObjectId => _holderNetworkObjectId;

    Light[] _lights;
    Quaternion _heldLocalRotation;
    Transform _heldAnchor;
    Transform _heldRotationSource;
    bool _isLightOn;
    ulong _holderNetworkObjectId;
    ulong _cachedItemId;
    bool _hasCachedItemId;
    Vector3 _identityHintPosition;
    Quaternion _identityHintRotation;

    public static IEnumerable<FlashlightItem> GetRegisteredFlashlights()
    {
        return RegisteredFlashlights.Values;
    }

    public static bool TryGetRegisteredFlashlight(ulong itemId, out FlashlightItem flashlight)
    {
        return RegisteredFlashlights.TryGetValue(itemId, out flashlight);
    }

    public static bool TryResolveRegisteredFlashlightForPickup(ulong itemId, Vector3 hintPosition, out FlashlightItem flashlight)
    {
        if (TryGetRegisteredFlashlight(itemId, out flashlight) && flashlight != null && !flashlight.IsHeld)
            return true;

        return TryFindNearestRegisteredFlashlight(hintPosition, false, out flashlight);
    }

    public static bool TryResolveRegisteredFlashlightForState(ulong itemId, Vector3 hintPosition, out FlashlightItem flashlight)
    {
        if (TryGetRegisteredFlashlight(itemId, out flashlight) && flashlight != null)
            return true;

        return TryFindNearestRegisteredFlashlight(hintPosition, null, out flashlight);
    }

    void Awake()
    {
        CacheLights();
        CacheHeldRotation();
        CacheIdentityHint();

        if (itemRigidbody == null)
            itemRigidbody = GetComponent<Rigidbody>();

        if (itemColliders == null || itemColliders.Length == 0)
            itemColliders = GetComponentsInChildren<Collider>(true);

        _isLightOn = AreAnyLightsEnabled();
    }

    void OnEnable()
    {
        RegisteredFlashlights[ItemId] = this;
    }

    void OnDisable()
    {
        if (RegisteredFlashlights.TryGetValue(ItemId, out FlashlightItem existing) && existing == this)
            RegisteredFlashlights.Remove(ItemId);
    }

    public void Pickup(Transform holdPoint, Transform followTransform = null)
    {
        if (holdPoint == null)
            return;

        _holderNetworkObjectId = 0;
        BeginHeldState();
        AttachToHoldPoint(holdPoint, followTransform);
    }

    public void Drop(Vector3 impulse)
    {
        EndHeldState();

        if (itemRigidbody != null)
            itemRigidbody.AddForce(impulse, ForceMode.Impulse);
    }

    void LateUpdate()
    {
        if (!IsHeld)
            return;

        if (_heldAnchor == null && _holderNetworkObjectId != 0)
            TryAttachToNetworkHolder(_holderNetworkObjectId);

        if (_heldAnchor == null)
            return;

        UpdateHeldTransform();
    }

    public void ToggleLight()
    {
        CacheLights();

        if (_lights == null || _lights.Length == 0)
            return;

        SetLightEnabled(!AreAnyLightsEnabled());
    }

    public void SetLightEnabled(bool enabled)
    {
        CacheLights();

        if (_lights == null || _lights.Length == 0)
        {
            _isLightOn = enabled;
            return;
        }

        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light == null)
                continue;

            if (enabled)
                ApplyPeerVisibleLightSettings(light);

            light.enabled = enabled;
        }

        _isLightOn = enabled;
    }

    public void ApplyNetworkHeldState(ulong holderNetworkObjectId, bool lightEnabled)
    {
        SetLightEnabled(lightEnabled);
        _holderNetworkObjectId = holderNetworkObjectId;
        BeginHeldState();
        TryAttachToNetworkHolder(holderNetworkObjectId);
    }

    public void ApplyNetworkWorldState(Vector3 worldPosition, Quaternion worldRotation, bool lightEnabled)
    {
        SetLightEnabled(lightEnabled);
        _holderNetworkObjectId = 0;
        EndHeldState(enableWorldPhysics: true);
        transform.SetPositionAndRotation(worldPosition, worldRotation);
    }

    void CacheLights()
    {
        if (flashlightLight != null)
        {
            _lights = new[] { flashlightLight };
            ApplyPeerVisibleLightSettings(flashlightLight);
            return;
        }

        _lights = GetComponentsInChildren<Light>(true);
        if (_lights.Length > 0)
        {
            flashlightLight = _lights[0];
            for (int i = 0; i < _lights.Length; i++)
                ApplyPeerVisibleLightSettings(_lights[i]);
        }
    }

    static void ApplyPeerVisibleLightSettings(Light light)
    {
        if (light == null)
            return;

        // URP only applies a small number of additional lights per surface (Additional Lights Per Object Limit on the
        // URP asset). Without this, the holder's light wins the sort locally, but other clients often drop this
        // spotlight so they see the mesh aim correctly but no illumination on the world.
        light.renderMode = LightRenderMode.ForcePixel;
    }

    void CacheHeldRotation()
    {
        _heldLocalRotation = Quaternion.Euler(heldLocalEulerAngles);
        if (!alignHeldRotationToLight || flashlightLight == null)
            return;

        Quaternion lightRotationRelativeToRoot = Quaternion.Inverse(transform.rotation) * flashlightLight.transform.rotation;
        _heldLocalRotation = Quaternion.Inverse(lightRotationRelativeToRoot);
    }

    void CacheIdentityHint()
    {
        _identityHintPosition = transform.position;
        _identityHintRotation = transform.rotation;
    }

    void UpdateHeldTransform()
    {
        if (_heldAnchor == null)
            return;

        transform.localPosition = heldLocalPosition;

        Transform rotationSource = _heldRotationSource != null ? _heldRotationSource : _heldAnchor;
        Quaternion worldRotation = rotationSource.rotation * _heldLocalRotation;
        transform.localRotation = Quaternion.Inverse(_heldAnchor.rotation) * worldRotation;
    }

    void BeginHeldState()
    {
        IsHeld = true;
        SetCollidersEnabled(false);

        if (itemRigidbody == null)
            return;

        itemRigidbody.isKinematic = true;
        itemRigidbody.useGravity = false;
        itemRigidbody.linearVelocity = Vector3.zero;
        itemRigidbody.angularVelocity = Vector3.zero;
    }

    void EndHeldState(bool enableWorldPhysics = true)
    {
        IsHeld = false;
        _heldAnchor = null;
        _heldRotationSource = null;
        transform.SetParent(null, true);
        SetCollidersEnabled(true);

        if (itemRigidbody == null)
            return;

        itemRigidbody.linearVelocity = Vector3.zero;
        itemRigidbody.angularVelocity = Vector3.zero;
        itemRigidbody.isKinematic = !enableWorldPhysics;
        itemRigidbody.useGravity = enableWorldPhysics;
    }

    void AttachToHoldPoint(Transform holdPoint, Transform followTransform)
    {
        if (holdPoint == null)
            return;

        _heldAnchor = holdPoint;
        _heldRotationSource = followTransform != null ? followTransform : holdPoint;
        transform.SetParent(_heldAnchor, false);
        transform.localPosition = heldLocalPosition;
        UpdateHeldTransform();
    }

    void TryAttachToNetworkHolder(ulong holderNetworkObjectId)
    {
        if (holderNetworkObjectId == 0)
            return;

        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null || networkManager.SpawnManager == null)
            return;

        if (!networkManager.SpawnManager.SpawnedObjects.TryGetValue(holderNetworkObjectId, out NetworkObject holderObject)
            || holderObject == null)
        {
            return;
        }

        NetworkPlayerAvatar holderAvatar = holderObject.GetComponent<NetworkPlayerAvatar>();
        if (holderAvatar == null || !holderAvatar.TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform))
            return;

        AttachToHoldPoint(holdPoint, followTransform);
    }

    bool AreAnyLightsEnabled()
    {
        if (_lights == null || _lights.Length == 0)
            return false;

        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light != null && light.enabled)
                return true;
        }

        return false;
    }

    ulong ComputeStableItemId()
    {
        StringBuilder builder = new StringBuilder();
        // scene.path differs between Editor and player builds (often empty in builds), which breaks ItemId across peers.
        builder.Append(gameObject.scene.buildIndex);
        builder.Append('|');
        builder.Append(gameObject.scene.name);

        Stack<Transform> hierarchy = new Stack<Transform>();
        Transform current = transform;
        while (current != null)
        {
            hierarchy.Push(current);
            current = current.parent;
        }

        while (hierarchy.Count > 0)
        {
            Transform next = hierarchy.Pop();
            builder.Append('/');
            builder.Append(next.name);
            builder.Append('[');
            builder.Append(next.GetSiblingIndex());
            builder.Append(']');
        }

        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = fnvOffset;
        string key = builder.ToString();
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    static bool TryFindNearestRegisteredFlashlight(Vector3 hintPosition, bool? requireHeldState, out FlashlightItem flashlight)
    {
        const float maxMatchDistance = 8f;

        flashlight = null;
        float bestDistanceSquared = maxMatchDistance * maxMatchDistance;

        foreach (FlashlightItem candidate in RegisteredFlashlights.Values)
        {
            if (candidate == null)
                continue;

            if (requireHeldState.HasValue && candidate.IsHeld != requireHeldState.Value)
                continue;

            float distanceSquared = (candidate.transform.position - hintPosition).sqrMagnitude;
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            flashlight = candidate;
        }

        return flashlight != null;
    }

    void SetCollidersEnabled(bool enabled)
    {
        if (itemColliders == null)
            return;

        foreach (Collider itemCollider in itemColliders)
        {
            if (itemCollider != null)
                itemCollider.enabled = enabled;
        }
    }
}
