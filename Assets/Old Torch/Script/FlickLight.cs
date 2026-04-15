using UnityEngine;

public class FlickLight : MonoBehaviour
{
    Vector3 _startLocalPosLight;
    float _flickIntensity;
    float _targetIntensity;
    float _nextFlickTime;

    [Tooltip("The light attached to this script")]
    public Light lig;
    [Tooltip("The light color")]
    public Color colorLight = Color.white;
    [Space]
    [Tooltip("The minimun Intensity Light")]
    public float min = 0.0f;
    [Tooltip("The maximun Intensity Light")]
    public float max = 2.0f;
    [Space(20)]
    [Tooltip("The timing of the speed for flick Intensity of the light")]
    public float timer = 1.0f;
    [Tooltip("The waiting time for the light to flicker")]
    public float smooth = 0.1f;
    [Space(10)]
    [Header("Set a false movement of the shadow")]
    [Space(10)]
    public bool moveShadow = false;
    [Tooltip("The speed of the movement of light")]
    public float speedMoveShadow = 1f;
    [Tooltip("The speed smooth of the movement of light")]
    public float speedSmoothShadow = 50f;

    void Awake()
    {
        lig ??= GetComponent<Light>();

        if (lig == null)
        {
            return;
        }

        _startLocalPosLight = lig.transform.localPosition;
        _targetIntensity = Mathf.Clamp(lig.intensity > 0.0f ? lig.intensity : max, min, max);
        _flickIntensity = _targetIntensity;
        lig.color = colorLight;
    }

    void Update()
    {
        if (lig == null)
        {
            return;
        }

        if (Time.time >= _nextFlickTime)
        {
            _targetIntensity = Random.Range(min, max);
            _nextFlickTime = Time.time + Mathf.Max(0.01f, smooth);
        }

        _flickIntensity = Mathf.Lerp(_flickIntensity, _targetIntensity, Mathf.Max(0.0f, timer) * Time.deltaTime);
        lig.intensity = _flickIntensity;
        lig.color = colorLight;
        MoveShadowLight();
    }

    void MoveShadowLight()
    {
        if (moveShadow)
        {
            lig.transform.localPosition = _startLocalPosLight + (Random.insideUnitSphere * speedMoveShadow / speedSmoothShadow);
        }
        else
        {
            lig.transform.localPosition = _startLocalPosLight;
        }
    }
}
