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
        Assert.That(result.Message, Does.Contain("Translate into the user's language"));
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
}
