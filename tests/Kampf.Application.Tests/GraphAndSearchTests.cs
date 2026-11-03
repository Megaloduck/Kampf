using Kampf.Application.Services;
using Kampf.Domain.Entities;
using Kampf.Domain.ValueObjects;
using Xunit;

namespace Kampf.Application.Tests;

public class GraphAndSearchTests
{
    [Fact]
    public void Graph_nodes_carry_degree_and_documents_are_distinct()
    {
        var a = Note.Create("A");
        var b = Note.Create("B");
        var doc = PdfDocument.Create("/x/paper.pdf", "Paper", 3);

        var links = new List<Link>
        {
            Link.Create(a.Id, b.Id, EdgeKind.Wiki),
            Link.Create(b.Id, doc.Id, EdgeKind.Manual),
            Link.Create("ghost", a.Id, EdgeKind.Manual), // dangling: must be ignored
        };

        var graph = GraphService.Build(new[] { a, b }, new[] { doc }, links);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal(2, graph.Nodes.Single(n => n.Id == b.Id).Degree);
        Assert.Equal(NodeKind.Document, graph.Nodes.Single(n => n.Id == doc.Id).Kind);
    }

    [Fact]
    public void Graph_deduplicates_repeated_edges()
    {
        var a = Note.Create("A");
        var b = Note.Create("B");

        var links = new List<Link>
        {
            Link.Create(a.Id, b.Id, EdgeKind.Wiki),
            Link.Create(a.Id, b.Id, EdgeKind.Manual),
        };

        var graph = GraphService.Build(new[] { a, b }, Array.Empty<PdfDocument>(), links);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public async Task Tag_filter_narrows_results()
    {
        var repo = new FakeVaultRepository();
        var notes = new NoteService(repo);
        var tagged = await notes.CreateAsync("Network methods", "modularity and communities");
        await notes.CreateAsync("Other methods", "modularity elsewhere");
        await notes.SetTagsAsync(tagged, new[] { "networks" });

        var search = new SearchService(repo);

        var filtered = await search.SearchAsync("modularity tag:networks");

        Assert.Single(filtered.Hits);
        Assert.Equal(tagged.Id, filtered.Hits[0].Id);
    }

    [Fact]
    public void Query_parser_extracts_tag_and_text()
    {
        var (text, tag) = SearchService.ParseQuery("community  detection tag:#graphs");

        Assert.Equal("community detection", text);
        Assert.Equal("graphs", tag);
    }
}
