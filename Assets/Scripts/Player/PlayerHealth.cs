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
    [Tooltip("Optional UI Image (set to Filled) to display the health bar. If empty, the body gauge on the shared vitals cluster is used.")]
    [SerializeField] Image healthBarImage;
    [Tooltip("Auto-create the HUD vitals cluster health gauge if no image is assigned.")]
    [SerializeField] bool autoCreateHealthBar = true;
    [Tooltip("When CurrentHealth increases, the health bar fill moves toward the new value at this many HP per second. Damage still updates the bar immediately.")]
    [SerializeField, Min(1f)] float healthBarHealFillSpeedHps = 25f;

    float _displayHealth;
    PlayerVitalsHud _vitalsHud;
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
            _vitalsHud = PlayerVitalsHud.Ensure(gameObject);

        _displayHealth = CurrentHealth;
        UpdateHealthBar();
    }

    void OnEnable()
    {
        PlayerHealthRegistry.Register(this);
    }

    void OnDisable()
    {
        PlayerHealthRegistry.Unregister(this);
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
        _vitalsHud?.NotifyDamaged();
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
        float previousHealth = CurrentHealth;
        CurrentHealth = Mathf.Clamp(currentHealth, 0f, Mathf.Max(1f, maxHealth));
        IsDead = isDead;
        if (CurrentHealth < _displayHealth)
            _displayHealth = CurrentHealth;
        // Clients take damage via replication (TakeDamage is server-only), so flash on decreases here.
        if (CurrentHealth < previousHealth)
            _vitalsHud?.NotifyDamaged();
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
        if (_vitalsHud != null)
            _vitalsHud.SetHealth(t);
        else if (healthBarImage != null)
            healthBarImage.fillAmount = t;
    }

    public void SetHudVisible(bool visible)
    {
        // Health owns the whole cluster's visibility; PlayerController toggles only the stamina column.
        if (_vitalsHud != null)
            _vitalsHud.SetHealthVisible(visible);
        else if (healthBarImage != null)
            healthBarImage.enabled = visible;
    }
}
