using Kampf.Application.Abstractions;
using Kampf.Application.Embeddings;
using Kampf.Application.Models;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;

namespace Kampf.Application.Services;

/// <summary>Outcome of one suggestion run, shown in the status bar.</summary>
public sealed record SuggestionBatch(
    int LinkSuggestions,
    int TagSuggestions,
    int ConnectTheDots,
    int ConsideredNotes,
    int Clusters)
{
    public int Total => LinkSuggestions + TagSuggestions + ConnectTheDots;
}

/// <summary>
/// Produces proposals for the review queue: related-note links from embedding
/// similarity, and tags that nearby notes already agree on. It only ever writes
/// Pending rows — the approval gate in <see cref="SuggestionService"/> is what
/// touches the graph, so the domain rule stays intact.
/// </summary>
public sealed class SuggestionEngine
{
    private const int MaxLinksPerRun = 40;
    private const int MaxLinksPerNote = 2;
    private const int MaxTagsPerRun = 20;
    private const int MaxTagsPerNote = 2;

    /// <summary>Bridges between separate clusters proposed in one run.</summary>
    private const int MaxBridgesPerRun = 12;

    /// <summary>Isolated notes offered a way back into the graph in one run.</summary>
    private const int MaxOrphansPerRun = 12;

    /// <summary>Below this many notes every pair is compared; above it, candidates are blocked by shared words.</summary>
    private const int FullScanLimit = 1500;

    private const int BlockedCandidatesPerNote = 24;

    /// <summary>Sum of neighbour similarities a tag needs before it is worth proposing.</summary>
    private const float TagWeightFloor = 0.80f;

    private readonly IVaultRepository _repo;
    private readonly NoteEmbeddingIndex _index;

    public SuggestionEngine(IVaultRepository repo, IEmbeddingProvider embeddings)
    {
        _repo = repo;
        _index = new NoteEmbeddingIndex(repo, embeddings);
    }

    public async Task<SuggestionBatch> GenerateAsync(CancellationToken ct = default)
    {
        var allNotes = await _repo.ListNotesAsync(ct);

        var notes = new List<Note>(allNotes.Count);
        var shares = new List<IReadOnlyList<string>>(allNotes.Count);

        foreach (var note in allNotes)
        {
            var tokens = TextTokenizer.DistinctTokens(NoteEmbeddingIndex.TextFor(note));
            if (tokens.Count < 3)
                continue; // titles alone are too thin to connect meaningfully

            notes.Add(note);
            shares.Add(tokens);
        }

        if (notes.Count < 2)
            return new SuggestionBatch(0, 0, 0, notes.Count, notes.Count);

        var byId = await _index.EnsureAsync(
            notes.Select(n => (n.Id, NoteEmbeddingIndex.TextFor(n))).ToList(),
            ct);
        var vectors = notes.Select(n => byId[n.Id]).ToArray();

        var pairCandidates = CandidatePairs(shares);
        var floor = _index.Provider.SimilarityFloor;

        var pairs = new List<(int A, int B, float Similarity)>();
        foreach (var (a, b) in pairCandidates)
        {
            var similarity = VectorMath.Cosine(vectors[a], vectors[b]);
            if (similarity >= floor)
                pairs.Add((a, b, similarity));
        }

        var links = await _repo.ListLinksAsync(ct);
        var history = await _repo.ListSuggestionsAsync(null, ct);

        // A pair is settled once it is linked, already queued, or explicitly rejected.
        var settledPairs = new HashSet<(string, string)>();
        foreach (var link in links)
            settledPairs.Add(PairKey(link.SourceId, link.TargetId));

        var settledTags = new HashSet<(string, string)>();
        foreach (var suggestion in history)
        {
            switch (suggestion.Kind)
            {
                case SuggestionKind.LinkNotes:
                {
                    var payload = SuggestionJson.Deserialize<LinkNotesPayload>(suggestion.PayloadJson);
                    if (payload is not null)
                        settledPairs.Add(PairKey(payload.SourceNoteId, payload.TargetNoteId));
                    break;
                }

                case SuggestionKind.ApplyTag:
                {
                    var payload = SuggestionJson.Deserialize<ApplyTagPayload>(suggestion.PayloadJson);
                    if (payload is not null)
                        settledTags.Add(TagKey(payload.NoteId, payload.TagName));
                    break;
                }
            }
        }

        var linkCount = 0;
        var tagCount = 0;
        var dotCount = 0;
        var orphanCount = 0;

        // Phase 3: communities. Components come from accepted/written edges only —
        // a pending proposal is not an edge, so nothing is wired behind the gate.
        var components = GraphService.NoteComponents(notes, links);
        var componentSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in components.Values)
            componentSizes[root] = componentSizes.GetValueOrDefault(root) + 1;

        var bridged = new HashSet<string>(StringComparer.Ordinal);

        // Connect the dots first: a link between two established clusters is the
        // more interesting proposal, and it wins the pair over an ordinary link.
        foreach (var (a, b, similarity) in pairs.OrderByDescending(p => p.Similarity))
        {
            if (dotCount >= MaxBridgesPerRun)
                break;

            var idA = notes[a].Id;
            var idB = notes[b].Id;
            var rootA = components[idA];
            var rootB = components[idB];

            if (string.Equals(rootA, rootB, StringComparison.Ordinal))
                continue; // already one community

            if (componentSizes[rootA] < 2 || componentSizes[rootB] < 2)
                continue; // a lone note is an orphan, handled below

            if (bridged.Contains(idA) || bridged.Contains(idB))
                continue; // one bridge per note per run keeps the queue readable

            if (!settledPairs.Add(PairKey(idA, idB)))
                continue;

            bridged.Add(idA);
            bridged.Add(idB);

            var percent = (int)Math.Round(similarity * 100, MidpointRounding.AwayFromZero);
            var shared = SharedTerms(shares[a], shares[b]);
            var reason = $"connects two separate clusters ({componentSizes[rootA]} and " +
                         $"{componentSizes[rootB]} notes) \u00b7 {percent}% similar" +
                         (shared.Count == 0 ? string.Empty : $" \u00b7 shares \u201c{string.Join("\u201d, \u201c", shared)}\u201d");

            await _repo.SaveSuggestionAsync(
                Suggestion.Create(
                    SuggestionKind.ConnectDots,
                    $"Connect the dots: \u201c{notes[a].Title}\u201d \u2194 \u201c{notes[b].Title}\u201d",
                    SuggestionJson.Serialize(new LinkNotesPayload(idA, idB, reason))),
                ct);
            dotCount++;
        }

        var linksPerNote = new Dictionary<string, int>(StringComparer.Ordinal);
        var tagVotes = new Dictionary<int, Dictionary<string, (float Weight, int Notes)>>();

        foreach (var (a, b, similarity) in pairs.OrderByDescending(p => p.Similarity))
        {
            Vote(tagVotes, a, b, similarity, notes);
            Vote(tagVotes, b, a, similarity, notes);

            if (linkCount >= MaxLinksPerRun)
                continue;

            if (linksPerNote.GetValueOrDefault(notes[a].Id) >= MaxLinksPerNote ||
                linksPerNote.GetValueOrDefault(notes[b].Id) >= MaxLinksPerNote)
                continue;

            // Two established clusters are the connect-the-dots pass's business;
            // somewhere to start from, a singleton is linked normally.
            var clusterA = components[notes[a].Id];
            var clusterB = components[notes[b].Id];
            if (!string.Equals(clusterA, clusterB, StringComparison.Ordinal) &&
                componentSizes[clusterA] >= 2 &&
                componentSizes[clusterB] >= 2)
                continue;

            if (!settledPairs.Add(PairKey(notes[a].Id, notes[b].Id)))
                continue;

            var shared = SharedTerms(shares[a], shares[b]);
            var suggestion = Suggestion.Create(
                SuggestionKind.LinkNotes,
                $"Link \u201c{notes[a].Title}\u201d \u2194 \u201c{notes[b].Title}\u201d",
                SuggestionJson.Serialize(new LinkNotesPayload(
                    notes[a].Id,
                    notes[b].Id,
                    BuildReason(similarity, shared))));

            await _repo.SaveSuggestionAsync(suggestion, ct);
            linkCount++;
            linksPerNote[notes[a].Id] = linksPerNote.GetValueOrDefault(notes[a].Id) + 1;
            linksPerNote[notes[b].Id] = linksPerNote.GetValueOrDefault(notes[b].Id) + 1;
        }

        // An orphan has no written link and no highlight anchor, so it floats alone.
        // The ordinary pass above may have filled its neighbours' link budget; this
        // pass makes sure the isolated note itself is still offered a way in.
        for (var i = 0; i < notes.Count && orphanCount < MaxOrphansPerRun; i++)
        {
            var note = notes[i];
            if (componentSizes[components[note.Id]] != 1 || note.SourceDocumentId is not null)
                continue;

            var best = (Similarity: 0f, Other: -1);
            foreach (var (a, b, similarity) in pairs)
            {
                if (a != i && b != i)
                    continue;

                if (similarity > best.Similarity)
                    best = (similarity, a == i ? b : a);
            }

            if (best.Other < 0)
                continue;

            if (!settledPairs.Add(PairKey(note.Id, notes[best.Other].Id)))
                continue;

            var percent = (int)Math.Round(best.Similarity * 100, MidpointRounding.AwayFromZero);
            var shared = SharedTerms(shares[i], shares[best.Other]);
            var reason = $"this note has no connections \u00b7 {percent}% similar" +
                         (shared.Count == 0 ? string.Empty : $" \u00b7 shares \u201c{string.Join("\u201d, \u201c", shared)}\u201d");

            await _repo.SaveSuggestionAsync(
                Suggestion.Create(
                    SuggestionKind.ConnectDots,
                    $"Connect unconnected note \u201c{note.Title}\u201d to \u201c{notes[best.Other].Title}\u201d",
                    SuggestionJson.Serialize(new LinkNotesPayload(note.Id, notes[best.Other].Id, reason))),
                ct);
            orphanCount++;
        }

        foreach (var (index, votes) in tagVotes.OrderByDescending(v => v.Value.Values.Sum(x => x.Weight)))
        {
            if (tagCount >= MaxTagsPerRun)
                break;

            var note = notes[index];
            var existing = note.Tags
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var perNote = 0;

            foreach (var (tag, vote) in votes.OrderByDescending(v => v.Value.Weight))
            {
                if (tagCount >= MaxTagsPerRun || perNote >= MaxTagsPerNote)
                    break;

                if (vote.Weight < TagWeightFloor)
                    break;

                if (existing.Contains(tag) || !settledTags.Add(TagKey(note.Id, tag)))
                    continue;

                var suggestion = Suggestion.Create(
                    SuggestionKind.ApplyTag,
                    $"Tag \u201c{tag}\u201d on \u201c{note.Title}\u201d \u2014 {vote.Notes} related note(s) use it",
                    SuggestionJson.Serialize(new ApplyTagPayload(note.Id, tag)));

                await _repo.SaveSuggestionAsync(suggestion, ct);
                tagCount++;
                perNote++;
            }
        }

        var dots = dotCount + orphanCount;

        if (linkCount + tagCount + dots > 0)
        {
            await _repo.AppendAuditAsync(
                AuditEntry.Create(
                    "suggestions.generated",
                    $"{linkCount} link \u00b7 {tagCount} tag \u00b7 {dotCount} bridge \u00b7 " +
                    $"{orphanCount} orphan from {notes.Count} notes in {componentSizes.Count} cluster(s)"),
                ct);
        }

        return new SuggestionBatch(linkCount, tagCount, dots, notes.Count, componentSizes.Count);
    }

    // --------------------------------------------------------------- internals

    private static List<(int A, int B)> CandidatePairs(IReadOnlyList<IReadOnlyList<string>> shares)
    {
        var candidates = new List<(int A, int B)>();
        var count = shares.Count;

        if (count <= FullScanLimit)
        {
            for (var i = 0; i < count; i++)
                for (var j = i + 1; j < count; j++)
                    candidates.Add((i, j));

            return candidates;
        }

        // Large vault: block pairs that share words, then rank by overlap.
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            foreach (var token in shares[i])
            {
                if (!postings.TryGetValue(token, out var list))
                    postings[token] = list = new List<int>();
                list.Add(i);
            }
        }

        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < count; i++)
        {
            var overlap = new Dictionary<int, int>();
            foreach (var token in shares[i])
            {
                if (!postings.TryGetValue(token, out var list) || list.Count > 200)
                    continue; // very common word: not selective

                foreach (var j in list)
                {
                    if (j != i)
                        overlap[j] = overlap.GetValueOrDefault(j) + 1;
                }
            }

            foreach (var j in overlap.OrderByDescending(o => o.Value).Take(BlockedCandidatesPerNote).Select(o => o.Key))
            {
                var pair = i < j ? (i, j) : (j, i);
                if (seen.Add(pair))
                    candidates.Add(pair);
            }
        }

        return candidates;
    }

    private static void Vote(
        Dictionary<int, Dictionary<string, (float Weight, int Notes)>> votes,
        int target,
        int neighbour,
        float similarity,
        IReadOnlyList<Note> notes)
    {
        if (notes[neighbour].Tags.Count == 0)
            return;

        if (!votes.TryGetValue(target, out var byTag))
            votes[target] = byTag = new Dictionary<string, (float, int)>(StringComparer.Ordinal);

        foreach (var tag in notes[neighbour].Tags)
        {
            var current = byTag.GetValueOrDefault(tag.Name);
            byTag[tag.Name] = (current.Weight + similarity, current.Notes + 1);
        }
    }

    private static List<string> SharedTerms(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var other = new HashSet<string>(b, StringComparer.Ordinal);
        var shared = new List<string>();
        foreach (var token in a)
        {
            if (other.Contains(token))
                shared.Add(token);
            if (shared.Count == 3)
                break;
        }

        return shared;
    }

    private static string BuildReason(float similarity, IReadOnlyList<string> shared)
    {
        var percent = (int)Math.Round(similarity * 100, MidpointRounding.AwayFromZero);
        return shared.Count == 0
            ? $"{percent}% similar"
            : $"{percent}% similar \u00b7 shares \u201c{string.Join("\u201d, \u201c", shared)}\u201d";
    }

    private static (string, string) PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static (string, string) TagKey(string noteId, string tag) =>
        (noteId, Tag.Normalize(tag));
}
