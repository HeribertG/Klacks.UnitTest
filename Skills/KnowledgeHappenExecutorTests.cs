// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;

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
        _sut = new KnowledgeHappenExecutor(
            _memoryRepository,
            Substitute.For<ILogger<KnowledgeHappenExecutor>>());
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

    private const string StagedContent =
        "# Groups\n" +
        "<!-- level:short -->\nSHORT-PART\n" +
        "<!-- level:elements -->\nELEMENTS-PART\n" +
        "<!-- level:effects -->\nEFFECTS-PART\n" +
        "<!-- level:pitfalls -->\nPITFALLS-PART";

    private const string StagedContentWithoutPitfalls =
        "# Groups\n" +
        "<!-- level:short -->\nSHORT-PART\n" +
        "<!-- level:elements -->\nELEMENTS-PART\n" +
        "<!-- level:effects -->\nEFFECTS-PART";

    private static readonly ExplainDepthProfile Beginner = new(ExplainExperienceLevel.Beginner, false);
    private static readonly ExplainDepthProfile Advanced = new(ExplainExperienceLevel.Advanced, false);

    private async Task<SkillResult> ExecuteStagedAsync(string content, string? level, ExplainDepthProfile? depth)
    {
        _memoryRepository.GetByKeyAsync("explain_group_model", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_group_model", Content = content });

        var parameters = level == null
            ? null
            : new Dictionary<string, object> { [KnowledgeHappenLevels.ParameterName] = level };

        return await _sut.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = "explain_group_model" }, parameters, depth);
    }

    private static string Serialized(SkillResult result) => System.Text.Json.JsonSerializer.Serialize(result.Data);

    [Test]
    public async Task Effects_Beginner_ReturnsEffectsAndPitfalls()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Effects, Beginner);

        var json = Serialized(result);
        json.ShouldContain("EFFECTS-PART");
        json.ShouldContain("PITFALLS-PART");
        json.ShouldNotContain("ELEMENTS-PART");
        json.ShouldNotContain("SHORT-PART");
        json.ShouldContain("\"Stages\":[\"effects\",\"pitfalls\"]");
    }

    [Test]
    public async Task Effects_Advanced_ReturnsEffectsAndPitfalls()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Effects, Advanced);

        var json = Serialized(result);
        json.ShouldContain("EFFECTS-PART");
        json.ShouldContain("PITFALLS-PART");
        json.ShouldNotContain("ELEMENTS-PART");
    }

    [Test]
    public async Task Elements_Beginner_ReturnsElementsWithoutPitfalls()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Elements, Beginner);

        var json = Serialized(result);
        json.ShouldContain("ELEMENTS-PART");
        json.ShouldNotContain("PITFALLS-PART");
        json.ShouldNotContain("EFFECTS-PART");
        json.ShouldContain("\"Stages\":[\"elements\"]");
    }

    [Test]
    public async Task Elements_Advanced_ReturnsElementsAndPitfalls()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Elements, Advanced);

        var json = Serialized(result);
        json.ShouldContain("ELEMENTS-PART");
        json.ShouldContain("PITFALLS-PART");
        json.ShouldNotContain("EFFECTS-PART");
        json.ShouldContain("\"Stages\":[\"elements\",\"pitfalls\"]");
    }

    [Test]
    public async Task Short_Advanced_NeverReturnsPitfalls()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Short, Advanced);

        var json = Serialized(result);
        json.ShouldContain("SHORT-PART");
        json.ShouldNotContain("PITFALLS-PART");
        json.ShouldNotContain("ELEMENTS-PART");
    }

    [Test]
    public async Task NoLevel_Advanced_ReturnsWholeBody()
    {
        var result = await ExecuteStagedAsync(StagedContent, null, Advanced);

        var json = Serialized(result);
        json.ShouldContain("SHORT-PART");
        json.ShouldContain("ELEMENTS-PART");
        json.ShouldContain("EFFECTS-PART");
        json.ShouldContain("PITFALLS-PART");
    }

    [Test]
    public async Task NoMarkers_Advanced_ReturnsWholeBody()
    {
        var result = await ExecuteStagedAsync("# Groups\nPLAIN-BODY", KnowledgeHappenLevels.Elements, Advanced);

        Serialized(result).ShouldContain("PLAIN-BODY");
    }

    [Test]
    public async Task PitfallsRequestedAsLevel_FallsBackToWholeBody()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Pitfalls, Advanced);

        var json = Serialized(result);
        json.ShouldContain("SHORT-PART");
        json.ShouldContain("ELEMENTS-PART");
        json.ShouldContain("PITFALLS-PART");
    }

    [Test]
    public async Task Elements_NullDepth_BehavesLikeBeginner()
    {
        var result = await ExecuteStagedAsync(StagedContent, KnowledgeHappenLevels.Elements, null);

        var json = Serialized(result);
        json.ShouldContain("ELEMENTS-PART");
        json.ShouldNotContain("PITFALLS-PART");
    }

    [Test]
    public async Task Effects_WithoutPitfallsMarker_ReturnsEffectsOnly()
    {
        var result = await ExecuteStagedAsync(StagedContentWithoutPitfalls, KnowledgeHappenLevels.Effects, Advanced);

        var json = Serialized(result);
        json.ShouldContain("EFFECTS-PART");
        json.ShouldContain("\"Stages\":[\"effects\"]");
    }

    [Test]
    public async Task PageHappen_WithInternalNameInProse_IsSanitized()
    {
        _memoryRepository.GetByKeyAsync("explain_page_shifts", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory
            {
                Key = "explain_page_shifts",
                Content = "# Shifts\nEine SealedOrder erzeugt eine OriginalShift."
            });

        var result = await _sut.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = "explain_page_shifts" });

        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Not.Contain("SealedOrder"));
        Assert.That(serialized, Does.Not.Contain("OriginalShift"));
        Assert.That(serialized, Does.Contain("versiegelte Bestellung"));
        Assert.That(serialized, Does.Contain("planbare Schicht"));
    }

    [Test]
    public async Task LifecycleHappen_IsExemptFromSanitization()
    {
        _memoryRepository.GetByKeyAsync(SkillNames.ExplainShiftLifecycle, Arg.Any<CancellationToken>())
            .Returns(new AgentMemory
            {
                Key = SkillNames.ExplainShiftLifecycle,
                Content = "Eine SealedOrder erzeugt eine OriginalShift."
            });

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = SkillNames.ExplainShiftLifecycle });

        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(serialized, Does.Contain("SealedOrder"));
        Assert.That(serialized, Does.Contain("OriginalShift"));
    }
    [Test]
    public async Task AnswerInstruction_AsksForOneScrollMarkerWhenTheUserAsksWhereAnElementIs()
    {
        _memoryRepository.GetByKeyAsync("explain_page_settings_erp_drop_points", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_page_settings_erp_drop_points", Content = "# ERP\nUpload zone." });

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = "explain_page_settings_erp_drop_points" });

        Assert.That(result.Message, Does.Contain("asks WHERE"));
        Assert.That(result.Message, Does.Contain("exactly one [SCROLL:anchor-id] marker"));
    }

    [Test]
    public async Task OpenPage_ForbidsDescribingTheClickPathToIt()
    {
        SetupErpMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = ErpSkill, PageKey = "settings" },
            new Dictionary<string, object> { ["level"] = "elements" },
            pageAccess: KnowledgePageAccess.Open);

        Assert.That(result.Message, Does.Contain(KnowledgeHappenExecutor.PageAlreadyOpenInstruction));
        Assert.That(result.Message, Does.Not.Contain(KnowledgeHappenExecutor.NoPageAccessInstruction));
    }

    [Test]
    public async Task DeniedPage_ExplainsBrieflyAndNamesTheAdministratorInsteadOfPointing()
    {
        SetupErpMemory();

        var result = await _sut.ExecuteAsync(
            new KnowledgeHappenConfig { MemoryKey = ErpSkill, PageKey = "settings" },
            pageAccess: KnowledgePageAccess.Denied);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain(KnowledgeHappenExecutor.NoPageAccessInstruction));
        Assert.That(result.Message, Does.Not.Contain(KnowledgeHappenExecutor.PageAlreadyOpenInstruction));
    }

    [Test]
    public async Task NoPage_KeepsTheClickPathBecauseTheAppDoesNotOpenAPage()
    {
        _memoryRepository.GetByKeyAsync("explain_why_cannot_book", Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = "explain_why_cannot_book", Content = "# Why\nReasons." });

        var result = await _sut.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = "explain_why_cannot_book" });

        Assert.That(result.Message, Does.Not.Contain(KnowledgeHappenExecutor.PageAlreadyOpenInstruction));
        Assert.That(result.Message, Does.Not.Contain(KnowledgeHappenExecutor.NoPageAccessInstruction));
        Assert.That(result.Message, Does.Not.Contain("highlighted"));
    }

    private const string ErpSkill = "explain_page_settings_erp_drop_points";

    private void SetupErpMemory()
    {
        _memoryRepository.GetByKeyAsync(ErpSkill, Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { Key = ErpSkill, Content = "# ERP\nUpload zone." });
    }
}
