using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kampf.Controls;

namespace Kampf.ViewModels;

/// <summary>
/// One deleted note or paper waiting in the trash bin. Nothing here is visible to
/// the rest of the vault until it is restored. Deleting for good is confirmed by
/// the shared dialog, then held for the undo window by the main view-model.
/// </summary>
public sealed partial class TrashRowViewModel : ObservableObject
{
    private readonly Action<TrashRowViewModel> _restore;
    private readonly Action<TrashRowViewModel> _delete;

    public TrashRowViewModel(
        string id,
        string title,
        bool isPaper,
        DateTimeOffset? deletedAt,
        Action<TrashRowViewModel> restore,
        Action<TrashRowViewModel> delete)
    {
        Id = id;
        Title = title;
        IsPaper = isPaper;
        DeletedLabel = Describe(deletedAt);
        _restore = restore;
        _delete = delete;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>An imported paper rather than a note.</summary>
    public bool IsPaper { get; }

    public string KindLabel => IsPaper ? "PAPER" : "NOTE";

    public string Icon => IsPaper ? Symbols.Description : Symbols.StickyNote;

    /// <summary>"12 min ago", so the bin reads like a list of recent deletions.</summary>
    public string DeletedLabel { get; }

    [RelayCommand]
    private void Restore() => _restore(this);

    [RelayCommand]
    private void DeleteForever() => _delete(this);

    private static string Describe(DateTimeOffset? deletedAt)
    {
        if (deletedAt is null)
            return "deleted earlier";

        var age = DateTimeOffset.Now - deletedAt.Value;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";

        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";

        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";

        return $"{(int)age.TotalDays} d ago";
    }
}
