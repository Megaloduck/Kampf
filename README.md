# Kampf

A private, local-first research encyclopedia for desktop: markdown notes, PDF
highlight-to-note capture, an Obsidian-style graph, full-text search, and an
approval-gated review queue. No cloud, no accounts, no silent AI edits.

This repository implements the **Phase 1 MVP** from `Plan_Kampf.pdf`.

## What works today

- **Notes** — markdown notes with `[[wikilinks]]`. Linking is resolved by title;
  unresolved targets are *not* auto-created, they become review-queue proposals.
- **PDF import** — PDFs are copied into the vault and their text is extracted per
  page (PdfPig). Citation metadata (title, authors, year, DOI) is captured, plus
  the detail a PDF or its XMP packet carries: item type, abstract, publication,
  volume, issue, page range, ISSN, URL, keywords and language, with DOI / ISSN /
  page-range fallbacks read from the opening pages.
- **Reader details** — the reader header lists every captured field for the open
  paper (missing ones are omitted, not shown blank) with the abstract underneath.
  Older vaults gain the new columns automatically when they are opened.
- **Highlight → note** — select text on a page in the Reader, click
  *Capture selection → note*; the note keeps the document and page anchor, and
  appears in the graph connected to its paper.
- **Force-directed graph** — custom-drawn canvas: node size = connections,
  colour = primary tag, edge colour = why the edge exists (written link vs.
  accepted suggestion vs. provenance). Click to inspect, double-click to open,
  drag to arrange, scroll to zoom. Filter by source type or tag.
- **Idea-Craft chat** — a fourth workspace tab: ask "what connects X and Y?" and
  get an answer built only from retrieved notes, with clickable citations
  (note title, source paper, page). With a local model configured the answer is
  prose that must cite `[n]`; with none, the answer *is* the cited passages — it
  never synthesises. A model answer with no valid citation is labelled
  unverified, and an unreachable model falls back to the passages with a
  warning. Every turn is stored in `chat_messages` and restored on restart.
- **Hybrid search** — SQLite FTS5 over note bodies (with a `LIKE` fallback) plus
  `tag:name` filtering, then a semantic pass: notes whose cached vector is close
  to the query vector are appended even when they never use the query's words.
  Exact-term hits always stay first; the header and status line report how many
  results came "by meaning", and a stopped local model degrades to text-only
  with a warning instead of failing the search.
- **Review queue** — every proposal shows its reason and does nothing until you
  accept or reject it. Decisions are written to an activity log.
- **Find connections** — a button in the review panel runs the Phase 2 suggestion
  producers: note pairs that are close in embedding space become *Link* proposals
  (with the words they share and a percentage), and tags that neighbouring notes
  agree on become *Tag* proposals. Nothing is written to the graph until you
  accept, a rejected pair is never proposed again, and note vectors are cached
  by content hash so only edited notes are re-embedded.
- **Dark / light theme** — a header button switches the whole workspace; the
graph canvas and markdown preview follow the active theme.
- **Deletion** — notes and imported papers can be deleted (both ask for a second
click). Deleting a paper removes its stored PDF, highlights and edges; it is
refused while notes still carry page anchors into it.
- **Undo / redo** — icon buttons in the window header, next to the theme switch,
  backed by Avalonia's text history (`Ctrl+Z` / `Ctrl+Y` also work). They stay
  disabled until a note is loaded and its history has something to undo.
  History starts fresh per note, so undo can never pull text from another note.
- **Resizable panels** — the note list and the review queue are separated from
  the workspace by draggable splitters (hover highlights the grip, minimum
  widths keep both readable). Each has its own header button to hide it and
  bring it back with the width you last dragged it to.
- **Drag and drop** — drop one or more PDFs anywhere on the workspace to import
  them; the cursor shows a copy badge only when the payload contains a `.pdf`.
- **Markdown files on disk** — every note is mirrored to `vault/notes/{id}.md`
  with YAML front matter; SQLite is the index, markdown is your data. The mirror
  is a toggle in Settings and switches itself off when the vault is encrypted.
- **Encrypted vault** — turn encryption on in Settings and the database (notes,
  tags, links, highlights, embeddings, chat history) is rewritten under
  SQLCipher with a key derived from your passphrase. On the next launch the app
  asks for the passphrase before the shell exists; a wrong one is rejected
  without opening the database.
- **Settings** — the gear button in the header opens a settings view: theme,
  vault encryption (enable / change passphrase / turn off), markdown mirror
  toggle, the active embedding and chat backends, and where the vault lives.

## Architecture

```
Kampf.Domain          entities, value objects, invariants (no dependencies)
Kampf.Application     services + ports: notes, import, graph, search, suggestions,
                     embeddings cache, RAG chat
Kampf.Infrastructure  SQLite/FTS5 repository, SQLCipher encryption, markdown
                     mirror, PdfPig extraction, embeddings (hashing or Ollama)
Kampf                 Avalonia UI: view models, graph canvas, markdown preview
Kampf.Desktop         desktop host
tests/*               domain rules, application services, SQLite + PDF round trips
```

The Application layer depends only on the Domain through ports
(`IVaultRepository`, `IVaultFileStore`, `IPdfExtractor`), so it can be tested with
in-memory fakes and later reused by other hosts.

## Running

```bash
dotnet run --project Kampf.Desktop
dotnet test                      # 76 tests across three projects
```

Requires the .NET 9 SDK. The desktop app targets Windows/macOS/Linux; the Android
and iOS heads are still the template shell and are not part of Phase 1.

### Vault location

Default: `%LOCALAPPDATA%\Kampf` (Windows), `~/.local/share/Kampf` elsewhere.

```
Kampf/
  kampf.db          SQLite index (notes, links, tags, documents, suggestions, audit)
  vault.lock        present only when encrypted: KDF, salt, verifier (never the key)
  notes/            one markdown file per note, YAML front matter
  assets/           imported PDFs
  backups/          kampf-<timestamp>.db taken before every encryption change
```

Set `KAMPF_VAULT` to use a different folder (portable vault or a second vault).

## The one rule that matters

Every edge in the graph is traceable:

| Edge | Origin |
| --- | --- |
| `Wiki` | a `[[link]]` you wrote in a note |
| `Manual` | note ↔ source PDF provenance from a highlight you captured |
| `AcceptedSuggestion` | a suggestion you explicitly accepted |

Nothing writes into the graph on its own. Suggestions are produced on demand and
land in the queue as `Pending`; only an accept (or edit-then-accept) writes an
edge.

## Suggestions and embeddings

`SuggestionEngine` (Application) proposes links and tags; `SuggestionService` is
the gate that applies them. Two embedding backends implement the same port:

| Backend | When | Notes |
| --- | --- | --- |
| `kampf-hash-512` | default | Dependency-free signed feature hashing over words and word pairs. Lexical overlap, works offline, no model to install. |
| `ollama:<model>` | `KAMPF_OLLAMA_URL` + `KAMPF_EMBEDDING_MODEL` are set | Semantic vectors from a local Ollama server, e.g. `http://localhost:11434` with `nomic-embed-text`. |

The chat model is separate: set `KAMPF_OLLAMA_URL` + `KAMPF_CHAT_MODEL` (e.g.
`llama3.2`) and Idea-Craft answers with prose that cites the numbered sources it
was given. Leave it unset and the same retrieval runs, but the answer is the
passages themselves. Either way nothing is sent to a hosted API.

Each backend publishes its own similarity floor, because lexical and semantic
cosines do not live on the same scale. Vectors are stored in `note_embeddings`
tagged with the model: switching models re-embeds instead of comparing mixed
spaces, and notes whose text hash is unchanged are reused.

Candidate pairs are compared exhaustively up to 1,500 notes; above that they are
blocked by shared significant words so a large vault stays responsive. A run is
capped (40 links, 20 tags), and every harvested pair is remembered — accepted,
pending or rejected — so the queue never nags about the same thing twice.

The same cache powers search: `SearchService` embeds the query and compares it
with the stored note vectors, so a note that says "modularity" is found by a
query about "community structure". Imported papers are still text-search only
(they have citation metadata, not note bodies), and semantic hits are capped at
10 per query.

## Encryption at rest

Settings → *Vault & encryption* → **Encrypt vault** rewrites `kampf.db` through
SQLCipher's `sqlcipher_export` and drops a `vault.lock` beside it. The key is
PBKDF2-HMAC-SHA256 (32 bytes, 16-byte salt, 600,000 iterations) over your
passphrase, applied per connection as `PRAGMA key`. Nothing derived from the
passphrase is ever stored: `vault.lock` holds only the KDF parameters, the salt
and an HMAC verifier, which is enough to reject a wrong passphrase before the
database is opened.

The rewrite is deliberately careful, because a half-encrypted vault is data
loss: the current database is copied to `backups/kampf-<timestamp>.db` first,
the exported file's row count must match the source, and only then is it moved
into place. Turning encryption off reverses it the same way. Markdown mirrors
are deleted on the way in (they would expose note bodies as plaintext) and
rebuilt from the database on the way out.

What encryption does **not** cover: PDFs in `assets/` stay readable files, and a
backup taken while the vault was plaintext stays plaintext. There is no recovery
path for a forgotten passphrase — that is the point of it, but it means the
passphrase is the only way back in.

Tests cover the round trip: plaintext bytes are gone from the file after
encryption, wrong and missing passphrases are rejected, decryption restores a
readable vault with its mirrors, and changing the passphrase retires the old one.

## Deliberately deferred

- Rich in-place markdown editing (AvaloniaEdit) — Phase 1 uses a plain editor plus
  a rendered preview.
- Visual PDF page rendering (WebView + PDF.js) — Phase 1 shows extracted page text
  with selectable ranges for highlight-to-note.
- LLM summaries, cross-paper synthesis, and links proposed *by the chatbot* —
  chat currently cites notes, it does not write suggestions into the queue.
- Answers are grounded in **notes**, not raw paper text: a question about a
  paragraph you never highlighted will not find it.
- Streaming chat output and multi-turn memory: each question is answered
  independently (the transcript is history, not context).
- Vector search over *documents*: only notes are embedded, so paper text is
  still found by FTS5 alone.
- Sync and publishing — Phase 4.

## Icons

UI icons use **Material Symbols Rounded**, bundled as a variable font at
`Kampf/Assets/Fonts/MaterialSymbolsRounded.ttf` (Apache-2.0, © Google).
`Kampf/Controls/Symbols.cs` is the single glyph map: each constant is the icon's
upstream name and codepoint, so adding an icon is one line plus the name.
Codepoints are looked up in the checked-in `MaterialSymbolsRounded.codepoints`
next to the font. Glyphs are rendered with the `TextBlock.icon` style, sized per
context and paired with a tooltip whenever the button is icon-only.
