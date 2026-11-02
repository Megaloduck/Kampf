namespace Kampf.ViewModels;

/// <summary>One extracted page of text in the reader pane.</summary>
public sealed class PageViewModel
{
    public PageViewModel(int number, string text)
    {
        Number = number;
        Text = text;
    }

    public int Number { get; }

    public string Text { get; }

    public string HeaderLabel => $"Page {Number}";

    /// <summary>
    /// The last text the reader selected on this page. Kept because
    /// SelectableTextBlock clears its own selection as soon as the capture
    /// button takes focus.
    /// </summary>
    public string SelectedText { get; set; } = string.Empty;
}
