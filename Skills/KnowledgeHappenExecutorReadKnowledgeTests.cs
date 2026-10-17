// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for KnowledgeHappenExecutor.ReadKnowledgeAsync, the plain-text read the gate explain offer grounds its model turn
/// in: the same stage cut as ExecuteAsync for the same level and depth, the whole body when it carries no markers, and
/// null when the happen is not seeded or empty.
/// </summary>

using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class KnowledgeHappenExecutorReadKnowledgeTests
{
    private const string Key = "explain_final_vs_correctable";

    private const string StagedContent =
        "# Final or correctable\n" +
        "<!-- level:short -->\nPurpose only.\n" +
        "<!-- level:elements -->\nSteps.\n" +
        "<!-- level:effects -->\nWhat happens.\n" +
        "<!-- level:pitfalls -->\nNothing can be undone.";

    private IAgentMemoryRepository _memoryRepository = null!;
    private KnowledgeHappenExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _sut = new KnowledgeHappenExecutor(_memoryRepository, Substitute.For<ILogger<KnowledgeHappenExecutor>>());
    }

    private void Seed(string? content) =>
        _memoryRepository.GetByKeyAsync(Key, Arg.Any<CancellationToken>())
            .Returns(content == null ? null : new AgentMemory { Key = Key, Content = content });

    [Test]
    public async Task Effects_ReturnsEffectsAndPitfalls_ForEveryone()
    {
        Seed(StagedContent);

        var beginner = await _sut.ReadKnowledgeAsync(Key, KnowledgeHappenLevels.Effects);
        var advanced = await _sut.ReadKnowledgeAsync(
            Key, KnowledgeHappenLevels.Effects, new ExplainDepthProfile(ExplainExperienceLevel.Advanced, false));

        foreach (var text in new[] { beginner, advanced })
        {
            text.ShouldNotBeNull();
            text.ShouldContain("What happens.");
            text.ShouldContain("Nothing can be undone.");
            text.ShouldNotContain("Purpose only.");
            text.ShouldNotContain("Steps.");
        }
    }

    [Test]
    public async Task Elements_AddsPitfalls_ForAdvancedOnly()
    {
        Seed(StagedContent);

        var beginner = await _sut.ReadKnowledgeAsync(Key, KnowledgeHappenLevels.Elements);
        var advanced = await _sut.ReadKnowledgeAsync(
            Key, KnowledgeHappenLevels.Elements, new ExplainDepthProfile(ExplainExperienceLevel.Advanced, false));

        beginner!.ShouldNotContain("Nothing can be undone.");
        advanced!.ShouldContain("Nothing can be undone.");
    }

    [Test]
    public async Task ABodyWithoutMarkers_ComesBackWhole()
    {
        Seed("# Plain\nNo stages here.");

        (await _sut.ReadKnowledgeAsync(Key, KnowledgeHappenLevels.Effects)).ShouldContain("No stages here.");
    }

    [TestCase(null)]
    [TestCase("   ")]
    public async Task AMissingOrEmptyHappen_IsNull(string? content)
    {
        Seed(content);

        (await _sut.ReadKnowledgeAsync(Key, KnowledgeHappenLevels.Effects)).ShouldBeNull();
    }
}
