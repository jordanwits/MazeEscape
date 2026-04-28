using UnityEngine;

/// <summary>
/// Vertical bob on the humanoid Hips bone after animation, driven by the locomotion state's
/// normalized time so it stays in phase with the walk/run clip (not an independent fast sine).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(500)]
public class MovementViewBob : MonoBehaviour
{
    [SerializeField] Animator animator;
    [SerializeField] int baseLayerIndex;

    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";

    [Tooltip("Animator Speed below this: no bob.")]
    [SerializeField] float minimumSpeed = 0.08f;

    [Tooltip("Match Animator transitions: Speed above this blends toward run amplitude.")]
    [SerializeField] float runSpeedThreshold = 0.52f;

    [SerializeField] float walkBobAmplitude = 0.048f;
    [SerializeField] float runBobAmplitude = 0.09f;

    [Tooltip("How many bob peaks per one full loop of the locomotion clip. Mixamo walk/run is usually 2 (left + right step).")]
    [SerializeField] int bobPeaksPerAnimationCycle = 2;

    [Header("Melee (optional)")]
    [Tooltip("If set, skips vertical hips bob while the masked upper-body melee state plays so fist height matches the clip (bob stacks on hips and lifts everything).")]
    [SerializeField] bool suppressBobDuringMaskedMelee = true;
    [SerializeField] bool meleeLayerByName = true;
    [SerializeField] string upperBodyLayerName = "Upper Body";
    [SerializeField] int upperBodyLayerIndexFallback = 1;
    [SerializeField] string meleeStateNameOnUpperLayer = "RightHook";

    void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
    }

    void LateUpdate()
    {
        if (animator == null || !animator.isHuman || !animator.isInitialized)
            return;

        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null)
            return;

        bool grounded = animator.GetBool(groundedParameter);
        float speed = animator.GetFloat(speedParameter);

        if (!grounded || speed < minimumSpeed)
            return;

        if (suppressBobDuringMaskedMelee && IsPlayingUpperBodyMeleeState())
            return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(baseLayerIndex);
        float cycle = Mathf.Repeat(info.normalizedTime, 1f);
        int peaks = Mathf.Max(1, bobPeaksPerAnimationCycle);
        float angle = cycle * peaks * 2f * Mathf.PI;

        float runBlend = Mathf.Clamp01((speed - runSpeedThreshold) / Mathf.Max(0.01f, 1f - runSpeedThreshold));
        float amp = Mathf.Lerp(walkBobAmplitude, runBobAmplitude, runBlend);

        float bob = (1f - Mathf.Cos(angle)) * 0.5f * amp;
        hips.localPosition += Vector3.up * bob;
    }

    bool IsPlayingUpperBodyMeleeState()
    {
        int layer = ResolveMeleeLayerIndex();
        if (layer < 0 || layer >= animator.layerCount || string.IsNullOrEmpty(meleeStateNameOnUpperLayer))
            return false;

        if (animator.IsInTransition(layer))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            if (next.IsName(meleeStateNameOnUpperLayer))
                return true;
        }

        return animator.GetCurrentAnimatorStateInfo(layer).IsName(meleeStateNameOnUpperLayer);
    }

    int ResolveMeleeLayerIndex()
    {
        if (!meleeLayerByName || string.IsNullOrEmpty(upperBodyLayerName))
            return upperBodyLayerIndexFallback;

        for (int i = 0; i < animator.layerCount; i++)
        {
            if (animator.GetLayerName(i) == upperBodyLayerName)
                return i;
        }

        return upperBodyLayerIndexFallback;
    }
}
