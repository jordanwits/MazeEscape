using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Purely-visual, non-networked renderer for a blackjack table. Runs on every peer and rebuilds in-world card
/// sprites from the replicated <see cref="BlackjackGameController"/> state whenever it changes. Cards lie FLAT on
/// the felt and slide out from the <see cref="cardHolderAnchor"/> (the dealer's shoe) when dealt. The dealer's
/// hidden hole card shows a face-down back until the server reveals it. Card visuals carry no colliders so they
/// never block the interact raycast.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackTableView : MonoBehaviour
{
    const int DealerPoolIndex = BlackjackConfig.SeatCount; // pools[4] == dealer

    [SerializeField] BlackjackGameController controller;
    [SerializeField] BlackjackCardSprites sprites;

    [Header("Anchors (authored flat on the felt; +X = fan direction)")]
    [SerializeField] Transform[] seatCardAnchors = new Transform[BlackjackConfig.SeatCount];
    [SerializeField] Transform dealerCardAnchor;
    [Tooltip("The shoe / card holder cards are dealt FROM (deal animation origin).")]
    [SerializeField] Transform cardHolderAnchor;

    [Header("Card layout")]
    [SerializeField] float cardScale = 0.32f;
    [SerializeField] float cardSpacing = 0.26f;   // along the anchor's local +X
    [SerializeField] float stackHeight = 0.004f;  // tiny lift per card (anchor local +Z = world up) to avoid z-fighting
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] int baseSortingOrder = 100;

    [Header("Deal animation")]
    [SerializeField] float dealDuration = 0.32f;
    [SerializeField] float dealStagger = 0.07f;   // delay between cards in the same hand
    [SerializeField] float recenterDuration = 0.15f;
    [SerializeField] float flipDuration = 0.45f;  // dealer hole-card reveal flip

    [Header("Audio")]
    [Tooltip("Played as each card slides out from the shoe and when the dealer's hole card flips face-up.")]
    [SerializeField] AudioClip cardDealClip;
    [SerializeField, Range(0f, 1f)] float cardDealVolume = 1f;

    [Header("Hand total labels (floating above each hand)")]
    [SerializeField] bool showHandTotals = true;
    [SerializeField] float totalCharacterSize = 0.024f;
    [SerializeField] float totalLiftY = 0.06f;       // world up, off the felt
    [SerializeField] float totalUpScreen = 0.05f;    // toward the dealer side (up on the seat camera)
    [SerializeField] Color totalColor = new(1f, 0.97f, 0.85f, 1f);

    sealed class CardVisual
    {
        public SpriteRenderer sr;
        public bool inUse;
        public byte card;
        public bool faceDown;
        public Vector3 fromPos, toPos;
        public Quaternion fromRot, toRot;
        public float t, dur, delay;
        public bool arrived;
        public bool flipping;
        public float flipT;
        public byte flipToCard;
        public bool flipSwapped;
        public bool needsDealSound; // play the deal SFX when this freshly-dealt card starts sliding
    }

    struct CardEntry
    {
        public byte card;
        public bool faceDown;
    }

    readonly List<CardVisual>[] _pools = new List<CardVisual>[BlackjackConfig.SeatCount + 1];
    readonly TextMesh[] _totals = new TextMesh[BlackjackConfig.SeatCount + 1];
    readonly List<CardEntry> _entries = new(16);
    bool _subscribed;
    AudioSource _audio;
    bool _firstRebuildDone; // suppress deal SFX for the cards present on the very first state sync (e.g. late join)

    void Awake()
    {
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
        for (int i = 0; i < _pools.Length; i++)
            _pools[i] = new List<CardVisual>();
        EnsureAudio();
    }

    void EnsureAudio()
    {
        if (_audio != null)
            return;
        _audio = GetComponent<AudioSource>();
        if (_audio == null)
            _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.loop = false;
        _audio.spatialBlend = 1f;
        _audio.minDistance = 1f;
        _audio.maxDistance = 45f;
        _audio.rolloffMode = AudioRolloffMode.Linear;
        GameAudioManager.RouteSfxSource(_audio);
    }

    void PlayCardSfx()
    {
        if (cardDealClip == null)
            return;
        EnsureAudio();
        _audio.PlayOneShot(cardDealClip, Mathf.Clamp01(cardDealVolume));
    }

    void OnEnable()
    {
        TrySubscribe();
        Rebuild();
    }

    void OnDisable()
    {
        if (controller != null && _subscribed)
        {
            controller.StateChanged -= Rebuild;
            _subscribed = false;
        }
    }

    void TrySubscribe()
    {
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
        if (controller != null && !_subscribed)
        {
            controller.StateChanged += Rebuild;
            _subscribed = true;
        }
    }

    void Update()
    {
        if (!_subscribed)
        {
            TrySubscribe();
            Rebuild();
        }
        AnimateCards();
    }

    void Rebuild()
    {
        if (controller == null || sprites == null)
            return;

        int seats = controller.ActiveSeatCount;
        for (int i = 0; i < BlackjackConfig.SeatCount; i++)
        {
            Transform anchor = (seatCardAnchors != null && i < seatCardAnchors.Length) ? seatCardAnchors[i] : null;
            _entries.Clear();
            bool hasCards = i < seats && anchor != null && controller.GetSeat(i).Cards.Length > 0;
            if (i < seats && anchor != null)
            {
                SeatState s = controller.GetSeat(i);
                AppendCards(s.Cards, false);
            }
            SyncAnchor(i, anchor, _entries);
            UpdateTotal(i, anchor, hasCards, hasCards ? controller.SeatTotal(i, out _, out _) : 0);
        }

        // Dealer
        _entries.Clear();
        DealerState d = controller.Dealer;
        AppendCards(d.Cards, false);
        if (d.HoleHidden == 1)
            _entries.Add(new CardEntry { card = BlackjackCard.None, faceDown = true });
        SyncAnchor(DealerPoolIndex, dealerCardAnchor, _entries);
        bool dealerHas = dealerCardAnchor != null && d.Cards.Length > 0;
        UpdateTotal(DealerPoolIndex, dealerCardAnchor, dealerHas, dealerHas ? controller.DealerVisibleTotal() : 0);

        // OnEnable paints once before the spawn deserializes any state, so that pass sees an empty table and must
        // not spend the suppression — the first post-spawn repaint is the one carrying a late joiner's live hand.
        if (controller.IsSpawned)
            _firstRebuildDone = true;
    }

    void UpdateTotal(int idx, Transform anchor, bool show, int total)
    {
        if (!showHandTotals || anchor == null)
            show = false;

        TextMesh tm = _totals[idx];
        if (!show)
        {
            if (tm != null)
                tm.gameObject.SetActive(false);
            return;
        }

        if (tm == null)
            tm = _totals[idx] = CreateTotalLabel();

        tm.text = total.ToString();
        tm.color = totalColor;
        tm.characterSize = totalCharacterSize;
        // Float above the hand: lifted off the felt + nudged toward the dealer (up on the seat camera).
        tm.transform.SetPositionAndRotation(
            anchor.position + Vector3.up * totalLiftY + anchor.up * totalUpScreen,
            anchor.rotation);
        tm.gameObject.SetActive(true);
    }

    TextMesh CreateTotalLabel()
    {
        GameObject go = new("HandTotal");
        go.transform.SetParent(transform, false);
        // Mirror on X (negative scale) for the same reason the card sprites use flipX — the seat camera
        // faces -Z, so flat text would otherwise read backwards.
        go.transform.localScale = new Vector3(-1f, 1f, 1f);
        TextMesh tm = go.AddComponent<TextMesh>();
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.fontSize = 64;
        tm.characterSize = totalCharacterSize;
        tm.color = totalColor;

        // Rebind the built-in font + its material so glyphs render (the blank-quad gotcha, see CarnivalWorldNumberDisplay).
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (f != null)
        {
            tm.font = f;
            if (mr != null && f.material != null)
                mr.sharedMaterial = f.material;
        }
        if (mr != null)
        {
            mr.sortingLayerName = sortingLayerName;
            mr.sortingOrder = baseSortingOrder + 500; // draw above the cards
        }
        return tm;
    }

    void AppendCards(FixedList32Bytes<byte> cards, bool faceDown)
    {
        for (int i = 0; i < cards.Length; i++)
            _entries.Add(new CardEntry { card = cards[i], faceDown = faceDown });
    }

    void SyncAnchor(int poolIndex, Transform anchor, List<CardEntry> entries)
    {
        List<CardVisual> pool = _pools[poolIndex];
        int n = anchor != null ? entries.Count : 0;

        Vector3 dealFromPos = cardHolderAnchor != null ? cardHolderAnchor.position : (anchor != null ? anchor.position : transform.position);
        Quaternion dealFromRot = anchor != null ? anchor.rotation : transform.rotation;

        for (int i = 0; i < n; i++)
        {
            float fanX = (i - (n - 1) * 0.5f) * cardSpacing;
            Vector3 localOffset = new(fanX, 0f, i * stackHeight);
            Vector3 targetPos = anchor.TransformPoint(localOffset);
            Quaternion targetRot = anchor.rotation;

            CardEntry e = entries[i];

            CardVisual v = EnsureVisual(pool, i, out bool isNew);
            // Seat cameras face the dealer (-Z), so the camera's screen-right is world -X while the flat
            // card's right is +X — without this the cards read mirrored. flipX cancels that.
            v.sr.flipX = true;
            v.sr.sortingLayerName = sortingLayerName;
            v.sr.sortingOrder = baseSortingOrder + poolIndex * 20 + i;
            v.sr.gameObject.SetActive(true);

            if (isNew)
            {
                // Deal it: start at the shoe and slide to the slot.
                v.sr.sprite = e.faceDown ? sprites.back : sprites.Get(e.card);
                v.sr.transform.localScale = Vector3.one * cardScale;
                v.fromPos = dealFromPos;
                v.fromRot = dealFromRot;
                v.toPos = targetPos;
                v.toRot = targetRot;
                v.t = 0f;
                v.dur = dealDuration;
                v.delay = i * dealStagger;
                v.arrived = false;
                v.flipping = false;
                v.card = e.card;
                v.faceDown = e.faceDown;
                // Thwack when it actually starts sliding (after its stagger delay), not all at once here.
                v.needsDealSound = _firstRebuildDone;
                v.sr.transform.SetPositionAndRotation(dealFromPos, dealFromRot);
            }
            else if (v.flipping)
            {
                // A flip owns the sprite + scale until it finishes; just keep the slot pinned.
                v.toPos = targetPos;
                v.toRot = targetRot;
            }
            else if (v.faceDown && !e.faceDown)
            {
                // The hole card is being revealed: play a flip instead of snapping the sprite.
                v.flipping = true;
                v.flipT = 0f;
                v.flipToCard = e.card;
                v.flipSwapped = false;
                v.arrived = true;
                v.toPos = targetPos;
                v.toRot = targetRot;
                v.sr.transform.SetPositionAndRotation(targetPos, targetRot);
                if (_firstRebuildDone)
                    PlayCardSfx(); // dealer's second-card (hole) reveal flip
            }
            else
            {
                v.sr.sprite = e.faceDown ? sprites.back : sprites.Get(e.card);
                v.sr.transform.localScale = Vector3.one * cardScale;
                bool faceChanged = v.faceDown != e.faceDown || v.card != e.card;
                v.card = e.card;
                v.faceDown = e.faceDown;
                // Re-target (fan recenters as new cards arrive) with a quick settle, unless it's basically unchanged.
                if ((v.toPos - targetPos).sqrMagnitude > 0.0000001f || Quaternion.Angle(v.toRot, targetRot) > 0.5f)
                {
                    v.fromPos = v.sr.transform.position;
                    v.fromRot = v.sr.transform.rotation;
                    v.toPos = targetPos;
                    v.toRot = targetRot;
                    v.t = 0f;
                    v.dur = recenterDuration;
                    v.delay = 0f;
                    v.arrived = false;
                }
                else if (faceChanged && v.arrived)
                {
                    v.sr.transform.SetPositionAndRotation(targetPos, targetRot);
                }
            }
        }

        for (int i = n; i < pool.Count; i++)
        {
            if (pool[i].inUse)
            {
                pool[i].inUse = false;
                pool[i].sr.gameObject.SetActive(false);
            }
        }
    }

    CardVisual EnsureVisual(List<CardVisual> pool, int index, out bool isNew)
    {
        while (pool.Count <= index)
        {
            GameObject go = new($"Card{pool.Count}");
            go.transform.SetParent(transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            pool.Add(new CardVisual { sr = sr, inUse = false });
        }
        CardVisual v = pool[index];
        isNew = !v.inUse;
        v.inUse = true;
        return v;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (cardDealClip == null)
            cardDealClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Carnival/CardDeal.wav");
    }
#endif

    void AnimateCards()
    {
        float dt = Time.deltaTime;
        for (int p = 0; p < _pools.Length; p++)
        {
            List<CardVisual> pool = _pools[p];
            for (int i = 0; i < pool.Count; i++)
            {
                CardVisual v = pool[i];
                if (!v.inUse)
                    continue;
                if (v.flipping)
                {
                    v.flipT += flipDuration > 0f ? dt / flipDuration : 1f;
                    float ft = Mathf.Clamp01(v.flipT);
                    // Collapse width to 0 at the halfway point (edge-on), then expand — a single-quad card flip.
                    float widthFactor = Mathf.Abs(Mathf.Cos(ft * Mathf.PI));
                    if (!v.flipSwapped && ft >= 0.5f)
                    {
                        v.flipSwapped = true;
                        v.sr.sprite = sprites.Get(v.flipToCard);
                        v.card = v.flipToCard;
                        v.faceDown = false;
                    }
                    v.sr.transform.localScale = new Vector3(cardScale * widthFactor, cardScale, cardScale);
                    if (v.flipT >= 1f)
                    {
                        v.flipping = false;
                        v.sr.transform.localScale = Vector3.one * cardScale;
                    }
                    continue;
                }
                if (v.arrived)
                    continue;
                if (v.delay > 0f)
                {
                    v.delay -= dt;
                    continue;
                }
                if (v.needsDealSound)
                {
                    v.needsDealSound = false;
                    PlayCardSfx();
                }
                v.t += v.dur > 0f ? dt / v.dur : 1f;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(v.t));
                v.sr.transform.SetPositionAndRotation(
                    Vector3.LerpUnclamped(v.fromPos, v.toPos, e),
                    Quaternion.SlerpUnclamped(v.fromRot, v.toRot, e));
                if (v.t >= 1f)
                {
                    v.arrived = true;
                    v.sr.transform.SetPositionAndRotation(v.toPos, v.toRot);
                }
            }
        }
    }
}
