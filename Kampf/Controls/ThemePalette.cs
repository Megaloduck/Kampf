using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Styling;

namespace Kampf.Controls;

/// <summary>
/// Resolves brushes for surfaces that are drawn in code (graph canvas,
/// markdown preview) instead of through XAML styles, so they follow the
/// active light/dark theme.
/// </summary>
internal static class ThemePalette
{
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["TextBrush"] = ("#E4E8F1", "#12151C"),
        ["MutedBrush"] = ("#8C94AC", "#5C6478"),
        ["AccentBrush"] = ("#8FA6FF", "#4C63C9"),
        ["CodeBrush"] = ("#F0B27A", "#A14A00"),
        ["CodeBackgroundBrush"] = ("#141821", "#F3F4F8"),
        ["RuleBrush"] = ("#262C3A", "#E2E6EF"),
        ["QuoteBorderBrush"] = ("#3A4A56", "#C3CBD9"),
        ["GraphCanvasBrush"] = ("#0E1118", "#FBFBFD"),
        ["GraphLabelBrush"] = ("#D7DCEA", "#2A3040"),
        ["GraphMutedBrush"] = ("#5A6178", "#8A92A6"),
        ["GraphEdgeManualBrush"] = ("#3D465C", "#B9C1D0"),
        ["GraphEdgeWikiBrush"] = ("#4C5876", "#9AA5BC"),
        ["GraphEdgeAcceptedBrush"] = ("#A78BFA", "#7A5BD0"),
    };

    public static bool IsLight =>
        Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    public static IBrush Brush(string key)
    {
        if (!Palette.TryGetValue(key, out var pair))
            return Brushes.Gray;

        return new SolidColorBrush(Color.Parse(IsLight ? pair.Light : pair.Dark));
    }
}
