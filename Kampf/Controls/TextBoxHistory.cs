using System;
using System.Reflection;
using Avalonia.Controls;

namespace Kampf.Controls;

/// <summary>
/// Avalonia's TextBox keeps its undo stack when Text is replaced
/// programmatically, so loading another note would let Ctrl+Z restore the
/// previous note's body into the new one. The public TextBox API has no way to
/// drop that history, so this calls the internal helper's Clear(). Failure is
/// non-fatal: the worst case is a stale undo step.
/// </summary>
internal static class TextBoxHistory
{
    private static readonly FieldInfo? HelperField = typeof(TextBox)
        .GetField("_undoRedoHelper", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Clear(TextBox box)
    {
        try
        {
            if (HelperField?.GetValue(box) is not { } helper)
                return;

            helper.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(helper, null);
        }
        catch (Exception)
        {
            // Reflection into Avalonia internals: never break note switching.
        }
    }
}
