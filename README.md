# Kampf

A private, local-first research encyclopedia for desktop: markdown notes, PDF
highlight-to-note capture, an Obsidian-style graph, full-text search, and an
approval-gated review queue. No cloud, no accounts, no silent AI edits.

This repository implements the **Phase 1 MVP** from `Plan_Kampf.pdf`.

## What works today

- **Notes** — markdown notes with `[[wikilinks]]`. Linking is resolved by title;
  unresolved targets are *not* auto-created, they become review-queue proposals.
- **PDF import** — PDFs are copied into the vault and their text is extracted per
  page (PdfPig). Citation metadata (title, author, year, DOI when present) is captured.
- **Highlight → note** — select text on a page in the Reader, click
  *Capture selection → note*; the note keeps the document and page anchor, and
  appears in the graph connected to its paper.
- **Force-directed graph** — custom-drawn canvas: node size = connections,
  colour = primary tag, edge colour = why the edge exists (written link vs.
  accepted suggestion vs. provenance). Click to inspect, double-click to open,
  drag to arrange, scroll to zoom. Filter by source type or tag.
- **Full-text search** — SQLite FTS5 over note bodies (with a `LIKE` fallback),
  plus `tag:name` filtering.
- **Review queue** — every proposal shows its reason and does nothing until you
  accept or reject it. Decisions are written to an activity log.
- **Dark / light theme** — a header button switches the whole workspace; the
graph canvas and markdown preview follow the active theme.
- **Deletion** — notes and imported papers can be deleted (both ask for a second
click). Deleting a paper removes its stored PDF, highlights and edges; it is
refused while notes still carry page anchors into it.
- **Markdown files on disk** — every note is mirrored to `vault/notes/{id}.md`
  with YAML front matter; SQLite is the index, markdown is your data.

## Architecture

```
Kampf.Domain          entities, value objects, invariants (no dependencies)
Kampf.Application     services + ports: notes, import, graph, search, suggestions
Kampf.Infrastructure  SQLite/FTS5 repository, markdown mirror, PdfPig extraction
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
dotnet test                      # 39 tests across three projects
```

Requires the .NET 9 SDK. The desktop app targets Windows/macOS/Linux; the Android
and iOS heads are still the template shell and are not part of Phase 1.

### Vault location

Default: `%LOCALAPPDATA%\Kampf` (Windows), `~/.local/share/Kampf` elsewhere.

```
Kampf/
  kampf.db          SQLite index (notes, links, tags, documents, suggestions, audit)
  notes/            one markdown file per note, YAML front matter
  assets/           imported PDFs
```

Set `KAMPF_VAULT` to use a different folder (portable vault or a second vault).

## The one rule that matters

Every edge in the graph is traceable:

| Edge | Origin |
| --- | --- |
| `Wiki` | a `[[link]]` you wrote in a note |
| `Manual` | note ↔ source PDF provenance from a highlight you captured |
| `AcceptedSuggestion` | a suggestion you explicitly accepted |

Nothing writes into the graph on its own. AI-assisted suggestion generation is
Phase 2; the queue, payloads and accept/reject paths already exist, so Phase 2
only needs to produce suggestions.

## Deliberately deferred

- Rich in-place markdown editing (AvaloniaEdit) — Phase 1 uses a plain editor plus
  a rendered preview.
- Visual PDF page rendering (WebView + PDF.js) — Phase 1 shows extracted page text
  with selectable ranges for highlight-to-note.
- Vector search, embeddings, AI drafting, sync, publishing — Phase 2+.

## Icons

UI icons use **Material Symbols Rounded**, bundled as a variable font at
`Kampf/Assets/Fonts/MaterialSymbolsRounded.ttf` (Apache-2.0, © Google).
`Kampf/Controls/Symbols.cs` is the single glyph map: each constant is the icon's
upstream name and codepoint, so adding an icon is one line plus the name.
Codepoints are looked up in the checked-in `MaterialSymbolsRounded.codepoints`
next to the font. Glyphs are rendered with the `TextBlock.icon` style, sized per
context and paired with a tooltip whenever the button is icon-only.
