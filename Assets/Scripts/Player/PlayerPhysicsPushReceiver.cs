using UnityEngine;

/// <summary>
/// Enables the local <see cref="PlayerController"/> CharacterController hits to impart horizontal momentum
/// to this rigidbody. Without this (or manual OnControllerColliderHit handling), movable props often feel
/// "stuck" because CC does not apply forces like a Rigidbody player would.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public sealed class PlayerPhysicsPushReceiver : MonoBehaviour
{
    [SerializeField, Min(0.01f)]
    [Tooltip("Multiplies horizontal push strength from PlayerController.")]
    float pushGainMultiplier = 1f;

    public float PushGainMultiplier => pushGainMultiplier;
}
