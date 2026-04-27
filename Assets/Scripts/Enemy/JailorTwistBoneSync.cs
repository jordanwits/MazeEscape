using UnityEngine;

/// <summary>
/// Rigify-style twist bones (arm_twist, forearm_twist, leg_twist, …) are not animated by Mixamo
/// clips. When only the main stretch bones move, vertices weighted to twist helpers collapse at
/// elbows, shoulders, and knees. This applies a fraction of each driver bone's local rotation
/// delta to the matching twist bones after the Animator runs.
/// </summary>
[DisallowMultipleComponent]
public class JailorTwistBoneSync : MonoBehaviour
{
    [SerializeField] float armTwistAmount = 0.28f;
    [SerializeField] float armTwist2Amount = 0.42f;
    [SerializeField] float forearmTwistAmount = 0.38f;
    [SerializeField] float forearmTwist2Amount = 0.52f;
    [SerializeField] float thighTwistAmount = 0.3f;
    [SerializeField] float thighTwist2Amount = 0.45f;
    [SerializeField] float legTwistAmount = 0.35f;
    [SerializeField] float legTwist2Amount = 0.5f;

    struct TwistPair
    {
        public Transform Twist;
        public Transform Driver;
        public float Amount;
        public Quaternion RestTwistLocal;
        public Quaternion RestDriverLocal;
    }

    TwistPair[] _pairs;

    void Awake()
    {
        Transform root = transform.Find("root/root.x");
        if (root == null)
        {
            enabled = false;
            return;
        }

        string spineToShoulder = "spine_01.x/spine_02.x/spine_03.x/spine_04.x/spine_05.x/shoulder";

        var list = new System.Collections.Generic.List<TwistPair>(32);
        void AddLimb(string sideDot, float arm, float arm2, float fore, float fore2, float thigh, float thigh2, float leg, float leg2)
        {
            Transform shoulder = root.Find($"{spineToShoulder}{sideDot}");
            if (shoulder == null)
                return;

            Transform upperArm = shoulder.Find($"arm_stretch{sideDot}");
            Transform forearm = upperArm != null ? upperArm.Find($"forearm_stretch{sideDot}") : null;
            Transform thighBone = root.Find($"thigh_stretch{sideDot}");
            Transform shinBone = thighBone != null ? thighBone.Find($"leg_stretch{sideDot}") : null;

            void AddPair(Transform twist, Transform driver, float amount)
            {
                if (twist == null || driver == null || amount <= 0f)
                    return;
                list.Add(new TwistPair
                {
                    Twist = twist,
                    Driver = driver,
                    Amount = amount,
                    RestTwistLocal = twist.localRotation,
                    RestDriverLocal = driver.localRotation
                });
            }

            if (upperArm != null)
            {
                AddPair(upperArm.Find($"arm_twist{sideDot}"), upperArm, arm);
                AddPair(upperArm.Find($"arm_twist_2{sideDot}"), upperArm, arm2);
            }

            if (forearm != null)
            {
                AddPair(forearm.Find($"forearm_twist{sideDot}"), forearm, fore);
                AddPair(forearm.Find($"forearm_twist_2{sideDot}"), forearm, fore2);
            }

            if (thighBone != null)
            {
                AddPair(thighBone.Find($"thigh_twist{sideDot}"), thighBone, thigh);
                AddPair(thighBone.Find($"thigh_twist_2{sideDot}"), thighBone, thigh2);
            }

            if (shinBone != null)
            {
                AddPair(shinBone.Find($"leg_twist{sideDot}"), shinBone, leg);
                AddPair(shinBone.Find($"leg_twist_2{sideDot}"), shinBone, leg2);
            }
        }

        AddLimb(".l", armTwistAmount, armTwist2Amount, forearmTwistAmount, forearmTwist2Amount,
            thighTwistAmount, thighTwist2Amount, legTwistAmount, legTwist2Amount);
        AddLimb(".r", armTwistAmount, armTwist2Amount, forearmTwistAmount, forearmTwist2Amount,
            thighTwistAmount, thighTwist2Amount, legTwistAmount, legTwist2Amount);

        _pairs = list.ToArray();
        if (_pairs.Length == 0)
            enabled = false;
    }

    void LateUpdate()
    {
        if (_pairs == null)
            return;

        for (int i = 0; i < _pairs.Length; i++)
        {
            TwistPair p = _pairs[i];
            if (p.Twist == null || p.Driver == null)
                continue;

            Quaternion delta = Quaternion.Inverse(p.RestDriverLocal) * p.Driver.localRotation;
            p.Twist.localRotation = p.RestTwistLocal * Quaternion.Slerp(Quaternion.identity, delta, p.Amount);
        }
    }
}
