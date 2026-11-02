using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kampf.Application.Models;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Kampf.Infrastructure;

namespace Kampf.ViewModels;

public enum VaultView
{
    Graph,
    Editor,
    Reader,
}

/// <summary>
/// Orchestrates the Phase 1 surface: capture (notes + PDFs + highlights),
/// the graph, full-text search and the approval-gated review queue.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private const string AllSources = "All sources";
    private const string NotesOnly = "Notes only";
    private const string DocumentsOnly = "Documents only";
    private const string AllTags = "All tags";

    private readonly Vault _vault;
    private readonly NoteService _noteService;
    private readonly ImportService _importService;
    private readonly GraphService _graphService;
    private readonly SearchService _searchService;
    private readonly SuggestionService _suggestionService;
    private readonly Dictionary<string, IReadOnlyList<PageViewModel>> _pageCache = new(StringComparer.Ordinal);

    private List<Note> _allNotes = new();
    private List<PdfDocument> _allDocuments = new();
    private List<Link> _allLinks = new();

    private string? _editingNoteId;
    private bool _suppressSelection;
    private bool _suppressEditor;

    public MainViewModel(Vault vault)
    {
        _vault = vault;
        _noteService = new NoteService(vault.Repository);
        _importService = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files);
        _graphService = new GraphService(vault.Repository);
        _searchService = new SearchService(vault.Repository);
        _suggestionService = new SuggestionService(vault.Repository, _noteService);

        VaultPath = vault.RootPath;
        SourceFilters = new ObservableCollection<string> { AllSources, NotesOnly, DocumentsOnly };
        TagFilters = new ObservableCollection<string> { AllTags };
    }

    /// <summary>Set by the view so the view model stays free of window APIs.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickPdfFiles { get; set; }

    public string VaultPath { get; }

    public ObservableCollection<NoteRowViewModel> Notes { get; } = new();
    public ObservableCollection<DocumentRowViewModel> Documents { get; } = new();
    public ObservableCollection<SuggestionRowViewModel> Suggestions { get; } = new();
    public ObservableCollection<AuditRowViewModel> Activity { get; } = new();
    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<NoteRowViewModel> AnchoredNotes { get; } = new();
    public ObservableCollection<string> SourceFilters { get; }
    public ObservableCollection<string> TagFilters { get; }

    // ------------------------------------------------------------- shell state

    [ObservableProperty] private string _statusMessage = "Opening vault\u2026";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeButtonLabel))]
    private bool _isDarkTheme = true;

    public string ThemeButtonLabel => IsDarkTheme ? "Light theme" : "Dark theme";

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Avalonia.Application.Current is { } application)
            application.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _countsSummary = string.Empty;
    [ObservableProperty] private string _graphSummary = string.Empty;
    [ObservableProperty] private int _pendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGraphView), nameof(IsEditorView), nameof(IsReaderView))]
    private VaultView _view = VaultView.Graph;

    public bool IsGraphView => View == VaultView.Graph;
    public bool IsEditorView => View == VaultView.Editor;
    public bool IsReaderView => View == VaultView.Reader;

    // ------------------------------------------------------------ sidebar state

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _hasSearchResults;
    [ObservableProperty] private string _listHeader = "NOTES";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNote))]
    private NoteRowViewModel? _selectedNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDocument))]
    private DocumentRowViewModel? _selectedDocument;

    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private string _sourceFilter = AllSources;
    [ObservableProperty] private string _tagFilter = AllTags;
    [ObservableProperty] private string _readerTitle = string.Empty;
    [ObservableProperty] private string _readerCitation = string.Empty;
    [ObservableProperty] private string _readerMeta = string.Empty;
    [ObservableProperty] private bool _confirmDeleteDocument;

    public bool HasSelectedNote => SelectedNote is not null;
    public bool HasSelectedDocument => SelectedDocument is not null;

    partial void OnSelectedNoteChanged(NoteRowViewModel? value)
    {
        if (_suppressSelection || value is null)
        {
            ConfirmDelete = false;
            return;
        }

        SelectedNodeId = value.Note.Id;
        _ = SelectNoteAsync(value.Note);
    }

    private async Task SelectNoteAsync(Note note)
    {
        await SaveEditorIfDirtyAsync();
        LoadEditor(note);
        ConfirmDelete = false;
        View = VaultView.Editor;
    }

    partial void OnSelectedDocumentChanged(DocumentRowViewModel? value)
    {
        ConfirmDeleteDocument = false;

        if (_suppressSelection || value is null)
            return;

        SelectedNodeId = value.Document.Id;
        _ = LoadDocumentAsync(value.Document);
    }

    partial void OnSourceFilterChanged(string value) => RebuildGraph();

    partial void OnTagFilterChanged(string value) => RebuildGraph();

    // ------------------------------------------------------------ editor state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveNote))]
    private string _editorTitle = string.Empty;

    [ObservableProperty] private string _editorBody = string.Empty;
    [ObservableProperty] private string _editorTags = string.Empty;
    [ObservableProperty] private string _editorMeta = string.Empty;
    [ObservableProperty] private string _provenanceText = string.Empty;
    [ObservableProperty] private string _backlinksText = string.Empty;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _confirmDelete;
    [ObservableProperty] private bool _canOpenSource;

    public bool HasActiveNote => _editingNoteId is not null;

    partial void OnEditorTitleChanged(string value)
    {
        if (!_suppressEditor)
            IsDirty = true;
    }

    partial void OnEditorBodyChanged(string value)
    {
        if (!_suppressEditor)
            IsDirty = true;
    }

    partial void OnEditorTagsChanged(string value)
    {
        if (!_suppressEditor)
            IsDirty = true;
    }

    // --------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        try
        {
            await RefreshAsync();
            View = VaultView.Graph;
            StatusMessage = _allNotes.Count == 0 && _allDocuments.Count == 0
                ? "Empty vault \u2014 import a PDF or create your first note."
                : $"Vault ready \u00b7 {_allNotes.Count} notes \u00b7 {_allDocuments.Count} papers";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open vault: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _allNotes = (await _vault.Repository.ListNotesAsync()).ToList();
            _allDocuments = (await _vault.Repository.ListDocumentsAsync()).ToList();
            _allLinks = (await _vault.Repository.ListLinksAsync()).ToList();

            var noteId = SelectedNote?.Note.Id ?? _editingNoteId;
            var documentId = SelectedDocument?.Document.Id;

            RebuildNoteAndDocumentLists();
            await ReloadReviewAsync();
            RebuildTagFilters();
            RebuildGraph();

            if (noteId is not null)
                SelectNoteById(noteId);
            else if (documentId is not null && Documents.FirstOrDefault(d => d.Document.Id == documentId) is { } doc)
                SelectDocumentSilently(doc);

            HasSearchResults = false;
            ListHeader = "NOTES";
            UpdateCounts();
        }
        catch (Exception ex)
        {
            StatusMessage = "Refresh failed: " + ex.Message;
        }
    }

    private void RebuildNoteAndDocumentLists()
    {
        _suppressSelection = true;

        var anchoredCounts = _allNotes
            .Where(n => n.SourceDocumentId is not null)
            .GroupBy(n => n.SourceDocumentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Notes.Clear();
        foreach (var note in _allNotes)
            Notes.Add(new NoteRowViewModel(note));

        Documents.Clear();
        foreach (var document in _allDocuments)
            Documents.Add(new DocumentRowViewModel(document, anchoredCounts.GetValueOrDefault(document.Id)));

        _suppressSelection = false;
    }

    private async Task ReloadReviewAsync()
    {
        var pending = await _suggestionService.ListPendingAsync();
        Suggestions.Clear();
        foreach (var suggestion in pending)
        {
            Suggestions.Add(new SuggestionRowViewModel(
                suggestion,
                AcceptSuggestionAsync,
                RejectSuggestionAsync));
        }
        PendingCount = Suggestions.Count;

        var audit = await _vault.Repository.ListAuditAsync(8);
        Activity.Clear();
        foreach (var entry in audit)
            Activity.Add(new AuditRowViewModel(entry));
    }

    private void RebuildTagFilters()
    {
        var names = _allNotes
            .SelectMany(n => n.Tags)
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = TagFilter;
        TagFilters.Clear();
        TagFilters.Add(AllTags);
        foreach (var name in names)
            TagFilters.Add(name);

        TagFilter = TagFilters.Contains(previous) ? previous : AllTags;
    }

    private void UpdateCounts()
    {
        var tagCount = _allNotes.SelectMany(n => n.Tags).Select(t => t.Name).Distinct().Count();
        CountsSummary = $"{_allNotes.Count} notes \u00b7 {_allDocuments.Count} papers \u00b7 {_allLinks.Count} links \u00b7 {tagCount} tags";
    }

    // ------------------------------------------------------------------ graph

    private void RebuildGraph()
    {
        IEnumerable<Note> notes = _allNotes;
        IEnumerable<PdfDocument> documents = _allDocuments;

        if (SourceFilter == DocumentsOnly)
            notes = Enumerable.Empty<Note>();
        else if (SourceFilter == NotesOnly)
            documents = Enumerable.Empty<PdfDocument>();

        if (TagFilter != AllTags)
        {
            notes = notes.Where(n => n.Tags.Any(t => t.Name.Equals(TagFilter, StringComparison.OrdinalIgnoreCase)));
            documents = Enumerable.Empty<PdfDocument>();
        }

        var noteList = notes.ToList();
        var documentList = documents.ToList();
        var ids = noteList.Select(n => n.Id)
            .Concat(documentList.Select(d => d.Id))
            .ToHashSet(StringComparer.Ordinal);

        var links = _allLinks
            .Where(l => ids.Contains(l.SourceId) && ids.Contains(l.TargetId))
            .ToList();

        Graph = GraphService.Build(noteList, documentList, links);

        GraphSummary = Graph.IsEmpty
            ? "No nodes in view"
            : $"{noteList.Count} notes \u00b7 {documentList.Count} papers \u00b7 {links.Count} edges";
    }

    [ObservableProperty] private GraphModel? _graph;

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        try
        {
            await SaveEditorIfDirtyAsync();
            var note = await _noteService.CreateAsync("Untitled note");
            await RefreshAsync();
            SelectNoteById(note.Id);
            View = VaultView.Editor;
            StatusMessage = "New note \u2014 markdown supported, [[Title]] connects notes.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not create note: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        var saved = await SaveEditorIfDirtyAsync(force: true);
        if (!saved)
        {
            StatusMessage = "Nothing to save.";
            return;
        }

        await RefreshAsync();
        if (_editingNoteId is not null)
            SelectNoteById(_editingNoteId);
        StatusMessage = "Saved.";
    }

    /// <summary>Persists the editor when it has unsaved changes.</summary>
    private async Task<bool> SaveEditorIfDirtyAsync(bool force = false)
    {
        if (_editingNoteId is null || (!IsDirty && !force))
            return false;

        var note = _allNotes.FirstOrDefault(n => n.Id == _editingNoteId);
        if (note is null)
        {
            note = await _vault.Repository.GetNoteAsync(_editingNoteId);
            if (note is null)
                return false;
            _allNotes.Add(note);
        }

        note.Title = string.IsNullOrWhiteSpace(EditorTitle) ? "Untitled note" : EditorTitle.Trim();
        note.Body = EditorBody ?? string.Empty;

        try
        {
            await _noteService.SetTagsAsync(note, ParseTags(EditorTags));
            await _noteService.SaveAsync(note);
            IsDirty = false;

            // Keep derived state (graph edges, review queue, activity) current.
            _allLinks = (await _vault.Repository.ListLinksAsync()).ToList();
            await ReloadReviewAsync();
            RebuildGraph();
            UpdateCounts();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private async Task DeleteNoteAsync()
    {
        if (_editingNoteId is null)
            return;

        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            StatusMessage = "Click delete again to remove this note permanently.";
            return;
        }

        try
        {
            var id = _editingNoteId;
            await _noteService.DeleteAsync(id);

            _editingNoteId = null;
            _suppressSelection = true;
            SelectedNote = null;
            _suppressSelection = false;
            IsDirty = false;
            ConfirmDelete = false;

            await RefreshAsync();
            StatusMessage = "Note deleted.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Delete failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteDocumentAsync()
    {
        if (SelectedDocument is null)
            return;

        var document = SelectedDocument.Document;

        if (!ConfirmDeleteDocument)
        {
            ConfirmDeleteDocument = true;
            StatusMessage = "Deleting also removes the stored PDF and its highlights. Click delete again to confirm.";
            return;
        }

        try
        {
            await _importService.DeleteDocumentAsync(document);

            _pageCache.Remove(document.Id);
            Pages.Clear();
            AnchoredNotes.Clear();
            ReaderTitle = string.Empty;
            ReaderCitation = string.Empty;
            ReaderMeta = string.Empty;
            ConfirmDeleteDocument = false;

            _suppressSelection = true;
            SelectedDocument = null;
            SelectedNodeId = null;
            _suppressSelection = false;

            await RefreshAsync();
            View = VaultView.Graph;
            StatusMessage = $"Deleted \u201c{document.Title}\u201d and its stored PDF.";
        }
        catch (Exception ex)
        {
            ConfirmDeleteDocument = false;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportPdfAsync()
    {
        if (PickPdfFiles is null)
        {
            StatusMessage = "File picker unavailable.";
            return;
        }

        var files = await PickPdfFiles();
        if (files.Count == 0)
            return;

        IsBusy = true;
        string? lastImported = null;
        var failures = new List<string>();

        try
        {
            foreach (var file in files)
            {
                try
                {
                    StatusMessage = $"Importing {Path.GetFileName(file)}\u2026";
                    var result = await _importService.ImportAsync(file);
                    lastImported = result.Document.Id;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();

        if (lastImported is not null)
        {
            SelectDocumentById(lastImported);
            View = VaultView.Reader;
        }

        StatusMessage = failures.Count == 0
            ? $"Imported {files.Count} PDF(s). Highlight text to create a page-anchored note."
            : $"Imported with {failures.Count} failure(s): {string.Join("; ", failures)}";
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            await RefreshAsync();
            return;
        }

        try
        {
            var hits = await _searchService.SearchAsync(query);
            var noteOrder = hits.Where(h => h.Kind == "note").Select(h => h.Id).ToList();
            var documentOrder = hits.Where(h => h.Kind == "document").Select(h => h.Id).ToList();

            _suppressSelection = true;

            Notes.Clear();
            foreach (var id in noteOrder)
            {
                var note = _allNotes.FirstOrDefault(n => n.Id == id);
                if (note is not null)
                    Notes.Add(new NoteRowViewModel(note));
            }

            Documents.Clear();
            var anchoredCounts = _allNotes
                .Where(n => n.SourceDocumentId is not null)
                .GroupBy(n => n.SourceDocumentId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            foreach (var id in documentOrder)
            {
                var document = _allDocuments.FirstOrDefault(d => d.Id == id);
                if (document is not null)
                    Documents.Add(new DocumentRowViewModel(document, anchoredCounts.GetValueOrDefault(document.Id)));
            }

            SelectedNote = null;
            SelectedDocument = null;

            _suppressSelection = false;

            HasSearchResults = true;
            ListHeader = $"RESULTS \u00b7 {hits.Count}";
            StatusMessage = hits.Count == 0
                ? $"No matches for \u201c{query}\u201d."
                : $"{hits.Count} result(s) for \u201c{query}\u201d.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Search failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchQuery = string.Empty;
        await RefreshAsync();
        StatusMessage = "Search cleared.";
    }

    [RelayCommand]
    private void ShowGraph() => View = VaultView.Graph;

    [RelayCommand]
    private void ShowEditor()
    {
        if (SelectedNote is not null)
            View = VaultView.Editor;
    }

    [RelayCommand]
    private void ShowReader()
    {
        if (SelectedDocument is not null)
            View = VaultView.Reader;
    }

    public async Task AcceptSuggestionAsync(SuggestionRowViewModel? row)
    {
        if (row is null)
            return;

        try
        {
            var decision = await _suggestionService.DecideAsync(row.Id, accept: true);
            await RefreshAsync();

            if (decision.CreatedNoteId is not null)
            {
                SelectNoteById(decision.CreatedNoteId);
                StatusMessage = "Note created and linked \u2014 the edge is now in your graph.";
            }
            else
            {
                StatusMessage = "Suggestion accepted \u2014 graph updated.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not accept suggestion: " + ex.Message;
        }
    }

    public async Task RejectSuggestionAsync(SuggestionRowViewModel? row)
    {
        if (row is null)
            return;

        try
        {
            await _suggestionService.DecideAsync(row.Id, accept: false);
            await RefreshAsync();
            StatusMessage = "Suggestion rejected \u2014 nothing was changed.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not reject suggestion: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenSourceAsync()
    {
        var note = _allNotes.FirstOrDefault(n => n.Id == _editingNoteId);
        if (note?.SourceDocumentId is null)
            return;

        SelectDocumentById(note.SourceDocumentId);
        View = VaultView.Reader;
        await Task.CompletedTask;
    }

    public async Task OpenWikiLinkAsync(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        var note = _allNotes.FirstOrDefault(n =>
            n.Title.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase));

        if (note is null)
        {
            var suggestionExists = Suggestions.Any(s =>
                s.Suggestion.Kind == SuggestionKind.CreateMissingNote);
            StatusMessage = $"No note called \u201c{target}\u201d yet" +
                            (suggestionExists ? " \u2014 it is waiting in the review queue." : ".");
            return;
        }

        SelectNoteById(note.Id);
        View = VaultView.Editor;
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------- reader / PDF

    private async Task LoadDocumentAsync(PdfDocument document)
    {
        SelectedDocument = Documents.FirstOrDefault(d => d.Document.Id == document.Id) ?? SelectedDocument;
        View = VaultView.Reader;
        ReaderTitle = document.Title;
        ReaderCitation = string.IsNullOrWhiteSpace(document.Authors)
            ? $"{(document.Year?.ToString() ?? "n.d.")} \u00b7 {document.FileName}"
            : $"{document.Authors} \u00b7 {document.Year?.ToString() ?? "n.d."}";
        ReaderMeta = $"{document.PageCount} pages";
        IsBusy = true;

        try
        {
            if (!_pageCache.TryGetValue(document.Id, out var pages))
            {
                if (!File.Exists(document.FilePath))
                {
                    Pages.Clear();
                    StatusMessage = $"PDF missing from vault: {document.FileName}";
                    return;
                }

                var extraction = await _vault.PdfExtractor.ExtractAsync(document.FilePath);
                pages = extraction.Pages.Select(p => new PageViewModel(p.Page, p.Text)).ToList();
                _pageCache[document.Id] = pages;
            }

            Pages.Clear();
            foreach (var page in pages)
                Pages.Add(page);

            AnchoredNotes.Clear();
            var anchored = await _vault.Repository.ListNotesForDocumentAsync(document.Id);
            foreach (var note in anchored)
                AnchoredNotes.Add(new NoteRowViewModel(note));

            StatusMessage = $"{document.Title} \u00b7 {document.PageCount} pages \u00b7 {anchored.Count} anchored note(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not read PDF: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Called by the reader when the user highlights text and captures it.</summary>
    public async Task CreateNoteFromHighlightAsync(int page, string text)
    {
        if (SelectedDocument is null)
        {
            StatusMessage = "Open a PDF first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "Select some text on a page first.";
            return;
        }

        try
        {
            var note = await _noteService.CreateFromHighlightAsync(SelectedDocument.Document, page, text);
            await RefreshAsync();
            SelectNoteById(note.Id);
            View = VaultView.Editor;
            StatusMessage = $"Note captured from page {page} \u2014 it keeps the page anchor.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not capture highlight: " + ex.Message;
        }
    }

    // -------------------------------------------------------------- selection

    public void SelectNoteById(string? noteId)
    {
        if (noteId is null)
            return;

        var row = Notes.FirstOrDefault(n => n.Note.Id == noteId);
        if (row is null)
            return;

        _suppressSelection = true;
        SelectedNote = row;
        _suppressSelection = false;

        SelectedNodeId = row.Note.Id;
        LoadEditor(row.Note);
    }

    private void SelectDocumentById(string? documentId)
    {
        if (documentId is null)
            return;

        var row = Documents.FirstOrDefault(d => d.Document.Id == documentId);
        if (row is null)
            return;

        SelectDocumentSilently(row);
        _ = LoadDocumentAsync(row.Document);
    }

    private void SelectDocumentSilently(DocumentRowViewModel row)
    {
        _suppressSelection = true;
        SelectedDocument = row;
        _suppressSelection = false;
        SelectedNodeId = row.Document.Id;
    }

    /// <summary>Graph single click: highlight. Double click (activated) opens.</summary>
    public void HighlightNode(GraphNode node)
    {
        SelectedNodeId = node.Id;
        StatusMessage = $"{node.Label} \u00b7 {node.Degree} connection(s)" +
                        (node.Tag is null ? string.Empty : $" \u00b7 #{node.Tag}");
    }

    public void OpenNode(GraphNode node)
    {
        if (node.Kind == NodeKind.Document)
            SelectDocumentById(node.Id);
        else
        {
            SelectNoteById(node.Id);
            View = VaultView.Editor;
        }
    }

    // ----------------------------------------------------------------- editor

    private void LoadEditor(Note note)
    {
        _suppressEditor = true;
        _editingNoteId = note.Id;
        EditorTitle = note.Title;
        EditorBody = note.Body;
        EditorTags = string.Join(", ", note.Tags.Select(t => t.Name));
        _suppressEditor = false;
        IsDirty = false;
        OnPropertyChanged(nameof(HasActiveNote));

        var document = note.SourceDocumentId is null
            ? null
            : _allDocuments.FirstOrDefault(d => d.Id == note.SourceDocumentId);

        CanOpenSource = document is not null;
        ProvenanceText = note.SourcePage is null
            ? "Written note \u00b7 no PDF anchor"
            : $"Anchored to \u201c{document?.Title ?? "document"}\u201d, page {note.SourcePage}";

        var incoming = _allLinks.Where(l => l.TargetId == note.Id).ToList();
        var sources = incoming
            .Select(l => _allNotes.FirstOrDefault(n => n.Id == l.SourceId)?.Title)
            .Where(t => t is not null)
            .ToList();

        BacklinksText = sources.Count == 0
            ? "No incoming links yet"
            : $"{sources.Count} incoming: {string.Join(", ", sources.Take(3))}" +
              (sources.Count > 3 ? "\u2026" : string.Empty);

        EditorMeta = $"{note.CreatedAt.LocalDateTime:MMM d, yyyy} \u00b7 {(note.HasProvenance ? "from source" : "original")}";
    }

    private static IEnumerable<string> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
