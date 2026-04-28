using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class ZombieAnimatorSetup
{
    const string ZombiePrefabPath = "Assets/Prefabs/Enemies/Zombie/Zombie.prefab";
    const string ZombieControllerPath = "Assets/Prefabs/Enemies/Zombie/ZombieAnimator.controller";
    const string UpperBodyMaskPath = "Assets/Prefabs/Enemies/ZombieUpperBodyMask.mask";
    const string AutoRepairSessionKey = "MazeEscape.ZombieAnimatorAutoRepairRan";

    static readonly string[] ZombieModelPaths =
    {
        "Assets/Prefabs/Enemies/Warzombie F Pedroso.fbx",
        "Assets/Enemy/Warzombie F Pedroso.fbx"
    };

    const string IdleClipPath = "Assets/Prefabs/Enemies/ZombieIdle.anim";
    const string WalkClipPath = "Assets/Prefabs/Enemies/ZombieWalk.anim";
    const string AttackClipPath = "Assets/Prefabs/Enemies/ZombieAttack.anim";
    const string HitReactionClipPath = "Assets/Enemy/HitReaction.anim";
    const string Death1ClipPath = "Assets/Prefabs/Enemies/ZombieDeath1.anim";
    const string Death2ClipPath = "Assets/Prefabs/Enemies/ZombieDeath2.anim";

    [InitializeOnLoadMethod]
    static void AutoRepairBrokenControllerOnce()
    {
        if (SessionState.GetBool(AutoRepairSessionKey, false))
            return;

        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool(AutoRepairSessionKey, false))
                return;

            SessionState.SetBool(AutoRepairSessionKey, true);

            try
            {
                if (NeedsControllerRepair())
                    RepairZombieAnimator();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        };
    }

    [MenuItem("Maze Escape/Repair Zombie Animator")]
    public static void RepairZombieAnimator()
    {
        AnimatorController controller = RebuildZombieAnimatorController();
        ReassignZombiePrefab(controller);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("Repaired ZombieAnimator.controller and reassigned the zombie prefab.");
    }

    [MenuItem("Maze Escape/Setup Zombie Animator")]
    public static void SetupZombieAnimator()
    {
        RepairZombieAnimator();
    }

    static bool NeedsControllerRepair()
    {
        string fullPath = GetAbsoluteProjectPath(ZombieControllerPath);
        if (!File.Exists(fullPath))
            return true;

        string controllerText = File.ReadAllText(fullPath);

        // The handwritten placeholder IDs are from the corrupted graph variant.
        return controllerText.Contains("1234567890123456789")
            || controllerText.Contains("1234567890123456790")
            || controllerText.Contains("1234567890123456791");
    }

    static AnimatorController RebuildZombieAnimatorController()
    {
        DeleteControllerFilePreservingMeta();
        AssetDatabase.Refresh();

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ZombieControllerPath);
        if (controller == null)
            throw new InvalidOperationException("Failed to create a clean ZombieAnimator.controller asset.");

        AddParameters(controller);
        ConfigureBaseLayer(controller);
        ConfigureUpperBodyLayer(controller);

        EditorUtility.SetDirty(controller);
        AssetDatabase.ImportAsset(ZombieControllerPath, ImportAssetOptions.ForceSynchronousImport);

        return controller;
    }

    static void DeleteControllerFilePreservingMeta()
    {
        string fullPath = GetAbsoluteProjectPath(ZombieControllerPath);
        if (!File.Exists(fullPath))
            return;

        File.Delete(fullPath);
    }

    static void AddParameters(AnimatorController controller)
    {
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        controller.AddParameter("VerticalVelocity", AnimatorControllerParameterType.Float);
        controller.AddParameter("IsDead", AnimatorControllerParameterType.Bool);
        controller.AddParameter("DeathIndex", AnimatorControllerParameterType.Int);
        controller.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("HitReaction", AnimatorControllerParameterType.Trigger);
    }

    static void ConfigureBaseLayer(AnimatorController controller)
    {
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        stateMachine.name = "Base Layer";

        AnimatorState idleState = AddState(stateMachine, "Idle", LoadRequiredClip(IdleClipPath), new Vector3(240f, 120f));
        AnimatorState walkState = AddState(stateMachine, "Walk", LoadRequiredClip(WalkClipPath), new Vector3(510f, -150f));
        AnimatorState hitReactionState = AddState(stateMachine, "HitReaction", LoadRequiredClip(HitReactionClipPath), new Vector3(170f, -90f));
        AnimatorState death1State = AddState(stateMachine, "Death1", LoadRequiredClip(Death1ClipPath), new Vector3(760f, 360f));
        AnimatorState death2State = AddState(stateMachine, "Death2", LoadRequiredClip(Death2ClipPath), new Vector3(760f, 500f));

        walkState.speed = 2.7f;
        stateMachine.defaultState = idleState;

        AddTransition(idleState, walkState, false, 0.05f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        });
        AddTransition(walkState, idleState, false, 0.05f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        });

        AddTransition(hitReactionState, idleState, true, 0.1f, null, 1f);

        AddAnyStateTransition(stateMachine, hitReactionState, 0.1f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
            transition.AddCondition(AnimatorConditionMode.If, 0f, "HitReaction");
        });
        AddAnyStateTransition(stateMachine, death1State, 0.05f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.If, 0f, "IsDead");
            transition.AddCondition(AnimatorConditionMode.Equals, 0f, "DeathIndex");
        });
        AddAnyStateTransition(stateMachine, death2State, 0.05f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.If, 0f, "IsDead");
            transition.AddCondition(AnimatorConditionMode.Equals, 1f, "DeathIndex");
        });
    }

    static void ConfigureUpperBodyLayer(AnimatorController controller)
    {
        AvatarMask upperBodyMask = LoadRequiredMask(UpperBodyMaskPath);

        AnimatorStateMachine upperBodyStateMachine = new AnimatorStateMachine
        {
            name = "Upper Body"
        };
        AssetDatabase.AddObjectToAsset(upperBodyStateMachine, controller);

        AnimatorControllerLayer upperBodyLayer = new AnimatorControllerLayer
        {
            name = "Upper Body",
            defaultWeight = 1f,
            avatarMask = upperBodyMask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            iKPass = false,
            syncedLayerAffectsTiming = false,
            syncedLayerIndex = -1,
            stateMachine = upperBodyStateMachine
        };

        AnimatorControllerLayer[] layers = controller.layers;
        Array.Resize(ref layers, layers.Length + 1);
        layers[layers.Length - 1] = upperBodyLayer;
        controller.layers = layers;

        AnimatorState emptyState = AddState(upperBodyStateMachine, "Empty", null, new Vector3(240f, 120f));
        emptyState.writeDefaultValues = false;

        AnimatorState attackState = AddState(upperBodyStateMachine, "Attack", LoadRequiredClip(AttackClipPath), new Vector3(500f, 120f));
        upperBodyStateMachine.defaultState = emptyState;

        AddAnyStateTransition(upperBodyStateMachine, attackState, 0.08f, transition =>
        {
            transition.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsDead");
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Attack");
        });

        AddTransition(attackState, emptyState, true, 0.1f, null, 0.9f);
    }

    static void ReassignZombiePrefab(AnimatorController controller)
    {
        GameObject zombieRoot = PrefabUtility.LoadPrefabContents(ZombiePrefabPath);
        try
        {
            Animator animator = GetOrAddComponent<Animator>(zombieRoot);
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;

            if (animator.avatar == null)
                animator.avatar = LoadZombieAvatar();

            EditorUtility.SetDirty(zombieRoot);
            PrefabUtility.SaveAsPrefabAsset(zombieRoot, ZombiePrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(zombieRoot);
        }
    }

    static Avatar LoadZombieAvatar()
    {
        foreach (string modelPath in ZombieModelPaths)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
            Avatar avatar = assets.OfType<Avatar>().FirstOrDefault(candidate => candidate != null && candidate.isValid);
            if (avatar != null)
                return avatar;
        }

        Debug.LogWarning("Zombie avatar could not be found automatically. Assign the avatar on the Animator if clips do not play.");
        return null;
    }

    static AnimationClip LoadRequiredClip(string path)
    {
        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (clip == null)
            throw new FileNotFoundException($"Missing required zombie animation clip at {path}");

        return clip;
    }

    static AvatarMask LoadRequiredMask(string path)
    {
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask == null)
            throw new FileNotFoundException($"Missing required upper body mask at {path}");

        return mask;
    }

    static AnimatorState AddState(AnimatorStateMachine stateMachine, string stateName, Motion motion, Vector3 position)
    {
        AnimatorState state = stateMachine.AddState(stateName, position);
        state.motion = motion;
        state.writeDefaultValues = motion != null;
        return state;
    }

    static void AddTransition(
        AnimatorState fromState,
        AnimatorState toState,
        bool hasExitTime,
        float duration,
        Action<AnimatorStateTransition> configureConditions,
        float exitTime = 0f)
    {
        AnimatorStateTransition transition = fromState.AddTransition(toState);
        transition.hasExitTime = hasExitTime;
        transition.exitTime = exitTime;
        transition.duration = duration;
        transition.hasFixedDuration = true;
        transition.canTransitionToSelf = false;
        transition.interruptionSource = TransitionInterruptionSource.None;
        configureConditions?.Invoke(transition);
    }

    static void AddAnyStateTransition(
        AnimatorStateMachine stateMachine,
        AnimatorState destinationState,
        float duration,
        Action<AnimatorStateTransition> configureConditions)
    {
        AnimatorStateTransition transition = stateMachine.AddAnyStateTransition(destinationState);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.hasFixedDuration = true;
        transition.canTransitionToSelf = false;
        transition.interruptionSource = TransitionInterruptionSource.None;
        configureConditions?.Invoke(transition);
    }

    static string GetAbsoluteProjectPath(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetPath));
    }

    static T GetOrAddComponent<T>(GameObject target) where T : Component
    {
        T component = target.GetComponent<T>();
        if (component == null)
            component = target.AddComponent<T>();

        return component;
    }
}
