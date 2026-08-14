using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Why a purchase request was refused (or that it went through). Sent back to the buyer only.</summary>
public enum CarnivalStorePurchaseResult : byte
{
    Granted = 0,
    /// <summary>The wallet could not cover the price.</summary>
    NotEnoughTickets = 1,
    /// <summary>The buyer was too far from the counter when the server processed the request.</summary>
    OutOfRange = 2,
    /// <summary>Store/item could not be resolved on the authority (bad index, store not built yet, no session).</summary>
    Unavailable = 3,
    /// <summary>A one-per-player upgrade this buyer already owns. Nothing was charged.</summary>
    AlreadyOwned = 4,
}

/// <summary>What a stock row actually hands over when bought.</summary>
public enum CarnivalStoreGrant : byte
{
    /// <summary>Builds the row's prefab on the counter for someone to pick up (the default for goods).</summary>
    DispenseItem = 0,
    /// <summary>
    /// Unlocks the buyer's 4th hotbar slot. Nothing is dispensed and nothing is recorded in the sale list —
    /// the upgrade lives on the player's own <see cref="NetworkPlayerInventory"/>, which already replicates
    /// and already survives respawns and section switches. One per player, enforced on the server.
    /// </summary>
    ExtraInventorySlot = 1,
}

/// <summary>
/// One row of stock on a <see cref="CarnivalStore"/> counter. Authored on the Store prefab, so the roster and
/// prices are tuned in the inspector rather than in code.
/// </summary>
[Serializable]
public struct CarnivalStoreStockEntry
{
    [Tooltip("Name shown in the shop UI. Falls back to the prefab name when empty.")]
    public string displayName;

    [Tooltip("What buying this row hands over: goods on the counter, or a one-per-player upgrade.")]
    public CarnivalStoreGrant grant;

    [Tooltip("Item prefab dispensed onto the counter on purchase. Must carry a GrabbableInventoryItem. " +
             "Ignored (and may be empty) for upgrade rows.")]
    public GameObject prefab;

    [Tooltip("Optional shop icon. Leave empty to use the item's own HUD hotbar icon.")]
    public Sprite icon;

    [Tooltip("Ticket price.")]
    [Min(0)] public int price;

    [Tooltip("Stack size to dispense for stackable items (glowsticks, flare rounds). 0 = a single unit.")]
    [Min(0)] public int stackCount;
}

/// <summary>
/// The carnival prize counter ("REDEEM TICKETS"): players interact with it to open a shop UI and spend the
/// tickets they won at the booths (<see cref="NetworkPlayerCarnivalTickets"/>) on real inventory items.
///
/// Like the radio and the jail skeleton, this prop is nested inside the deterministically-placed carnival room
/// prefab, so it is NOT Netcode-spawned and cannot own NetworkVariables or RPCs of its own. Purchases therefore
/// route through <see cref="CarnivalStoreNetworkStore"/> (hosted on the spawned DoorStateStore infrastructure
/// object), which is the authority: it validates range, debits the buyer's wallet, and appends the sale to a
/// replicated list. Every peer — including late joiners — reads that list and builds the sold item LOCALLY on
/// the counter with a stable, deterministic item id, exactly like maze/chest loot. Nothing about the goods
/// themselves crosses the wire, and the existing pickup/consume stores handle them from there.
///
/// Stock is unlimited; tickets are the scarce resource.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalStore : MonoBehaviour
{
    /// <summary>Dispense anchors are direct children whose name starts with this.</summary>
    const string DispenseAnchorNamePrefix = "DispenseAnchor";

    [Header("Networking")]
    [Tooltip("Stable id used to address this counter in purchase RPCs. Each store placed in a level needs a UNIQUE id.")]
    [SerializeField] int storeId = 1;

    [Header("Stock")]
    [Tooltip("What this counter sells, top to bottom in the shop UI.")]
    [SerializeField] CarnivalStoreStockEntry[] stock = Array.Empty<CarnivalStoreStockEntry>();

    [Header("Interaction")]
    [Tooltip("How close the player must stand to open the shop (and keep it open). Measured to the counter's centre.")]
    [SerializeField, Min(0.5f)] float interactMaxDistance = 4f;
    [SerializeField] string interactPromptMessage = "Press E to redeem tickets";

    [Header("Audio")]
    [Tooltip("One-shot played at the counter on every peer when an item is dispensed.")]
    [SerializeField] AudioClip dispenseClip;
    [SerializeField, Range(0f, 1f)] float dispenseVolume = 0.55f;

    // Stores register themselves so the network store can resolve them by id (they aren't Netcode-spawned).
    static readonly Dictionary<int, CarnivalStore> s_registry = new();

    Transform[] _dispenseAnchors;
    Vector3 _anchorPosition;
    bool _anchorCached;
    AudioSource _sfx;
    /// <summary>Offline (no session) sale counter; online the authority owns the sequence.</summary>
    int _offlineSaleSeq;

    public int StoreId => storeId;
    public int StockCount => stock != null ? stock.Length : 0;
    public float InteractMaxDistance => interactMaxDistance;
    public string InteractPromptMessage => interactPromptMessage;

    public static bool TryResolve(int id, out CarnivalStore store) =>
        s_registry.TryGetValue(id, out store) && store != null;

    /// <summary>Centre of the booth's renderers — the root pivot of a scaled prop is not a reliable stand-in.</summary>
    public Vector3 AnchorPosition
    {
        get
        {
            if (!_anchorCached)
            {
                _anchorPosition = transform.position;
                Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
                if (renderers != null && renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        bounds.Encapsulate(renderers[i].bounds);
                    _anchorPosition = bounds.center;
                }

                _anchorCached = true;
            }

            return _anchorPosition;
        }
    }

    void OnEnable()
    {
        // Register early (before the network store spawns) so replicated sales can resolve this counter.
        s_registry[storeId] = this;
    }

    void OnDisable()
    {
        if (s_registry.TryGetValue(storeId, out CarnivalStore current) && current == this)
            s_registry.Remove(storeId);
    }

    void Start()
    {
        // Built after the store already synced (late joiner): rebuild any sales made before we existed.
        CarnivalStoreNetworkStore.ApplyCurrentSalesToStore(this);
    }

    public bool TryGetStock(int index, out CarnivalStoreStockEntry entry)
    {
        if (stock == null || index < 0 || index >= stock.Length)
        {
            entry = default;
            return false;
        }

        entry = stock[index];
        return true;
    }

    /// <summary>Display name for a row, falling back to the prefab name so an unnamed entry is never blank.</summary>
    public string GetDisplayName(int index)
    {
        if (!TryGetStock(index, out CarnivalStoreStockEntry entry))
            return string.Empty;
        if (!string.IsNullOrWhiteSpace(entry.displayName))
            return entry.displayName;
        return entry.prefab != null ? entry.prefab.name : "?";
    }

    /// <summary>
    /// Icon for a shop tile: the authored override if there is one, else the item's own HUD hotbar icon (which
    /// itself falls back to a generated glyph), so a row is never a blank tile. Read straight off the prefab.
    /// </summary>
    public Sprite GetIcon(int index)
    {
        if (!TryGetStock(index, out CarnivalStoreStockEntry entry))
            return null;
        if (entry.icon != null)
            return entry.icon;
        if (entry.grant == CarnivalStoreGrant.ExtraInventorySlot)
            return ExtraSlotIcon();
        if (entry.prefab == null)
            return null;

        GrabbableInventoryItem grabbable = entry.prefab.GetComponent<GrabbableInventoryItem>();
        return grabbable != null ? grabbable.GetEffectiveSlotIconForHud() : null;
    }

    /// <summary>True when the given row is a one-per-player upgrade this player already has.</summary>
    public bool IsAlreadyOwnedBy(PlayerController player, int index)
    {
        if (player == null || !TryGetStock(index, out CarnivalStoreStockEntry entry))
            return false;
        return entry.grant == CarnivalStoreGrant.ExtraInventorySlot && player.HasExtraInventorySlot;
    }

    static Sprite s_extraSlotIcon;

    /// <summary>
    /// Shop glyph for the slot upgrade: three filled hotbar boxes and a dashed empty fourth, drawn to match
    /// the HUD row it adds to. Generated rather than authored so the row needs no art to ship.
    /// </summary>
    static Sprite ExtraSlotIcon()
    {
        if (s_extraSlotIcon != null)
            return s_extraSlotIcon;

        const int w = 128;
        const int h = 96;
        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 bone = new Color32(230, 225, 211, 255);
        Color32 amber = new Color32(217, 138, 78, 255);
        Color32[] px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = clear;

        // Four boxes in a 2x2 block; the fourth (bottom-right) is the bought one and is drawn hollow/amber.
        int boxW = 52, boxH = 38, gap = 8;
        int originX = (w - (boxW * 2 + gap)) / 2;
        int originY = (h - (boxH * 2 + gap)) / 2;
        for (int b = 0; b < 4; b++)
        {
            int bx = originX + (b % 2) * (boxW + gap);
            int by = originY + (b / 2) * (boxH + gap);
            bool isNewSlot = b == 3;
            Color32 tint = isNewSlot ? amber : bone;
            for (int y = 0; y < boxH; y++)
            {
                for (int x = 0; x < boxW; x++)
                {
                    bool edge = x < 3 || x >= boxW - 3 || y < 3 || y >= boxH - 3;
                    // The new slot reads as an outline with a dashed edge; the owned three are solid plates.
                    if (isNewSlot)
                    {
                        if (!edge)
                            continue;
                        if (((x + y) / 5) % 2 == 1)
                            continue;
                    }
                    else if (!edge && ((x + y) % 2 == 1))
                    {
                        // subtle hatch inside the solid boxes so they don't read as flat blocks
                        continue;
                    }

                    px[(by + y) * w + (bx + x)] = tint;
                }
            }
        }

        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        tex.SetPixels32(px);
        tex.Apply();
        s_extraSlotIcon = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        return s_extraSlotIcon;
    }

    public bool IsInInteractRange(Vector3 worldPosition, float extraSlack = 0f)
    {
        float max = interactMaxDistance + Mathf.Max(0f, extraSlack);
        return (AnchorPosition - worldPosition).sqrMagnitude <= max * max;
    }

    // ---- Local player entry points ---------------------------------------------------------------

    /// <summary>The counter offers a shop while the player is close enough and no other modal overlay owns the screen.</summary>
    public bool CanOfferShop(Vector3 viewerPosition)
    {
        if (!isActiveAndEnabled || StockCount == 0)
            return false;
        if (CarnivalStoreOverlayController.IsInteractive)
            return false;
        return IsInInteractRange(viewerPosition);
    }

    /// <summary>Player pressed E while aiming at the counter.</summary>
    public void RequestShopInteract(PlayerController player)
    {
        if (player == null || !CanOfferShop(player.transform.position))
            return;

        CarnivalStoreOverlayController.Show(player, this);
    }

    /// <summary>
    /// Local player clicked BUY. Online this is a request the server adjudicates; offline (dev scenes, where no
    /// wallet exists to debit) this peer is the authority and dispenses straight away.
    /// </summary>
    public void RequestPurchase(PlayerController player, int itemIndex)
    {
        if (player == null || !TryGetStock(itemIndex, out CarnivalStoreStockEntry entry))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool online = nm != null && nm.IsListening && CarnivalStoreNetworkStore.Instance != null;

        if (online)
        {
            CarnivalStoreNetworkStore.RequestPurchase(storeId, itemIndex);
            return;
        }

        // Offline (dev scenes): this peer is the authority. There is no wallet to debit, but the
        // one-per-player rule still holds so the UI behaves the same as it will online.
        if (entry.grant == CarnivalStoreGrant.ExtraInventorySlot)
        {
            if (!player.HasExtraInventorySlot)
                player.GrantLocalExtraInventorySlot();
            return;
        }

        ApplyDispense(_offlineSaleSeq++, itemIndex);
    }

    // ---- Dispensing (runs on EVERY peer, driven by the replicated sale list) ----------------------

    /// <summary>
    /// Builds the sold item on the counter. Called on every peer for every replicated sale (and again on late
    /// joiners when they sync the list), so it is idempotent: a sale whose item already exists is skipped.
    /// </summary>
    public void ApplyDispense(int saleSequence, int itemIndex)
    {
        if (!TryGetStock(itemIndex, out CarnivalStoreStockEntry entry) || entry.prefab == null)
            return;
        // Upgrade rows hand over a player-side unlock, not goods — there is nothing to build on the counter.
        if (entry.grant != CarnivalStoreGrant.DispenseItem)
            return;

        ulong itemId = ComputeSoldItemId(storeId, saleSequence);
        if (GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem existing) && existing != null)
            return;

        Transform anchor = ResolveDispenseAnchor(saleSequence);
        Vector3 position = anchor != null ? anchor.position : AnchorPosition;
        float yaw = ResolveDispenseYaw(anchor);

        // No parent: the counter prop is authored at a large uniform scale, so parenting the item to it (or to any
        // of its children) would inherit that scale. Plain scene-root instances behave like all other maze loot.
        GameObject instance = UnityEngine.Object.Instantiate(entry.prefab, position, Quaternion.identity);
        StandOnCounter(instance, entry.prefab, position, yaw);

        if (instance.TryGetComponent(out GrabbableInventoryItem grabbable))
        {
            grabbable.AssignNetworkItemId(itemId);
            if (entry.stackCount > 1 && grabbable.IsStackable)
                grabbable.SetStackCount(entry.stackCount);
        }

        ApplyAtRest(instance);
        PlayDispenseSfx();
    }

    /// <summary>
    /// Which way a dispensed item faces, as a pure yaw. Read off the anchor's forward flattened onto the floor
    /// rather than <c>eulerAngles.y</c>: the booth prop is authored Z-up (a -90° X on its root), so the euler
    /// decomposition of anything under it does not give the heading you see in the level.
    /// </summary>
    float ResolveDispenseYaw(Transform anchor)
    {
        Vector3 facing = anchor != null ? anchor.forward : -transform.up;
        facing.y = 0f;

        if (facing.sqrMagnitude < 0.0001f)
        {
            // Anchor points straight up/down: fall back to the booth's own outward direction (its authored -up).
            facing = -transform.up;
            facing.y = 0f;
        }

        if (facing.sqrMagnitude < 0.0001f)
            return 0f;

        return Quaternion.LookRotation(facing.normalized, Vector3.up).eulerAngles.y;
    }

    /// <summary>
    /// Sits the item upright on the counter surface. Item prefabs are not authored at identity (the EnergyDrink
    /// root carries a -90° X so the can stands), and their pivots sit at the mesh centre, so "upright" means the
    /// prefab's own rotation under the anchor's yaw, lifted until the bottom of its bounds meets the anchor.
    /// Mirrors <c>ProceduralMazeCoordinator.StandPickupOnMarker</c>.
    /// </summary>
    static void StandOnCounter(GameObject instance, GameObject prefab, Vector3 surfacePoint, float yawDegrees)
    {
        Transform root = instance.transform;
        root.SetPositionAndRotation(surfacePoint, Quaternion.Euler(0f, yawDegrees, 0f) * prefab.transform.rotation);

        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        Bounds bounds = new(root.position, Vector3.zero);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            // Only solid geometry defines where the item's bottom is — an idle particle system (the flare gun's
            // muzzle flash) or a renderer on an inactive child reports zero-size bounds at the world origin and
            // would drag the fit down by the counter's entire world height.
            if (renderer is ParticleSystemRenderer)
                continue;

            Bounds rendererBounds = renderer.bounds;
            if (rendererBounds.size == Vector3.zero)
                continue;

            if (!hasBounds)
            {
                bounds = rendererBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(rendererBounds);
            }
        }

        if (!hasBounds)
            return;

        float lift = surfacePoint.y - bounds.min.y;
        root.position += new Vector3(0f, lift, 0f);
    }

    /// <summary>Sold goods wait on the counter until grabbed (kinematic, no gravity), matching chest/maze loot.</summary>
    static void ApplyAtRest(GameObject root)
    {
        Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody rb = bodies[i];
            if (rb == null)
                continue;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }

        // Freezing once is not enough for an item nobody touches — MazeItemPickupRest holds the pose until pickup.
        if (bodies.Length > 0 && root.GetComponent<MazeItemPickupRest>() == null)
            root.AddComponent<MazeItemPickupRest>();
    }

    Transform ResolveDispenseAnchor(int saleSequence)
    {
        CacheDispenseAnchors();
        if (_dispenseAnchors == null || _dispenseAnchors.Length == 0)
            return null;

        // Spread consecutive sales along the counter so two purchases in a row don't land on top of each other.
        int index = (int)((uint)saleSequence % (uint)_dispenseAnchors.Length);
        return _dispenseAnchors[index];
    }

    void CacheDispenseAnchors()
    {
        if (_dispenseAnchors != null)
            return;

        List<Transform> found = new();
        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t != null && t != transform && t.name.StartsWith(DispenseAnchorNamePrefix, StringComparison.Ordinal))
                found.Add(t);
        }

        // Ordinal name sort keeps the anchor order identical on every peer regardless of hierarchy iteration.
        found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        _dispenseAnchors = found.ToArray();

        if (_dispenseAnchors.Length == 0)
        {
            Debug.LogWarning(
                $"{nameof(CarnivalStore)} on '{name}' has no '{DispenseAnchorNamePrefix}' children; purchases will "
                + "be dispensed at the booth centre.",
                this);
        }
    }

    /// <summary>Stable per-sale item id, derived identically on every peer (same model as chest/maze loot ids).</summary>
    public static ulong ComputeSoldItemId(int storeId, int saleSequence)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        string key = $"store-item:{storeId}:{saleSequence}";
        ulong hash = fnvOffset;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    void PlayDispenseSfx()
    {
        if (dispenseClip == null)
            return;

        if (_sfx == null)
        {
            _sfx = GetComponent<AudioSource>();
            if (_sfx == null)
                _sfx = gameObject.AddComponent<AudioSource>();
            _sfx.playOnAwake = false;
            _sfx.loop = false;
            _sfx.spatialBlend = 1f;
            _sfx.dopplerLevel = 0f;
            _sfx.rolloffMode = AudioRolloffMode.Linear;
            _sfx.minDistance = 1.5f;
            _sfx.maxDistance = 20f;
        }

        GameAudioManager.RouteSfxSource(_sfx);
        _sfx.PlayOneShot(dispenseClip, Mathf.Clamp01(dispenseVolume));
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.55f, 0.2f, 0.5f);
        Gizmos.DrawWireSphere(AnchorPosition, interactMaxDistance);

        Gizmos.color = new Color(0.9f, 0.8f, 0.3f, 0.9f);
        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null || t == transform || !t.name.StartsWith(DispenseAnchorNamePrefix, StringComparison.Ordinal))
                continue;
            Gizmos.DrawWireCube(t.position, new Vector3(0.25f, 0.02f, 0.25f));
        }
    }
#endif
}
