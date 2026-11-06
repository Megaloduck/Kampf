using System.Collections.Generic;
using Avalonia.Input;
using Kampf.Controls;
using Xunit;

namespace Kampf.Ui.Tests;

/// <summary>
/// The key map is where a rebinding can quietly break the app's only reflex
/// (Ctrl+S), so the parts that compare, parse and store gestures are pinned here.
/// </summary>
public class ShortcutMapTests
{
    [Fact]
    public void A_chord_still_saves_while_the_editor_has_focus()
    {
        var map = ShortcutMap.CreateDefault();

        Assert.Equal(ShortcutAction.SaveNote, map.Resolve(Key.S, KeyModifiers.Control, typing: true));
        Assert.Equal(ShortcutAction.None, map.Resolve(Key.S, KeyModifiers.None, typing: true));
    }

    [Fact]
    public void A_bare_key_waits_until_no_text_box_has_focus()
    {
        var map = ShortcutMap.CreateDefault();
        Assert.True(map.TrySet(ShortcutAction.FocusSearch, Key.F2, KeyModifiers.None, out _));

        Assert.Equal(ShortcutAction.None, map.Resolve(Key.F2, KeyModifiers.None, typing: true));
        Assert.Equal(ShortcutAction.FocusSearch, map.Resolve(Key.F2, KeyModifiers.None, typing: false));
    }

    [Fact]
    public void Rebinding_moves_the_gesture_and_refuses_one_already_taken()
    {
        var map = ShortcutMap.CreateDefault();

        Assert.True(map.TrySet(ShortcutAction.SaveNote, Key.J, KeyModifiers.Control, out _));
        Assert.Equal(ShortcutAction.SaveNote, map.Resolve(Key.J, KeyModifiers.Control, typing: true));
        Assert.Equal(ShortcutAction.None, map.Resolve(Key.S, KeyModifiers.Control, typing: true));

        Assert.False(map.TrySet(ShortcutAction.NewNote, Key.J, KeyModifiers.Control, out var conflict));
        Assert.Equal(ShortcutAction.SaveNote, conflict);

        // A refused change leaves the old chord alone.
        Assert.Equal(ShortcutAction.NewNote, map.Resolve(Key.N, KeyModifiers.Control, typing: true));
    }

    [Fact]
    public void Gestures_round_trip_through_the_saved_settings()
    {
        var map = ShortcutMap.CreateDefault();
        map.TrySet(ShortcutAction.SaveNote, Key.J, KeyModifiers.Control, out _);
        map.TrySet(ShortcutAction.ShowGraph, Key.F3, KeyModifiers.None, out _);

        var reloaded = ShortcutMap.CreateDefault();
        reloaded.Apply(map.ToSettings());

        Assert.Equal("Ctrl+J", reloaded.Describe(ShortcutAction.SaveNote));
        Assert.Equal("F3", reloaded.Describe(ShortcutAction.ShowGraph));
        Assert.Equal(ShortcutAction.ShowGraph, reloaded.Resolve(Key.F3, KeyModifiers.None, typing: false));
    }

    [Fact]
    public void An_unreadable_or_clashing_setting_is_ignored()
    {
        var map = ShortcutMap.CreateDefault();

        map.Apply(new Dictionary<string, string>
        {
            ["SaveNote"] = "Ctrl+J",
            ["NewNote"] = "Ctrl+J",   // already taken by the line above
            ["ShowChat"] = "not a chord",
            ["Nonsense"] = "Ctrl+K",
        });

        Assert.Equal("Ctrl+J", map.Describe(ShortcutAction.SaveNote));
        Assert.Equal("Ctrl+N", map.Describe(ShortcutAction.NewNote));
        Assert.Equal("Ctrl+4", map.Describe(ShortcutAction.ShowChat));
    }

    [Fact]
    public void Digits_and_slash_survive_being_written_and_read_back()
    {
        Assert.True(ShortcutMap.TryParse("Ctrl+1", out var digit, out var modifiers));
        Assert.Equal(Key.D1, digit);
        Assert.Equal("Ctrl+1", ShortcutMap.Describe(digit, modifiers));

        Assert.Equal("/", ShortcutMap.KeyName(Key.OemQuestion));
        Assert.Equal("Escape", ShortcutMap.Describe(Key.Escape, KeyModifiers.None));
    }
}
