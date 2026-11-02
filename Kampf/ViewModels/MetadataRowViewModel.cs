namespace Kampf.ViewModels;

/// <summary>
/// One label/value pair in the reader's citation detail table. Rows are only
/// created for values the PDF actually carried, so nothing shows up blank.
/// </summary>
public sealed record MetadataRowViewModel(string Label, string Value);
