using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// World pickup that can be held at the player hold point, stored in 3 hotbar slots, and synced in multiplayer
/// (holder id + per-item state via <see cref="NetworkPlayerInventory"/>).
/// </summary>
public class GrabbableInventoryItem : MonoBehaviour
{
    public const byte TypeIdNone = 0;
    public const byte TypeIdFlashlight = 1;
    public const byte TypeIdGlowstick = 2;

    static readonly Dictionary<ulong, GrabbableInventoryItem> Registered = new();

    [SerializeField] protected byte _itemTypeId = TypeIdNone;
    [Tooltip("Icon shown in the HUD hotbar for this item.")]
    [SerializeField] protected Sprite _slotIcon;
    [SerializeField] Rigidbody itemRigidbody;
    [SerializeField] Collider[] itemColliders;
    [SerializeField] Renderer[] itemRenderers;
    [SerializeField] protected Vector3 heldLocalPosition;
    [SerializeField] protected Vector3 heldLocalEulerAngles;
    [Tooltip("If true, the held mesh follows the follow transform (camera) rotation. Set false to lock to the hold point.")]
    [SerializeField] bool alignRotationWithFollow = true;

    public bool IsHeld { get; private set; }
    public bool IsStashed { get; private set; }

    /// <summary>Called by the player inventory when applying hand vs non-selected slot layout.</summary>
    public void SetStashViewStateForInventory(bool isStashed)
    {
        IsStashed = isStashed;
        RefreshInventoryVisibility();
    }
    public byte ItemTypeId => _itemTypeId;
    public Sprite SlotIcon => _slotIcon;

    // Renamed to invalidate one-time cached disc sprites if placeholder art changes.
    static Sprite s_hudPhDefault;
    static Sprite s_hudPhFlash;
    static Sprite s_hudPhGlow;

    /// <summary>Inspector <see cref="_slotIcon"/> if set; otherwise a simple circular runtime glyph (transparent outside the disk).</summary>
    public Sprite GetEffectiveSlotIconForHud()
    {
        if (_slotIcon != null)
            return _slotIcon;
        return GetPlaceholderForItemType(ResolveTypeForPlaceholder());
    }

    byte ResolveTypeForPlaceholder()
    {
        if (_itemTypeId != TypeIdNone)
            return _itemTypeId;
        if (GetComponent<FlashlightItem>() != null)
            return TypeIdFlashlight;
        if (GetComponent<GlowstickItem>() != null)
            return TypeIdGlowstick;
        return TypeIdNone;
    }

    static Sprite GetPlaceholderForItemType(byte typeId)
    {
        return typeId switch
        {
            TypeIdFlashlight => s_hudPhFlash ??= CreatePlaceholderSprite(0.95f, 0.9f, 0.5f),
            TypeIdGlowstick => s_hudPhGlow ??= CreatePlaceholderSprite(0.35f, 1f, 0.35f),
            _ => s_hudPhDefault ??= CreatePlaceholderSprite(0.65f, 0.65f, 0.68f)
        };
    }

    static Sprite CreatePlaceholderSprite(float r, float g, float b)
    {
        const int w = 64;
        float cx = (w - 1) * 0.5f;
        float cy = (w - 1) * 0.5f;
        const float outerR = 30f;
        const float innerR = 23f;
        Color fill = new Color(r, g, b, 1f);
        Color edge = new Color(
            Mathf.Clamp01(r * 0.55f + 0.1f),
            Mathf.Clamp01(g * 0.55f + 0.1f),
            Mathf.Clamp01(b * 0.55f + 0.1f), 1f);
        Color[] p = new Color[w * w];
        for (int y = 0; y < w; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > outerR)
                {
                    p[y * w + x] = Color.clear;
                }
                else if (d > innerR)
                {
                    float t = (d - innerR) / (outerR - innerR);
                    p[y * w + x] = Color.Lerp(fill, edge, t);
                }
                else
                {
                    p[y * w + x] = fill;
                }
            }
        }

        // Soft highlight (reads less like a highlighter over the full slot)
        float hx = cx - 7f;
        float hy = cy - 6f;
        for (int y = 0; y < w; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float d2 = (x - hx) * (x - hx) + (y - hy) * (y - hy);
                if (d2 < 36f)
                {
                    float br = 1f - Mathf.Sqrt(d2) / 6f;
                    int i = y * w + x;
                    if (p[i].a > 0.01f)
                        p[i] = Color.Lerp(p[i], new Color(1f, 1f, 1f, 0.45f * br * p[i].a), br * 0.7f);
                }
            }
        }

        Texture2D tex = new Texture2D(w, w, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixels(p);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, w), new Vector2(0.5f, 0.5f), 100f);
    }
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
    public Transform StashOverrideParent { get; set; }

    protected Transform _heldAnchor;
    protected Transform _heldRotationSource;
    protected Quaternion _heldLocalRotation;
    protected ulong _holderNetworkObjectId;
    ulong _cachedItemId;
    bool _hasCachedItemId;
    Vector3 _identityHintPosition;
    Quaternion _identityHintRotation;
    Vector3 _authoredLocalScale;

    public static IEnumerable<GrabbableInventoryItem> GetRegisteredItems()
    {
        return Registered.Values;
    }

    public static bool TryGetRegistered(ulong itemId, out GrabbableInventoryItem item)
    {
        return Registered.TryGetValue(itemId, out item);
    }

    public static bool TryResolveForPickup(ulong itemId, Vector3 hintPosition, out GrabbableInventoryItem item)
    {
        if (TryGetRegistered(itemId, out item) && item != null && !item.IsHeld)
            return true;

        return TryFindNearestRegistered(hintPosition, false, out item);
    }

    public static bool TryResolveForState(ulong itemId, Vector3 hintPosition, out GrabbableInventoryItem item)
    {
        if (TryGetRegistered(itemId, out item) && item != null)
            return true;

        return TryFindNearestRegistered(hintPosition, null, out item);
    }

    protected virtual void Awake()
    {
        RebuildCachedHoldRotation();
        CacheIdentityHint();
        _authoredLocalScale = transform.localScale;

        if (itemRigidbody == null)
            itemRigidbody = GetComponent<Rigidbody>();

        if (itemColliders == null || itemColliders.Length == 0)
            itemColliders = GetComponentsInChildren<Collider>(true);

        if (itemRenderers == null || itemRenderers.Length == 0)
            itemRenderers = GetComponentsInChildren<Renderer>(true);

        RefreshInventoryVisibility();
    }

    protected void OnEnable()
    {
        Registered[ItemId] = this;
    }

    protected void OnDisable()
    {
        if (Registered.TryGetValue(ItemId, out GrabbableInventoryItem existing) && existing == this)
            Registered.Remove(ItemId);
    }

    /// <summary>Single-player or non-networked pickup.</summary>
    public void Pickup(Transform holdPoint, Transform followTransform = null)
    {
        if (holdPoint == null)
            return;

        _holderNetworkObjectId = 0;
        IsStashed = false;
        BeginHeldState();
        AttachToHoldPoint(holdPoint, followTransform);
    }

    public void Drop(Vector3 impulse)
    {
        StashOverrideParent = null;
        IsStashed = false;
        EndHeldState();

        if (itemRigidbody != null)
            itemRigidbody.AddForce(impulse, ForceMode.Impulse);
    }

    public virtual void OnLateUpdateHeld() { }

    void LateUpdate()
    {
        if (!IsHeld)
            return;

        if (_heldAnchor == null && _holderNetworkObjectId != 0)
            TryAttachToNetworkHolder(_holderNetworkObjectId);

        if (_heldAnchor == null)
            return;

        if (IsStashed)
        {
            OnLateUpdateHeld();
            return;
        }

        UpdateHeldTransform();
        OnLateUpdateHeld();
    }

    /// <summary>Used when the item is held in inventory on another client (replicated from server).</summary>
    public virtual void ApplyNetworkHeldState(ulong holderNetworkObjectId)
    {
        _holderNetworkObjectId = holderNetworkObjectId;
        BeginHeldState();
        TryAttachToNetworkHolder(holderNetworkObjectId);
    }

    public void ApplyNetworkWorldState(Vector3 worldPosition, Quaternion worldRotation, Vector3 worldImpulse = default)
    {
        StashOverrideParent = null;
        _holderNetworkObjectId = 0;
        IsStashed = false;
        EndHeldState(enableWorldPhysics: true);
        transform.SetPositionAndRotation(worldPosition, worldRotation);
        transform.localScale = _authoredLocalScale;

        if (worldImpulse.sqrMagnitude > 0.0001f && itemRigidbody != null && !itemRigidbody.isKinematic)
        {
            itemRigidbody.angularVelocity = Vector3.zero;
            itemRigidbody.AddForce(worldImpulse, ForceMode.Impulse);
        }
    }

    public void StashInInventory(Transform stashParent)
    {
        if (stashParent == null)
            return;

        IsStashed = true;
        BeginHeldState();
        transform.SetParent(stashParent, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = _authoredLocalScale;
        RefreshInventoryVisibility();
    }

    public void UnstashToEmptyWorld(Vector3 worldPosition, Quaternion worldRotation, bool worldPhysics = false)
    {
        StashOverrideParent = null;
        _holderNetworkObjectId = 0;
        IsStashed = false;
        EndHeldState(enableWorldPhysics: worldPhysics);
        transform.SetPositionAndRotation(worldPosition, worldRotation);
        transform.localScale = _authoredLocalScale;
    }

    /// <summary>Override to align held rotation to a child mesh/light after base euler angles are applied.</summary>
    protected virtual void FinalizeCachedHoldRotation() { }

    protected void RebuildCachedHoldRotation()
    {
        _heldLocalRotation = Quaternion.Euler(heldLocalEulerAngles);
        FinalizeCachedHoldRotation();
    }

    void CacheIdentityHint()
    {
        _identityHintPosition = transform.position;
        _identityHintRotation = transform.rotation;
    }

    protected void UpdateHeldTransform()
    {
        if (_heldAnchor == null)
            return;

        transform.localPosition = heldLocalPosition;

        Transform rotationSource = _heldRotationSource != null ? _heldRotationSource : _heldAnchor;
        Quaternion worldRotation;
        if (alignRotationWithFollow)
            worldRotation = rotationSource.rotation * _heldLocalRotation;
        else
            worldRotation = _heldAnchor.rotation * _heldLocalRotation;

        transform.localRotation = Quaternion.Inverse(_heldAnchor.rotation) * worldRotation;
    }

    protected void BeginHeldState()
    {
        IsHeld = true;
        SetCollidersEnabled(false);
        RefreshInventoryVisibility();

        if (itemRigidbody == null)
            return;

        if (!itemRigidbody.isKinematic)
        {
            itemRigidbody.linearVelocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
        }

        itemRigidbody.isKinematic = true;
        itemRigidbody.useGravity = false;
    }

    protected void EndHeldState(bool enableWorldPhysics = true)
    {
        IsHeld = false;
        IsStashed = false;
        _heldAnchor = null;
        _heldRotationSource = null;
        transform.SetParent(null, true);
        SetCollidersEnabled(true);
        RefreshInventoryVisibility();

        if (itemRigidbody == null)
            return;

        itemRigidbody.isKinematic = !enableWorldPhysics;
        itemRigidbody.useGravity = enableWorldPhysics;
        if (!itemRigidbody.isKinematic)
        {
            itemRigidbody.linearVelocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
        }
    }

    protected void AttachToHoldPoint(Transform holdPoint, Transform followTransform)
    {
        if (holdPoint == null)
            return;

        IsStashed = false;
        _heldAnchor = holdPoint;
        _heldRotationSource = followTransform != null ? followTransform : holdPoint;
        transform.SetParent(_heldAnchor, false);
        transform.localPosition = heldLocalPosition;
        transform.localScale = _authoredLocalScale;
        UpdateHeldTransform();
        RefreshInventoryVisibility();
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

        if (StashOverrideParent != null)
        {
            StashInInventory(StashOverrideParent);
            return;
        }

        NetworkPlayerAvatar holderAvatar = holderObject.GetComponent<NetworkPlayerAvatar>();
        if (holderAvatar == null
            || !holderAvatar.TryGetInventoryAttachmentTargets(out Transform holdPoint, out Transform followTransform, out Transform stash))
        {
            return;
        }

        if (IsStashed && stash != null)
        {
            StashInInventory(stash);
            return;
        }

        AttachToHoldPoint(holdPoint, followTransform);
    }

    static bool TryFindNearestRegistered(Vector3 hintPosition, bool? requireHeldState, out GrabbableInventoryItem item)
    {
        const float maxMatchDistance = 8f;
        item = null;
        float bestDistanceSquared = maxMatchDistance * maxMatchDistance;

        foreach (GrabbableInventoryItem candidate in Registered.Values)
        {
            if (candidate == null)
                continue;

            if (requireHeldState.HasValue && candidate.IsHeld != requireHeldState.Value)
                continue;

            float distanceSquared = (candidate.transform.position - hintPosition).sqrMagnitude;
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            item = candidate;
        }

        return item != null;
    }

    ulong ComputeStableItemId()
    {
        StringBuilder builder = new StringBuilder();
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

    void SetCollidersEnabled(bool enabled)
    {
        if (itemColliders == null)
            return;

        foreach (Collider c in itemColliders)
        {
            if (c != null)
                c.enabled = enabled;
        }
    }

    void RefreshInventoryVisibility()
    {
        if (itemRenderers == null)
            return;

        bool hideRenderers = IsHeld && IsStashed;
        foreach (Renderer renderer in itemRenderers)
        {
            if (renderer != null)
                renderer.forceRenderingOff = hideRenderers;
        }
    }
}
