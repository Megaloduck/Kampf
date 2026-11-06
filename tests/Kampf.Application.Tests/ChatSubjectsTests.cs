using Kampf.Application.Services;
using Xunit;

namespace Kampf.Application.Tests;

public class ChatSubjectsTests
{
    [Fact]
    public void A_linking_question_yields_its_two_subjects()
    {
        var subjects = ChatSubjects.Extract("What connects modularity and homophily?");

        Assert.Equal(new[] { "modularity", "homophily" }, subjects);
    }

    [Fact]
    public void Articles_and_punctuation_are_stripped()
    {
        var subjects = ChatSubjects.Extract("What is the relationship between the graph and the vault?");

        Assert.Equal(new[] { "graph", "vault" }, subjects);
    }

    [Fact]
    public void A_versus_question_works_too()
    {
        var subjects = ChatSubjects.Extract("modularity vs homophily");

        Assert.Equal(new[] { "modularity", "homophily" }, subjects);
    }

    [Fact]
    public void Quoted_subjects_win_over_the_wording()
    {
        var subjects = ChatSubjects.Extract("How do \u201ccommunity detection\u201d and \u201chomophily\u201d relate?");

        Assert.Equal(new[] { "community detection", "homophily" }, subjects);
    }

    [Fact]
    public void A_plain_question_has_no_subjects()
    {
        Assert.Empty(ChatSubjects.Extract("Summarise my notes on modularity."));
    }
}
