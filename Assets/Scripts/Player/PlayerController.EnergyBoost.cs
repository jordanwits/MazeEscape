using UnityEngine;

/// <summary>
/// Energy-drink boost: a short, owner-local buff that gives unlimited stamina and a movement-speed
/// multiplier, and recolors the HUD stamina bar so the effect reads clearly. Movement/stamina/HUD are
/// all owner-side, so only the local player's controller ever runs this — remote avatars keep the timer
/// at zero and <see cref="TickEnergyBoost"/> is a no-op for them.
/// </summary>
public partial class PlayerController
{
    float _energyBoostTimer;
    float _energyBoostSpeedMultiplier = 1f;

    bool _energyBoostVisualActive;
    bool _hasEnergyBoostRestoreColor;
    Color _energyBoostRestoreFillColor;

    // Vivid, high-energy hues the stamina fill pulses between while boosted.
    static readonly Color EnergyBoostFillColorA = new Color(1f, 0.9f, 0.2f, 0.95f);   // electric yellow
    static readonly Color EnergyBoostFillColorB = new Color(0.35f, 1f, 0.55f, 0.95f); // lime

    /// <summary>True while the energy-drink buff is running (unlimited stamina + speed boost).</summary>
    public bool EnergyBoostActive => _energyBoostTimer > 0f;

    /// <summary>Movement speed multiplier from the boost (1 when inactive).</summary>
    public float EnergyBoostSpeedMultiplier => EnergyBoostActive ? _energyBoostSpeedMultiplier : 1f;

    /// <summary>
    /// Owner-side entry point (called from the local use flow and the server-authoritative consume RPC).
    /// Starts or refreshes the boost. Duration/multiplier come from the consumed <see cref="EnergyDrinkItem"/>.
    /// </summary>
    public void ActivateEnergyDrinkBoost(float durationSeconds, float speedMultiplier)
    {
        if (durationSeconds <= 0f)
            return;

        _energyBoostTimer = Mathf.Max(_energyBoostTimer, durationSeconds);
        _energyBoostSpeedMultiplier = Mathf.Max(1f, speedMultiplier);

        // Top off immediately so sprinting is available the instant you drink, and the bar snaps to full.
        _currentStamina = maxStamina;
        _staminaRegenTimer = 0f;
        BeginEnergyBoostStaminaBarVisual();
        RefreshStaminaUI();
    }

    /// <summary>Advances the boost timer and drives the HUD pulse. Call once per frame from Update.</summary>
    void TickEnergyBoost()
    {
        if (_energyBoostTimer <= 0f)
        {
            if (_energyBoostVisualActive)
                EndEnergyBoostStaminaBarVisual();
            return;
        }

        _energyBoostTimer -= Time.deltaTime;
        // Pin stamina to full for the whole buff so the bar reads as "unlimited" even while sprinting.
        _currentStamina = maxStamina;

        if (_energyBoostTimer <= 0f)
        {
            _energyBoostTimer = 0f;
            _energyBoostSpeedMultiplier = 1f;
            EndEnergyBoostStaminaBarVisual();
            RefreshStaminaUI();
            return;
        }

        UpdateEnergyBoostStaminaBarVisual();
    }

    void BeginEnergyBoostStaminaBarVisual()
    {
        if (_energyBoostVisualActive || staminaBarImage == null)
            return;

        _energyBoostRestoreFillColor = staminaBarImage.color;
        _hasEnergyBoostRestoreColor = true;
        _energyBoostVisualActive = true;
        UpdateEnergyBoostStaminaBarVisual();
    }

    void UpdateEnergyBoostStaminaBarVisual()
    {
        if (!_energyBoostVisualActive || staminaBarImage == null)
            return;

        float t = 0.5f + 0.5f * Mathf.Sin(Time.time * 10f);
        staminaBarImage.color = Color.Lerp(EnergyBoostFillColorA, EnergyBoostFillColorB, t);
    }

    void EndEnergyBoostStaminaBarVisual()
    {
        _energyBoostVisualActive = false;
        if (staminaBarImage != null && _hasEnergyBoostRestoreColor)
            staminaBarImage.color = _energyBoostRestoreFillColor;
        _hasEnergyBoostRestoreColor = false;
    }
}
