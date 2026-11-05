using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kampf.Application.Abstractions;
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
    Chat,
    Settings,
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
    private const string AllYears = "All years";
    private const string AllAuthors = "All authors";

    private readonly Vault _vault;
    private readonly NoteService _noteService;
    private readonly ImportService _importService;
    private readonly GraphService _graphService;
    private readonly SearchService _searchService;
    private readonly SuggestionService _suggestionService;
    private readonly SuggestionEngine _suggestionEngine;
    private readonly ChatService _chatService;
    private readonly DocumentBriefService _briefService;
    private readonly PublishService _publishService;
    private readonly DocumentPageIndex _pageIndex;
    private readonly IPdfPageRenderer _renderer;
    private readonly Dictionary<string, IReadOnlyList<PageViewModel>> _pageCache = new(StringComparer.Ordinal);

    /// <summary>Cancels the page rendering of the paper that was open before this one.</summary>
    private CancellationTokenSource? _renderCts;

    /// <summary>
    /// The page the reader was told to jump to but has not rendered yet. Its
    /// placeholder is shorter than the page itself, so the scroll is repeated
    /// once the image arrives and the card reaches its real height.
    /// </summary>
    private PageJump? _pendingJump;

    /// <summary>Width available to a page card at 100% zoom, reported by the reader.</summary>
    private double _readerViewportWidth = 680;

    /// <summary>Where the reader is now, saved when the paper is left.</summary>
    private int _readerPage = 1;
    private double _readerOffset;

    /// <summary>The paper the saved reading position belongs to.</summary>
    private string? _readerDocumentId;

    /// <summary>True while a paper's saved position is being applied.</summary>
    private bool _restoringReader;

    /// <summary>Cancels the answer that is currently streaming.</summary>
    private CancellationTokenSource? _chatCts;

    private List<Note> _allNotes = new();
    private List<PdfDocument> _allDocuments = new();
    private List<Link> _allLinks = new();

    private string? _editingNoteId;
    private bool _suppressSelection;
    private bool _suppressEditor;

    /// <summary>Raised after a note is loaded into the editor, i.e. a fresh undo baseline.</summary>
    public event EventHandler? EditorReloaded;

    public MainViewModel(Vault vault)
    {
        _vault = vault;
        _noteService = new NoteService(vault.Repository);
        _pageIndex = vault.Pages;
        _renderer = vault.PageRenderer;
        _importService = new ImportService(vault.Repository, vault.PdfExtractor, vault.Files, _pageIndex);
        _graphService = new GraphService(vault.Repository);
        _searchService = new SearchService(
            vault.Repository,
            new NoteEmbeddingIndex(vault.Repository, vault.Embeddings),
            _pageIndex);
        _suggestionService = new SuggestionService(vault.Repository, _noteService);
        _suggestionEngine = new SuggestionEngine(vault.Repository, vault.Embeddings);
        _chatService = new ChatService(vault.Repository, _searchService, vault.Chat);
        _briefService = new DocumentBriefService(vault.Chat);
        _publishService = new PublishService(vault.Repository);

        VaultPath = vault.RootPath;
        SourceFilters = new ObservableCollection<string> { AllSources, NotesOnly, DocumentsOnly };
        TagFilters = new ObservableCollection<string> { AllTags };
        YearFilters = new ObservableCollection<string> { AllYears };
        AuthorFilters = new ObservableCollection<string> { AllAuthors };
        SyncSecurityState();
    }

    /// <summary>Set by the view so the view model stays free of window APIs.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickPdfFiles { get; set; }

    /// <summary>Folder chooser for publishing a static copy of the vault.</summary>
    public Func<Task<string?>>? PickPublishFolder { get; set; }

    public string VaultPath { get; }

    public ObservableCollection<NoteRowViewModel> Notes { get; } = new();
    public ObservableCollection<DocumentRowViewModel> Documents { get; } = new();
    public ObservableCollection<SuggestionRowViewModel> Suggestions { get; } = new();
    public ObservableCollection<AuditRowViewModel> Activity { get; } = new();
    public ObservableCollection<PageViewModel> Pages { get; } = new();
    public ObservableCollection<NoteRowViewModel> AnchoredNotes { get; } = new();
    public ObservableCollection<MetadataRowViewModel> ReaderDetails { get; } = new();
    public ObservableCollection<ChatExchangeViewModel> ChatExchanges { get; } = new();
    public ObservableCollection<string> SourceFilters { get; }
    public ObservableCollection<string> TagFilters { get; }
    public ObservableCollection<string> YearFilters { get; }
    public ObservableCollection<string> AuthorFilters { get; }

    // ------------------------------------------------------------- shell state

    [ObservableProperty] private string _statusMessage = "Opening vault\u2026";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeButtonLabel))]
    private bool _isDarkTheme = true;

    public string ThemeButtonLabel => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

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
    [NotifyPropertyChangedFor(nameof(IsGraphView), nameof(IsEditorView), nameof(IsReaderView), nameof(IsChatView), nameof(IsSettingsView))]
    private VaultView _view = VaultView.Graph;

    public bool IsGraphView => View == VaultView.Graph;
    public bool IsEditorView => View == VaultView.Editor;
    public bool IsReaderView => View == VaultView.Reader;
    public bool IsChatView => View == VaultView.Chat;
    public bool IsSettingsView => View == VaultView.Settings;

    // ---------------------------------------------------------------- settings

    [ObservableProperty] private string _currentPassphrase = string.Empty;
    [ObservableProperty] private string _newPassphrase = string.Empty;
    [ObservableProperty] private string _confirmPassphrase = string.Empty;
    [ObservableProperty] private bool _isSecurityBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncryptionStatus), nameof(EncryptionHint), nameof(MirrorToggleEnabled), nameof(CanChangePassphrase))]
    private bool _isVaultEncrypted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MirrorStatus))]
    private bool _writeMarkdownMirrors = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsMessage))]
    private string _settingsMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsError))]
    private string _settingsError = string.Empty;

    public bool HasSettingsMessage => SettingsMessage.Length > 0;
    public bool HasSettingsError => SettingsError.Length > 0;
    public bool CanChangePassphrase => IsVaultEncrypted && !IsSecurityBusy;

    public string EncryptionStatus => IsVaultEncrypted
        ? "Encrypted at rest \u00b7 SQLCipher (AES-256)"
        : "Not encrypted \u00b7 plain SQLite file";

    public string EncryptionHint => IsVaultEncrypted
        ? "Note bodies, highlights and chat history are inside the encrypted database. There is no recovery if the passphrase is lost, so keep it somewhere safe."
        : "Kampf can encrypt the vault database with a passphrase. The database and its search index are rewritten under SQLCipher; PDFs in assets/ always stay readable on disk.";

    public string MirrorStatus => IsVaultEncrypted
        ? "Off while the vault is encrypted: a markdown file beside the database would expose note bodies in plaintext."
        : WriteMarkdownMirrors
            ? "On: every note is also written to notes/<id>.md so the vault is readable without Kampf."
            : "Off: notes live only in the database.";

    public bool MirrorToggleEnabled => !IsVaultEncrypted;

    public string VaultFolder => VaultPath;
    public string DatabaseFile => _vault.DatabasePath;
    public string NotesFolder => _vault.NotesDirectory;
    public string EmbeddingLabel =>
        $"{_vault.Embeddings.Model} \u00b7 similarity floor {_vault.Embeddings.SimilarityFloor:0.00}";

    partial void OnWriteMarkdownMirrorsChanged(bool value)
    {
        if (IsVaultEncrypted)
            return;

        _vault.Repository.WriteMarkdownMirror = value;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        View = VaultView.Settings;
        SyncSecurityState();
        ClearPassphraseFields();
        SettingsError = string.Empty;
        SettingsMessage = "";
    }

    [RelayCommand]
    private void CloseSettings() => View = VaultView.Graph;

    /// <summary>
    /// Phase 4 publishing: writes a static, self-contained copy of the notes and
    /// papers into a folder the researcher chooses. Nothing is uploaded — the
    /// folder can be zipped, committed or served however they like.
    /// </summary>
    [RelayCommand]
    private async Task PublishAsync()
    {
        if (PickPublishFolder is null)
        {
            SettingsError = "Folder picker unavailable.";
            return;
        }

        var folder = await PickPublishFolder();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        IsBusy = true;
        SettingsError = string.Empty;
        SettingsMessage = "Publishing\u2026";

        try
        {
            await SaveEditorIfDirtyAsync();

            var result = await _publishService.PublishAsync(folder);
            SettingsMessage =
                $"Published {result.Notes} note(s) and {result.Papers} paper(s) to {result.Directory}. " +
                "Open index.html there.";
            StatusMessage = $"Static copy published \u00b7 {result.Notes} note(s)";
        }
        catch (Exception ex)
        {
            SettingsMessage = string.Empty;
            SettingsError = "Publish failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnableEncryptionAsync()
    {
        if (IsVaultEncrypted || IsSecurityBusy)
            return;

        if (ValidateNewPassphrase() is { } problem)
        {
            SettingsError = problem;
            return;
        }

        IsSecurityBusy = true;
        SettingsError = string.Empty;
        SettingsMessage = "Encrypting the vault\u2026";

        try
        {
            await _vault.EnableEncryptionAsync(NewPassphrase);
            ClearPassphraseFields();
            SyncSecurityState();
            StatusMessage = "Vault encrypted. Plaintext note mirrors were removed; the PDFs in assets/ stay readable.";
            SettingsMessage = "Encryption is on. Keep the passphrase safe \u2014 without it the vault cannot be opened.";
        }
        catch (Exception ex)
        {
            SettingsError = "Encryption failed: " + ex.Message;
            SettingsMessage = string.Empty;
        }
        finally
        {
            IsSecurityBusy = false;
            SyncSecurityState();
        }
    }

    [RelayCommand]
    private async Task ChangePassphraseAsync()
    {
        if (!IsVaultEncrypted || IsSecurityBusy)
            return;

        if (!_vault.VerifyPassphrase(CurrentPassphrase))
        {
            SettingsError = "The current passphrase is not correct.";
            return;
        }

        if (ValidateNewPassphrase() is { } problem)
        {
            SettingsError = problem;
            return;
        }

        IsSecurityBusy = true;
        SettingsError = string.Empty;
        SettingsMessage = "Re-encrypting the vault\u2026";

        try
        {
            await _vault.ChangePassphraseAsync(NewPassphrase);
            ClearPassphraseFields();
            StatusMessage = "Vault passphrase changed.";
            SettingsMessage = "The vault was re-encrypted under the new passphrase.";
        }
        catch (Exception ex)
        {
            SettingsError = "Could not change the passphrase: " + ex.Message;
            SettingsMessage = string.Empty;
        }
        finally
        {
            IsSecurityBusy = false;
            SyncSecurityState();
        }
    }

    [RelayCommand]
    private async Task DisableEncryptionAsync()
    {
        if (!IsVaultEncrypted || IsSecurityBusy)
            return;

        if (!_vault.VerifyPassphrase(CurrentPassphrase))
        {
            SettingsError = "Enter the vault passphrase to turn encryption off.";
            return;
        }

        IsSecurityBusy = true;
        SettingsError = string.Empty;
        SettingsMessage = "Decrypting the vault\u2026";

        try
        {
            await _vault.DisableEncryptionAsync();
            ClearPassphraseFields();
            SyncSecurityState();
            WriteMarkdownMirrors = _vault.Repository.WriteMarkdownMirror;
            await RefreshAsync();
            StatusMessage = "Encryption is off. The database is a plain SQLite file again.";
            SettingsMessage = "Encryption removed; markdown mirrors were rewritten for every note.";
        }
        catch (Exception ex)
        {
            SettingsError = "Could not remove encryption: " + ex.Message;
            SettingsMessage = string.Empty;
        }
        finally
        {
            IsSecurityBusy = false;
            SyncSecurityState();
        }
    }

    private void SyncSecurityState()
    {
        IsVaultEncrypted = _vault.IsEncrypted;
        WriteMarkdownMirrors = _vault.Repository.WriteMarkdownMirror;
        OnPropertyChanged(nameof(EncryptionStatus));
        OnPropertyChanged(nameof(EncryptionHint));
        OnPropertyChanged(nameof(MirrorStatus));
        OnPropertyChanged(nameof(MirrorToggleEnabled));
        OnPropertyChanged(nameof(CanChangePassphrase));
    }

    private void ClearPassphraseFields()
    {
        CurrentPassphrase = string.Empty;
        NewPassphrase = string.Empty;
        ConfirmPassphrase = string.Empty;
    }

    private string? ValidateNewPassphrase()
    {
        if (NewPassphrase.Length < 8)
            return "Use at least 8 characters for the passphrase.";

        if (!string.Equals(NewPassphrase, ConfirmPassphrase, StringComparison.Ordinal))
            return "The two passphrases do not match.";

        return null;
    }

    // ------------------------------------------------------------- idea-craft

    [ObservableProperty] private string _chatQuestion = string.Empty;
    [ObservableProperty] private bool _isChatting;
    [ObservableProperty] private bool _hasChatHistory;

    /// <summary>The answer as it arrives, before the stored turn replaces it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveAnswer))]
    private string _liveAnswer = string.Empty;

    public bool HasLiveAnswer => LiveAnswer.Length > 0;

    /// <summary>The question being answered right now, kept while its answer streams.</summary>
    [ObservableProperty] private string _liveQuestion = string.Empty;

    /// <summary>
    /// Raised when the reader should scroll to a page (citation, search hit) and,
    /// when the jump knows which passage it came from, mark that passage.
    /// </summary>
    public event EventHandler<PageJump>? PageJumpRequested;

    /// <summary>Which backend answers: a local model, or cited passages only.</summary>
    public string ChatModelLabel => _chatService.ModelLabel;
    public bool HasLocalChatModel => _chatService.HasLocalModel;

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
    [ObservableProperty] private string _yearFilter = AllYears;
    [ObservableProperty] private string _authorFilter = AllAuthors;
    [ObservableProperty] private bool _unreviewedOnly;
    [ObservableProperty] private string _readerTitle = string.Empty;
    [ObservableProperty] private string _readerCitation = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReaderAbstract))]
    private string _readerAbstract = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReaderBrief))]
    private string _readerBrief = string.Empty;
    [ObservableProperty] private bool _isBriefing;
    [ObservableProperty] private bool _confirmDeleteDocument;

    /// <summary>The paper's abstract, when the PDF carried one.</summary>
    public bool HasReaderAbstract => ReaderAbstract.Length > 0;

    /// <summary>A model-written brief the researcher has not saved or dismissed yet.</summary>
    public bool HasReaderBrief => ReaderBrief.Length > 0;

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

    partial void OnYearFilterChanged(string value) => RebuildGraph();

    partial void OnAuthorFilterChanged(string value) => RebuildGraph();

    partial void OnUnreviewedOnlyChanged(bool value) => RebuildGraph();

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
            await ReloadChatAsync();
            View = VaultView.Graph;
            StatusMessage = _allNotes.Count == 0 && _allDocuments.Count == 0
                ? "Empty vault \u2014 import a PDF or create your first note."
                : $"Vault ready \u00b7 {_allNotes.Count} notes \u00b7 {_allDocuments.Count} papers";

            if (_allDocuments.Count > 0)
                _ = IndexPaperPagesAsync(_allDocuments);
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
            RebuildYearFilters();
            RebuildAuthorFilters();
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

    /// <summary>Rebuilds the Idea-Craft transcript from the stored turns.</summary>
    /// <summary>
    /// Brings paper page text and vectors up to date in the background, so a
    /// vault imported before this existed becomes searchable by body text.
    /// </summary>
    private async Task IndexPaperPagesAsync(IReadOnlyList<PdfDocument> documents)
    {
        try
        {
            // Hashing and embedding are CPU-bound, so they stay off the UI thread.
            var embedded = await Task.Run(() => _pageIndex.IndexAsync(documents));
            if (embedded > 0)
                StatusMessage = $"Indexed {embedded} paper page(s) for search.";
        }
        catch (Exception)
        {
            // Text search still works over stored pages; only vectors are missing.
        }
    }

    private async Task ReloadChatAsync()
    {
        var messages = await _chatService.HistoryAsync();
        ChatExchanges.Clear();

        string? pendingQuestion = null;
        foreach (var message in messages)
        {
            if (message.IsUser)
            {
                pendingQuestion = message.Text;
                continue;
            }

            if (pendingQuestion is null)
                continue;

            var citations = SuggestionJson.Deserialize<List<ChatCitation>>(message.CitationsJson ?? "[]")
                            ?? new List<ChatCitation>();

            ChatExchanges.Add(new ChatExchangeViewModel(
                pendingQuestion,
                message.Text,
                citations.Select(c => new ChatCitationViewModel(c, OpenChatNoteAsync, OpenChatPageAsync)).ToList(),
                message.CreatedAt,
                warning: null));

            pendingQuestion = null;
        }

        HasChatHistory = ChatExchanges.Count > 0;
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

    private void RebuildYearFilters()
    {
        var previous = YearFilter;
        YearFilters.Clear();
        YearFilters.Add(AllYears);

        var years = _allDocuments
            .Where(d => d.Year is not null)
            .Select(d => d.Year!.Value)
            .Distinct()
            .OrderByDescending(y => y);

        foreach (var year in years)
            YearFilters.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));

        YearFilter = YearFilters.Contains(previous) ? previous : AllYears;
    }

    private void RebuildAuthorFilters()
    {
        var previous = AuthorFilter;
        AuthorFilters.Clear();
        AuthorFilters.Add(AllAuthors);

        foreach (var author in _allDocuments
                     .SelectMany(d => SplitAuthors(d.Authors))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
            AuthorFilters.Add(author);

        AuthorFilter = AuthorFilters.Contains(previous) ? previous : AllAuthors;
    }

    /// <summary>"Wang; Huang" and "Wang, Huang" both yield two names.</summary>
    private static IEnumerable<string> SplitAuthors(string? authors) =>
        string.IsNullOrWhiteSpace(authors)
            ? Enumerable.Empty<string>()
            : authors
                .Split(new[] { ';', ',', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(a => a.Length > 0);

    /// <summary>Notes referenced by any pending proposal in the review queue.</summary>
    private HashSet<string> NotesWithPendingSuggestions()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in Suggestions)
        {
            switch (row.Suggestion.Kind)
            {
                case SuggestionKind.LinkNotes:
                case SuggestionKind.ConnectDots:
                {
                    var payload = SuggestionJson.Deserialize<LinkNotesPayload>(row.Suggestion.PayloadJson);
                    if (payload is not null)
                    {
                        ids.Add(payload.SourceNoteId);
                        ids.Add(payload.TargetNoteId);
                    }

                    break;
                }

                case SuggestionKind.ApplyTag:
                {
                    var payload = SuggestionJson.Deserialize<ApplyTagPayload>(row.Suggestion.PayloadJson);
                    if (payload is not null)
                        ids.Add(payload.NoteId);

                    break;
                }

                case SuggestionKind.CreateMissingNote:
                {
                    var payload = SuggestionJson.Deserialize<CreateMissingNotePayload>(row.Suggestion.PayloadJson);
                    if (payload is not null)
                        ids.Add(payload.SourceNoteId);

                    break;
                }
            }
        }

        return ids;
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

        if (YearFilter != AllYears && int.TryParse(YearFilter, out var year))
        {
            var yearByDocument = _allDocuments
                .Where(d => d.Year is not null)
                .ToDictionary(d => d.Id, d => d.Year!.Value, StringComparer.Ordinal);

            notes = notes.Where(n => n.SourceDocumentId is not null &&
                                     yearByDocument.TryGetValue(n.SourceDocumentId, out var noteYear) &&
                                     noteYear == year);
            documents = documents.Where(d => d.Year == year);
        }

        if (AuthorFilter != AllAuthors)
        {
            var authorsById = _allDocuments.ToDictionary(d => d.Id, d => d.Authors, StringComparer.Ordinal);

            static bool Names(string? authors, string name) =>
                SplitAuthors(authors).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            notes = notes.Where(n => n.SourceDocumentId is not null &&
                                     authorsById.TryGetValue(n.SourceDocumentId, out var authors) &&
                                     Names(authors, AuthorFilter));
            documents = documents.Where(d => Names(d.Authors, AuthorFilter));
        }

        // "Unreviewed": only the notes a pending proposal is about, so the graph
        // shows exactly what the queue is asking the researcher to decide.
        if (UnreviewedOnly)
        {
            var flagged = NotesWithPendingSuggestions();
            notes = notes.Where(n => flagged.Contains(n.Id));
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

            var anchored = AnchoredNotes.FirstOrDefault(n => n.Note.Id == id);
            if (anchored is not null)
                AnchoredNotes.Remove(anchored);

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
            _vault.PageRenderer.Forget(document.FilePath);
            Pages.Clear();
            AnchoredNotes.Clear();
            ReaderTitle = string.Empty;
            ReaderCitation = string.Empty;
            ReaderAbstract = string.Empty;
            ReaderBrief = string.Empty;
            ReaderDetails.Clear();
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

        await ImportFilesAsync(files);
    }

    /// <summary>
    /// Imports PDFs that arrived from somewhere other than the file picker
    /// (a drop onto the window). Extension filtering happens in the view.
    /// </summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> files)
    {
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

        var queued = failures.Count == files.Count ? 0 : await QueueSuggestionsAsync();

        if (lastImported is not null)
        {
            SelectDocumentById(lastImported);
            View = VaultView.Reader;
        }

        StatusMessage = failures.Count == 0
            ? $"Imported {files.Count} PDF(s)." + (queued > 0
                ? $" {queued} connection suggestion(s) queued for review."
                : " Highlight text to create a page-anchored note.")
            : $"Imported with {failures.Count} failure(s): {string.Join("; ", failures)}";
    }

    /// <summary>
    /// An import or a captured note changes what can be connected, so the engine
    /// runs after it instead of waiting for the button. It only fills the review
    /// queue: the graph is untouched until a proposal is accepted, and a failure
    /// here must not turn a successful import into an error.
    /// </summary>
    private async Task<int> QueueSuggestionsAsync()
    {
        try
        {
            await SaveEditorIfDirtyAsync();

            var batch = await _suggestionEngine.GenerateAsync();
            if (batch.Total == 0)
                return 0;

            await ReloadReviewAsync();
            UpdateCounts();
            return batch.Total;
        }
        catch (Exception)
        {
            return 0;
        }
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
            var outcome = await _searchService.SearchAsync(query);
            var hits = outcome.Hits;
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

            var pagesById = hits
                .Where(h => h.Kind == "document" && h.Page is not null)
                .GroupBy(h => h.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Page, StringComparer.Ordinal);

            foreach (var id in documentOrder)
            {
                var document = _allDocuments.FirstOrDefault(d => d.Id == id);
                if (document is not null)
                    Documents.Add(new DocumentRowViewModel(
                        document,
                        anchoredCounts.GetValueOrDefault(document.Id),
                        pagesById.GetValueOrDefault(id)));
            }

            SelectedNote = null;
            SelectedDocument = null;

            _suppressSelection = false;

            HasSearchResults = true;
            ListHeader = $"RESULTS \u00b7 {hits.Count}" +
                         (outcome.SemanticOnly > 0 ? $" \u00b7 {outcome.SemanticOnly} by meaning" : string.Empty);
            var meaning = outcome.SemanticOnly > 0
                ? $" \u00b7 {outcome.SemanticOnly} by meaning"
                : string.Empty;
            var warning = outcome.Warning is null ? string.Empty : $" \u00b7 {outcome.Warning}";

            StatusMessage = hits.Count == 0
                ? $"No matches for \u201c{query}\u201d."
                : $"{hits.Count} result(s) for \u201c{query}\u201d{meaning}{warning}.";
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
    private void ShowChat() => View = VaultView.Chat;

    /// <summary>
    /// Asks Idea-Craft a question. The answer is retrieval plus, when configured,
    /// a local model; either way it carries the notes and pages behind it.
    /// </summary>
    [RelayCommand]
    private async Task AskIdeaCraftAsync()
    {
        var question = ChatQuestion.Trim();
        if (question.Length == 0 || IsChatting)
            return;

        IsChatting = true;
        ChatQuestion = string.Empty;
        LiveQuestion = question;
        LiveAnswer = string.Empty;
        View = VaultView.Chat;

        using var cts = new CancellationTokenSource();
        _chatCts = cts;

        var buffer = new StringBuilder();
        var lastPost = DateTime.UtcNow;

        try
        {
            var answer = await _chatService.AskStreamingAsync(
                question,
                delta =>
                {
                    buffer.Append(delta);

                    // Post at most ~15 times a second: a token-by-token binding
                    // churn is wasted layout, but the answer must feel live.
                    var now = DateTime.UtcNow;
                    if ((now - lastPost).TotalMilliseconds < 65 && buffer.Length > 0)
                        return Task.CompletedTask;

                    lastPost = now;
                    var snapshot = buffer.ToString();
                    Dispatcher.UIThread.Post(() => LiveAnswer = snapshot);
                    return Task.CompletedTask;
                },
                cts.Token);

            await ReloadChatAsync();

            StatusMessage = answer.Citations.Count == 0
                ? "Idea-Craft found nothing to cite \u2014 it says so instead of guessing."
                : $"Answer built from {answer.Citations.Count} source(s)" +
                  (answer.UsedLocalModel ? "." : " \u2014 no local model configured, so the passages are the answer.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Answer stopped.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Idea-Craft failed: " + ex.Message;
        }
        finally
        {
            _chatCts = null;
            LiveAnswer = string.Empty;
            LiveQuestion = string.Empty;
            IsChatting = false;
        }
    }

    [RelayCommand]
    private void CancelChat() => _chatCts?.Cancel();

    /// <summary>A citation opens the note it points at, so the claim can be checked.</summary>
    private async Task OpenChatNoteAsync(string noteId)
    {
        if (Notes.All(n => n.Note.Id != noteId))
            await RefreshAsync();

        SelectNoteById(noteId);
        View = VaultView.Editor;
    }

    /// <summary>
    /// A page citation opens the paper at the page, where the claim lives, and
    /// marks the cited passage so it can be read in place.
    /// </summary>
    private async Task OpenChatPageAsync(string documentId, int page, string? quote = null)
    {
        var document = _allDocuments.FirstOrDefault(d => d.Id == documentId);
        if (document is null)
        {
            await RefreshAsync();
            document = _allDocuments.FirstOrDefault(d => d.Id == documentId);
        }

        if (document is null)
            return;

        await LoadDocumentAsync(document);
        MarkCitedPassage(page, quote);
        _pendingJump = new PageJump(page, quote);
        PageJumpRequested?.Invoke(this, _pendingJump);
    }

    /// <summary>Double-clicking a captured passage on the page opens its note.</summary>
    public void OpenAnchoredNote(string noteId)
    {
        SelectNoteById(noteId);

        if (SelectedNote?.Note.Id == noteId)
            View = VaultView.Editor;
    }

    // ------------------------------------------------- reading: zoom + search

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private double _readerZoom = ReadingState.DefaultZoom;

    public string ZoomLabel => $"{Math.Round(ReaderZoom * 100):0}%";

    partial void OnReaderZoomChanged(double value)
    {
        ApplyZoom();

        // Restoring a paper sets the zoom before its saved page and offset are
        // in place; writing now would overwrite them with the old position.
        if (!_restoringReader)
            _ = SaveReadingStateAsync();
    }

    [RelayCommand]
    private void ZoomIn() => ReaderZoom = Math.Min(ReadingState.MaximumZoom, Math.Round(ReaderZoom + 0.1, 2));

    [RelayCommand]
    private void ZoomOut() => ReaderZoom = Math.Max(ReadingState.MinimumZoom, Math.Round(ReaderZoom - 0.1, 2));

    [RelayCommand]
    private void ResetZoom() => ReaderZoom = ReadingState.DefaultZoom;

    /// <summary>The reader reports its width so page cards can be sized at the current zoom.</summary>
    public void SetReaderViewport(double width)
    {
        if (width <= 0 || Math.Abs(width - _readerViewportWidth) < 1)
            return;

        _readerViewportWidth = width;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        var cardWidth = Math.Max(320, _readerViewportWidth) * ReaderZoom;

        foreach (var page in Pages)
        {
            page.Zoom = ReaderZoom;
            page.ZoomWidth = Math.Round(cardWidth);
        }
    }

    /// <summary>The reader reports the page at the top of the viewport.</summary>
    public void UpdateReadingPosition(int page, double offset)
    {
        _readerPage = Math.Max(1, page);
        _readerOffset = Math.Clamp(double.IsFinite(offset) ? offset : 0, 0, 1);
    }

    /// <summary>Remembers where the open paper was left, so reopening resumes there.</summary>
    public async Task SaveReadingStateAsync()
    {
        if (_readerDocumentId is null)
            return;

        try
        {
            await _vault.Repository.SaveReadingStateAsync(new ReadingState(
                _readerDocumentId,
                _readerPage,
                _readerOffset,
                ReaderZoom,
                DateTimeOffset.Now));
        }
        catch (Exception)
        {
            // Losing a bookmark is not worth interrupting reading.
        }
    }

    [ObservableProperty] private string _readerSearchQuery = string.Empty;

    [ObservableProperty] private string _readerSearchLabel = string.Empty;

    [ObservableProperty] private bool _hasReaderSearch;

    private readonly List<(int Page, int Start, int Length)> _readerMatches = new();
    private int _readerMatchIndex = -1;

    partial void OnReaderSearchQueryChanged(string value) => RunReaderSearch(value);

    /// <summary>Marks every hit on every page, then jumps to the first.</summary>
    private void RunReaderSearch(string query)
    {
        foreach (var page in Pages)
        {
            page.ClearSpans(PageSpanKind.Match);
            page.ClearSpans(PageSpanKind.ActiveMatch);
        }

        _readerMatches.Clear();
        _readerMatchIndex = -1;

        if (query.Trim().Length < 2)
        {
            HasReaderSearch = false;
            ReaderSearchLabel = string.Empty;
            return;
        }

        foreach (var page in Pages)
        {
            foreach (var (start, length) in PageTextSearch.FindAll(page.Text, query))
            {
                page.AddSpan(start, length, PageSpanKind.Match);
                _readerMatches.Add((page.Number, start, length));
            }
        }

        HasReaderSearch = true;

        if (_readerMatches.Count == 0)
        {
            ReaderSearchLabel = "No matches";
            return;
        }

        GoToMatch(0);
    }

    [RelayCommand]
    private void NextReaderMatch() => StepMatch(1);

    [RelayCommand]
    private void PreviousReaderMatch() => StepMatch(-1);

    private void StepMatch(int direction)
    {
        if (_readerMatches.Count == 0)
            return;

        GoToMatch((_readerMatchIndex + direction + _readerMatches.Count) % _readerMatches.Count);
    }

    private void GoToMatch(int index)
    {
        if (index < 0 || index >= _readerMatches.Count)
            return;

        foreach (var page in Pages)
            page.ClearSpans(PageSpanKind.ActiveMatch);

        _readerMatchIndex = index;
        var (pageNumber, start, length) = _readerMatches[index];

        Pages.FirstOrDefault(p => p.Number == pageNumber)
            ?.AddSpan(start, length, PageSpanKind.ActiveMatch);

        ReaderSearchLabel = $"{index + 1} of {_readerMatches.Count}";
        _readerPage = pageNumber;
        _readerOffset = 0;
        PageJumpRequested?.Invoke(this, new PageJump(pageNumber));
    }

    /// <summary>Resumes the saved page, offset and zoom for this paper.</summary>
    private async Task RestoreReadingStateAsync(PdfDocument document)
    {
        ReadingState state;

        try
        {
            state = await _vault.Repository.GetReadingStateAsync(document.Id)
                    ?? ReadingState.For(document.Id);
        }
        catch (Exception)
        {
            state = ReadingState.For(document.Id);
        }

        _restoringReader = true;
        try
        {
            _readerPage = state.Page;
            _readerOffset = state.Offset;

            if (Math.Abs(ReaderZoom - state.Zoom) > 0.001)
                ReaderZoom = state.Zoom;
            else
                ApplyZoom();
        }
        finally
        {
            _restoringReader = false;
        }
    }

    /// <summary>Marks the cited passage on one page and clears the marker from the others.</summary>
    private void MarkCitedPassage(int pageNumber, string? quote)
    {
        foreach (var page in Pages)
        {
            if (quote is null || page.Number != pageNumber)
            {
                page.ClearCitation();
                continue;
            }

            var hit = PageTextSearch.Find(page.Text, quote);
            page.SetCitation(hit?.Start, hit?.Length);
        }
    }

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

    /// <summary>
    /// Runs the suggestion producers. Links and tags land in the review queue as
    /// Pending rows; the graph is untouched until the researcher accepts them.
    /// </summary>
    [RelayCommand]
    private async Task SuggestConnectionsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await SaveEditorIfDirtyAsync();

            var batch = await _suggestionEngine.GenerateAsync();
            await RefreshAsync();

            if (batch.Total == 0)
            {
                StatusMessage = "No new connections found \u2014 write a little more, then try again.";
            }
            else
            {
                var parts = new List<string>();
                if (batch.LinkSuggestions > 0)
                    parts.Add($"{batch.LinkSuggestions} link");
                if (batch.TagSuggestions > 0)
                    parts.Add($"{batch.TagSuggestions} tag");
                if (batch.ConnectTheDots > 0)
                    parts.Add($"{batch.ConnectTheDots} connect-the-dots");

                StatusMessage = $"{string.Join(", ", parts)} suggestion(s) queued across " +
                                $"{batch.Clusters} cluster(s) \u2014 nothing changes until you accept.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Suggestion run failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
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

    // -------------------------------------------------------------- paper brief

    /// <summary>
    /// Asks the local model for a brief of the open paper. Nothing is stored here:
    /// the brief appears in the reader and the researcher decides what happens to it.
    /// </summary>
    [RelayCommand]
    private async Task BriefDocumentAsync()
    {
        var document = SelectedDocument?.Document;
        if (document is null)
            return;

        if (!_briefService.HasLocalModel)
        {
            StatusMessage = "A brief needs a local model. Choose one in Settings, then try again.";
            return;
        }

        IsBriefing = true;
        try
        {
            var pages = await _vault.Repository.ListDocumentPagesAsync(document.Id);
            ReaderBrief = await _briefService.BriefAsync(document, pages.Select(p => p.Text).ToList());
            StatusMessage = $"Brief written by {_briefService.ModelLabel} \u2014 save it as a note or dismiss it. Nothing is stored yet.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Brief failed: " + ex.Message;
        }
        finally
        {
            IsBriefing = false;
        }
    }

    [RelayCommand]
    private async Task SaveBriefAsNoteAsync()
    {
        var document = SelectedDocument?.Document;
        if (document is null || !HasReaderBrief)
            return;

        var note = await _noteService.CreateAsync($"Brief: {document.Title}", BriefNoteBody());
        note.SourceDocumentId = document.Id;
        await _noteService.SaveAsync(note);
        await RefreshAsync();

        ReaderBrief = string.Empty;
        StatusMessage = $"Saved the brief as \u201c{note.Title}\u201d \u2014 it is anchored to the paper and was only written because you asked.";
    }

    [RelayCommand]
    private void DismissBrief() => ReaderBrief = string.Empty;

    /// <summary>The note body keeps the model's name, so the claim stays traceable.</summary>
    private string BriefNoteBody() =>
        ReaderBrief + "\n\n> Generated by " + _briefService.ModelLabel +
        " from this paper's text; it is a draft, not a finding. Check it against the paper before relying on it.";

    private async Task LoadDocumentAsync(PdfDocument document)
    {
        // Leaving a paper remembers where it was left; opening one resumes it.
        if (_readerDocumentId is { } previous && previous != document.Id)
            await SaveReadingStateAsync();

        _readerDocumentId = document.Id;
        ReaderSearchQuery = string.Empty;
        ReaderBrief = string.Empty;

        SelectedDocument = Documents.FirstOrDefault(d => d.Document.Id == document.Id) ?? SelectedDocument;
        View = VaultView.Reader;
        ReaderTitle = document.Title;
        IsBusy = true;

        try
        {
            PdfExtraction? extraction = null;

            if (!_pageCache.TryGetValue(document.Id, out var pages))
            {
                if (!File.Exists(document.FilePath))
                {
                    Pages.Clear();
                    ReaderCitation = BuildCitationLine(document);
                    FillReaderDetails(document);
                    StatusMessage = $"PDF missing from vault: {document.FileName}";
                    return;
                }

                extraction = await _vault.PdfExtractor.ExtractAsync(document.FilePath);
                pages = extraction.Pages.Select(p => new PageViewModel(p.Page, p.Text)).ToList();
                _pageCache[document.Id] = pages;
            }

            // Papers imported before the detail capture existed fill in on first open.
            if (extraction is not null && ApplyDetail(document, extraction))
                await _vault.Repository.SaveDocumentAsync(document);

            // Same for page text: a vault built before it was stored becomes
            // searchable the first time each paper is opened.
            if (extraction is not null && await _pageIndex.EnsureTextAsync(document, extraction.Pages))
                _ = IndexPaperPagesAsync(new[] { document });

            ReaderCitation = BuildCitationLine(document);
            ReaderAbstract = document.Abstract ?? string.Empty;
            FillReaderDetails(document);

            Pages.Clear();
            foreach (var page in pages)
                Pages.Add(page);

            AnchoredNotes.Clear();
            var anchored = await _vault.Repository.ListNotesForDocumentAsync(document.Id);
            foreach (var note in anchored)
                AnchoredNotes.Add(new NoteRowViewModel(note));                await LoadHighlightsAsync(document, pages);
            await RestoreReadingStateAsync(document);


            StatusMessage = $"{document.Title} \u00b7 {document.PageCount} pages \u00b7 {anchored.Count} anchored note(s)";

            var jump = SelectedDocument?.MatchPage is { } matchPage
                ? new PageJump(matchPage)
                : new PageJump(_readerPage, null, _readerOffset);

            _pendingJump = jump;
            PageJumpRequested?.Invoke(this, jump);

            // Rendered pages replace the stored text as the visual surface, so
            // start with the page the reader is about to look at.
            StartRendering(document, pages, jump.Page);
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

    /// <summary>
    /// Restores every captured quote as a highlight on its page, so the reader
    /// shows where the notes came from instead of only listing them.
    /// </summary>
    private async Task LoadHighlightsAsync(PdfDocument document, IReadOnlyList<PageViewModel> pages)
    {
        try
        {
            var highlights = await _vault.Repository.ListHighlightsAsync(document.Id);

            foreach (var highlight in highlights)
            {
                var page = pages.FirstOrDefault(p => p.Number == highlight.Page);
                if (page is null)
                    continue;

                var hit = PageTextSearch.Find(page.Text, highlight.Text);
                if (hit is { } span)
                    page.AddSpan(span.Start, span.Length, PageSpanKind.Note, highlight.NoteId);
            }
        }
        catch (Exception)
        {
            // Highlights are a reading aid: a failure here must not block the page.
        }
    }

    /// <summary>
    /// Renders the pages in the background, closest to the page the reader is
    /// looking at first. Rendering the whole paper up front would stall the
    /// reader on the first click of a long PDF.
    /// </summary>
    private void StartRendering(
        PdfDocument document,
        IReadOnlyList<PageViewModel> pages,
        int? firstPage)
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;

        if (pages.Count == 0)
            return;

        var cts = new CancellationTokenSource();
        _renderCts = cts;

        // Page images are large and no longer visible for any other paper.
        foreach (var (id, cached) in _pageCache)
        {
            if (id == document.Id)
                continue;

            foreach (var cachedPage in cached)
            {
                cachedPage.Image?.Dispose();
                cachedPage.Image = null;
            }
        }

        _ = RenderPagesAsync(document, pages, firstPage ?? 1, cts.Token);
    }

    private async Task RenderPagesAsync(
        PdfDocument document,
        IReadOnlyList<PageViewModel> pages,
        int firstPage,
        CancellationToken ct)
    {
        var startIndex = Math.Clamp(firstPage - 1, 0, pages.Count - 1);
        var order = new List<PageViewModel>(pages.Count) { pages[startIndex] };

        for (var distance = 1; order.Count < pages.Count; distance++)
        {
            if (startIndex + distance < pages.Count)
                order.Add(pages[startIndex + distance]);

            if (startIndex - distance >= 0)
                order.Add(pages[startIndex - distance]);
        }

        var path = document.FilePath;

        foreach (var page in order)
        {
            if (ct.IsCancellationRequested)
                return;

            // Pages cached from an earlier open of this paper keep their image.
            if (page.Image is not null)
                continue;

            page.IsRendering = true;

            try
            {
                var layout = await _renderer.GetLayoutAsync(path, page.Number, ct).ConfigureAwait(false);
                var image = await _renderer.RenderAsync(path, page.Number, 820, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                    return;

                // Bitmaps belong to the UI thread, and so do the notifications
                // that make the page card resize around the image.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    page.Layout = layout;
                    page.Image = ToBitmap(image);

                    if (_pendingJump is { } pending && pending.Page == page.Number)
                    {
                        _pendingJump = null;
                        PageJumpRequested?.Invoke(this, pending);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // No PDFium (or a broken page): the stored text stays readable.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    page.RenderError = ex.Message;
                    page.ShowText = true;
                });
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    await Dispatcher.UIThread.InvokeAsync(() => page.IsRendering = false);
            }
        }
    }

    /// <summary>Wraps PDFium's BGRA bytes in a bitmap the page control can draw.</summary>
    private static WriteableBitmap ToBitmap(PdfPageImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (var frame = bitmap.Lock())
        {
            var stride = image.Width * 4;

            for (var row = 0; row < image.Height; row++)
            {
                Marshal.Copy(
                    image.Pixels,
                    row * stride,
                    frame.Address + row * frame.RowBytes,
                    stride);
            }
        }

        return bitmap;
    }

    /// <summary>Authors, year and journal when known; the file name otherwise.</summary>
    private static string BuildCitationLine(PdfDocument document)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(document.Authors))
            parts.Add(document.Authors);

        parts.Add(document.Year?.ToString() ?? "n.d.");

        parts.Add(string.IsNullOrWhiteSpace(document.Publication)
            ? document.FileName
            : document.Publication);

        return string.Join(" \u00b7 ", parts);
    }

    /// <summary>
    /// The reader's citation table: every field the import actually captured.
    /// Missing fields are omitted rather than shown empty.
    /// </summary>
    private void FillReaderDetails(PdfDocument document)
    {
        ReaderDetails.Clear();

        Add("Item type", document.ItemType ?? "Document");
        Add("Authors", document.Authors);
        Add("Publication", document.Publication);
        Add("Volume", document.Volume);
        Add("Issue", document.Issue);

        if (string.IsNullOrWhiteSpace(document.PageRange))
        {
            Add("Pages", $"{document.PageCount} in file");
        }
        else
        {
            Add("Pages", document.PageRange);
            Add("PDF pages", document.PageCount.ToString());
        }

        Add("Date", document.Year?.ToString());
        Add("DOI", document.Doi);
        Add("ISSN", document.Issn);
        Add("URL", document.Url);
        Add("Language", document.Language);
        Add("Keywords", document.Keywords);
        Add("File", document.FileName);
        Add("Size", FormatSize(document.FilePath));
        Add("Imported", document.ImportedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                ReaderDetails.Add(new MetadataRowViewModel(label, value.Trim()));
        }
    }

    private static string? FormatSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            var kilobytes = info.Length / 1024.0;
            return kilobytes >= 1024 ? $"{kilobytes / 1024:0.0} MB" : $"{kilobytes:0} KB";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Fills missing citation detail from a fresh extraction. Papers imported before
    /// the detail capture existed get populated the first time they are opened.
    /// </summary>
    private static bool ApplyDetail(PdfDocument document, PdfExtraction extraction)
    {
        var changed = false;

        void Fill(Func<string?> current, Action<string?> set, string? value)
        {
            if (!string.IsNullOrWhiteSpace(current()) || string.IsNullOrWhiteSpace(value))
                return;

            set(value);
            changed = true;
        }

        Fill(() => document.ItemType, v => document.ItemType = v, extraction.ItemType);
        Fill(() => document.Abstract, v => document.Abstract = v, extraction.Abstract);
        Fill(() => document.Publication, v => document.Publication = v, extraction.Publication);
        Fill(() => document.Volume, v => document.Volume = v, extraction.Volume);
        Fill(() => document.Issue, v => document.Issue = v, extraction.Issue);
        Fill(() => document.PageRange, v => document.PageRange = v, extraction.PageRange);
        Fill(() => document.Issn, v => document.Issn = v, extraction.Issn);
        Fill(() => document.Url, v => document.Url = v, extraction.Url);
        Fill(() => document.Keywords, v => document.Keywords = v, extraction.Keywords);
        Fill(() => document.Language, v => document.Language = v, extraction.Language);
        Fill(() => document.Doi, v => document.Doi = v, extraction.Doi);

        if (document.Year is null && extraction.Year is not null)
        {
            document.Year = extraction.Year;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Called by the reader when the user highlights text and captures it.
    /// <paramref name="start"/> and <paramref name="length"/> are the offsets of
    /// the selection in the page text, used to draw the highlight straight away.
    /// </summary>
    public async Task CreateNoteFromHighlightAsync(int page, string text, int start = -1, int length = 0)
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

            // The reader is left open behind the editor, so its list must not go stale.
            if (SelectedDocument?.Document.Id == note.SourceDocumentId)
            {
                AnchoredNotes.Insert(0, new NoteRowViewModel(note));

                var pageViewModel = Pages.FirstOrDefault(p => p.Number == page);
                if (pageViewModel is not null)
                {
                    // Text-mode selections have no offsets, so locate the quote
                    // in the page text; either way the highlight appears at once.
                    if (start < 0 || length <= 0)
                    {
                        var located = PageTextSearch.Find(pageViewModel.Text, text);
                        start = located?.Start ?? -1;
                        length = located?.Length ?? 0;
                    }

                    if (start >= 0 && length > 0)
                        pageViewModel.AddSpan(start, length, PageSpanKind.Note, note.Id);
                }
            }

            View = VaultView.Editor;

            // A new note is a new thing the vault can connect; the queue fills
            // itself rather than waiting for the button to be pressed.
            var queued = await QueueSuggestionsAsync();
            StatusMessage = $"Note captured from page {page} \u2014 it keeps the page anchor." +
                            (queued > 0 ? $" {queued} connection suggestion(s) queued." : string.Empty);
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
        EditorReloaded?.Invoke(this, EventArgs.Empty);

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
