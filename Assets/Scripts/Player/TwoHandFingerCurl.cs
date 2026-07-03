using UnityEngine;

/// <summary>
/// Forces both hands into a matching flat open-palm while the two-hand carry (HoldPose == 2, e.g. StarBall)
/// is active, by straightening the finger bones after animation. This is needed because the Item Hold layer
/// only masks the RIGHT arm — the left hand's fingers otherwise come from the locomotion pose, so the two
/// hands wouldn't match. Straightening both identically gives a clean symmetric open-palm press. One-hand
/// poses and empty hands are untouched.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(650)]
public class TwoHandFingerCurl : MonoBehaviour
{
    [Range(0f, 1f)] public float openAmount = 1f;
    [Tooltip("Only open the hands while the two-hand hold (HoldPose == 2) is active.")]
    public bool onlyWhenTwoHandHold = true;

    static readonly int HoldPoseHash = Animator.StringToHash("HoldPose");
    Animator _an;

    void LateUpdate()
    {
        if (_an == null) _an = GetComponent<Animator>();
        if (_an == null || !_an.isHuman) return;
        if (onlyWhenTwoHandHold && _an.GetInteger(HoldPoseHash) != 2) return;
        if (openAmount <= 0.001f) return;
        OpenHand(false);
        OpenHand(true);
    }

    void OpenHand(bool left)
    {
        Transform hand = _an.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        if (hand == null) return;
        OpenFinger(left, "Index", hand);
        OpenFinger(left, "Middle", hand);
        OpenFinger(left, "Ring", hand);
        OpenFinger(left, "Little", hand);
    }

    void OpenFinger(bool left, string finger, Transform hand)
    {
        Transform p = Bone(left, finger, "Proximal");
        Transform i = Bone(left, finger, "Intermediate");
        Transform d = Bone(left, finger, "Distal");
        if (p != null && i != null) Straighten(hand, p, i);
        if (p != null && i != null && d != null) Straighten(p, i, d);
    }

    // Rotate `bone` so the segment to `child` continues straight out of `parent` (flattens the joint).
    void Straighten(Transform parent, Transform bone, Transform child)
    {
        Vector3 pd = bone.position - parent.position;
        Vector3 bd = child.position - bone.position;
        if (pd.sqrMagnitude < 1e-8f || bd.sqrMagnitude < 1e-8f) return;
        Quaternion full = Quaternion.FromToRotation(bd.normalized, pd.normalized);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, full, openAmount) * bone.rotation;
    }

    Transform Bone(bool left, string finger, string joint)
    {
        string n = (left ? "Left" : "Right") + finger + joint;
        return System.Enum.TryParse(n, out HumanBodyBones hbb) ? _an.GetBoneTransform(hbb) : null;
    }
}
