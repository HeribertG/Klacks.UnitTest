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
            _identity, _ontology, _memory, _sentiment,
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
}
