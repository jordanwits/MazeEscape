using UnityEngine;

/// <summary>
/// Carnival bottle stays kinematic on spawn so imperfect shelf geometry / non-uniform parent scales
/// can't tip it over. First impact from a <see cref="HeavyThrowableHoldItem"/> (Ball / StarBall / RingToss)
/// releases it to dynamic and transfers a fraction of the projectile's momentum at the contact point so
/// the knockdown reads as a physical hit rather than a delayed wake-up.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public sealed class CarnivalBottleKnockdown : MonoBehaviour
{
    [Tooltip("Fraction of the projectile's linear momentum (mass * velocity) applied to the bottle at the contact point on first impact.")]
    [SerializeField, Range(0.02f, 1f)] float impactImpulseScale = 0.15f;

    Rigidbody _rb;
    bool _knockedDown;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (_knockedDown)
            return;
        if (collision.rigidbody == null)
            return;
        if (collision.rigidbody.GetComponent<HeavyThrowableHoldItem>() == null)
            return;

        _knockedDown = true;
        _rb.isKinematic = false;

        ContactPoint contact = collision.GetContact(0);
        Vector3 impulse = collision.rigidbody.linearVelocity * collision.rigidbody.mass * impactImpulseScale;
        _rb.AddForceAtPosition(impulse, contact.point, ForceMode.Impulse);
    }
}
