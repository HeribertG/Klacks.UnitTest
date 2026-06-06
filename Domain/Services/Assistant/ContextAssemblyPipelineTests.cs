// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContextAssemblyPipeline, focused on the Klacks ontology world-model
/// block injection (S1 of the autonomy roadmap).
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ContextAssemblyPipelineTests
{
    private const string IdentityText = "You are Klacksy, the Klacks assistant.";
    private const string OntologyText = "=== KLACKS WORLD MODEL ===\n- Client\n  * Client --hasMany--> Contract\n=== END WORLD MODEL ===";
    private const string MemoryText = "[MEMORIES]\n- user prefers Bern group.";
    private const int ExpectedOntologyTokenBudget = 1500;

    private const string SchedulingMarker = "[SCHEDULING CONTEXT]";

    private IIdentityContextProvider _identity = null!;
    private IKlacksOntologyService _ontology = null!;
    private IMemoryRetrievalService _memory = null!;
    private ISentimentAnalyzer _sentiment = null!;
    private ContextAssemblyPipeline _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _identity = Substitute.For<IIdentityContextProvider>();
        _ontology = Substitute.For<IKlacksOntologyService>();
        _memory = Substitute.For<IMemoryRetrievalService>();
        _sentiment = Substitute.For<ISentimentAnalyzer>();

        _identity.GetIdentityPromptAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdentityText);
        _ontology.RenderWorldModelBlock(Arg.Any<int>()).Returns(OntologyText);
        _memory.RetrieveRelevantMemoriesAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MemoryText);
        _sentiment.AnalyzeSentimentAsync(Arg.Any<string>())
            .Returns(new SentimentResult(SentimentMood.Neutral, 0f));

        _sut = new ContextAssemblyPipeline(
            _identity, _ontology, _memory, _sentiment, new RuleContextProvider(),
            NullLogger<ContextAssemblyPipeline>.Instance);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_IncludesWorldModelBlock()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result, Does.Contain(OntologyText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_PlacesOntologyBetweenIdentityAndMemory()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        var identityIdx = result.IndexOf(IdentityText, StringComparison.Ordinal);
        var ontologyIdx = result.IndexOf(OntologyText, StringComparison.Ordinal);
        var memoryIdx = result.IndexOf(MemoryText, StringComparison.Ordinal);

        Assert.That(identityIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(ontologyIdx, Is.GreaterThan(identityIdx));
        Assert.That(memoryIdx, Is.GreaterThan(ontologyIdx));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_CallsOntologyServiceWithConfiguredTokenBudget()
    {
        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        _ontology.Received(1).RenderWorldModelBlock(ExpectedOntologyTokenBudget);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_SkipsBlock_WhenOntologyEmpty()
    {
        _ontology.RenderWorldModelBlock(Arg.Any<int>()).Returns(string.Empty);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result, Does.Not.Contain("WORLD MODEL"));
        Assert.That(result, Does.Contain(IdentityText));
        Assert.That(result, Does.Contain(MemoryText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_SkipsSentimentAndMemory_ForShortUtterance()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "ja");

        Assert.That(result, Does.Contain(IdentityText));
        Assert.That(result, Does.Contain(OntologyText));
        Assert.That(result, Does.Not.Contain(MemoryText));
        await _sentiment.DidNotReceive().AnalyzeSentimentAsync(Arg.Any<string>());
        await _memory.DidNotReceive().RetrieveRelevantMemoriesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_RunsSentimentAndMemory_ForLongerUtterance()
    {
        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "please show me my open shifts for tomorrow");

        await _sentiment.Received(1).AnalyzeSentimentAsync(Arg.Any<string>());
        await _memory.Received(1).RetrieveRelevantMemoriesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsSchedulingNudge_WhenSchedulingSkillInScope()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "cover Anna's absence next week", null, new[] { "cover_absence", "get_user_context" });

        Assert.That(result, Does.Contain(SchedulingMarker));
        var ontologyIdx = result.IndexOf(OntologyText, StringComparison.Ordinal);
        var nudgeIdx = result.IndexOf(SchedulingMarker, StringComparison.Ordinal);
        Assert.That(nudgeIdx, Is.GreaterThan(ontologyIdx));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoSchedulingNudge_WhenNoSchedulingSkillInScope()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "what is my name and email address", null, new[] { "get_user_context", "search_employees" });

        Assert.That(result, Does.Not.Contain(SchedulingMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoSchedulingNudge_WhenNoSkillsPassed()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result, Does.Not.Contain(SchedulingMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsSchedulingNudge_EvenForShortUtterance()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "ok", null, new[] { "place_work" });

        Assert.That(result, Does.Contain(SchedulingMarker));
        Assert.That(result, Does.Not.Contain(MemoryText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_OmitsWorldModel_OnConversationalTurn_WhenNoDomainSkillContext()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "thanks, that is good to know", hasDomainSkillContext: false);

        Assert.That(result, Does.Not.Contain(OntologyText));
        Assert.That(result, Does.Contain(IdentityText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_KeepsWorldModel_WhenSchedulingSkill_DespiteNoDomainSkillContext()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "ok do it now please", null, new[] { "place_work" }, hasDomainSkillContext: false);

        Assert.That(result, Does.Contain(OntologyText));
    }
}
