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
    public const byte TypeIdKey = 3;
    public const byte TypeIdBandage = 4;
    /// <summary>Carnival StarBall; carried via <see cref="NetworkHeavyThrowableHold"/>, not hotbar slots.</summary>
    public const byte TypeIdStarBall = 5;
    public const byte TypeIdRingBlue = 6;
    public const byte TypeIdRingGreen = 7;
    public const byte TypeIdRingYellow = 8;
    public const byte TypeIdEnergyDrink = 9;

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
    [Tooltip("Optional direct child marking where the right hand grips this item. Falls back to the item origin.")]
    [SerializeField] Transform gripPointRight;
    [Tooltip("Optional direct child for the left hand (two-handed carries). No left-hand IK when unset.")]
    [SerializeField] Transform gripPointLeft;
    [Tooltip("Extra local rotation applied while held (degrees). Use e.g. (0,180,0) to flip an item that is authored facing backward.")]
    [SerializeField] Vector3 heldRotationOffsetEuler;
    [Tooltip("Held items ride the avatar's animated hand socket (perfect grip, natural wrist). Disable for chest-carried heavy items (StarBall/rings) that use two-hand IK instead.")]
    [SerializeField] bool heldAttachToHandSocket = true;
    [Tooltip("Extra wrist rotation in player space (degrees) while this item is held. The fist's finger tunnel points forward by default, so tilt the wrist up (negative X) to grip an upright item like a can or a raised glowstick.")]
    [SerializeField] Vector3 heldWristEulerOffset;
    [Tooltip("How the hand shapes itself around this item: a closed fist for thin items, a thumb/index pinch for flat ones, or an open C for cans and rolls.")]
    [SerializeField] HeldGripStyle gripStyle = HeldGripStyle.Fist;

    /// <summary>True when the in-hand visual follows the hand socket instead of the HoldPoint float.</summary>
    public bool HeldAttachToHandSocket => heldAttachToHandSocket;

    /// <summary>
    /// Player-space wrist rotation applied to the right hand while this item is held, so the fist's finger
    /// tunnel can be turned to match the item (e.g. tilted up to cup a can). Zero leaves the clip pose alone.
    /// </summary>
    public Vector3 HeldWristEulerOffset => heldWristEulerOffset;

    /// <summary>How the hand shapes itself around this item while held.</summary>
    public HeldGripStyle GripStyle => gripStyle;

    /// <summary>
    /// The "HoldPose" animator value for this item's grip: 1 fist, 3 pinch, 4 cup, 5 ball. Kept here so the
    /// style-to-clip mapping lives in one place. (2 is the two-hand chest carry, which is not a grip style.)
    /// </summary>
    public int HeldPoseIndex
    {
        get
        {
            if (gripStyle == HeldGripStyle.Pinch)
                return 3;
            if (gripStyle == HeldGripStyle.Cup)
                return 4;
            if (gripStyle == HeldGripStyle.Ball)
                return 5;
            return 1;
        }
    }

    /// <summary>
    /// When held on the hand socket, aim the item's forward along the player's view (camera pitch) instead
    /// of a fixed forward — so e.g. a flashlight tilts up/down as you look. Off for items that should just
    /// sit in the hand (key, glowstick). <see cref="FlashlightItem"/> overrides this to true.
    /// </summary>
    public virtual bool HeldAimsAlongView => false;

    public bool IsHeld { get; private set; }
    public bool IsStashed { get; private set; }

    /// <summary>World rigidbody, if any (cached in <see cref="Awake"/> when unassigned in the inspector).</summary>
    public Rigidbody ItemRigidbody => itemRigidbody;

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
    static Sprite s_hudPhKey;
    static Sprite s_hudPhBandage;
    static Sprite s_hudPhStarBall;
    static Sprite s_hudPhRingBlue;
    static Sprite s_hudPhRingGreen;
    static Sprite s_hudPhRingYellow;
    static Sprite s_hudPhEnergyDrink;

    /// <summary>Inspector <see cref="_slotIcon"/> if set; otherwise a simple circular runtime glyph (transparent outside the disk).</summary>
    public Sprite GetEffectiveSlotIconForHud()
    {
        if (_slotIcon != null)
            return _slotIcon;
        return GetPlaceholderForItemType(ResolveTypeForPlaceholder());
    }

    public static Sprite GetPlaceholderSlotIcon(byte typeId)
    {
        return GetPlaceholderForItemType(typeId);
    }

    byte ResolveTypeForPlaceholder()
    {
        if (_itemTypeId != TypeIdNone)
            return _itemTypeId;
        if (GetComponent<FlashlightItem>() != null)
            return TypeIdFlashlight;
        if (GetComponent<GlowstickItem>() != null)
            return TypeIdGlowstick;
        if (GetComponent<KeyItem>() != null)
            return TypeIdKey;
        if (GetComponent<BandageItem>() != null)
            return TypeIdBandage;
        if (GetComponent<EnergyDrinkItem>() != null)
            return TypeIdEnergyDrink;
        if (GetComponent<RingTossItem>() != null)
            return _itemTypeId != TypeIdNone ? _itemTypeId : TypeIdRingBlue;
        if (GetComponent<StarBallItem>() != null)
            return TypeIdStarBall;
        return TypeIdNone;
    }

    static Sprite GetPlaceholderForItemType(byte typeId)
    {
        return typeId switch
        {
            TypeIdFlashlight => s_hudPhFlash ??= CreatePlaceholderSprite(0.95f, 0.9f, 0.5f),
            TypeIdGlowstick => s_hudPhGlow ??= CreatePlaceholderSprite(0.35f, 1f, 0.35f),
            TypeIdKey => KeyItem.SharedHudSlotIcon ?? (s_hudPhKey ??= CreatePlaceholderSprite(0.92f, 0.75f, 0.2f)),
            TypeIdBandage => BandageItem.SharedHudSlotIcon ?? (s_hudPhBandage ??= CreatePlaceholderSprite(0.95f, 0.35f, 0.35f)),
            TypeIdEnergyDrink => EnergyDrinkItem.SharedHudSlotIcon ?? (s_hudPhEnergyDrink ??= CreatePlaceholderSprite(0.2f, 0.95f, 0.85f)),
            TypeIdStarBall => s_hudPhStarBall ??= CreatePlaceholderSprite(0.95f, 0.55f, 0.2f),
            TypeIdRingBlue => s_hudPhRingBlue ??= CreatePlaceholderSprite(0.25f, 0.45f, 0.95f),
            TypeIdRingGreen => s_hudPhRingGreen ??= CreatePlaceholderSprite(0.25f, 0.85f, 0.35f),
            TypeIdRingYellow => s_hudPhRingYellow ??= CreatePlaceholderSprite(0.95f, 0.85f, 0.25f),
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

    /// <summary>
    /// True for items that are spawned NetworkObjects (heavy throwables) rather than the local, seed-built
    /// copies every peer instantiates. Level NetworkObjects are despawned on a section switch, so they can
    /// never be carried across one.
    /// </summary>
    public bool IsNetworkSpawnedItem => _networkObject != null && _networkObject.IsSpawned;

    /// <summary>NetworkObjectId of a spawned network item; 0 for the local, seed-built items.</summary>
    public ulong SpawnedNetworkObjectId => IsNetworkSpawnedItem ? _networkObject.NetworkObjectId : 0UL;

    /// <summary>
    /// Source prefab hash of a spawned network item, for re-creating an identical one after a section switch
    /// destroys this instance (see <see cref="LevelCarryOverStore"/>). False for the local, seed-built items.
    /// </summary>
    public bool TryGetSpawnedNetworkPrefabHash(out uint prefabHash)
    {
        prefabHash = 0;
        if (_networkObject == null || !_networkObject.IsSpawned)
            return false;

        prefabHash = _networkObject.PrefabIdHash;
        return prefabHash != 0;
    }

    /// <summary>
    /// Re-derives this item's id from its (now spawned) NetworkObject immediately, instead of waiting for the
    /// next <see cref="LateUpdate"/>. Every peer derives the same id from the same NetworkObjectId, so calling
    /// this right after a spawn lets the server put the id straight into an inventory slot and lets receivers
    /// resolve that slot on the very same frame.
    /// </summary>
    public void RefreshSpawnedNetworkItemId()
    {
        TryUseSpawnedNetworkObjectId();
    }

    /// <summary>
    /// Detaches this held item from the avatar that is about to be destroyed by a section switch and parks it
    /// in the carry-over pen (see <see cref="LevelCarryOverStore"/>), keeping it alive — and registered under
    /// the same item id — across the scene load. Stays in the inert held/stashed state (no colliders, no
    /// physics, renderers off) until the restored inventory re-seats it on the new avatar through the normal
    /// <see cref="ApplyNetworkHeldState"/> path.
    /// </summary>
    public void PrepareForLevelCarryOver(Transform penRoot)
    {
        if (penRoot == null)
            return;

        StashOverrideParent = null;
        _heldAnchor = null;
        _heldRotationSource = null;
        // The old holder is about to despawn; clearing it stops LateUpdate from chasing a dead holder id.
        _holderNetworkObjectId = 0;
        IsHeld = true;
        IsStashed = true;

        SetCollidersEnabled(false);
        RefreshInventoryVisibility();

        if (itemRigidbody != null)
        {
            itemRigidbody.isKinematic = true;
            itemRigidbody.useGravity = false;
        }

        transform.SetParent(penRoot, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = _authoredLocalScale;
    }

    /// <summary>
    /// For interaction LOS and the in-view grabbable fallback: closest point among all child colliders.
    /// Compound colliders (rings) must not use only <see cref="Component.GetComponentInChildren{T}"/>.
    /// </summary>
    public Vector3 GetInteractAimPointClosestTo(Vector3 worldObserver)
    {
        // Reuse the collider array cached in Awake rather than re-allocating a GetComponentsInChildren
        // result every call — this runs per registered item, per frame in the player's interact-prompt
        // fallback scan.
        Collider[] cols = itemColliders != null && itemColliders.Length > 0
            ? itemColliders
            : GetComponentsInChildren<Collider>(true);
        if (cols == null || cols.Length == 0)
            return transform.position;

        Vector3 best = transform.position;
        float bestSq = float.PositiveInfinity;

        for (int i = 0; i < cols.Length; i++)
        {
            Collider c = cols[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;

            Vector3 pt = c.ClosestPoint(worldObserver);
            float sq = (pt - worldObserver).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = pt;
            }
        }

        return float.IsInfinity(bestSq) ? transform.position : best;
    }

    protected Transform _heldAnchor;
    protected Transform _heldRotationSource;
    protected Quaternion _heldLocalRotation;
    protected ulong _holderNetworkObjectId;
    NetworkObject _networkObject;
    ulong _cachedItemId;
    bool _hasCachedItemId;
    bool _hasExplicitItemId;
    ulong _explicitItemId;
    ulong _lastHashedNetworkObjectId;
    bool _hasHashedNetworkObjectId;
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

    /// <summary>
    /// Resolve the exact item the client aimed at for a pickup. Item ids are deterministic across peers
    /// (network-object id, or a stable hierarchy-path hash), so a pickup must resolve to that specific id
    /// or fail — we intentionally do NOT fall back to the nearest registered item. That fallback used to
    /// hand the player whatever unheld item was closest to the client-supplied hint <b>of any type</b>, so
    /// two players lunging for the same pickup could leave the loser holding an unrelated item (e.g. the
    /// key instead of the contested glowstick), because the exact-id lookup fails the moment the other
    /// player's grab flips <see cref="IsHeld"/>. Failing is the correct outcome there: the aimed item is
    /// gone/held, the client gets no pickup and can simply try again. <paramref name="hintPosition"/> is
    /// retained for signature stability but no longer used.
    /// </summary>
    public static bool TryResolveForPickup(ulong itemId, Vector3 hintPosition, out GrabbableInventoryItem item)
    {
        if (TryGetRegistered(itemId, out item) && item != null && !item.IsHeld)
            return true;

        item = null;
        return false;
    }

    public static bool TryResolveForState(ulong itemId, Vector3 hintPosition, out GrabbableInventoryItem item)
    {
        if (TryGetRegistered(itemId, out item) && item != null)
            return true;

        return TryFindNearestRegistered(hintPosition, null, out item);
    }

    public static bool TryResolveForStateByType(ulong itemId, Vector3 hintPosition, byte itemTypeId, out GrabbableInventoryItem item)
    {
        if (TryGetRegistered(itemId, out item) && item != null)
        {
            if (itemTypeId == TypeIdNone || item.ItemTypeId == itemTypeId)
                return true;
        }

        return TryFindNearestRegisteredByType(hintPosition, null, itemTypeId, out item);
    }

    public void AssignNetworkItemId(ulong itemId)
    {
        if (itemId == 0UL)
            return;

        UnregisterCurrentItemId();
        _explicitItemId = itemId;
        _hasExplicitItemId = true;
        _cachedItemId = itemId;
        _hasCachedItemId = true;
        Registered[itemId] = this;
        SuppressIfConsumedGhost(itemId);
    }

    protected virtual void Awake()
    {
        RebuildCachedHoldRotation();
        CacheIdentityHint();
        _authoredLocalScale = transform.localScale;
        _networkObject = GetComponent<NetworkObject>();

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
        TryUseSpawnedNetworkObjectId();
        Registered[ItemId] = this;
        SuppressIfConsumedGhost(ItemId);
    }

    protected void OnDisable()
    {
        UnregisterCurrentItemId();
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
        TryUseSpawnedNetworkObjectId();

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

    /// <summary>
    /// Aligns this held item so its right-hand grip sits exactly on the avatar's hand socket. Called by
    /// HeldItemHandSocketFollow after animation, IK and view bob have finalized the hand pose, so the item
    /// rides the animated hand with zero lag and the wrist keeps its authored (natural) orientation.
    /// </summary>
    public void ApplyHandSocketHeldPose(Transform handSocket)
    {
        if (handSocket == null)
            return;

        Quaternion gripLocalRotation = gripPointRight != null ? gripPointRight.localRotation : Quaternion.identity;
        Vector3 gripLocalPosition = gripPointRight != null
            ? Vector3.Scale(gripPointRight.localPosition, transform.localScale)
            : Vector3.zero;

        Quaternion itemRotation = handSocket.rotation * Quaternion.Inverse(gripLocalRotation) * Quaternion.Euler(heldRotationOffsetEuler);
        Vector3 itemPosition = handSocket.position - itemRotation * gripLocalPosition;
        transform.SetPositionAndRotation(itemPosition, itemRotation);
    }

    /// <summary>
    /// Like <see cref="ApplyHandSocketHeldPose"/> but the item's forward (its light/barrel) is locked to a
    /// fixed world aim instead of the hand's rotation — so a flashlight always points forward while the arm
    /// and wrist can be posed freely. Position still seats the grip point at the socket (the fist).
    /// </summary>
    public void ApplyHandSocketHeldPoseForwardAim(Transform handSocket, Vector3 worldAim, Vector3 worldUp)
    {
        if (handSocket == null || worldAim.sqrMagnitude < 1e-6f)
            return;

        Quaternion gripLocalRotation = gripPointRight != null ? gripPointRight.localRotation : Quaternion.identity;
        Vector3 gripLocalPosition = gripPointRight != null
            ? Vector3.Scale(gripPointRight.localPosition, transform.localScale)
            : Vector3.zero;

        Quaternion itemRotation = Quaternion.LookRotation(worldAim.normalized, worldUp) * Quaternion.Inverse(gripLocalRotation) * Quaternion.Euler(heldRotationOffsetEuler);
        Vector3 itemPosition = handSocket.position - itemRotation * gripLocalPosition;
        transform.SetPositionAndRotation(itemPosition, itemRotation);
    }

    /// <summary>
    /// Seats the grip at the socket while orienting the item so its light/barrel matches a full world
    /// rotation (e.g. the camera-pitch transform) — used for view-aimed items like the flashlight so the
    /// mesh tilts up/down with the look direction, matching the beam.
    /// </summary>
    public void ApplyHandSocketHeldPoseAim(Transform handSocket, Quaternion lightWorldRotation)
    {
        if (handSocket == null)
            return;

        Quaternion gripLocalRotation = gripPointRight != null ? gripPointRight.localRotation : Quaternion.identity;
        Vector3 gripLocalPosition = gripPointRight != null
            ? Vector3.Scale(gripPointRight.localPosition, transform.localScale)
            : Vector3.zero;

        Quaternion itemRotation = lightWorldRotation * Quaternion.Inverse(gripLocalRotation) * Quaternion.Euler(heldRotationOffsetEuler);
        Vector3 itemPosition = handSocket.position - itemRotation * gripLocalPosition;
        transform.SetPositionAndRotation(itemPosition, itemRotation);
    }

    /// <summary>
    /// Where a hand should grip this held item, in world space, computed from *current-frame* anchor and
    /// camera-pitch transforms. The item's own transform is written in LateUpdate and is one frame stale
    /// during the animator's IK pass, so hand IK must re-derive the pose from the same inputs
    /// <see cref="UpdateHeldTransform"/> uses instead of reading the grip transform directly.
    /// </summary>
    public bool TryComputeHeldGripWorldPose(bool leftHand, out Vector3 worldPosition, out Quaternion worldRotation)
    {
        worldPosition = default;
        worldRotation = Quaternion.identity;
        if (!IsHeld || IsStashed || _heldAnchor == null)
            return false;

        Transform rotationSource = _heldRotationSource != null ? _heldRotationSource : _heldAnchor;
        Quaternion itemRotation = alignRotationWithFollow
            ? rotationSource.rotation * _heldLocalRotation
            : _heldAnchor.rotation * _heldLocalRotation;
        Vector3 itemPosition = _heldAnchor.TransformPoint(heldLocalPosition);

        Transform grip = leftHand ? gripPointLeft : gripPointRight;
        if (grip == null)
        {
            // Right hand can target the item body itself; a missing left grip means no left-hand IK.
            worldPosition = itemPosition;
            worldRotation = itemRotation;
            return !leftHand;
        }

        worldPosition = itemPosition + itemRotation * Vector3.Scale(grip.localPosition, transform.localScale);
        worldRotation = itemRotation * grip.localRotation;
        return true;
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

    static bool TryFindNearestRegisteredByType(Vector3 hintPosition, bool? requireHeldState, byte itemTypeId, out GrabbableInventoryItem item)
    {
        const float maxMatchDistance = 8f;
        item = null;
        float bestDistanceSquared = maxMatchDistance * maxMatchDistance;

        foreach (GrabbableInventoryItem candidate in Registered.Values)
        {
            if (candidate == null)
                continue;

            if (itemTypeId != TypeIdNone && candidate.ItemTypeId != itemTypeId)
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
        if (_hasExplicitItemId)
            return _explicitItemId;

        if (_networkObject != null && _networkObject.IsSpawned)
            return ComputeHash($"network-object:{_networkObject.NetworkObjectId}");

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

        return ComputeHash(builder.ToString());
    }

    static ulong ComputeHash(string key)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = fnvOffset;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    void TryUseSpawnedNetworkObjectId()
    {
        if (_hasExplicitItemId)
            return;

        if (_networkObject == null)
            _networkObject = GetComponent<NetworkObject>();
        if (_networkObject == null || !_networkObject.IsSpawned)
            return;

        // Called every LateUpdate for network-spawned items. Skip the interpolated-string hash entirely
        // when the NetworkObjectId hasn't changed (the common case) instead of allocating a string per frame.
        ulong netId = _networkObject.NetworkObjectId;
        if (_hasCachedItemId && _hasHashedNetworkObjectId && _lastHashedNetworkObjectId == netId)
            return;

        ulong networkItemId = ComputeHash($"network-object:{netId}");
        _lastHashedNetworkObjectId = netId;
        _hasHashedNetworkObjectId = true;
        if (_hasCachedItemId && _cachedItemId == networkItemId)
            return;

        UnregisterCurrentItemId();
        _cachedItemId = networkItemId;
        _hasCachedItemId = true;
        Registered[networkItemId] = this;
        SuppressIfConsumedGhost(networkItemId);
    }

    /// <summary>
    /// Client-side ghost suppression. Consumable world items (chest loot, seed pickups) are plain LOCAL
    /// copies rebuilt identically on every peer from the deterministic seed. If a copy was already consumed
    /// this level, a late joiner would otherwise rebuild a dead ghost of it (an interactable whose pickup
    /// fails server-side). Destroy ourselves when the replicated <see cref="ConsumedItemNetworkStore"/> says
    /// this id is consumed. No-op on the server/host (it destroyed the real item and never rebuilds it) and
    /// offline. Ids are unique per level and tombstones are cleared each build, so this never hits a
    /// legitimate item.
    /// </summary>
    void SuppressIfConsumedGhost(ulong itemId)
    {
        if (itemId == 0UL)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || nm.IsServer)
            return;

        if (ConsumedItemNetworkStore.IsConsumed(itemId))
            Destroy(gameObject);
    }

    void UnregisterCurrentItemId()
    {
        if (!_hasCachedItemId)
            return;

        if (Registered.TryGetValue(_cachedItemId, out GrabbableInventoryItem existing) && existing == this)
            Registered.Remove(_cachedItemId);
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
