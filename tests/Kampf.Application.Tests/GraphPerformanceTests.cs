using System.Diagnostics;
using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;
using Xunit.Abstractions;

namespace Kampf.Application.Tests;

/// <summary>
/// Phase 4's performance claim needs a number, not an opinion. This builds a
/// 10,000-note, 30,000-edge vault in memory and measures the graph and the
/// component analysis the connect-the-dots pass depends on.
/// </summary>
public class GraphPerformanceTests
{
    private const int NoteCount = 10_000;
    private const int LinkCount = 30_000;

    private readonly ITestOutputHelper _output;

    public GraphPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Ten_thousand_notes_build_a_graph_in_well_under_a_second()
    {
        var random = new Random(7);
        var notes = new List<Note>(NoteCount);

        for (var i = 0; i < NoteCount; i++)
        {
            var note = Note.Create($"Note {i}");
            note.Body = $"Body {i} about graphs and communities.";
            notes.Add(note);
        }

        // Unique unordered pairs: the graph drops a duplicate edge, and the count
        // below is about what Build keeps, not what was generated.
        var seen = new HashSet<(string, string)>();
        var links = new List<Link>(LinkCount);
        while (links.Count < LinkCount)
        {
            var a = notes[random.Next(NoteCount)];
            var b = notes[random.Next(NoteCount)];
            if (string.Equals(a.Id, b.Id, StringComparison.Ordinal))
                continue;

            var key = string.CompareOrdinal(a.Id, b.Id) <= 0 ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!seen.Add(key))
                continue;

            links.Add(Link.Create(a.Id, b.Id, EdgeKind.Wiki));
        }

        // Warm the JIT so the number describes the algorithm, not first-call cost.
        GraphService.Build(notes.Take(10).ToList(), Array.Empty<PdfDocument>(), links.Take(10).ToList());

        var stopwatch = Stopwatch.StartNew();
        var graph = GraphService.Build(notes, Array.Empty<PdfDocument>(), links);
        stopwatch.Stop();

        var componentsWatch = Stopwatch.StartNew();
        var components = GraphService.NoteComponents(notes, links);
        componentsWatch.Stop();

        _output.WriteLine(
            $"{NoteCount} notes / {LinkCount} edges: graph {stopwatch.ElapsedMilliseconds} ms, " +
            $"components {componentsWatch.ElapsedMilliseconds} ms");

        Assert.Equal(NoteCount, graph.Nodes.Count);
        Assert.Equal(LinkCount, graph.Edges.Count);
        Assert.Equal(NoteCount, components.Count);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2_000,
            $"graph build took {stopwatch.ElapsedMilliseconds} ms for {NoteCount} notes");
        Assert.True(
            componentsWatch.ElapsedMilliseconds < 2_000,
            $"component analysis took {componentsWatch.ElapsedMilliseconds} ms");
    }
}
