using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class PitKillZone : MonoBehaviour
{
    [SerializeField] bool destroyIfNoZombieHealth;
    [SerializeField] bool addKinematicRigidbody = true;

    Collider _zoneCollider;

    void Reset()
    {
        ConfigureZone();
    }

    void Awake()
    {
        ConfigureZone();
    }

    void OnTriggerEnter(Collider other)
    {
        TryKill(other);
    }

    void OnTriggerStay(Collider other)
    {
        TryKill(other);
    }

    void ConfigureZone()
    {
        _zoneCollider = GetComponent<Collider>();
        if (_zoneCollider != null)
            _zoneCollider.isTrigger = true;

        if (!addKinematicRigidbody)
            return;

        Rigidbody zoneRigidbody = GetComponent<Rigidbody>();
        if (zoneRigidbody == null)
            zoneRigidbody = gameObject.AddComponent<Rigidbody>();

        zoneRigidbody.isKinematic = true;
        zoneRigidbody.useGravity = false;
    }

    void TryKill(Collider other)
    {
        if (other == null)
            return;

        ZombieHealth zombieHealth = other.GetComponentInParent<ZombieHealth>();
        if (zombieHealth != null)
        {
            zombieHealth.Die();
            return;
        }

        if (destroyIfNoZombieHealth)
            Destroy(other.transform.root.gameObject);
    }
}
