using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The dead player's view. Death is permanent for the section (see <see cref="NetworkPlayerRespawn"/>), so after
/// a short death cam — the existing ragdoll head view — this takes the player's own view node
/// (<see cref="PlayerController.LookPitchTransform"/>, the <c>CameraPitch</c> that carries the <c>PlayerView</c>
/// camera at identity) off the body and orbits it around a living teammate until the next section replaces the
/// avatar.
///
/// It moves the player's own camera rather than enabling a second one on purpose: that camera also carries the
/// AudioListener, the per-camera URP/post settings, and it is what <see cref="WorldRenderCuller"/>,
/// <see cref="MazeLightCuller"/>, <see cref="MazeDistanceFog"/> and the audio occlusion pass resolve as "the
/// local view", so culling, fog and hearing all follow the spectator with no changes of their own.
///
/// The node is NOT reparented: it stays wherever the ragdoll camera path parked it and this simply owns its world
/// pose every LateUpdate. Nothing else writes it after this while dead — PlayerController's look and shake are
/// gated off, <see cref="RagdollCameraCollision"/> stands down — and leaving the hierarchy alone keeps the
/// avatar's teardown (PlayerController.OnDisable hands the node back to its authored parent) exactly as it was;
/// a reparent under the avatar made that hand-back fail mid-deactivation.
///
/// Owner-only (or the lone player with no session); added at runtime by <see cref="PlayerController"/>.
/// Runs after <see cref="RagdollCameraCollision"/> (601) and before the culler (700) so the pass sees this
/// frame's camera.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(650)]
public class PlayerSpectatorCamera : MonoBehaviour
{
    [Header("Timing")]
    [Tooltip("Death-cam length used only when MultiplayerProjectSettings cannot be loaded (it owns the real value).")]
    [SerializeField, Min(0f)] float fallbackDeathCamSeconds = 3f;
    [Tooltip("Seconds to keep watching a teammate who just died before moving on to the next living one.")]
    [SerializeField, Min(0f)] float watchedDeathLingerSeconds = 2f;

    [Header("Orbit")]
    [SerializeField, Min(0.5f)] float orbitDistance = 2.8f;
    [SerializeField] float defaultPitchDegrees = 14f;
    [SerializeField] float minPitchDegrees = -40f;
    [SerializeField] float maxPitchDegrees = 75f;
    [Tooltip("Focus height above a standing target's feet (m) — shoulder height, so the orbit frames head and back.")]
    [SerializeField] float standingFocusHeight = 1.5f;
    [SerializeField] float crouchedFocusHeight = 1f;
    [Tooltip("Focus rise above the hips while the target is down (ragdoll) or held by an enemy (m).")]
    [SerializeField] float downedFocusRise = 0.3f;
    [Tooltip("Seconds for the focus to catch the target. Smooths remote interpolation and the 20 Hz ragdoll stream.")]
    [SerializeField, Min(0f)] float focusSmoothTime = 0.07f;
    [Tooltip("A target jumping farther than this in one frame (teleport orb) snaps the focus instead of streaking it through walls.")]
    [SerializeField, Min(0.5f)] float focusSnapDistance = 6f;
    [Tooltip("Seconds for the orbit to ease back out after a wall pulled it in. Pulling in is always instant.")]
    [SerializeField, Min(0.01f)] float distanceRecoverSmoothTime = 0.25f;

    [Header("Collision")]
    [SerializeField, Min(0.01f)] float collisionRadius = 0.2f;
    [SerializeField, Min(0f)] float collisionSkin = 0.06f;

    static PlayerSpectatorCamera s_local;

    PlayerController _controller;
    PlayerHealth _health;
    NetworkPlayerAvatar _avatar;
    RagdollCameraCollision _ragdollCameraCollision;
    float _deathCamSeconds;
    float _deadSince = -1f;

    Transform _view;
    Transform _savedViewParent;
    Vector3 _savedViewLocalPosition;
    Quaternion _savedViewLocalRotation;
    bool _spectating;

    readonly List<PlayerHealth> _targets = new();
    PlayerHealth _target;
    PlayerRagdollController _targetRagdoll;
    PlayerController _targetController;
    float _targetDiedAt = -1f;
    float _yaw;
    float _pitch;
    Vector3 _focus;
    Vector3 _focusVelocity;
    bool _snapFocus;
    float _distance;
    float _distanceVelocity;

    int _solidMask;
    readonly RaycastHit[] _hits = new RaycastHit[16];

    GameObject _hudRoot;
    CanvasGroup _hudGroup;
    TextMeshProUGUI _nameLabel;
    GameObject _cycleRow;
    float _nextNameRefresh;

    /// <summary>True once the camera has left the body for a teammate (not during the death cam).</summary>
    public bool IsSpectating => _spectating;

    /// <summary>
    /// Where the local player's view effectively is while spectating: the watched teammate's feet. For
    /// systems that ask "is the local player inside X" (e.g. <see cref="MazeEnvironmentZone"/>) — a dead player
    /// is lying somewhere else entirely, but sees and hears what the spectated player does.
    /// </summary>
    public static bool TryGetLocalSpectatedPosition(out Vector3 position)
    {
        if (s_local != null && s_local._spectating && s_local._target != null)
        {
            position = s_local._target.transform.position;
            return true;
        }

        position = default;
        return false;
    }

    void Awake()
    {
        _controller = GetComponent<PlayerController>();
        _health = GetComponent<PlayerHealth>();
        _avatar = GetComponent<NetworkPlayerAvatar>();

        MultiplayerProjectSettings settings = Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
        _deathCamSeconds = settings != null ? settings.DeathCamSeconds : fallbackDeathCamSeconds;

        _solidMask = BuildSolidMask();
    }

    void Start()
    {
        // Added by PlayerController.Awake, possibly before RagdollCameraCollision exists.
        _ragdollCameraCollision = GetComponent<RagdollCameraCollision>();
    }

    void OnDisable()
    {
        // Only ever reached as the avatar goes down (section switch, disconnect): the view node goes with it and
        // OnDestroy takes the HUD, so there is nothing to hand back.
        _spectating = false;
        if (s_local == this)
            s_local = null;
        _deadSince = -1f;
    }

    void OnDestroy()
    {
        if (s_local == this)
            s_local = null;
        if (_hudRoot != null)
            Destroy(_hudRoot);
    }

    /// <summary>
    /// Owner only. With no session at all the lone player is local; inside a session an unspawned (dormant)
    /// avatar is nobody's view.
    /// </summary>
    bool IsLocalView()
    {
        if (_avatar == null)
            return true;
        if (_avatar.IsSpawned)
            return _avatar.IsOwner;

        NetworkManager nm = NetworkManager.Singleton;
        return nm == null || !nm.IsListening;
    }

    void LateUpdate()
    {
        bool dead = _health != null && _health.IsDead;
        if (!dead || !IsLocalView())
        {
            _deadSince = -1f;
            if (_spectating)
                StopSpectating();
            return;
        }

        if (_deadSince < 0f)
            _deadSince = Time.time;

        RefreshLivingTargets();

        if (!_spectating)
        {
            if (Time.time - _deadSince < _deathCamSeconds)
                return;
            // Nobody left to watch: stay on the body. That is a team wipe, and its card is on the way.
            if (_targets.Count == 0)
                return;

            StartSpectating(_targets[0]);
            if (!_spectating)
                return;
        }

        if (!EnsureWatchableTarget())
        {
            StopSpectating();
            return;
        }

        HandleInput();
        UpdateCameraPose();
        UpdateHud();

        // The view is carried around by somebody else's movement, not this player's look input — same case
        // as a ragdoll tumble, so the culler must not wait for its throttled tick.
        WorldRenderCuller.RequestContinuousEvaluation();
    }

    // ---------------------------------------------------------------- targets

    void RefreshLivingTargets()
    {
        _targets.Clear();
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth candidate = players[i];
            if (IsExistingTeammate(candidate) && !candidate.IsDead)
                _targets.Add(candidate);
        }

        // Stable order, so cycling walks the same loop on every press.
        _targets.Sort((a, b) => SortKey(a).CompareTo(SortKey(b)));
    }

    static ulong SortKey(PlayerHealth player)
    {
        // Without a session there is one player, so there is nothing to order.
        return player.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned
            ? networkObject.OwnerClientId
            : 0UL;
    }

    /// <summary>Another player's avatar that is live on this peer (spawned, when there is a session).</summary>
    bool IsExistingTeammate(PlayerHealth candidate)
    {
        if (candidate == null || candidate == _health || !candidate.isActiveAndEnabled)
            return false;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;

        return candidate.TryGetComponent(out NetworkObject networkObject) && networkObject.IsSpawned;
    }

    /// <summary>
    /// Keeps <see cref="_target"/> pointing at someone worth watching. A teammate who dies on camera is watched
    /// a moment longer, then the view moves to the next living player; with nobody else alive it stays on that
    /// body. False when there is nobody at all (the watched avatar is gone and no one is alive).
    /// </summary>
    bool EnsureWatchableTarget()
    {
        bool targetExists = IsExistingTeammate(_target);
        if (targetExists && !_target.IsDead)
        {
            _targetDiedAt = -1f;
            return true;
        }

        if (targetExists && _targetDiedAt < 0f)
            _targetDiedAt = Time.time;

        bool lingering = targetExists && Time.time - _targetDiedAt < watchedDeathLingerSeconds;
        if (_targets.Count > 0 && !lingering)
        {
            SetTarget(PickRelative(_target, 1));
            return true;
        }

        return targetExists;
    }

    /// <summary>The living player <paramref name="step"/> places after <paramref name="from"/> in the loop.</summary>
    PlayerHealth PickRelative(PlayerHealth from, int step)
    {
        int count = _targets.Count;
        if (count == 0)
            return null;

        int index = _targets.IndexOf(from);
        if (index < 0)
            return _targets[step >= 0 ? 0 : count - 1];

        return _targets[((index + step) % count + count) % count];
    }

    void SetTarget(PlayerHealth target)
    {
        if (target == null)
            return;

        bool changed = target != _target;
        _target = target;
        _targetDiedAt = -1f;
        _targetRagdoll = target.GetComponent<PlayerRagdollController>();
        _targetController = target.GetComponent<PlayerController>();

        if (!changed)
            return;

        // Start behind the new target, looking where they look.
        _yaw = target.transform.eulerAngles.y;
        _pitch = defaultPitchDegrees;
        _snapFocus = true;
        _distance = orbitDistance;
        _distanceVelocity = 0f;
        _nextNameRefresh = 0f;
    }

    // ---------------------------------------------------------------- camera hand-over

    void StartSpectating(PlayerHealth target)
    {
        _view = _controller != null ? _controller.LookPitchTransform : null;
        if (_view == null)
        {
            Camera ownCamera = GetComponentInChildren<Camera>(true);
            _view = ownCamera != null ? ownCamera.transform : null;
        }
        if (_view == null)
            return;

        // Restore the camera and body BEFORE revealing the head below (see RagdollCameraCollision.ReleaseView).
        if (_ragdollCameraCollision != null)
            _ragdollCameraCollision.ReleaseView();

        _savedViewParent = _view.parent;
        _savedViewLocalPosition = _view.localPosition;
        _savedViewLocalRotation = _view.localRotation;

        _spectating = true;
        s_local = this;
        if (_avatar != null)
            _avatar.SetSpectatorViewActive(true);
        if (_controller != null)
            _controller.CaptureCursorForSpectating();

        _target = null;
        SetTarget(target);
        SetHudVisible(true);
    }

    void StopSpectating()
    {
        // Back onto the body exactly where the death cam left it; the ragdoll camera path takes over from there.
        if (_view != null && _view.parent == _savedViewParent)
            _view.SetLocalPositionAndRotation(_savedViewLocalPosition, _savedViewLocalRotation);

        _spectating = false;
        if (s_local == this)
            s_local = null;
        if (_avatar != null)
            _avatar.SetSpectatorViewActive(false);

        _target = null;
        _targetRagdoll = null;
        _targetController = null;
        SetHudVisible(false);
    }

    // ---------------------------------------------------------------- input

    void HandleInput()
    {
        if (PauseMenuController.BlocksGameplayInput || BlackjackOverlayController.IsInteractive
            || SkeletonRpsOverlayController.IsInteractive || CarnivalStoreOverlayController.IsInteractive)
            return;

        // A click on a loose cursor only buys it back — it must not also switch players.
        if (_controller != null && _controller.TryRecaptureCursorForSpectating())
            return;

        float mouseSensitivity = _controller != null ? _controller.MouseLookSensitivity : 0.08f;
        float padDegreesPerSecond = _controller != null ? _controller.GamepadLookSensitivityDegrees : 140f;

        Mouse mouse = Mouse.current;
        Gamepad pad = Gamepad.current;
        Keyboard keyboard = Keyboard.current;

        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            _yaw += delta.x * mouseSensitivity;
            _pitch -= delta.y * mouseSensitivity;
        }

        if (pad != null)
        {
            Vector2 stick = pad.rightStick.ReadValue();
            _yaw += stick.x * padDegreesPerSecond * Time.deltaTime;
            _pitch -= stick.y * padDegreesPerSecond * Time.deltaTime;
        }

        _pitch = Mathf.Clamp(_pitch, minPitchDegrees, maxPitchDegrees);

        // Gamepad left shoulder is the push-to-talk fallback, which the dead use for ghost chat — so not that.
        bool next = (mouse != null && mouse.leftButton.wasPressedThisFrame)
            || (pad != null && (pad.dpad.right.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame))
            || (keyboard != null && keyboard.rightArrowKey.wasPressedThisFrame);
        bool previous = (mouse != null && mouse.rightButton.wasPressedThisFrame)
            || (pad != null && pad.dpad.left.wasPressedThisFrame)
            || (keyboard != null && keyboard.leftArrowKey.wasPressedThisFrame);

        if (next != previous && _targets.Count > 0)
            SetTarget(PickRelative(_target, next ? 1 : -1));
    }

    // ---------------------------------------------------------------- pose

    void UpdateCameraPose()
    {
        Vector3 desiredFocus = ComputeFocus();
        if (_snapFocus || (desiredFocus - _focus).sqrMagnitude > focusSnapDistance * focusSnapDistance)
        {
            _focus = desiredFocus;
            _focusVelocity = Vector3.zero;
            _snapFocus = false;
        }
        else
        {
            _focus = Vector3.SmoothDamp(_focus, desiredFocus, ref _focusVelocity, focusSmoothTime);
        }

        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 back = rotation * Vector3.back;
        float allowed = ProbeOrbitDistance(_focus, back, orbitDistance);

        if (allowed < _distance)
        {
            _distance = allowed;
            _distanceVelocity = 0f;
        }
        else
        {
            _distance = Mathf.SmoothDamp(_distance, allowed, ref _distanceVelocity, distanceRecoverSmoothTime);
        }

        _view.SetPositionAndRotation(_focus + back * _distance, rotation);
    }

    Vector3 ComputeFocus()
    {
        if (_targetRagdoll != null && (_targetRagdoll.IsRagdolled || _targetRagdoll.IsHeld)
            && _targetRagdoll.HipsTransform != null)
        {
            return _targetRagdoll.HipsTransform.position + Vector3.up * downedFocusRise;
        }

        float height = _targetController != null && _targetController.IsCrouching
            ? crouchedFocusHeight
            : standingFocusHeight;
        return _target.transform.position + Vector3.up * height;
    }

    /// <summary>
    /// How far back the camera may sit before it would enter world geometry. Characters and loose physics props
    /// never block — the view should not pop in every time a bottle rolls past — and a cast that starts already
    /// overlapping something has no usable hit distance, so it is skipped.
    /// </summary>
    float ProbeOrbitDistance(Vector3 origin, Vector3 direction, float maxDistance)
    {
        int count = Physics.SphereCastNonAlloc(origin, collisionRadius, direction, _hits, maxDistance, _solidMask,
            QueryTriggerInteraction.Ignore);

        float nearest = maxDistance;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            Collider hitCollider = hit.collider;
            if (hitCollider == null)
                continue;
            if (hit.distance <= 0f && hit.point == Vector3.zero)
                continue;
            if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                continue;
            if (hitCollider.GetComponentInParent<PlayerHealth>() != null)
                continue;

            if (hit.distance < nearest)
                nearest = hit.distance;
        }

        return nearest >= maxDistance ? maxDistance : Mathf.Max(0.05f, nearest - collisionSkin);
    }

    /// <summary>World geometry only: the same exclusions as <see cref="RagdollCameraCollision"/>, plus the remote-player blocking proxies on Ignore Raycast.</summary>
    int BuildSolidMask()
    {
        int mask = ~0;
        string[] excluded = { "Player", "Enemy", "Jailor", "Clown", "Ignore Raycast" };
        foreach (string layerName in excluded)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0)
                mask &= ~(1 << layer);
        }

        mask &= ~(1 << gameObject.layer);
        return mask;
    }

    // ---------------------------------------------------------------- HUD

    void SetHudVisible(bool visible)
    {
        if (visible && _hudRoot == null)
            BuildHud();
        if (_hudRoot != null)
            _hudRoot.SetActive(visible);
        if (visible && _hudGroup != null)
            _hudGroup.alpha = 0f;
    }

    void UpdateHud()
    {
        if (_hudGroup == null)
            return;

        _hudGroup.alpha = Mathf.MoveTowards(_hudGroup.alpha, 1f, Time.unscaledDeltaTime * 4f);

        bool canCycle = _targets.Count > 1;
        if (_cycleRow.activeSelf != canCycle)
            _cycleRow.SetActive(canCycle);

        // Names can land after the avatar does (a joiner's persona name arrives on its own message).
        if (Time.unscaledTime >= _nextNameRefresh)
        {
            _nextNameRefresh = Time.unscaledTime + 1f;
            string display = PlayerIdentity.DisplayName(_target);
            if (_nameLabel.text != display)
                _nameLabel.text = display;
        }
    }

    void BuildHud()
    {
        Canvas canvas = HudKit.EnsureHudCanvas();

        var root = new GameObject("SpectatorHud", typeof(RectTransform));
        root.layer = 5;
        var rootRt = (RectTransform)root.transform;
        rootRt.SetParent(canvas.transform, false);
        rootRt.anchorMin = new Vector2(0.5f, 0f);
        rootRt.anchorMax = new Vector2(0.5f, 0f);
        rootRt.pivot = new Vector2(0.5f, 0f);
        rootRt.anchoredPosition = new Vector2(0f, 64f);
        rootRt.localRotation = Quaternion.Euler(0f, 0f, -0.4f);

        _hudGroup = root.AddComponent<CanvasGroup>();
        _hudGroup.alpha = 0f;
        _hudGroup.blocksRaycasts = false;
        _hudGroup.interactable = false;

        HudKit.AddPlate(root, 0.78f, 0.24f);

        var layout = root.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(26, 26, 10, 12);
        layout.spacing = 2f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var fitter = root.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        CreateLabel(rootRt, "Overline", "SPECTATING", 13f, MenuTheme.Mist, 9f);
        _nameLabel = CreateLabel(rootRt, "Name", string.Empty, 28f, MenuTheme.Bone, 3f);

        var row = new GameObject("Cycle", typeof(RectTransform));
        row.layer = 5;
        var rowRt = (RectTransform)row.transform;
        rowRt.SetParent(rootRt, false);
        var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 8f;
        rowLayout.padding = new RectOffset(0, 0, 4, 0);
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        CreateWideKeycap(rowRt, "RMB");
        CreateLabel(rowRt, "Prev", "PREV", 14f, MenuTheme.Mist, 3f);
        CreateLabel(rowRt, "Gap", string.Empty, 14f, MenuTheme.Mist, 0f).GetComponent<LayoutElement>().minWidth = 14f;
        CreateWideKeycap(rowRt, "LMB");
        CreateLabel(rowRt, "Next", "NEXT", 14f, MenuTheme.Mist, 3f);
        _cycleRow = row;

        _hudRoot = root;
    }

    static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float size, Color color, float spacing)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        ((RectTransform)go.transform).SetParent(parent, false);
        var label = go.AddComponent<TextMeshProUGUI>();
        label.font = MenuTheme.DisplayFont;
        label.text = text;
        label.fontSize = size;
        label.color = color;
        label.characterSpacing = spacing;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.richText = false; // persona names are user-authored
        label.raycastTarget = false;
        go.AddComponent<LayoutElement>();
        return label;
    }

    /// <summary>Mouse-button legends are three letters, wider than the square key caps the HUD uses for E.</summary>
    static void CreateWideKeycap(Transform parent, string key)
    {
        const float height = 27f;
        const float width = 42f;
        RectTransform keycap = HudKit.CreateKeycap(parent, key, height);
        keycap.sizeDelta = new Vector2(width, height);
        LayoutElement element = keycap.GetComponent<LayoutElement>();
        element.minWidth = width;
        element.preferredWidth = width;
    }
}
