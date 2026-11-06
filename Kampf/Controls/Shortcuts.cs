using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Kampf.Controls;

/// <summary>What a key press asks the shell to do.</summary>
public enum ShortcutAction
{
    None,
    SaveNote,
    NewNote,
    ImportPdf,
    FocusSearch,
    ShowGraph,
    ShowEditor,
    ShowReader,
    ShowChat,
    ShowSettings,
    ToggleTheme,
    Dismiss,
}

/// <summary>One key plus the modifiers it needs.</summary>
public sealed record ShortcutBinding(ShortcutAction Action, Key Key, KeyModifiers Modifiers);

/// <summary>
/// The shell's key map, kept apart from the window so it can be tested without a
/// keyboard and rebound in Settings without one either. The defaults are the
/// chords the app has always used.
///
/// Chords work everywhere, including inside a text box: Ctrl+S in the note body
/// must save, not insert anything. A binding without modifiers is narrower,
/// because typing beats navigation; only Escape keeps working while typing.
/// Gestures match exactly, so adding Shift to a chord makes it a different one.
/// </summary>
public sealed class ShortcutMap
{
    private static readonly ShortcutBinding[] Defaults =
    {
        new(ShortcutAction.SaveNote, Key.S, KeyModifiers.Control),
        new(ShortcutAction.NewNote, Key.N, KeyModifiers.Control),
        new(ShortcutAction.ImportPdf, Key.O, KeyModifiers.Control),
        new(ShortcutAction.FocusSearch, Key.F, KeyModifiers.Control),
        new(ShortcutAction.ToggleTheme, Key.T, KeyModifiers.Control),
        new(ShortcutAction.ShowGraph, Key.D1, KeyModifiers.Control),
        new(ShortcutAction.ShowEditor, Key.D2, KeyModifiers.Control),
        new(ShortcutAction.ShowReader, Key.D3, KeyModifiers.Control),
        new(ShortcutAction.ShowChat, Key.D4, KeyModifiers.Control),
        new(ShortcutAction.ShowSettings, Key.D5, KeyModifiers.Control),
        new(ShortcutAction.Dismiss, Key.Escape, KeyModifiers.None),
    };

    private readonly Dictionary<ShortcutAction, ShortcutBinding> _bindings = new();

    private ShortcutMap()
    {
    }

    /// <summary>The actions Settings offers a row for, in display order.</summary>
    public static IReadOnlyList<ShortcutAction> RebindableActions { get; } =
        Defaults.Select(binding => binding.Action).ToArray();

    public static ShortcutMap CreateDefault()
    {
        var map = new ShortcutMap();
        map.ResetToDefaults();
        return map;
    }

    public void ResetToDefaults()
    {
        _bindings.Clear();
        foreach (var binding in Defaults)
            _bindings[binding.Action] = binding;
    }

    public ShortcutBinding? BindingFor(ShortcutAction action) =>
        _bindings.TryGetValue(action, out var binding) ? binding : null;

    /// <summary>Binds one gesture, refusing one that another action already owns.</summary>
    public bool TrySet(ShortcutAction action, Key key, KeyModifiers modifiers, out ShortcutAction conflict)
    {
        conflict = ShortcutAction.None;

        foreach (var (other, binding) in _bindings)
        {
            if (other == action || binding.Key != key || Normalize(binding.Modifiers) != Normalize(modifiers))
                continue;

            conflict = other;
            return false;
        }

        _bindings[action] = new ShortcutBinding(action, key, modifiers);
        return true;
    }

    /// <summary>The gesture for an action, e.g. "Ctrl+S"; empty when it has none.</summary>
    public string Describe(ShortcutAction action) =>
        BindingFor(action) is { } binding ? Describe(binding.Key, binding.Modifiers) : string.Empty;

    public ShortcutAction Resolve(Key key, KeyModifiers modifiers, bool typing)
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Key != key || Normalize(binding.Modifiers) != Normalize(modifiers))
                continue;

            // A bare key would eat typed text; Escape is the one exception.
            if (typing && Normalize(binding.Modifiers) == KeyModifiers.None && key != Key.Escape)
                continue;

            return binding.Action;
        }

        return ShortcutAction.None;
    }

    /// <summary>Written to app-settings.json: one gesture per action.</summary>
    public Dictionary<string, string> ToSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (action, binding) in _bindings)
            settings[action.ToString()] = Describe(binding.Key, binding.Modifiers);

        return settings;
    }

    /// <summary>
    /// Applies saved gestures over the defaults. Anything unreadable, unknown or
    /// clashing is skipped, so a hand-edited file cannot leave the app without a
    /// way to save note.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null)
            return;

        foreach (var (name, gesture) in settings)
        {
            if (!Enum.TryParse<ShortcutAction>(name, ignoreCase: true, out var action))
                continue;
            if (!_bindings.ContainsKey(action))
                continue;
            if (!TryParse(gesture, out var key, out var modifiers))
                continue;

            TrySet(action, key, modifiers, out _);
        }
    }

    public static string Describe(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        var normalized = Normalize(modifiers);

        if (normalized.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (normalized.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (normalized.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        Key.OemQuestion => "/",
        _ => key.ToString(),
    };

    /// <summary>Reads a gesture back out of app-settings.json.</summary>
    public static bool TryParse(string? gesture, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;

        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                default:
                    return false;
            }
        }

        var name = parts[^1];

        if (name.Length == 1 && char.IsDigit(name[0]))
            key = Key.D0 + (name[0] - '0');
        else if (name == "/")
            key = Key.OemQuestion;
        else if (!Enum.TryParse(name, ignoreCase: true, out key))
            return false;

        return key != Key.None;
    }

    /// <summary>Ctrl and the Mac command key count as the same modifier.</summary>
    private static KeyModifiers Normalize(KeyModifiers modifiers)
    {
        var normalized = modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);

        if (modifiers.HasFlag(KeyModifiers.Meta))
            normalized |= KeyModifiers.Control;

        return normalized;
    }

    /// <summary>A modifier pressed on the way to a chord is not the chord.</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    /// <summary>
    /// What may be bound: a chord, or a function key on its own. A bare letter or
    /// digit would fight with typing in the note body.
    /// </summary>
    public static bool IsBindable(Key key, KeyModifiers modifiers) =>
        Normalize(modifiers) != KeyModifiers.None || (key >= Key.F1 && key <= Key.F12);
}
