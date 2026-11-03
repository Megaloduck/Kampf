using System.Globalization;
using System.Text;
using Kampf.Application.Abstractions;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Microsoft.Data.Sqlite;

namespace Kampf.Infrastructure.Storage;

/// <summary>
/// SQLite-backed vault: a normalised relational schema for note/link/document
/// metadata plus an FTS5 index for search. Note bodies are also mirrored to
/// plain markdown files so the vault is readable without the app.
/// </summary>
public sealed class SqliteVaultRepository : IVaultRepository
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Column order shared by every document query and <see cref="ReadDocument"/>.</summary>
    private const string DocumentColumns =
        "id, file_path, file_name, title, authors, year, doi, page_count, imported_at, " +
        "item_type, abstract, publication, volume, issue, page_range, issn, url, keywords, language";

    /// <summary>Citation detail added after the first release; older vaults gain the columns on open.</summary>
    private static readonly string[] DocumentDetailColumns =
    {
        "item_type", "abstract", "publication", "volume", "issue",
        "page_range", "issn", "url", "keywords", "language",
    };

    private readonly string _connectionString;
    private readonly string _notesDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _ftsAvailable = true;

    public SqliteVaultRepository(string databasePath, string notesDirectory)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
        }.ToString();
        _notesDirectory = notesDirectory;
    }

    // ---------------------------------------------------------------- schema

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_notesDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_notesDirectory) ?? ".");

        await using var connection = await OpenAsync(ct);

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", ct);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS notes (
                id                 TEXT PRIMARY KEY,
                title              TEXT NOT NULL,
                body               TEXT NOT NULL,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL,
                source_document_id TEXT NULL,
                source_page        INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS tags (
                id   TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS note_tags (
                note_id TEXT NOT NULL,
                tag_id  TEXT NOT NULL,
                PRIMARY KEY (note_id, tag_id)
            );

            CREATE TABLE IF NOT EXISTS links (
                id         TEXT PRIMARY KEY,
                source_id  TEXT NOT NULL,
                target_id  TEXT NOT NULL,
                kind       INTEGER NOT NULL,
                rationale  TEXT NULL,
                created_at TEXT NOT NULL,
                UNIQUE (source_id, target_id, kind)
            );

            CREATE TABLE IF NOT EXISTS documents (
                id          TEXT PRIMARY KEY,
                file_path   TEXT NOT NULL,
                file_name   TEXT NOT NULL,
                title       TEXT NOT NULL,
                authors     TEXT NOT NULL,
                year        INTEGER NULL,
                doi         TEXT NULL,
                page_count  INTEGER NOT NULL,
                imported_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS highlights (
                id          TEXT PRIMARY KEY,
                document_id TEXT NOT NULL,
                page        INTEGER NOT NULL,
                text        TEXT NOT NULL,
                note_id     TEXT NULL,
                created_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS suggestions (
                id           TEXT PRIMARY KEY,
                kind         INTEGER NOT NULL,
                title        TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                status       INTEGER NOT NULL,
                created_at   TEXT NOT NULL,
                decided_at   TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS audit (
                id     TEXT PRIMARY KEY,
                at     TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_messages (
                id             TEXT PRIMARY KEY,
                role           TEXT NOT NULL,
                text           TEXT NOT NULL,
                citations_json TEXT NULL,
                created_at     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS note_embeddings (
                note_id      TEXT PRIMARY KEY,
                model        TEXT NOT NULL,
                dim          INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                vector       BLOB NOT NULL,
                updated_at   TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_notes_updated ON notes (updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_links_source ON links (source_id);
            CREATE INDEX IF NOT EXISTS ix_links_target ON links (target_id);
            CREATE INDEX IF NOT EXISTS ix_highlights_document ON highlights (document_id);
            CREATE INDEX IF NOT EXISTS ix_suggestions_status ON suggestions (status);
            CREATE INDEX IF NOT EXISTS ix_note_embeddings_model ON note_embeddings (model);
            CREATE INDEX IF NOT EXISTS ix_chat_messages_created ON chat_messages (created_at);
            """, ct);

        await EnsureDocumentDetailColumnsAsync(connection, ct);

        try
        {
            await ExecuteAsync(connection,
                "CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(note_id UNINDEXED, title, body, tokenize = 'porter unicode61');", ct);
            _ftsAvailable = true;
        }
        catch (SqliteException)
        {
            // Build without FTS5: search degrades to LIKE instead of failing.
            _ftsAvailable = false;
        }
    }

    /// <summary>Adds the optional citation columns to a vault created before they existed.</summary>
    private static async Task EnsureDocumentDetailColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(documents);";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetString(1));
        }

        foreach (var column in DocumentDetailColumns)
        {
            if (existing.Contains(column))
                continue;

            await ExecuteAsync(connection, $"ALTER TABLE documents ADD COLUMN {column} TEXT NULL;", ct);
        }
    }

    // ----------------------------------------------------------------- notes

    public async Task<IReadOnlyList<Note>> ListNotesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, created_at, updated_at, source_document_id, source_page
            FROM notes ORDER BY updated_at DESC;
            """;

        var notes = new List<Note>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                notes.Add(ReadNote(reader));
        }

        await AttachTagsAsync(connection, notes, ct);
        return notes;
    }

    public async Task<Note?> GetNoteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, created_at, updated_at, source_document_id, source_page
            FROM notes WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        Note? note = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                note = ReadNote(reader);
        }

        if (note is null)
            return null;

        await AttachTagsAsync(connection, new[] { note }, ct);
        return note;
    }

    public async Task<IReadOnlyList<Note>> ListNotesForDocumentAsync(string documentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, body, created_at, updated_at, source_document_id, source_page
            FROM notes WHERE source_document_id = $doc ORDER BY source_page, updated_at DESC;
            """;
        command.Parameters.AddWithValue("$doc", documentId);

        var notes = new List<Note>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                notes.Add(ReadNote(reader));
        }

        await AttachTagsAsync(connection, notes, ct);
        return notes;
    }

    public async Task SaveNoteAsync(Note note, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO notes (id, title, body, created_at, updated_at, source_document_id, source_page)
                VALUES ($id, $title, $body, $created, $updated, $doc, $page)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    body = excluded.body,
                    updated_at = excluded.updated_at,
                    source_document_id = excluded.source_document_id,
                    source_page = excluded.source_page;
                """;
            command.Parameters.AddWithValue("$id", note.Id);
            command.Parameters.AddWithValue("$title", note.Title);
            command.Parameters.AddWithValue("$body", note.Body);
            command.Parameters.AddWithValue("$created", ToDb(note.CreatedAt));
            command.Parameters.AddWithValue("$updated", ToDb(note.UpdatedAt));
            command.Parameters.AddWithValue("$doc", (object?)note.SourceDocumentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$page", (object?)note.SourcePage ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);

            await UpsertFtsAsync(connection, note, ct);
        }
        finally
        {
            _writeGate.Release();
        }

        await WriteMarkdownAsync(note, ct);
    }

    public async Task DeleteNoteAsync(string noteId, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);

            foreach (var sql in new[]
                     {
                         "DELETE FROM links WHERE source_id = $id OR target_id = $id;",
                         "DELETE FROM note_tags WHERE note_id = $id;",
                         "DELETE FROM note_embeddings WHERE note_id = $id;",
                         "UPDATE highlights SET note_id = NULL WHERE note_id = $id;",
                         "DELETE FROM notes WHERE id = $id;",
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", noteId);
                await command.ExecuteNonQueryAsync(ct);
            }

            if (_ftsAvailable)
            {
                await using var fts = connection.CreateCommand();
                fts.CommandText = "DELETE FROM notes_fts WHERE note_id = $id;";
                fts.Parameters.AddWithValue("$id", noteId);
                await fts.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        var path = MarkdownPath(noteId);
        if (File.Exists(path))
            File.Delete(path);
    }

    // ------------------------------------------------------------------ tags

    public async Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM tags ORDER BY name;";

        var tags = new List<Tag>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tags.Add(new Tag { Id = reader.GetString(0), Name = reader.GetString(1) });

        return tags;
    }

    public async Task<Tag> EnsureTagAsync(string name, CancellationToken ct = default)
    {
        var normalized = Tag.Normalize(name);

        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id FROM tags WHERE name = $name;";
                select.Parameters.AddWithValue("$name", normalized);
                if (await select.ExecuteScalarAsync(ct) is string existing)
                    return new Tag { Id = existing, Name = normalized };
            }

            var tag = Tag.Create(normalized);
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO tags (id, name) VALUES ($id, $name) ON CONFLICT(name) DO NOTHING;";
            insert.Parameters.AddWithValue("$id", tag.Id);
            insert.Parameters.AddWithValue("$name", tag.Name);
            await insert.ExecuteNonQueryAsync(ct);

            await using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT id FROM tags WHERE name = $name;";
            verify.Parameters.AddWithValue("$name", tag.Name);
            var id = (string?)await verify.ExecuteScalarAsync(ct) ?? tag.Id;

            return new Tag { Id = id, Name = tag.Name };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetNoteTagsAsync(string noteId, IEnumerable<string> tagIds, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM note_tags WHERE note_id = $id;";
                delete.Parameters.AddWithValue("$id", noteId);
                await delete.ExecuteNonQueryAsync(ct);
            }

            foreach (var tagId in tagIds.Distinct(StringComparer.Ordinal))
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT OR IGNORE INTO note_tags (note_id, tag_id) VALUES ($note, $tag);";
                insert.Parameters.AddWithValue("$note", noteId);
                insert.Parameters.AddWithValue("$tag", tagId);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        // Refresh the markdown mirror so tags show up in the plain files too.
        var note = await GetNoteAsync(noteId, ct);
        if (note is not null)
            await WriteMarkdownAsync(note, ct);
    }

    // ----------------------------------------------------------------- links

    public async Task<IReadOnlyList<Link>> ListLinksAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source_id, target_id, kind, rationale, created_at FROM links;";

        var links = new List<Link>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            links.Add(new Link
            {
                Id = reader.GetString(0),
                SourceId = reader.GetString(1),
                TargetId = reader.GetString(2),
                Kind = (EdgeKind)reader.GetInt32(3),
                Rationale = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = FromDb(reader.GetString(5)),
            });
        }

        return links;
    }

    public async Task AddLinkAsync(Link link, CancellationToken ct = default)
    {
        if (link.SourceId == link.TargetId)
            throw new InvalidOperationException("A note cannot link to itself.");

        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO links (id, source_id, target_id, kind, rationale, created_at)
                VALUES ($id, $source, $target, $kind, $rationale, $created);
                """;
            command.Parameters.AddWithValue("$id", link.Id);
            command.Parameters.AddWithValue("$source", link.SourceId);
            command.Parameters.AddWithValue("$target", link.TargetId);
            command.Parameters.AddWithValue("$kind", (int)link.Kind);
            command.Parameters.AddWithValue("$rationale", (object?)link.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", ToDb(link.CreatedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteLinkAsync(string linkId, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM links WHERE id = $id;";
            command.Parameters.AddWithValue("$id", linkId);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ReplaceWikiLinksAsync(
        string sourceNoteId,
        IEnumerable<string> targetNoteIds,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM links WHERE source_id = $source AND kind = $kind;";
                delete.Parameters.AddWithValue("$source", sourceNoteId);
                delete.Parameters.AddWithValue("$kind", (int)EdgeKind.Wiki);
                await delete.ExecuteNonQueryAsync(ct);
            }

            foreach (var target in targetNoteIds.Where(t => t != sourceNoteId).Distinct(StringComparer.Ordinal))
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT OR IGNORE INTO links (id, source_id, target_id, kind, rationale, created_at)
                    VALUES ($id, $source, $target, $kind, NULL, $created);
                    """;
                insert.Parameters.AddWithValue("$id", Ids.NewId());
                insert.Parameters.AddWithValue("$source", sourceNoteId);
                insert.Parameters.AddWithValue("$target", target);
                insert.Parameters.AddWithValue("$kind", (int)EdgeKind.Wiki);
                insert.Parameters.AddWithValue("$created", ToDb(DateTimeOffset.Now));
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ------------------------------------------------------------- documents

    public async Task<IReadOnlyList<PdfDocument>> ListDocumentsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DocumentColumns} FROM documents ORDER BY imported_at DESC;";

        var documents = new List<PdfDocument>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            documents.Add(ReadDocument(reader));

        return documents;
    }

    public async Task<PdfDocument?> GetDocumentAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {DocumentColumns} FROM documents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDocument(reader) : null;
    }

    public async Task SaveDocumentAsync(PdfDocument document, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO documents ({DocumentColumns})
                VALUES ($id, $path, $name, $title, $authors, $year, $doi, $pages, $imported,
                        $itemType, $abstract, $publication, $volume, $issue,
                        $pageRange, $issn, $url, $keywords, $language)
                ON CONFLICT(id) DO UPDATE SET
                    file_path = excluded.file_path,
                    file_name = excluded.file_name,
                    title = excluded.title,
                    authors = excluded.authors,
                    year = excluded.year,
                    doi = excluded.doi,
                    page_count = excluded.page_count,
                    item_type = excluded.item_type,
                    abstract = excluded.abstract,
                    publication = excluded.publication,
                    volume = excluded.volume,
                    issue = excluded.issue,
                    page_range = excluded.page_range,
                    issn = excluded.issn,
                    url = excluded.url,
                    keywords = excluded.keywords,
                    language = excluded.language;
                """;
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$path", document.FilePath);
            command.Parameters.AddWithValue("$name", document.FileName);
            command.Parameters.AddWithValue("$title", document.Title);
            command.Parameters.AddWithValue("$authors", document.Authors);
            command.Parameters.AddWithValue("$year", (object?)document.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("$doi", (object?)document.Doi ?? DBNull.Value);
            command.Parameters.AddWithValue("$pages", document.PageCount);
            command.Parameters.AddWithValue("$imported", ToDb(document.ImportedAt));
            command.Parameters.AddWithValue("$itemType", (object?)document.ItemType ?? DBNull.Value);
            command.Parameters.AddWithValue("$abstract", (object?)document.Abstract ?? DBNull.Value);
            command.Parameters.AddWithValue("$publication", (object?)document.Publication ?? DBNull.Value);
            command.Parameters.AddWithValue("$volume", (object?)document.Volume ?? DBNull.Value);
            command.Parameters.AddWithValue("$issue", (object?)document.Issue ?? DBNull.Value);
            command.Parameters.AddWithValue("$pageRange", (object?)document.PageRange ?? DBNull.Value);
            command.Parameters.AddWithValue("$issn", (object?)document.Issn ?? DBNull.Value);
            command.Parameters.AddWithValue("$url", (object?)document.Url ?? DBNull.Value);
            command.Parameters.AddWithValue("$keywords", (object?)document.Keywords ?? DBNull.Value);
            command.Parameters.AddWithValue("$language", (object?)document.Language ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);

            foreach (var sql in new[]
                     {
                         "DELETE FROM links WHERE source_id = $id OR target_id = $id;",
                         "DELETE FROM highlights WHERE document_id = $id;",
                         "DELETE FROM documents WHERE id = $id;",
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ------------------------------------------------------------ highlights

    public async Task<IReadOnlyList<Highlight>> ListHighlightsAsync(string documentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, document_id, page, text, note_id, created_at
            FROM highlights WHERE document_id = $doc ORDER BY page, created_at;
            """;
        command.Parameters.AddWithValue("$doc", documentId);

        var highlights = new List<Highlight>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            highlights.Add(new Highlight
            {
                Id = reader.GetString(0),
                DocumentId = reader.GetString(1),
                Page = reader.GetInt32(2),
                Text = reader.GetString(3),
                NoteId = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = FromDb(reader.GetString(5)),
            });
        }

        return highlights;
    }

    public async Task SaveHighlightAsync(Highlight highlight, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO highlights (id, document_id, page, text, note_id, created_at)
                VALUES ($id, $doc, $page, $text, $note, $created)
                ON CONFLICT(id) DO UPDATE SET note_id = excluded.note_id, text = excluded.text;
                """;
            command.Parameters.AddWithValue("$id", highlight.Id);
            command.Parameters.AddWithValue("$doc", highlight.DocumentId);
            command.Parameters.AddWithValue("$page", highlight.Page);
            command.Parameters.AddWithValue("$text", highlight.Text);
            command.Parameters.AddWithValue("$note", (object?)highlight.NoteId ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", ToDb(highlight.CreatedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ----------------------------------------------------------- suggestions

    public async Task<IReadOnlyList<Suggestion>> ListSuggestionsAsync(
        SuggestionStatus? status = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, title, payload_json, status, created_at, decided_at
            FROM suggestions
            WHERE $status IS NULL OR status = $status
            ORDER BY created_at DESC;
            """;
        command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : (int)status.Value);

        var suggestions = new List<Suggestion>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            suggestions.Add(new Suggestion
            {
                Id = reader.GetString(0),
                Kind = (SuggestionKind)reader.GetInt32(1),
                Title = reader.GetString(2),
                PayloadJson = reader.GetString(3),
                Status = (SuggestionStatus)reader.GetInt32(4),
                CreatedAt = FromDb(reader.GetString(5)),
                DecidedAt = reader.IsDBNull(6) ? null : FromDb(reader.GetString(6)),
            });
        }

        return suggestions;
    }

    public async Task SaveSuggestionAsync(Suggestion suggestion, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO suggestions (id, kind, title, payload_json, status, created_at, decided_at)
                VALUES ($id, $kind, $title, $payload, $status, $created, $decided)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    decided_at = excluded.decided_at;
                """;
            command.Parameters.AddWithValue("$id", suggestion.Id);
            command.Parameters.AddWithValue("$kind", (int)suggestion.Kind);
            command.Parameters.AddWithValue("$title", suggestion.Title);
            command.Parameters.AddWithValue("$payload", suggestion.PayloadJson);
            command.Parameters.AddWithValue("$status", (int)suggestion.Status);
            command.Parameters.AddWithValue("$created", ToDb(suggestion.CreatedAt));
            command.Parameters.AddWithValue("$decided", suggestion.DecidedAt is null ? DBNull.Value : ToDb(suggestion.DecidedAt.Value));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ------------------------------------------------------------------ chat

    public async Task<IReadOnlyList<ChatMessage>> ListChatMessagesAsync(
        int limit = 200,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, text, citations_json, created_at
            FROM chat_messages ORDER BY created_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new ChatMessage
            {
                Id = reader.GetString(0),
                Role = reader.GetString(1),
                Text = reader.GetString(2),
                CitationsJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = FromDb(reader.GetString(4)),
            });
        }

        messages.Reverse(); // newest-first query, chronological result
        return messages;
    }

    public async Task SaveChatMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO chat_messages (id, role, text, citations_json, created_at)
                VALUES ($id, $role, $text, $citations, $created);
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$text", message.Text);
            command.Parameters.AddWithValue("$citations", (object?)message.CitationsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", ToDb(message.CreatedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ----------------------------------------------------------------- audit

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, at, action, detail FROM audit ORDER BY at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new AuditEntry
            {
                Id = reader.GetString(0),
                At = FromDb(reader.GetString(1)),
                Action = reader.GetString(2),
                Detail = reader.GetString(3),
            });
        }

        return entries;
    }

    public async Task AppendAuditAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO audit (id, at, action, detail) VALUES ($id, $at, $action, $detail);";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$at", ToDb(entry.At));
            command.Parameters.AddWithValue("$action", entry.Action);
            command.Parameters.AddWithValue("$detail", entry.Detail);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---------------------------------------------------------------- search

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchHit>();

        await using var connection = await OpenAsync(ct);
        var hits = new List<SearchHit>();

        if (_ftsAvailable)
        {
            var expression = BuildFtsExpression(query);
            if (expression.Length > 0)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT note_id, title, snippet(notes_fts, 2, '[', ']', ' \u2026 ', 12)
                    FROM notes_fts
                    WHERE notes_fts MATCH $q
                    ORDER BY rank
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$q", expression);
                command.Parameters.AddWithValue("$limit", limit);

                try
                {
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        hits.Add(new SearchHit(reader.GetString(0), "note", reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                }
                catch (SqliteException)
                {
                    hits.Clear(); // malformed MATCH: fall through to LIKE
                }
            }
        }

        if (hits.Count == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, substr(body, 1, 160)
                FROM notes
                WHERE title LIKE $like OR body LIKE $like
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$like", $"%{query.Trim()}%");
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new SearchHit(reader.GetString(0), "note", reader.GetString(1), reader.GetString(2)));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, substr(authors, 1, 120) || ' \u00b7 ' || COALESCE(year, 'n.d.')
                FROM documents
                WHERE title LIKE $like OR authors LIKE $like
                ORDER BY imported_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$like", $"%{query.Trim()}%");
            command.Parameters.AddWithValue("$limit", limit);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                hits.Add(new SearchHit(reader.GetString(0), "document", reader.GetString(1), reader.GetString(2)));
        }

        return hits.Take(limit).ToList();
    }

    /// <summary>Turns free text into a safe FTS5 expression (every token quoted).</summary>
    private static string BuildFtsExpression(string query)
    {
        var tokens = query
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('*', '"', '\'', '(', ')', ',', '.', ';', ':'))
            .Where(t => t.Length > 0)
            .Take(12)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*")
            .ToList();

        return string.Join(" AND ", tokens);
    }

    // ------------------------------------------------------------ embeddings

    public async Task<IReadOnlyList<NoteEmbedding>> ListEmbeddingsAsync(string model, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, model, content_hash, vector
            FROM note_embeddings WHERE model = $model;
            """;
        command.Parameters.AddWithValue("$model", model);

        var embeddings = new List<NoteEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            embeddings.Add(new NoteEmbedding(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                FromBlob((byte[])reader.GetValue(3))));
        }

        return embeddings;
    }

    /// <summary>
    /// Replaces the stored vector of each note. A note has one vector, so a
    /// different model supersedes the old one instead of comparing mixed spaces.
    /// </summary>
    public async Task SaveEmbeddingsAsync(IEnumerable<NoteEmbedding> embeddings, CancellationToken ct = default)
    {
        var rows = embeddings.ToList();
        if (rows.Count == 0)
            return;

        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = connection.BeginTransaction();

            foreach (var row in rows)
            {
                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM note_embeddings WHERE note_id = $id;";
                    delete.Parameters.AddWithValue("$id", row.NoteId);
                    await delete.ExecuteNonQueryAsync(ct);
                }

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO note_embeddings (note_id, model, dim, content_hash, vector, updated_at)
                    VALUES ($id, $model, $dim, $hash, $vector, $at);
                    """;
                insert.Parameters.AddWithValue("$id", row.NoteId);
                insert.Parameters.AddWithValue("$model", row.Model);
                insert.Parameters.AddWithValue("$dim", row.Vector.Length);
                insert.Parameters.AddWithValue("$hash", row.ContentHash);
                insert.Parameters.AddWithValue("$vector", ToBlob(row.Vector));
                insert.Parameters.AddWithValue("$at", ToDb(DateTimeOffset.Now));
                await insert.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // -------------------------------------------------------------- helpers

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Note ReadNote(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Body = reader.GetString(2),
        CreatedAt = FromDb(reader.GetString(3)),
        UpdatedAt = FromDb(reader.GetString(4)),
        SourceDocumentId = reader.IsDBNull(5) ? null : reader.GetString(5),
        SourcePage = reader.IsDBNull(6) ? null : reader.GetInt32(6),
    };

    private static PdfDocument ReadDocument(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        FilePath = reader.GetString(1),
        FileName = reader.GetString(2),
        Title = reader.GetString(3),
        Authors = reader.GetString(4),
        Year = reader.IsDBNull(5) ? null : reader.GetInt32(5),
        Doi = reader.IsDBNull(6) ? null : reader.GetString(6),
        PageCount = reader.GetInt32(7),
        ImportedAt = FromDb(reader.GetString(8)),
        ItemType = Text(reader, 9),
        Abstract = Text(reader, 10),
        Publication = Text(reader, 11),
        Volume = Text(reader, 12),
        Issue = Text(reader, 13),
        PageRange = Text(reader, 14),
        Issn = Text(reader, 15),
        Url = Text(reader, 16),
        Keywords = Text(reader, 17),
        Language = Text(reader, 18),
    };

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task AttachTagsAsync(SqliteConnection connection, IReadOnlyCollection<Note> notes, CancellationToken ct)
    {
        if (notes.Count == 0)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT nt.note_id, t.id, t.name
            FROM note_tags nt
            JOIN tags t ON t.id = nt.tag_id
            ORDER BY t.name;
            """;

        var byId = notes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var noteId = reader.GetString(0);
            if (!byId.TryGetValue(noteId, out var note))
                continue;

            note.Tags.Add(new Tag { Id = reader.GetString(1), Name = reader.GetString(2) });
        }
    }

    private async Task UpsertFtsAsync(SqliteConnection connection, Note note, CancellationToken ct)
    {
        if (!_ftsAvailable)
            return;

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM notes_fts WHERE note_id = $id;";
            delete.Parameters.AddWithValue("$id", note.Id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO notes_fts (note_id, title, body) VALUES ($id, $title, $body);";
        insert.Parameters.AddWithValue("$id", note.Id);
        insert.Parameters.AddWithValue("$title", note.Title);
        insert.Parameters.AddWithValue("$body", note.Body);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private string MarkdownPath(string noteId) => Path.Combine(_notesDirectory, $"{noteId}.md");

    /// <summary>Writes the note to a plain markdown file (mirror, SQLite stays canonical).</summary>
    private async Task WriteMarkdownAsync(Note note, CancellationToken ct)
    {
        Directory.CreateDirectory(_notesDirectory);

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"id: {note.Id}");
        builder.AppendLine($"title: {note.Title.Replace("\n", " ")}");
        if (note.Tags.Count > 0)
            builder.AppendLine($"tags: {string.Join(", ", note.Tags.Select(t => t.Name))}");
        if (note.SourceDocumentId is not null)
            builder.AppendLine($"source: {note.SourceDocumentId}#page={note.SourcePage}");
        builder.AppendLine($"updated: {ToDb(note.UpdatedAt)}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(note.Body);

        await File.WriteAllTextAsync(MarkdownPath(note.Id), builder.ToString(), ct);
    }

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", Invariant);

    private static DateTimeOffset FromDb(string value) =>
        DateTimeOffset.Parse(value, Invariant, DateTimeStyles.RoundtripKind);
}
