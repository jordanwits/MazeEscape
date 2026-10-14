using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The player's rebinds: binding overrides on the "Player" map of the Input Actions asset, saved as JSON in
/// PlayerPrefs. Every consumer runs its own instantiated copy of the asset (<see cref="PlayerController"/>,
/// <see cref="NetworkPlayerVoice"/>) and calls <see cref="ApplyTo"/> on it, then again on <see cref="Changed"/>.
/// The settings screen edits a private copy (<see cref="EditAsset"/>) through <see cref="BeginRebind"/> and
/// <see cref="ResetToDefaults"/>, both of which save and raise <see cref="Changed"/>.
/// </summary>
public static class KeybindSettings
{
    const string PrefKey = "input.bindingOverrides.v1";
    const string PlayerMapName = "Player";

    public const string KeyboardMouseGroup = "Keyboard&Mouse";
    public const string GamepadGroup = "Gamepad";

    /// <summary>One rebindable slot: an action, plus the composite part for the Move directions.</summary>
    public readonly struct Entry
    {
        public readonly string Label;
        public readonly string Action;
        public readonly string Part;

        public Entry(string label, string action, string part = null)
        {
            Label = label;
            Action = action;
            Part = part;
        }
    }

    public static readonly Entry[] KeyboardMouseEntries =
    {
        new("Move Forward", "Move", "up"),
        new("Move Back", "Move", "down"),
        new("Move Left", "Move", "left"),
        new("Move Right", "Move", "right"),
        new("Jump", "Jump"),
        new("Sprint", "Sprint"),
        new("Crouch", "Crouch"),
        new("Interact", "Interact"),
        new("Attack / Throw", "Attack"),
        new("Reload", "Reload"),
        new("Drop Item", "Drop"),
        new("Flashlight", "Flashlight"),
        new("Previous Item", "Previous"),
        new("Next Item", "Next"),
        new("Push to Talk", "PushToTalk"),
    };

    // Move and Look stay on the sticks.
    public static readonly Entry[] GamepadEntries =
    {
        new("Jump", "Jump"),
        new("Sprint", "Sprint"),
        new("Crouch", "Crouch"),
        new("Interact", "Interact"),
        new("Attack / Throw", "Attack"),
        new("Reload", "Reload"),
        new("Drop Item", "Drop"),
        new("Flashlight", "Flashlight"),
        new("Previous Item", "Previous"),
        new("Next Item", "Next"),
        new("Push to Talk", "PushToTalk"),
    };

    public static event Action Changed;

    static string s_json;
    static bool s_loaded;
    static InputActionAsset s_editAsset;
    // GetKeyboardLabel runs every frame a HUD prompt is up; cleared whenever the binds change.
    static readonly Dictionary<string, string> s_keyboardLabels = new();

    static string OverridesJson
    {
        get
        {
            if (!s_loaded)
            {
                s_json = PlayerPrefs.GetString(PrefKey, string.Empty);
                s_loaded = true;
            }
            return s_json;
        }
    }

    /// <summary>Puts the saved rebinds on a runtime copy of the actions asset (defaults first, so a reset sticks).</summary>
    public static void ApplyTo(InputActionAsset asset)
    {
        if (asset == null)
            return;

        asset.RemoveAllBindingOverrides();
        string json = OverridesJson;
        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            asset.LoadBindingOverridesFromJson(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{nameof(KeybindSettings)}: saved keybinds could not be read, using defaults. {e.Message}");
        }
    }

    /// <summary>
    /// The copy the settings screen rebinds, never enabled. Built from the project-wide actions asset, which is
    /// the same InputSystem_Actions asset the player prefabs reference. Null if the project has none.
    /// </summary>
    public static InputActionAsset EditAsset
    {
        get
        {
            if (s_editAsset != null)
                return s_editAsset;

            InputActionAsset source = InputSystem.actions;
            if (source == null)
                return null;

            s_editAsset = UnityEngine.Object.Instantiate(source);
            s_editAsset.name = source.name + " (Keybinds)";
            s_editAsset.hideFlags = HideFlags.HideAndDontSave;
            s_editAsset.Disable();
            ApplyTo(s_editAsset);
            return s_editAsset;
        }
    }

    public static InputAction FindEditAction(string actionName)
    {
        InputActionAsset asset = EditAsset;
        InputActionMap map = asset != null ? asset.FindActionMap(PlayerMapName) : null;
        return map?.FindAction(actionName);
    }

    /// <summary>The binding an entry edits: the first one in the group (W, not the up arrow, for Move Forward).</summary>
    public static int FindBindingIndex(InputAction action, Entry entry, string group)
    {
        if (action == null)
            return -1;

        var bindings = action.bindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            InputBinding binding = bindings[i];
            if (binding.isComposite)
                continue;
            if (entry.Part != null)
            {
                if (!binding.isPartOfComposite || !string.Equals(binding.name, entry.Part, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (binding.isPartOfComposite)
            {
                continue;
            }

            if (InGroup(binding, group))
                return i;
        }
        return -1;
    }

    /// <summary>Key legend for an entry as the settings screen shows it ("W", "LMB", "RB"); "—" when unbound.</summary>
    public static string GetLabel(Entry entry, string group)
    {
        InputAction action = FindEditAction(entry.Action);
        int index = FindBindingIndex(action, entry, group);
        return index < 0 ? "—" : PrettyPath(action.bindings[index].effectivePath);
    }

    /// <summary>Key legend for an action's keyboard/mouse binding, for HUD hints ("Press E to ...").</summary>
    public static string GetKeyboardLabel(string actionName, string fallback)
    {
        if (s_keyboardLabels.TryGetValue(actionName, out string cached))
            return cached;

        InputAction action = FindEditAction(actionName);
        int index = FindBindingIndex(action, new Entry(actionName, actionName), KeyboardMouseGroup);
        string label = index < 0 ? fallback : PrettyPath(action.bindings[index].effectivePath);
        s_keyboardLabels[actionName] = label;
        return label;
    }

    /// <summary>
    /// Listens for the next key / button and binds it to the entry. A key some other slot already uses swaps over
    /// to that slot (a hidden extra, such as the arrow keys on Move, just lets go of it), so nothing is ever bound
    /// twice or left dead by accident. Esc cancels, as does a few seconds of nothing. Returns null if the entry
    /// has no binding to edit; otherwise the started operation, which the caller may <c>Cancel()</c>.
    /// </summary>
    public static InputActionRebindingExtensions.RebindingOperation BeginRebind(Entry entry, string group,
        Action<bool> finished)
    {
        InputAction action = FindEditAction(entry.Action);
        int index = FindBindingIndex(action, entry, group);
        if (index < 0)
            return null;

        string oldPath = action.bindings[index].effectivePath;
        bool keyboardMouse = group == KeyboardMouseGroup;

        var op = action.PerformInteractiveRebinding(index)
            .WithCancelingThrough("<Keyboard>/escape")
            .WithControlsExcluding("<Keyboard>/escape")
            .WithControlsExcluding("<Keyboard>/anyKey")
            .WithControlsExcluding("<Pointer>/press")
            .WithControlsExcluding("<Mouse>/scroll")
            .WithControlsExcluding("<Gamepad>/start")
            .WithControlsExcluding("<Gamepad>/leftStick/*")
            .WithControlsExcluding("<Gamepad>/rightStick/*")
            .WithTimeout(6f)
            .OnComplete(o =>
            {
                o.Dispose();
                ResolveConflicts(action, index, oldPath, group);
                Save();
                finished?.Invoke(true);
            })
            .OnCancel(o =>
            {
                o.Dispose();
                // Esc cancelled it: that press is spent here, not on closing the settings screen behind.
                UiEventSystem.ConsumeUiPress();
                finished?.Invoke(false);
            });

        if (keyboardMouse)
        {
            op.WithControlsHavingToMatchPath("<Keyboard>");
            op.WithControlsHavingToMatchPath("<Mouse>");
        }
        else
        {
            op.WithControlsHavingToMatchPath("<Gamepad>");
        }

        op.Start();
        return op;
    }

    public static void ResetToDefaults()
    {
        InputActionAsset asset = EditAsset;
        if (asset != null)
            asset.RemoveAllBindingOverrides();
        Save();
    }

    static void Save()
    {
        InputActionAsset asset = EditAsset;
        s_json = asset != null ? asset.SaveBindingOverridesAsJson() : string.Empty;
        s_loaded = true;
        PlayerPrefs.SetString(PrefKey, s_json);
        PlayerPrefs.Save();
        s_keyboardLabels.Clear();
        Changed?.Invoke();
    }

    static void ResolveConflicts(InputAction rebound, int reboundIndex, string oldPath, string group)
    {
        string newPath = rebound.bindings[reboundIndex].effectivePath;
        if (string.IsNullOrEmpty(newPath) || SamePath(newPath, oldPath))
            return;

        Entry[] listed = group == KeyboardMouseGroup ? KeyboardMouseEntries : GamepadEntries;
        foreach (InputAction action in rebound.actionMap.actions)
        {
            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                if (action == rebound && i == reboundIndex)
                    continue;
                InputBinding binding = bindings[i];
                if (binding.isComposite || !InGroup(binding, group) || !SamePath(binding.effectivePath, newPath))
                    continue;

                action.ApplyBindingOverride(i, IsListed(action, i, listed, group) ? oldPath : string.Empty);
            }
        }
    }

    static bool IsListed(InputAction action, int bindingIndex, Entry[] listed, string group)
    {
        for (int i = 0; i < listed.Length; i++)
        {
            if (listed[i].Action == action.name && FindBindingIndex(action, listed[i], group) == bindingIndex)
                return true;
        }
        return false;
    }

    static bool InGroup(InputBinding binding, string group)
    {
        string groups = binding.groups;
        if (string.IsNullOrEmpty(groups))
            return false;
        foreach (string g in groups.Split(InputBinding.Separator))
        {
            if (string.Equals(g, group, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string PrettyPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "—";

        int slash = path.IndexOf('/');
        string device = slash > 0 ? path.Substring(0, slash) : string.Empty;
        string control = slash >= 0 ? path.Substring(slash + 1) : path;

        if (device.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            switch (control.ToLowerInvariant())
            {
                case "leftbutton": return "LMB";
                case "rightbutton": return "RMB";
                case "middlebutton": return "MMB";
                case "backbutton": return "MOUSE 4";
                case "forwardbutton": return "MOUSE 5";
            }
        }
        else if (device.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) < 0)
        {
            switch (control.ToLowerInvariant())
            {
                case "buttonsouth": return "A";
                case "buttoneast": return "B";
                case "buttonwest": return "X";
                case "buttonnorth": return "Y";
                case "leftshoulder": return "LB";
                case "rightshoulder": return "RB";
                case "lefttrigger": return "LT";
                case "righttrigger": return "RT";
                case "leftstickpress": return "L3";
                case "rightstickpress": return "R3";
                case "select": return "VIEW";
                case "dpad/up": return "D-PAD UP";
                case "dpad/down": return "D-PAD DOWN";
                case "dpad/left": return "D-PAD LEFT";
                case "dpad/right": return "D-PAD RIGHT";
            }
        }

        return InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice)
            .ToUpperInvariant();
    }
}
