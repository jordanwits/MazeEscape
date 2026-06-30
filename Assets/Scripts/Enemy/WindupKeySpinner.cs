using UnityEngine;

/// <summary>
/// Cosmetic spinner for the wind-up monkey's back key. Spins around its local Z axis while the
/// monkey is walking. Reads the monkey Animator's "Active" bool (replicated to every client by
/// ServerNetworkAnimator), so the key spins in sync on all peers without any extra networking.
/// </summary>
[DisallowMultipleComponent]
public class WindupKeySpinner : MonoBehaviour
{
    [Tooltip("Monkey Animator that drives the 'Active' bool. Auto-resolved from a parent if left empty.")]
    [SerializeField] Animator animator;
    [SerializeField] string activeBoolParam = "Active";
    [Tooltip("Spin speed in degrees/second around the key's local Z axis.")]
    [SerializeField] float spinSpeed = 360f;
    [Tooltip("If true, only spins while the monkey is active (walking + clapping); otherwise spins always.")]
    [SerializeField] bool onlyWhileActive = true;

    void Awake()
    {
        if (animator == null)
            animator = GetComponentInParent<Animator>();
    }

    void Update()
    {
        if (onlyWhileActive)
        {
            if (animator == null || !animator.GetBool(activeBoolParam))
                return;
        }

        transform.Rotate(0f, 0f, spinSpeed * Time.deltaTime, Space.Self);
    }
}
