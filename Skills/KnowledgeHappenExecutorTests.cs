// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class KnowledgeHappenExecutorTests
{
    private IAgentMemoryRepository _memoryRepository = null!;
    private KnowledgeHappenExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _sut = new KnowledgeHappenExecutor(_memoryRepository);
    }

    [Test]
    public async Task ExistingKey_ReturnsContentWithTranslationInstruction()
    {
        _memoryRepository.GetByKeyAsync("explain_page_dashboard", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_page_dashboard", Content = "# Dashboard\nFour sections." });

        var result = await _sut.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = "explain_page_dashboard" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("in the user's language"));
        Assert.That(result.Message, Does.Contain("NEVER print the anchor lists"));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("Four sections."));
    }

    [Test]
    public async Task MissingKey_ReturnsError()
    {
        _memoryRepository.GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentMemory?)null);

        var result = await _sut.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = "explain_page_unknown" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("explain_page_unknown"));
    }

    [Test]
    public async Task MissingMemoryKeyConfig_ReturnsError()
    {
        var result = await _sut.ExecuteAsync(new KnowledgeHappenConfig());

        Assert.That(result.Success, Is.False);
        await _memoryRepository.DidNotReceive().GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private const string LeveledContent =
        "# Absence Calendar\n" +
        "<!-- level:short -->\n## Purpose\nA year-wide Gantt chart.\n" +
        "<!-- level:elements -->\n## Elements\nHeader, row header, surface, mask.\n" +
        "<!-- level:effects -->\n## Effects\nFeeds the schedule and the resource monitor.";

    private void SetupLeveledMemory()
    {
        _memoryRepository.GetByKeyAsync("explain_page_absence", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_page_absence", Content = LeveledContent });
    }

    [Test]
    public async Task LevelShort_ReturnsPreambleAndShortSectionOnly()
    {
        SetupLeveledMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_absence" },
            new Dictionary<string, object> { ["level"] = "short" });

        Assert.That(result.Success, Is.True);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("Absence Calendar"));
        Assert.That(serialized, Does.Contain("A year-wide Gantt chart."));
        Assert.That(serialized, Does.Not.Contain("Header, row header"));
        Assert.That(serialized, Does.Not.Contain("resource monitor"));
        Assert.That(result.Message, Does.Contain("short"));
    }

    [Test]
    public async Task LevelEffects_ReturnsLastSectionUntilEndOfContent()
    {
        SetupLeveledMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_absence" },
            new Dictionary<string, object> { ["level"] = "EFFECTS" });

        Assert.That(result.Success, Is.True);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("Feeds the schedule and the resource monitor."));
        Assert.That(serialized, Does.Not.Contain("Header, row header"));
    }

    [Test]
    public async Task LevelRequested_ContentWithoutMarkers_ReturnsFullContent()
    {
        _memoryRepository.GetByKeyAsync("explain_page_dashboard", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_page_dashboard", Content = "# Dashboard\nFour sections." });

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_dashboard" },
            new Dictionary<string, object> { ["level"] = "short" });

        Assert.That(result.Success, Is.True);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("Four sections."));
    }

    [Test]
    public async Task UnknownLevelValue_ReturnsFullContent()
    {
        SetupLeveledMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_absence" },
            new Dictionary<string, object> { ["level"] = "everything" });

        Assert.That(result.Success, Is.True);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("A year-wide Gantt chart."));
        Assert.That(serialized, Does.Contain("Feeds the schedule and the resource monitor."));
    }

    [Test]
    public async Task NoLevelParameter_WithMarkers_ReturnsFullContent()
    {
        SetupLeveledMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_absence" });

        Assert.That(result.Success, Is.True);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("A year-wide Gantt chart."));
        Assert.That(serialized, Does.Contain("Header, row header, surface, mask."));
    }
}
