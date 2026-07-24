using UnityEditor;
using UnityEngine;

/// <summary>
/// Play-mode tuner for how a hand-socket item sits in the right fist. Edits the three values that decide it —
/// the item's hold rotation, the per-item wrist tilt, and the grip point — live on the held instance, then bakes
/// them back to the source prefab. Nothing is added to the player, so there is no component to forget to remove
/// (unlike <c>HandPoseTuner</c>, which freezes the arm if it is left enabled).
///
/// Flow: enter play mode, pick the item up, open Tools/Held Item Grip Tuner, drag until it reads right, Save.
/// </summary>
public class HeldItemGripTunerWindow : EditorWindow
{
    [MenuItem("Tools/Held Item Grip Tuner")]
    static void Open()
    {
        GetWindow<HeldItemGripTunerWindow>("Grip Tuner").minSize = new Vector2(340f, 320f);
    }

    GrabbableInventoryItem _item;
    Transform _player;
    string _prefabPath;
    Vector3 _holdRotation;
    Vector3 _wristTilt;
    Vector3 _gripPoint;
    HeldGripStyle _gripStyle;
    string _status = "";

    void OnEnable() { EditorApplication.update += DriveRepaint; }
    void OnDisable() { EditorApplication.update -= DriveRepaint; }

    void DriveRepaint()
    {
        if (Application.isPlaying)
            Repaint();
    }

    void OnGUI()
    {
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter play mode and hold an item to tune it.", MessageType.Info);
            return;
        }

        RefreshTarget();

        if (_item == null)
        {
            EditorGUILayout.HelpBox("No hand-socket item held right now. Pick one up.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("Item", _item.name);
        EditorGUILayout.LabelField("Source prefab", string.IsNullOrEmpty(_prefabPath) ? "<not resolved>" : _prefabPath);
        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();

        _gripStyle = (HeldGripStyle)EditorGUILayout.EnumPopup("Grip style", _gripStyle);
        EditorGUILayout.LabelField("Fist = thin items, Pinch = flat (seats on PinchSocket_R), Cup = cans and rolls.",
            EditorStyles.miniLabel);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Hold rotation", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("How the item is oriented, independent of the hand.", EditorStyles.miniLabel);
        _holdRotation = EditorGUILayout.Vector3Field("heldRotationOffsetEuler", _holdRotation);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Wrist tilt (player space)", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Turns the whole fist. X negative tilts the hand up.", EditorStyles.miniLabel);
        _wristTilt.x = EditorGUILayout.Slider("Tilt up / down", _wristTilt.x, -180f, 180f);
        _wristTilt.y = EditorGUILayout.Slider("Turn left / right", _wristTilt.y, -180f, 180f);
        _wristTilt.z = EditorGUILayout.Slider("Roll", _wristTilt.z, -180f, 180f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Grip point (item root-local)", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Which point of the item sits in the fist.", EditorStyles.miniLabel);
        _gripPoint = EditorGUILayout.Vector3Field("GripPoint_R", _gripPoint);

        if (EditorGUI.EndChangeCheck())
            ApplyToLiveInstance();

        EditorGUILayout.Space();
        if (GUILayout.Button("Align fist to item's long axis"))
        {
            AlignFistToItem();
            ApplyToLiveInstance();
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_prefabPath)))
            {
                if (GUILayout.Button("Save to prefab", GUILayout.Height(26f)))
                    SaveToPrefab();
            }
            if (GUILayout.Button("Reload from prefab", GUILayout.Height(26f)))
                PullFromLiveInstance(force: true);
        }

        if (!string.IsNullOrEmpty(_status))
            EditorGUILayout.HelpBox(_status, MessageType.None);
    }

    void RefreshTarget()
    {
        if (_item != null && _item.IsHeld && !_item.IsStashed)
            return;

        _item = null;
        _player = null;
        foreach (var g in Object.FindObjectsByType<GrabbableInventoryItem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!g.IsHeld || g.IsStashed || !g.HeldAttachToHandSocket)
                continue;
            _item = g;
            break;
        }
        if (_item == null)
            return;

        foreach (var pc in Object.FindObjectsByType<PlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (_item.transform.IsChildOf(pc.transform))
            {
                _player = pc.transform;
                break;
            }
        }
        _prefabPath = ResolvePrefabPath(_item);
        PullFromLiveInstance(force: true);
    }

    void PullFromLiveInstance(bool force)
    {
        if (_item == null || !force)
            return;
        var so = new SerializedObject(_item);
        _holdRotation = so.FindProperty("heldRotationOffsetEuler").vector3Value;
        _wristTilt = so.FindProperty("heldWristEulerOffset").vector3Value;
        _gripStyle = (HeldGripStyle)so.FindProperty("gripStyle").enumValueIndex;
        var grip = so.FindProperty("gripPointRight").objectReferenceValue as Transform;
        _gripPoint = grip != null ? grip.localPosition : Vector3.zero;
        _status = "";
    }

    void ApplyToLiveInstance()
    {
        if (_item == null)
            return;
        var so = new SerializedObject(_item);
        so.FindProperty("heldRotationOffsetEuler").vector3Value = _holdRotation;
        so.FindProperty("heldWristEulerOffset").vector3Value = _wristTilt;
        so.FindProperty("gripStyle").enumValueIndex = (int)_gripStyle;
        var grip = so.FindProperty("gripPointRight").objectReferenceValue as Transform;
        if (grip == null)
        {
            var created = new GameObject("GripPoint_R");
            created.transform.SetParent(_item.transform, false);
            grip = created.transform;
            so.FindProperty("gripPointRight").objectReferenceValue = grip;
        }
        grip.localPosition = _gripPoint;
        grip.localRotation = Quaternion.identity;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>
    /// Rotates the fist so the finger tunnel (the knuckle line, which is the axis a gripped cylinder lies along)
    /// points down the item's longest axis — the difference between "resting in the palm" and "wrapped around it".
    /// </summary>
    void AlignFistToItem()
    {
        if (_item == null || _player == null)
        {
            _status = "Need a held item on a player to align.";
            return;
        }
        var animator = _player.GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            _status = "No humanoid animator on the player.";
            return;
        }
        var indexKnuckle = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        var littleKnuckle = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        if (indexKnuckle == null || littleKnuckle == null)
        {
            _status = "Rig has no finger bones to measure the tunnel from.";
            return;
        }

        Vector3 tunnel = (indexKnuckle.position - littleKnuckle.position).normalized;
        Vector3 itemAxis = LongestWorldAxis(_item);
        if (Vector3.Dot(tunnel, itemAxis) < 0f)
            itemAxis = -itemAxis;

        Quaternion delta = Quaternion.FromToRotation(tunnel, itemAxis);
        Quaternion updated = Quaternion.Inverse(_player.rotation) * delta * _player.rotation * Quaternion.Euler(_wristTilt);
        _wristTilt = updated.eulerAngles;
        for (int i = 0; i < 3; i++)
            if (_wristTilt[i] > 180f) _wristTilt[i] -= 360f;
        _status = "Fist aligned to the item's long axis. Nudge the sliders from here.";
    }

    static Vector3 LongestWorldAxis(GrabbableInventoryItem item)
    {
        var root = item.transform;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (var r in item.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            var filter = r.GetComponent<MeshFilter>();
            if (filter != null) mesh = filter.sharedMesh;
            var skinned = r as SkinnedMeshRenderer;
            if (skinned != null) mesh = skinned.sharedMesh;
            if (mesh == null) continue;

            var toRootLocal = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            var b = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                var p = toRootLocal.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        if (float.IsInfinity(min.x))
            return root.forward;

        // Longest axis measured in world units, so a non-uniform root scale is accounted for.
        Vector3 scaled = Vector3.Scale(max - min, root.localScale);
        Vector3 localAxis = scaled.x >= scaled.y && scaled.x >= scaled.z ? Vector3.right
            : scaled.y >= scaled.z ? Vector3.up : Vector3.forward;
        return root.TransformDirection(localAxis).normalized;
    }

    string ResolvePrefabPath(GrabbableInventoryItem item)
    {
        string wanted = item.name.Replace("(Clone)", string.Empty).Trim();
        string byTypeId = null;
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Prefabs" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            var candidate = go.GetComponent<GrabbableInventoryItem>();
            if (candidate == null) continue;
            if (go.name == wanted) return path;
            if (byTypeId == null && candidate.ItemTypeId != 0 && candidate.ItemTypeId == item.ItemTypeId)
                byTypeId = path;
        }
        return byTypeId;
    }

    void SaveToPrefab()
    {
        if (string.IsNullOrEmpty(_prefabPath))
        {
            _status = "Could not resolve the source prefab.";
            return;
        }

        var contents = PrefabUtility.LoadPrefabContents(_prefabPath);
        try
        {
            var target = contents.GetComponent<GrabbableInventoryItem>();
            if (target == null)
            {
                _status = "Prefab root has no GrabbableInventoryItem.";
                return;
            }
            var grip = contents.transform.Find("GripPoint_R");
            if (grip == null)
            {
                var created = new GameObject("GripPoint_R");
                created.layer = contents.layer;
                grip = created.transform;
                grip.SetParent(contents.transform, false);
            }
            grip.localPosition = _gripPoint;
            grip.localRotation = Quaternion.identity;
            grip.localScale = Vector3.one;

            var so = new SerializedObject(target);
            so.FindProperty("heldRotationOffsetEuler").vector3Value = _holdRotation;
            so.FindProperty("heldWristEulerOffset").vector3Value = _wristTilt;
            so.FindProperty("gripStyle").enumValueIndex = (int)_gripStyle;
            so.FindProperty("gripPointRight").objectReferenceValue = grip;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(contents, _prefabPath);
            _status = "Saved to " + _prefabPath;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }
}
