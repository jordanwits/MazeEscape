using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using System;

[DisallowMultipleComponent]
public class PlayerHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField] float maxHealth = 100f;
    [SerializeField] UnityEvent onDamaged;
    [SerializeField] UnityEvent onDied;
    [Tooltip("Optional UI Image (set to Filled) to display the health bar. If empty, one is created automatically.")]
    [SerializeField] Image healthBarImage;
    [Tooltip("Auto-create a HUD health bar if none is assigned.")]
    [SerializeField] bool autoCreateHealthBar = true;
    [Tooltip("When CurrentHealth increases, the health bar fill moves toward the new value at this many HP per second. Damage still updates the bar immediately.")]
    [SerializeField, Min(1f)] float healthBarHealFillSpeedHps = 25f;

    float _displayHealth;
    RectTransform _healthFillRect;
    GameObject _healthBarRoot;
    NetworkObject _networkObject;

    public float MaxHealth => maxHealth;
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    public float HealthNormalized => maxHealth > 0f ? CurrentHealth / maxHealth : 0f;
    public event Action Damaged;
    public event Action Died;
    public event Action Restored;
    public event Action Healed;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        CurrentHealth = Mathf.Max(1f, maxHealth);

        if (healthBarImage == null && autoCreateHealthBar)
            healthBarImage = CreateHealthBarUI();

        _displayHealth = CurrentHealth;
        UpdateHealthBar();
    }

    void Update()
    {
        if (IsDead)
            return;

        if (CurrentHealth < _displayHealth)
            _displayHealth = CurrentHealth;
        else if (CurrentHealth > _displayHealth)
        {
            _displayHealth = Mathf.MoveTowards(
                _displayHealth,
                CurrentHealth,
                healthBarHealFillSpeedHps * Time.deltaTime);
        }

        UpdateHealthBar();
    }

    public void TakeDamage(float amount)
    {
        if (_networkObject != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            return;

        if (IsDead || amount <= 0f)
            return;

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        _displayHealth = CurrentHealth;
        UpdateHealthBar();
        onDamaged?.Invoke();
        Damaged?.Invoke();

        if (CurrentHealth > 0f)
            return;

        IsDead = true;
        onDied?.Invoke();
        Died?.Invoke();
    }

    public void Heal(float amount)
    {
        if (_networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer)
            return;

        if (IsDead || amount <= 0f)
            return;

        CurrentHealth = Mathf.Min(Mathf.Max(1f, maxHealth), CurrentHealth + amount);
        // Bar catches up in Update; do not snap _displayHealth here
        Healed?.Invoke();
    }

    public void RestoreFullHealth()
    {
        IsDead = false;
        CurrentHealth = Mathf.Max(1f, maxHealth);
        // Bar animates to full; do not set _displayHealth here
        Restored?.Invoke();
    }

    public void ApplyReplicatedState(float currentHealth, bool isDead)
    {
        bool wasDead = IsDead;
        CurrentHealth = Mathf.Clamp(currentHealth, 0f, Mathf.Max(1f, maxHealth));
        IsDead = isDead;
        if (CurrentHealth < _displayHealth)
            _displayHealth = CurrentHealth;
        UpdateHealthBar();
        if (!wasDead && isDead)
        {
            onDied?.Invoke();
            Died?.Invoke();
        }
    }

    void UpdateHealthBar()
    {
        float t = maxHealth > 0f ? Mathf.Clamp01(_displayHealth / maxHealth) : 0f;
        if (_healthFillRect != null)
            _healthFillRect.anchorMax = new Vector2(t, 1f);
        else if (healthBarImage != null)
            healthBarImage.fillAmount = t;
    }

    Image CreateHealthBarUI()
    {
        Canvas canvas = FindAnyObjectByType<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGo = new GameObject("HealthCanvas");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();
        }

        GameObject bg = new GameObject("HealthBarBG");
        bg.transform.SetParent(canvas.transform, false);
        Image bgImage = bg.AddComponent<Image>();
        bgImage.color = new Color(0f, 0f, 0f, 0.6f);
        RectTransform bgRect = bg.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0.5f, 1f);
        bgRect.anchorMax = new Vector2(0.5f, 1f);
        bgRect.pivot = new Vector2(0.5f, 1f);
        bgRect.anchoredPosition = new Vector2(0f, -60f);
        bgRect.sizeDelta = new Vector2(304f, 24f);
        _healthBarRoot = bg;

        GameObject fill = new GameObject("HealthBarFill");
        fill.transform.SetParent(bg.transform, false);
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.9f, 0.2f, 0.2f, 0.95f);
        RectTransform fillRect = fill.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.pivot = new Vector2(0f, 0.5f);
        fillRect.offsetMin = new Vector2(2f, 2f);
        fillRect.offsetMax = new Vector2(-2f, -2f);
        _healthFillRect = fillRect;

        return fillImage;
    }

    public void SetHudVisible(bool visible)
    {
        if (_healthBarRoot != null)
            _healthBarRoot.SetActive(visible);
        else if (healthBarImage != null)
            healthBarImage.enabled = visible;
    }
}
