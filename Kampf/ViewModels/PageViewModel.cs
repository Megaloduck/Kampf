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
}
