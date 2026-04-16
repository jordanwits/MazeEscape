using System;
using System.Reflection;
using Unity.Netcode.Components;
using UnityEngine;

public class OwnerNetworkAnimator : NetworkAnimator
{
    static FieldInfo s_AnimatorParameterEntriesField;
    static Type s_AnimatorParametersListContainerType;

    static void CacheReflection()
    {
        if (s_AnimatorParameterEntriesField != null)
            return;

        const BindingFlags instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        s_AnimatorParameterEntriesField = typeof(NetworkAnimator).GetField("AnimatorParameterEntries", instanceNonPublic);
        s_AnimatorParametersListContainerType = typeof(NetworkAnimator).GetNestedType(
            "AnimatorParametersListContainer",
            BindingFlags.NonPublic);
    }

    static void EnsureParameterListContainerExists(NetworkAnimator target)
    {
        CacheReflection();
        if (target == null || s_AnimatorParameterEntriesField == null || s_AnimatorParametersListContainerType == null)
            return;

        object existing = s_AnimatorParameterEntriesField.GetValue(target);
        if (existing != null)
            return;

        object container = Activator.CreateInstance(s_AnimatorParametersListContainerType);
        s_AnimatorParameterEntriesField.SetValue(target, container);
    }

#if UNITY_EDITOR
    void Reset()
    {
        if (Animator == null)
            Animator = GetComponent<Animator>();

        EnsureParameterListContainerExists(this);
    }
#endif

    protected override void Awake()
    {
        // Runtime AddComponent leaves serialized fields empty. NetworkAnimator assumes AnimatorParameterEntries
        // is non-null (Awake line ~789) and ProcessParameterEntries (editor OnValidate) iterates the same list.
        if (Animator == null)
            Animator = GetComponent<Animator>();

        EnsureParameterListContainerExists(this);

        base.Awake();
    }

    protected override bool OnIsServerAuthoritative()
    {
        return false;
    }
}
