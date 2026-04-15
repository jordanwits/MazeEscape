using UnityEngine;

public class FlashlightItem : MonoBehaviour
{
    [SerializeField] Light flashlightLight;
    [SerializeField] Rigidbody itemRigidbody;
    [SerializeField] Collider[] itemColliders;
    [SerializeField] Vector3 heldLocalPosition;
    [Tooltip("If enabled, the flashlight rotates so its Light points the same way as the hold point.")]
    [SerializeField] bool alignHeldRotationToLight = true;
    [SerializeField] Vector3 heldLocalEulerAngles;

    public bool IsHeld { get; private set; }

    Light[] _lights;
    Quaternion _heldLocalRotation;
    Transform _heldAnchor;
    Transform _heldRotationSource;

    void Awake()
    {
        CacheLights();
        CacheHeldRotation();

        if (itemRigidbody == null)
            itemRigidbody = GetComponent<Rigidbody>();

        if (itemColliders == null || itemColliders.Length == 0)
            itemColliders = GetComponentsInChildren<Collider>(true);
    }

    public void Pickup(Transform holdPoint, Transform followTransform = null)
    {
        if (holdPoint == null)
            return;

        IsHeld = true;
        SetCollidersEnabled(false);

        if (itemRigidbody != null)
        {
            itemRigidbody.isKinematic = true;
            itemRigidbody.useGravity = false;
            itemRigidbody.linearVelocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
        }

        _heldAnchor = holdPoint;
        _heldRotationSource = followTransform != null ? followTransform : holdPoint;

        transform.SetParent(_heldAnchor, false);
        transform.localPosition = heldLocalPosition;
        UpdateHeldTransform();
    }

    public void Drop(Vector3 impulse)
    {
        IsHeld = false;
        _heldAnchor = null;
        _heldRotationSource = null;
        transform.SetParent(null, true);
        SetCollidersEnabled(true);

        if (itemRigidbody != null)
        {
            itemRigidbody.isKinematic = false;
            itemRigidbody.useGravity = true;
            itemRigidbody.AddForce(impulse, ForceMode.Impulse);
        }
    }

    void LateUpdate()
    {
        if (!IsHeld || _heldAnchor == null)
            return;

        UpdateHeldTransform();
    }

    public void ToggleLight()
    {
        CacheLights();

        if (_lights == null || _lights.Length == 0)
            return;

        bool anyEnabled = false;
        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light == null)
                continue;

            if (light.enabled)
            {
                anyEnabled = true;
                break;
            }
        }

        bool enable = !anyEnabled;

        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light == null)
                continue;

            light.enabled = enable;
        }
    }

    void CacheLights()
    {
        if (flashlightLight != null)
        {
            _lights = new[] { flashlightLight };
            return;
        }

        _lights = GetComponentsInChildren<Light>(true);
        if (_lights.Length > 0)
            flashlightLight = _lights[0];
    }

    void CacheHeldRotation()
    {
        _heldLocalRotation = Quaternion.Euler(heldLocalEulerAngles);
        if (!alignHeldRotationToLight || flashlightLight == null)
            return;

        Quaternion lightRotationRelativeToRoot = Quaternion.Inverse(transform.rotation) * flashlightLight.transform.rotation;
        _heldLocalRotation = Quaternion.Inverse(lightRotationRelativeToRoot);
    }

    void UpdateHeldTransform()
    {
        if (_heldAnchor == null)
            return;

        transform.localPosition = heldLocalPosition;

        Transform rotationSource = _heldRotationSource != null ? _heldRotationSource : _heldAnchor;
        Quaternion worldRotation = rotationSource.rotation * _heldLocalRotation;
        transform.localRotation = Quaternion.Inverse(_heldAnchor.rotation) * worldRotation;
    }

    void SetCollidersEnabled(bool enabled)
    {
        if (itemColliders == null)
            return;

        foreach (Collider itemCollider in itemColliders)
        {
            if (itemCollider != null)
                itemCollider.enabled = enabled;
        }
    }
}
