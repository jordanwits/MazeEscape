using UnityEngine;

/// <summary>
/// Showers sparks out of a failing ceiling fixture when its tube cuts out. Added at runtime by
/// <see cref="SeveranceLightFaults"/> to the fixtures it makes flicker, alongside the
/// <see cref="RopeLightFlicker"/> that actually drives the dropouts.
///
/// The burst is triggered off <see cref="SeveranceCeilingLight.Output"/> falling to zero rather than by
/// talking to the flicker component, so it stays decoupled: anything that darkens the fixture throws
/// sparks, and the flicker script needs no knowledge of this effect.
///
/// Sparks simulate in **world space** so they keep falling from where they were born instead of riding
/// the fixture, and they get real gravity — from the panel at y≈5.85 down to a floor at y=1 is about
/// 4.85m, which is a hair under a second of freefall, so the lifetime is tuned to have them fade out
/// right around floor level. That reads as sparks hitting the ground without paying for particle
/// collision on dozens of fixtures.
///
/// Purely cosmetic and entirely local — no networking, same as the flicker it follows.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SeveranceCeilingLight))]
public class SeveranceLightSparks : MonoBehaviour
{
    [Tooltip("Additive material for the spark streaks. Supplied by SeveranceLightFaults.")]
    [SerializeField] Material sparkMaterial;

    [Tooltip("Sparks per burst.")]
    [SerializeField] int minSparks = 14;
    [SerializeField] int maxSparks = 30;

    [Tooltip("Beyond this distance from the local camera a fixture doesn't bother spawning particles. " +
             "Sparks are small and additive, so they read as noise long before this.")]
    [SerializeField] float maxEmitDistance = 28f;

    [Tooltip("Shortest gap between bursts. A stutter burst can blink several times a second and one " +
             "shower per blink turns into a firehose.")]
    [SerializeField] float minSecondsBetweenBursts = 0.35f;

    SeveranceCeilingLight _fixture;
    ParticleSystem _particles;
    Transform _viewpoint;
    bool _wasLit = true;
    float _nextBurstAllowed;
    float _nextViewpointCheck;

    void Awake()
    {
        _fixture = GetComponent<SeveranceCeilingLight>();
        BuildParticleSystem();
    }

    void LateUpdate()
    {
        if (_particles == null || _fixture == null)
            return;

        // Runs after SeveranceCeilingLight.LateUpdate has refreshed Output for this frame.
        bool lit = _fixture.Output > 0.001f;
        bool wentDark = _wasLit && !lit;
        _wasLit = lit;

        if (!wentDark || Time.unscaledTime < _nextBurstAllowed)
            return;

        if (!IsWithinEmitRange())
            return;

        _nextBurstAllowed = Time.unscaledTime + Mathf.Max(0.05f, minSecondsBetweenBursts);
        _particles.Emit(Random.Range(minSparks, maxSparks + 1));
    }

    bool IsWithinEmitRange()
    {
        if (Time.unscaledTime >= _nextViewpointCheck || _viewpoint == null)
        {
            _nextViewpointCheck = Time.unscaledTime + 1f;
            _viewpoint = ResolveViewpoint();
        }
        if (_viewpoint == null)
            return false;

        return (_viewpoint.position - transform.position).sqrMagnitude <= maxEmitDistance * maxEmitDistance;
    }

    /// <summary>Camera.main is null in this project (PlayerView is deliberately Untagged), so find the live Game camera.</summary>
    static Transform ResolveViewpoint()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cams = Camera.allCameras; // enabled cameras only
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                {
                    cam = cams[i];
                    break;
                }
            }
        }
        return cam != null ? cam.transform : null;
    }

    void BuildParticleSystem()
    {
        var host = new GameObject("SparkFx");
        host.transform.SetParent(transform, false);
        host.transform.localPosition = Vector3.zero;
        host.transform.localRotation = Quaternion.identity;
        // The panel quad is scaled; sparks must not inherit that or their size and cone go with it.
        host.transform.localScale = Vector3.one;
        host.transform.position = transform.position + Vector3.down * 0.12f;
        host.transform.rotation = Quaternion.identity;

        _particles = host.AddComponent<ParticleSystem>();

        var main = _particles.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.85f, 1.15f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 2.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.010f, 0.028f);
        // Whitish-yellow, not orange: an electrical arc runs hot and pale. Deliberately avoids cooling
        // through orange into ember red, which reads as fire rather than a shorting fluorescent.
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(1f, 0.995f, 0.94f), new Color(1f, 0.97f, 0.86f));
        main.gravityModifier = 1f;
        main.maxParticles = 200;

        // Emission is manual: Emit() on each dropout, nothing continuous.
        var emission = _particles.emission;
        emission.enabled = false;

        var shape = _particles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 22f;
        shape.radius = 0.3f;
        shape.rotation = new Vector3(90f, 0f, 0f);   // cone's +Z points straight down

        var colorOverLife = _particles.colorOverLifetime;
        colorOverLife.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] {
                new GradientColorKey(new Color(1f, 1f, 0.98f), 0f),
                new GradientColorKey(new Color(1f, 0.98f, 0.86f), 0.55f),
                new GradientColorKey(new Color(1f, 0.95f, 0.76f), 1f)
            },
            new[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 0.7f),
                new GradientAlphaKey(0f, 1f)   // gone by the time they reach the floor
            });
        colorOverLife.color = new ParticleSystem.MinMaxGradient(gradient);

        var sizeOverLife = _particles.sizeOverLifetime;
        sizeOverLife.enabled = true;
        sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.6f, 0.75f), new Keyframe(1f, 0.2f)));

        var renderer = host.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        // velocityScale is the term that runs away: a stretched particle grows by velocityScale * speed,
        // and these accelerate to ~10m/s under gravity, so a value like 0.05 turns them into long bars by
        // the time they are halfway down. Keep it small and let lengthScale carry the streak instead.
        renderer.velocityScale = 0.012f;
        renderer.lengthScale = 1.4f;
        renderer.cameraVelocityScale = 0f;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sortingOrder = 1;
        if (sparkMaterial != null)
            renderer.sharedMaterial = sparkMaterial;
    }

    /// <summary>Supplies the shared additive material. Call before the first burst.</summary>
    public void SetSparkMaterial(Material material)
    {
        sparkMaterial = material;
        if (_particles == null)
            return;
        var renderer = _particles.GetComponent<ParticleSystemRenderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;
    }
}
