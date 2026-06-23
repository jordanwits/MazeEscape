using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Purely-visual, non-networked renderer for a blackjack table. Runs on every peer and rebuilds in-world card
/// sprites from the replicated <see cref="BlackjackGameController"/> state whenever it changes. The dealer's
/// hidden hole card is drawn as a face-down placeholder until the server reveals it (the real card isn't
/// replicated until then). Card visuals carry no colliders so they never block the interact raycast.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackTableView : MonoBehaviour
{
    [SerializeField] BlackjackGameController controller;
    [SerializeField] BlackjackCardSprites sprites;

    [Header("Anchors (authored on the table prefab, under the scale-1 root)")]
    [SerializeField] Transform[] seatCardAnchors = new Transform[BlackjackConfig.SeatCount];
    [SerializeField] Transform dealerCardAnchor;

    [Header("Card layout")]
    [SerializeField] float cardScale = 0.3f;
    [SerializeField] float cardSpacing = 0.18f;
    [SerializeField] float depthStep = 0.004f;
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] int baseSortingOrder = 100;

    readonly List<SpriteRenderer>[] _seatPools = new List<SpriteRenderer>[BlackjackConfig.SeatCount];
    List<SpriteRenderer> _dealerPool;
    bool _subscribed;

    void Awake()
    {
        if (controller == null)
            controller = GetComponentInParent<BlackjackGameController>(true);
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

    void Update()
    {
        // The controller is on this prefab from instantiate, but guard for late binding regardless.
        if (!_subscribed)
        {
            TrySubscribe();
            Rebuild();
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

    void Rebuild()
    {
        if (controller == null || sprites == null)
            return;

        int seats = controller.ActiveSeatCount;
        for (int i = 0; i < BlackjackConfig.SeatCount; i++)
        {
            Transform anchor = (seatCardAnchors != null && i < seatCardAnchors.Length) ? seatCardAnchors[i] : null;
            if (anchor == null)
                continue;
            List<SpriteRenderer> pool = _seatPools[i] ??= new List<SpriteRenderer>();
            if (i < seats)
            {
                SeatState s = controller.GetSeat(i);
                RenderCards(anchor, pool, s.Cards, s.Cards.Length, false);
            }
            else
            {
                HideFrom(pool, 0);
            }
        }

        if (dealerCardAnchor != null)
        {
            _dealerPool ??= new List<SpriteRenderer>();
            DealerState d = controller.Dealer;
            RenderCards(dealerCardAnchor, _dealerPool, d.Cards, d.Cards.Length, d.HoleHidden == 1);
        }
    }

    void RenderCards(Transform anchor, List<SpriteRenderer> pool, FixedList32Bytes<byte> cards, int count, bool appendFaceDown)
    {
        int visual = 0;
        for (int i = 0; i < count; i++)
            SetCard(anchor, pool, visual++, sprites.Get(cards[i]));
        if (appendFaceDown)
            SetCard(anchor, pool, visual++, sprites.back);
        HideFrom(pool, visual);
    }

    void SetCard(Transform anchor, List<SpriteRenderer> pool, int index, Sprite sprite)
    {
        SpriteRenderer sr = EnsureRenderer(anchor, pool, index);
        sr.sprite = sprite;
        sr.transform.localPosition = new Vector3(index * cardSpacing, 0f, -index * depthStep);
        sr.transform.localRotation = Quaternion.identity;
        sr.transform.localScale = Vector3.one * cardScale;
        sr.sortingLayerName = sortingLayerName;
        sr.sortingOrder = baseSortingOrder + index;
        sr.gameObject.SetActive(sprite != null);
    }

    SpriteRenderer EnsureRenderer(Transform anchor, List<SpriteRenderer> pool, int index)
    {
        while (pool.Count <= index)
        {
            GameObject go = new GameObject($"Card{pool.Count}");
            go.transform.SetParent(anchor, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            pool.Add(sr);
        }
        // Re-parent in case the pooled renderer was created for a different anchor (shouldn't happen, but safe).
        if (pool[index].transform.parent != anchor)
            pool[index].transform.SetParent(anchor, false);
        return pool[index];
    }

    static void HideFrom(List<SpriteRenderer> pool, int from)
    {
        if (pool == null)
            return;
        for (int i = from; i < pool.Count; i++)
            if (pool[i] != null)
                pool[i].gameObject.SetActive(false);
    }
}
