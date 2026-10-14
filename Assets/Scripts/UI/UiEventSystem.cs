using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The game's one <see cref="EventSystem"/>. Unity's UI wants exactly one active at a time, and every screen here is
/// built at runtime, so a single instance is made when the game starts (<see cref="MultiplayerBootstrap"/>) and kept
/// for the whole process; every menu and overlay calls <see cref="Ensure"/> instead of making its own.
///
/// Before this, each overlay left its own DontDestroyOnLoad EventSystem behind and the Menu scene carried an authored
/// one, so the menu ran with two and Unity logged an error every frame. Scenes must not author an EventSystem: one
/// found on a scene load is switched off with a warning naming it.
///
/// It also owns pad and keyboard navigation for every screen, through <see cref="UiNavScope"/>s:
/// <list type="bullet">
/// <item>The last input decides the mode. A pad (or arrows / WASD / Enter while a screen is open) switches to
/// navigation, and a screen with nothing selected gets its default selection. The mouse switches back, and from
/// then on nothing stays selected — a click selects for a frame and is dropped again — so a stale selection can't
/// be fired later by Enter or A, and mouse play looks exactly as it did when plates had no navigation.</item>
/// <item>Navigation is narrowed to the scope on top: each frame its selectables get explicit neighbours found
/// among themselves only. Unity's Automatic navigation searches every selectable on every canvas, so it walked
/// off a modal onto the menu under it, or from the pause menu into the blackjack panel beneath.</item>
/// <item>Pad B / Esc is "back" for the scope on top (modal, then sub-card, then screen), spent through
/// <see cref="ConsumeUiPress"/> so one press never does two things.</item>
/// <item>The press that closes a screen is kept out of gameplay until it is released
/// (<see cref="HoldsGameplayInput"/>): A is also Jump, Enter is also Attack and B is also Crouch.</item>
/// </list>
/// </summary>
public static class UiEventSystem
{
    /// <summary>
    /// How long the closing press may stay held before gameplay gets it anyway. Covers a tap with room to spare,
    /// and a stuck or deliberately held button can't lock the player out.
    /// </summary>
    const float MaxGameplayHoldSeconds = 0.5f;

    /// <summary>
    /// A selection whose own button was just switched off (the prize card's buy cooldown, a picked survivor plate)
    /// keeps focus this long before the screen's default takes over.
    /// </summary>
    const float DisabledSelectionGraceSeconds = 0.6f;

    const float MouseMoveThresholdSqr = 4f;
    const float StickThresholdSqr = 0.25f;

    static EventSystem s_instance;
    static bool s_watchingSceneLoads;

    static readonly List<UiNavScope> s_scopes = new();
    static int s_serial;
    static bool s_navigating;
    static GameObject s_restoreCandidate;
    static GameObject s_lastSeenSelection;
    static GameObject s_disabledSelection;
    static float s_disabledSince;
    static CursorLockMode s_lastLockState;
    static int s_ignoreMouseDeltaUntilFrame = -1;

    static int s_consumedFrame = -1;
    static bool s_holdArmed;
    static int s_holdArmedFrame = -1;
    static float s_holdArmedAt;

    /// <summary>One navigable widget of the scope on top, measured once per frame.</summary>
    struct NavItem
    {
        public Selectable Selectable;
        public Vector2 Min;
        public Vector2 Max;
        public Vector2 Center;
        /// <summary>Viewport of the scroll view it sits in, if any.</summary>
        public RectTransform Viewport;
        /// <summary>Inside that viewport (always true outside a scroll view).</summary>
        public bool Visible;
        /// <summary>Interactable, so navigation may land on it.</summary>
        public bool Candidate;
        /// <summary>The segmented control it is an option of, if any.</summary>
        public MenuSegmented Group;
    }

    static readonly List<Selectable> s_gathered = new();
    static NavItem[] s_items = new NavItem[48];
    static readonly Vector3[] s_corners = new Vector3[4];

    /// <summary>The shared EventSystem, created on first use. Safe to call every time a screen opens.</summary>
    public static EventSystem Ensure()
    {
        if (!s_watchingSceneLoads)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            s_watchingSceneLoads = true;
        }

        if (s_instance != null)
            return s_instance;

        var go = new GameObject("EventSystem");
        UnityEngine.Object.DontDestroyOnLoad(go);
        s_instance = go.AddComponent<EventSystem>();
        var module = go.AddComponent<InputSystemUIInputModule>();
        // A module added from code picks up its actions in OnEnable: the UI map (Navigate = sticks, d-pad, arrows,
        // WASD; Submit = pad South / Enter; Cancel = pad East / Esc). Pad navigation depends on exactly those, so
        // never let it run with none.
        if (module.actionsAsset == null)
            module.AssignDefaultActions();
        go.AddComponent<UiNavigationDriver>();
        DisableOthers();
        return s_instance;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (s_instance != null)
            DisableOthers();
    }

    static void DisableOthers()
    {
        EventSystem[] all = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude);
        for (int i = 0; i < all.Length; i++)
        {
            EventSystem other = all[i];
            if (other == null || other == s_instance || !other.enabled)
                continue;

            other.enabled = false;
            Debug.LogWarning(
                $"[{nameof(UiEventSystem)}] Disabled the extra EventSystem '{other.name}' in scene '{other.gameObject.scene.name}'. "
                + "The game keeps one persistent EventSystem; remove this one from the scene.",
                other);
        }
    }

    // ================================================================ scopes

    /// <summary>True while the player drives the UI with a pad or the keyboard rather than the mouse.</summary>
    public static bool IsNavigating => s_navigating;

    /// <summary>A screen came up (or came back to the front). Idempotent.</summary>
    public static void Open(UiNavScope scope)
    {
        if (scope == null || scope.Root == null)
            return;

        Ensure();
        scope.Serial = ++s_serial;
        Canvas canvas = scope.Root.GetComponentInParent<Canvas>(true);
        scope.Order = canvas != null ? canvas.rootCanvas.sortingOrder : 0;
        if (scope.IsOpen)
            return;

        scope.IsOpen = true;
        scope.RestoreTarget = s_instance != null ? s_instance.currentSelectedGameObject : null;
        s_scopes.Add(scope);
    }

    /// <summary>
    /// A screen went away. Its opener gets the selection back, and the press that closed it is kept out of gameplay
    /// (see <see cref="HoldsGameplayInput"/>). Idempotent.
    /// </summary>
    public static void Close(UiNavScope scope)
    {
        if (scope == null || !scope.IsOpen)
            return;

        scope.IsOpen = false;
        s_scopes.Remove(scope);
        s_restoreCandidate = scope.RestoreTarget;
        scope.RestoreTarget = null;
        ArmGameplayHold();
    }

    static UiNavScope TopScope()
    {
        UiNavScope top = null;
        for (int i = s_scopes.Count - 1; i >= 0; i--)
        {
            UiNavScope scope = s_scopes[i];
            if (scope.Root == null)
            {
                // Its screen was destroyed without closing it (a scene unload).
                scope.IsOpen = false;
                s_scopes.RemoveAt(i);
                continue;
            }
            if (!scope.Root.gameObject.activeInHierarchy)
                continue;
            if (top == null || scope.Order > top.Order || (scope.Order == top.Order && scope.Serial > top.Serial))
                top = scope;
        }
        return top;
    }

    // ================================================================ press latch

    /// <summary>
    /// A UI handled this frame's press (back, the pause toggle): nothing else may act on it this frame, and gameplay
    /// doesn't get it until it is released.
    /// </summary>
    public static void ConsumeUiPress()
    {
        s_consumedFrame = Time.frameCount;
        ArmGameplayHold();
    }

    /// <summary>Someone already spent this frame's UI press. Check before acting on Esc / B / Start.</summary>
    public static bool UiPressConsumedThisFrame => s_consumedFrame == Time.frameCount;

    /// <summary>
    /// True on the frame a screen closed or a UI press was spent, and after that while a UI button (pad A / B /
    /// Start, Enter, Space, Esc) is still held, capped at <see cref="MaxGameplayHoldSeconds"/>. A press that closes
    /// a menu would otherwise reach gameplay the same frame (A jumps, Enter attacks), and B is the crouch binding,
    /// which reads as held for as long as the closing tap lasts. Reaches PlayerController through
    /// <see cref="SkeletonRpsOverlayController.ConsumesCancelInput"/>, which its UI gate already tests.
    /// </summary>
    public static bool HoldsGameplayInput
    {
        get
        {
            if (!s_holdArmed)
                return false;
            if (Time.frameCount == s_holdArmedFrame)
                return true;
            if (Time.unscaledTime - s_holdArmedAt < MaxGameplayHoldSeconds && AnyUiButtonHeld())
                return true;
            s_holdArmed = false;
            return false;
        }
    }

    static void ArmGameplayHold()
    {
        s_holdArmed = true;
        s_holdArmedFrame = Time.frameCount;
        s_holdArmedAt = Time.unscaledTime;
    }

    static bool AnyUiButtonHeld()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null
            && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed
                || keyboard.spaceKey.isPressed || keyboard.escapeKey.isPressed))
            return true;

        var pads = Gamepad.all;
        for (int i = 0; i < pads.Count; i++)
        {
            Gamepad pad = pads[i];
            if (pad.buttonSouth.isPressed || pad.buttonEast.isPressed || pad.startButton.isPressed)
                return true;
        }
        return false;
    }

    static bool BackPressedThisFrame()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            return true;

        var pads = Gamepad.all;
        for (int i = 0; i < pads.Count; i++)
        {
            if (pads[i].buttonEast.wasPressedThisFrame)
                return true;
        }
        return false;
    }

    // ================================================================ per frame

    /// <summary>Before the screens' own Update: settle the input mode and spend a back press on the top scope.</summary>
    internal static void EarlyTick()
    {
        PollInputMode();

        UiNavScope top = TopScope();
        if (top == null || top.Back == null || UiPressConsumedThisFrame || !BackPressedThisFrame())
            return;

        // Spent whatever Back did — even when it opened something (the lobby's leave confirm) — so the pause toggle
        // and the skeleton panel's own cancel leave this press alone.
        top.Back();
        ConsumeUiPress();
    }

    /// <summary>After the EventSystem moved the selection: keep it valid and give the top scope its neighbours.</summary>
    internal static void LateTick()
    {
        EventSystem eventSystem = s_instance;
        if (eventSystem == null || !eventSystem.isActiveAndEnabled)
            return;

        GameObject selected = eventSystem.currentSelectedGameObject;
        UiNavScope top = TopScope();

        if (!s_navigating || top == null)
        {
            // The mouse owns the UI (or no screen is open): nothing stays selected. Text fields keep focus — a
            // click is how they are typed into.
            if (selected != null && !IsTextInput(selected))
                eventSystem.SetSelectedGameObject(null);
            s_restoreCandidate = null;
            s_lastSeenSelection = null;
            return;
        }

        int count = GatherItems(top);

        if (!IsValidSelection(selected, top))
        {
            GameObject target = ResolveFallback(top, count);
            if (target != selected)
                eventSystem.SetSelectedGameObject(target);
            selected = target;
        }
        else if (selected != s_lastSeenSelection)
        {
            // The player moved focus: the same tick a pointer hover gives the plate.
            PlayMoveTick(selected);
        }

        s_restoreCandidate = null;
        s_lastSeenSelection = selected;

        AssignExplicitNavigation(count);
        s_gathered.Clear();
    }

    // ---------------------------------------------------------------- input mode

    static void PollInputMode()
    {
        // Locking or freeing the cursor can warp it; don't read that as the player picking up the mouse.
        if (Cursor.lockState != s_lastLockState)
        {
            s_lastLockState = Cursor.lockState;
            s_ignoreMouseDeltaUntilFrame = Time.frameCount + 2;
        }

        if (PadActivity() || (s_scopes.Count > 0 && KeyboardNavigationActivity()))
            s_navigating = true;
        else if (MouseActivity())
            s_navigating = false;
    }

    static bool MouseActivity()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null)
            return false;
        if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame
            || mouse.middleButton.wasPressedThisFrame)
            return true;
        if (mouse.scroll.ReadValue().sqrMagnitude > 0.01f)
            return true;
        return Time.frameCount > s_ignoreMouseDeltaUntilFrame
            && mouse.delta.ReadValue().sqrMagnitude > MouseMoveThresholdSqr;
    }

    static bool PadActivity()
    {
        var pads = Gamepad.all;
        for (int i = 0; i < pads.Count; i++)
        {
            Gamepad pad = pads[i];
            if (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame
                || pad.buttonWest.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame
                || pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame
                || pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame
                || pad.leftTrigger.wasPressedThisFrame || pad.rightTrigger.wasPressedThisFrame
                || pad.leftStickButton.wasPressedThisFrame || pad.rightStickButton.wasPressedThisFrame
                || pad.dpad.up.wasPressedThisFrame || pad.dpad.down.wasPressedThisFrame
                || pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame)
                return true;
            if (pad.leftStick.ReadValue().sqrMagnitude > StickThresholdSqr
                || pad.rightStick.ReadValue().sqrMagnitude > StickThresholdSqr)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Arrows, WASD and Enter — the UI map's Navigate and Submit keys. Only counted while a screen is open: in play
    /// they are movement and attack. Esc is left out on purpose, so a mouse player who backs out with Esc keeps the
    /// mouse look.
    /// </summary>
    static bool KeyboardNavigationActivity()
    {
        Keyboard keyboard = Keyboard.current;
        return keyboard != null
            && (keyboard.upArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame
                || keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame
                || keyboard.wKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame
                || keyboard.sKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame
                || keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
    }

    // ---------------------------------------------------------------- selection

    static bool IsTextInput(GameObject go)
    {
        return go.TryGetComponent(out TMP_InputField _) || go.TryGetComponent(out InputField _);
    }

    /// <summary>An active, navigable selectable under the scope that no CanvasGroup has switched off.</summary>
    static bool IsInScope(GameObject go, UiNavScope scope, out Selectable selectable)
    {
        selectable = null;
        if (go == null || !go.activeInHierarchy || scope == null || scope.Root == null)
            return false;
        if (!go.transform.IsChildOf(scope.Root))
            return false;
        if (!go.TryGetComponent(out selectable) || !selectable.enabled)
            return false;
        if (selectable.navigation.mode == Navigation.Mode.None)
            return false;
        // A fading-out sub-card or modal sits under the menu's root too, but its CanvasGroup is already off.
        return GroupsAllowInteraction(go.transform);
    }

    static bool GroupsAllowInteraction(Transform t)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            if (!p.TryGetComponent(out CanvasGroup group) || !group.enabled)
                continue;
            if (!group.interactable)
                return false;
            if (group.ignoreParentGroups)
                return true;
        }
        return true;
    }

    static bool IsValidSelection(GameObject go, UiNavScope top)
    {
        if (!IsInScope(go, top, out Selectable selectable))
            return false;

        if (selectable.interactable)
        {
            s_disabledSelection = null;
            return true;
        }

        if (s_disabledSelection != go)
        {
            s_disabledSelection = go;
            s_disabledSince = Time.unscaledTime;
        }
        return Time.unscaledTime - s_disabledSince < DisabledSelectionGraceSeconds;
    }

    static bool IsLandable(GameObject go, UiNavScope top)
    {
        return IsInScope(go, top, out Selectable selectable) && selectable.IsInteractable();
    }

    /// <summary>The opener of the scope that just closed if it belongs to this one, else this scope's default.</summary>
    static GameObject ResolveFallback(UiNavScope top, int count)
    {
        if (s_restoreCandidate != null && IsLandable(s_restoreCandidate, top))
            return s_restoreCandidate;

        if (top.DefaultSelection != null)
        {
            Selectable preferred = top.DefaultSelection();
            return preferred != null && IsLandable(preferred.gameObject, top) ? preferred.gameObject : null;
        }

        for (int i = 0; i < count; i++)
        {
            if (s_items[i].Candidate)
                return s_items[i].Selectable.gameObject;
        }
        return null;
    }

    static void PlayMoveTick(GameObject selected)
    {
        if (selected != null && selected.TryGetComponent(out MenuButtonFx fx) && !fx.suppressHoverAudio
            && (fx.button == null || fx.button.interactable))
            MenuUiAudio.PlayHover();
    }

    // ---------------------------------------------------------------- navigation

    static int GatherItems(UiNavScope top)
    {
        s_gathered.Clear();
        top.Root.GetComponentsInChildren(false, s_gathered);

        int count = 0;
        for (int i = 0; i < s_gathered.Count; i++)
        {
            Selectable selectable = s_gathered[i];
            if (selectable == null || !selectable.IsActive() || selectable.navigation.mode == Navigation.Mode.None)
                continue;
            var rect = selectable.transform as RectTransform;
            if (rect == null)
                continue;

            if (count == s_items.Length)
                Array.Resize(ref s_items, count * 2);

            ref NavItem item = ref s_items[count++];
            item.Selectable = selectable;
            GetWorldRect(rect, out item.Min, out item.Max);
            item.Center = (item.Min + item.Max) * 0.5f;
            item.Candidate = selectable.IsInteractable();

            ScrollRect scroll = selectable.GetComponentInParent<ScrollRect>();
            item.Viewport = scroll != null ? scroll.viewport : null;
            item.Visible = true;
            if (item.Viewport != null)
            {
                GetWorldRect(item.Viewport, out Vector2 viewMin, out Vector2 viewMax);
                item.Visible = item.Center.x >= viewMin.x && item.Center.x <= viewMax.x
                    && item.Center.y >= viewMin.y && item.Center.y <= viewMax.y;
            }

            Transform parent = selectable.transform.parent;
            item.Group = parent != null ? parent.GetComponent<MenuSegmented>() : null;
        }
        return count;
    }

    static void GetWorldRect(RectTransform rect, out Vector2 min, out Vector2 max)
    {
        rect.GetWorldCorners(s_corners);
        min = s_corners[0];
        max = s_corners[0];
        for (int i = 1; i < 4; i++)
        {
            min = Vector2.Min(min, s_corners[i]);
            max = Vector2.Max(max, s_corners[i]);
        }
    }

    /// <summary>
    /// Explicit neighbours for every widget of the top scope, chosen among that scope only. Set every frame so rows
    /// that appear or hide (blackjack turns, crew joins) are picked up; assigning an unchanged Navigation is a no-op.
    /// </summary>
    static void AssignExplicitNavigation(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Selectable selectable = s_items[i].Selectable;
            Selectable up = Neighbour(i, count, Vector2.up);
            Selectable down = Neighbour(i, count, Vector2.down);
            Selectable left = Neighbour(i, count, Vector2.left);
            Selectable right = Neighbour(i, count, Vector2.right);

            // A slider spends moves along its own axis on its value, which it only does when there is no neighbour
            // that way.
            if (selectable is Slider slider)
            {
                if (slider.direction == Slider.Direction.LeftToRight || slider.direction == Slider.Direction.RightToLeft)
                {
                    left = null;
                    right = null;
                }
                else
                {
                    up = null;
                    down = null;
                }
            }

            selectable.navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                wrapAround = false,
                selectOnUp = up,
                selectOnDown = down,
                selectOnLeft = left,
                selectOnRight = right,
            };
        }
    }

    /// <summary>
    /// Nearest landable widget in a direction. Sideways moves stay on the visual row when anything on it qualifies
    /// (Unity's own scoring jumps from a stepper's ◀ to the option row below instead of its ▶), and only then take
    /// something within 45°. Up/down weigh the sideways gap at half, so a list whose rows are laid out differently
    /// (a slider beside its label, options spanning the full width) still steps row by row.
    /// </summary>
    static Selectable Neighbour(int from, int count, Vector2 direction)
    {
        ref NavItem source = ref s_items[from];
        bool horizontal = direction.x != 0f;
        int axis = horizontal ? 0 : 1;
        int across = 1 - axis;
        float sign = horizontal ? direction.x : direction.y;

        int best = -1;
        float bestCost = float.MaxValue;
        int cone = -1;
        float coneCost = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            if (i == from)
                continue;
            ref NavItem item = ref s_items[i];
            if (!item.Candidate)
                continue;
            // Rows scrolled out of a list are only reachable by stepping through that list, not from outside it.
            if (item.Viewport != null && item.Viewport != source.Viewport && !item.Visible)
                continue;

            float ahead = (item.Center[axis] - source.Center[axis]) * sign;
            if (ahead <= 0.5f)
                continue;
            // It must reach past the source's leading edge, or a wide widget would count as beyond a narrow one inside it.
            float lead = sign > 0f ? item.Max[axis] - source.Max[axis] : source.Min[axis] - item.Min[axis];
            if (lead <= 0.5f)
                continue;

            float gap = Mathf.Max(0f, sign > 0f ? item.Min[axis] - source.Max[axis] : source.Min[axis] - item.Max[axis]);
            float acrossGap = Mathf.Max(0f, Mathf.Max(item.Min[across] - source.Max[across], source.Min[across] - item.Max[across]));
            float acrossOffset = Mathf.Abs(item.Center[across] - source.Center[across]);

            if (horizontal)
            {
                if (acrossGap <= 0f)
                {
                    float cost = gap + 0.01f * acrossOffset;
                    if (cost < bestCost)
                    {
                        best = i;
                        bestCost = cost;
                    }
                }
                else if (acrossOffset <= ahead)
                {
                    float cost = gap + 2f * acrossGap;
                    if (cost < coneCost)
                    {
                        cone = i;
                        coneCost = cost;
                    }
                }
            }
            else
            {
                float cost = gap + 0.5f * acrossGap + 0.01f * acrossOffset;
                if (cost < bestCost)
                {
                    best = i;
                    bestCost = cost;
                }
            }
        }

        if (best < 0)
            best = cone;
        if (best < 0)
            return null;

        // Stepping into a segmented control from outside lands on its active option, the way a radio group does.
        MenuSegmented group = s_items[best].Group;
        if (group != null && group != source.Group)
        {
            Selectable active = group.ActiveOption;
            if (active != null && active.IsActive() && active.IsInteractable())
                return active;
        }
        return s_items[best].Selectable;
    }
}
