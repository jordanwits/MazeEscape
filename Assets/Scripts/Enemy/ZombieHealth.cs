using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public class ZombieHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField] float maxHealth = 100f;

    [Header("Death")]
    [SerializeField] Animator animator;
    [SerializeField] ZombieAI zombieAI;
    [SerializeField] Rigidbody zombieRigidbody;
    [SerializeField] Collider[] zombieColliders;
    [SerializeField] float disableColliderDelay = 0.35f;
    [SerializeField] float destroyDelay = 10f;
    [SerializeField] string isDeadParameter = "IsDead";
    [SerializeField] string deathIndexParameter = "DeathIndex";

    public float MaxHealth => maxHealth;
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    void Reset()
    {
        CacheReferences();
    }

    void Awake()
    {
        CacheReferences();
        CurrentHealth = Mathf.Max(1f, maxHealth);
    }

    public void TakeDamage(float amount)
    {
        if (IsDead || amount <= 0f)
            return;

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f)
        {
            Die();
            return;
        }

        if (zombieAI != null)
            zombieAI.TakeHit();
    }

    public void Die()
    {
        if (IsDead)
            return;

        IsDead = true;
        CurrentHealth = 0f;

        int deathIndex = Random.Range(0, 2);
        if (animator != null)
        {
            animator.ResetTrigger("Attack");
            animator.ResetTrigger("Scream");
            animator.ResetTrigger("HitReaction");
            animator.SetInteger(deathIndexParameter, deathIndex);
            animator.SetBool(isDeadParameter, true);
        }

        if (zombieAI != null)
            zombieAI.HandleDeath();

        if (zombieRigidbody != null)
        {
            zombieRigidbody.useGravity = false;
            zombieRigidbody.isKinematic = true;
            zombieRigidbody.linearVelocity = Vector3.zero;
            zombieRigidbody.angularVelocity = Vector3.zero;
        }

        StartCoroutine(DeathCleanupRoutine());
    }

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        if (zombieAI == null)
            zombieAI = GetComponent<ZombieAI>();

        if (zombieRigidbody == null)
            zombieRigidbody = GetComponent<Rigidbody>();

        if (zombieColliders == null || zombieColliders.Length == 0)
            zombieColliders = GetComponentsInChildren<Collider>(true);
    }

    IEnumerator DeathCleanupRoutine()
    {
        if (disableColliderDelay > 0f)
            yield return new WaitForSeconds(disableColliderDelay);

        for (int i = 0; i < zombieColliders.Length; i++)
        {
            Collider zombieCollider = zombieColliders[i];
            if (zombieCollider != null)
                zombieCollider.enabled = false;
        }

        if (destroyDelay > 0f)
            yield return new WaitForSeconds(destroyDelay);

        Destroy(gameObject);
    }
}
