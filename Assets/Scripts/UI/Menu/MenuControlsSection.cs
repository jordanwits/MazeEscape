using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// CONTROLS block of the settings panel: one row per rebindable action for keyboard & mouse, or for a controller
/// (segmented toggle). Clicking a key plate listens for the next key / button (<see cref="KeybindSettings.BeginRebind"/>);
/// Esc cancels. Edits apply immediately, including to a player already in a level.
/// </summary>
[DisallowMultipleComponent]
public sealed class MenuControlsSection : MonoBehaviour
{
    sealed class Row
    {
        public KeybindSettings.Entry Entry;
        public string Group;
        public MenuButtonFx Plate;
    }

    readonly List<Row> _rows = new();
    GameObject _keyboardGroup;
    GameObject _gamepadGroup;
    MenuSegmented _device;

    InputActionRebindingExtensions.RebindingOperation _rebind;
    Row _listening;

    public static MenuControlsSection Build(Transform parent)
    {
        RectTransform root = MenuWidgets.CreateRect("ControlsSection", parent);
        MenuWidgets.AddVertical(root.gameObject, new RectOffset(0, 0, 0, 0), 10f);
        var section = root.gameObject.AddComponent<MenuControlsSection>();
        section.BuildContent();
        return section;
    }

    void BuildContent()
    {
        if (KeybindSettings.EditAsset == null)
        {
            MenuWidgets.CreateText(transform, "Unavailable", "UNAVAILABLE", 15.5f, MenuTheme.Mist,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 3f);
            return;
        }

        _device = MenuWidgets.CreateSegmented(transform, new[] { "KEYBOARD & MOUSE", "CONTROLLER" });
        _device.Changed += ShowDevice;

        _keyboardGroup = BuildGroup("Keyboard", KeybindSettings.KeyboardMouseEntries, KeybindSettings.KeyboardMouseGroup);
        _gamepadGroup = BuildGroup("Gamepad", KeybindSettings.GamepadEntries, KeybindSettings.GamepadGroup);

        RectTransform resetRow = MenuWidgets.CreateRow(transform, "ResetRow", 40f, 0f, TextAnchor.MiddleRight);
        MenuWidgets.CreateFlexibleSpacer(resetRow);
        MenuButtonFx reset = MenuWidgets.CreatePlate(resetRow, "Ghost_ResetKeybinds", "RESET TO DEFAULTS",
            ResetToDefaults, MenuWidgets.PlateStyle.Danger, 38f, 14f);
        MenuWidgets.SetLayout(reset.transform, minWidth: 240f, preferredWidth: 240f, minHeight: 38f, preferredHeight: 38f);

        _device.Set(0, false);
        ShowDevice(0);
        RefreshLabels();
    }

    GameObject BuildGroup(string name, KeybindSettings.Entry[] entries, string group)
    {
        RectTransform container = MenuWidgets.CreateRect("Binds_" + name, transform);
        MenuWidgets.AddVertical(container.gameObject, new RectOffset(0, 0, 0, 0), 6f);

        for (int i = 0; i < entries.Length; i++)
        {
            var row = new Row { Entry = entries[i], Group = group };
            RectTransform rt = MenuWidgets.CreateRow(container, "Bind_" + entries[i].Label, 40f, 18f);

            TextMeshProUGUI label = MenuWidgets.CreateText(rt, "Name", entries[i].Label.ToUpperInvariant(), 15.5f,
                MenuTheme.Bone, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 3f);
            MenuWidgets.SetLayout(label, flexibleWidth: 1f);

            row.Plate = MenuWidgets.CreatePlate(rt, "Key_" + name + "_" + entries[i].Label, "",
                () => StartRebind(row), MenuWidgets.PlateStyle.Ghost, 38f, 15f);
            MenuWidgets.SetLayout(row.Plate.transform, minWidth: 210f, preferredWidth: 210f, minHeight: 38f, preferredHeight: 38f);

            _rows.Add(row);
        }

        return container.gameObject;
    }

    void ShowDevice(int index)
    {
        CancelRebind();
        if (_keyboardGroup != null)
            _keyboardGroup.SetActive(index == 0);
        if (_gamepadGroup != null)
            _gamepadGroup.SetActive(index == 1);
    }

    void StartRebind(Row row)
    {
        // A second click on the slot that is listening just stops listening.
        bool same = _listening == row;
        CancelRebind();
        if (same)
            return;

        _listening = row;
        _rebind = KeybindSettings.BeginRebind(row.Entry, row.Group, _ => FinishRebind(row));
        if (_rebind == null)
        {
            _listening = null;
            return;
        }

        row.Plate.label.text = row.Group == KeybindSettings.GamepadGroup ? "PRESS BUTTON" : "PRESS KEY";
        row.Plate.SetSelected(true);
    }

    void FinishRebind(Row row)
    {
        _rebind = null;
        if (_listening == row)
            _listening = null;
        if (row.Plate != null)
            row.Plate.SetSelected(false);
        // A swap may have moved another slot's key too.
        RefreshLabels();
    }

    void CancelRebind()
    {
        // Cancel() runs FinishRebind through the operation's cancel callback.
        _rebind?.Cancel();
        _rebind = null;
        _listening = null;
    }

    void ResetToDefaults()
    {
        CancelRebind();
        KeybindSettings.ResetToDefaults();
        RefreshLabels();
    }

    void RefreshLabels()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            Row row = _rows[i];
            if (row == _listening || row.Plate == null || row.Plate.label == null)
                continue;
            row.Plate.label.text = KeybindSettings.GetLabel(row.Entry, row.Group);
        }
    }

    void OnEnable()
    {
        RefreshLabels();
    }

    void OnDisable()
    {
        // The settings screen closed (or the toggle hid this list) mid-listen: stop, keep the old key.
        CancelRebind();
    }
}
