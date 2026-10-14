using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One screen's claim on pad and keyboard navigation: a menu, a sub-card, a modal or an overlay. Built once by the
/// screen, then handed to <see cref="UiEventSystem.Open"/> whenever the screen shows and
/// <see cref="UiEventSystem.Close"/> whenever it goes away.
///
/// The open scope on top (highest canvas sort order, then most recently opened) owns navigation: only selectables
/// under its <see cref="Root"/> can be moved onto, its <see cref="DefaultSelection"/> is what a pad lands on, and
/// its <see cref="Back"/> is what pad B / Esc does. Closing a scope hands the selection back to whatever was
/// selected when it opened, so a modal returns to the plate that raised it.
/// </summary>
public sealed class UiNavScope
{
    /// <param name="root">Everything navigable on this screen sits under it.</param>
    /// <param name="defaultSelection">
    /// Where a pad lands when nothing on the screen is selected. Leave it null for "the first selectable on the
    /// screen"; a function that returns null means "nothing for now" (e.g. an overlay between turns), and the
    /// selection stays empty until it returns something.
    /// </param>
    /// <param name="back">What pad B / Esc does while this screen is on top. Null: the screen has no back.</param>
    public UiNavScope(Transform root, Func<Selectable> defaultSelection = null, Action back = null)
    {
        Root = root;
        DefaultSelection = defaultSelection;
        Back = back;
    }

    public Transform Root { get; }
    public Func<Selectable> DefaultSelection { get; }
    public Action Back { get; }
    public bool IsOpen { get; internal set; }

    /// <summary>Sort order of the canvas the root sits on, read when the scope opens.</summary>
    internal int Order;
    /// <summary>Open order; breaks ties between scopes on the same canvas (a modal over its own menu).</summary>
    internal int Serial;
    /// <summary>The selection when this scope opened — restored when it closes.</summary>
    internal GameObject RestoreTarget;
}
