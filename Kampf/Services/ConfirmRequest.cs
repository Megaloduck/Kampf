using System.Collections.Generic;

namespace Kampf.Services;

/// <summary>
/// What a confirmation dialog asks. The details are separate lines so the
/// consequences of an irreversible action are read one at a time instead of
/// disappearing inside a paragraph.
/// </summary>
public sealed record ConfirmRequest(
    string Title,
    string Body,
    string ConfirmLabel,
    bool IsDanger = false,
    IReadOnlyList<string>? Details = null);
