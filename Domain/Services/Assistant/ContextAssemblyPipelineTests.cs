// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContextAssemblyPipeline, covering the Klacks ontology world-model
/// block injection (S1 of the autonomy roadmap) and the stable/volatile prompt-cache split
/// (P1 of the Klacksy memory redesign).
/// </summary>

using Klacks.Api.Domain.Enums;
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
    private const int ExpectedOntologyTokenBudget = IKlacksOntologyService.DefaultMaxTokens;

    private const string SchedulingMarker = "[SCHEDULING CONTEXT]";
    private const string OpenFindingsMarker = "[OPEN_FINDINGS]";

    private IIdentityContextProvider _identity = null!;
    private IKlacksOntologyService _ontology = null!;
    private IMemoryRetrievalService _memory = null!;
    private ISentimentAnalyzer _sentiment = null!;
    private IPendingUserNoteRepository _pendingNotes = null!;
    private IRecentEntityRepository _recentEntities = null!;
    private IAgentConditionScopeResolver _conditionScope = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private ContextAssemblyPipeline _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _identity = Substitute.For<IIdentityContextProvider>();
        _ontology = Substitute.For<IKlacksOntologyService>();
        _memory = Substitute.For<IMemoryRetrievalService>();
        _sentiment = Substitute.For<ISentimentAnalyzer>();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _recentEntities = Substitute.For<IRecentEntityRepository>();
        _conditionScope = Substitute.For<IAgentConditionScopeResolver>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();

        _identity.GetIdentityPromptAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(IdentityText);
        _ontology.RenderWorldModelBlock(Arg.Any<int>()).Returns(OntologyText);
        _memory.RetrieveRelevantMemoriesAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRetrievalResult(MemoryText, Array.Empty<Guid>()));
        _memory.RetrieveToolsetLessonsAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
        _sentiment.AnalyzeSentimentAsync(Arg.Any<string>())
            .Returns(new SentimentResult(SentimentMood.Neutral, 0f));
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _recentEntities.GetRecentAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<RecentEntityRow>());
        _conditionScope.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());
        _conditionRepository.GetTopForContextAsync(
                Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        _sut = new ContextAssemblyPipeline(
            _identity, _ontology, _memory, _sentiment, new RuleContextProvider(),
            _pendingNotes,
            _recentEntities,
            _conditionScope,
            _conditionRepository,
            NullLogger<ContextAssemblyPipeline>.Instance);
    }

    private AgentMemory Lesson(string skillKey, string content) => new()
    {
        Id = Guid.NewGuid(),
        Category = "reflection",
        Key = skillKey,
        Content = content
    };

    [Test]
    public async Task ToolsetLessons_AreRenderedAsLessonsBlock_AndTheirIdsAreInjected()
    {
        var lesson = Lesson("update_client", "resolve the client id before updating");
        _memory.RetrieveToolsetLessonsAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { lesson });

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "bitte passe den Kunden an", availableSkillNames: new[] { "update_client" });

        Assert.That(result.VolatilePrompt, Does.Contain("[LESSONS]"));
        Assert.That(result.VolatilePrompt, Does.Contain("- [update_client] resolve the client id before updating"));
        Assert.That(result.InjectedMemoryIds, Does.Contain(lesson.Id));
    }

    [Test]
    public async Task ShortConfirmationTurn_StillReceivesToolsetLessons()
    {
        var lesson = Lesson("apply_grouping", "confirm the grouping wording first");
        _memory.RetrieveToolsetLessonsAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { lesson });

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "ja", availableSkillNames: new[] { "apply_grouping" });

        Assert.That(result.VolatilePrompt, Does.Contain("[LESSONS]"));
        Assert.That(result.InjectedMemoryIds, Does.Contain(lesson.Id));
        await _memory.DidNotReceive().RetrieveRelevantMemoriesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LessonsAlreadyInjectedByMemoryRetrieval_AreNotRenderedTwice()
    {
        var lesson = Lesson("update_client", "duplicate lesson");
        _memory.RetrieveToolsetLessonsAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { lesson });
        _memory.RetrieveRelevantMemoriesAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRetrievalResult(MemoryText, new[] { lesson.Id }));

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "bitte passe den Kunden an", availableSkillNames: new[] { "update_client" });

        Assert.That(result.VolatilePrompt, Does.Not.Contain("[LESSONS]"));
    }

    [Test]
    public async Task WithoutAToolset_NoLessonRetrievalHappens()
    {
        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        await _memory.DidNotReceive().RetrieveToolsetLessonsAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_IncludesWorldModelBlock()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result.StablePrompt, Does.Contain(OntologyText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_VoiceMode_SuppressesTextOnlyAffordancesInIdentityPrompt()
    {
        var agentId = Guid.NewGuid();

        await _sut.AssembleSoulAndMemoryPromptAsync(agentId, "hello there", isVoiceMode: true);

        await _identity.Received(1).GetIdentityPromptAsync(
            agentId, Arg.Any<string?>(), true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_TextMode_KeepsTextOnlyAffordancesInIdentityPrompt()
    {
        var agentId = Guid.NewGuid();

        await _sut.AssembleSoulAndMemoryPromptAsync(agentId, "hello there", isVoiceMode: false);

        await _identity.Received(1).GetIdentityPromptAsync(
            agentId, Arg.Any<string?>(), false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_PlacesOntologyAfterIdentityInStableSegment()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        var identityIdx = result.StablePrompt.IndexOf(IdentityText, StringComparison.Ordinal);
        var ontologyIdx = result.StablePrompt.IndexOf(OntologyText, StringComparison.Ordinal);

        Assert.That(identityIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(ontologyIdx, Is.GreaterThan(identityIdx));
        Assert.That(result.VolatilePrompt, Does.Contain(MemoryText));
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

        Assert.That(result.StablePrompt, Does.Not.Contain("WORLD MODEL"));
        Assert.That(result.StablePrompt, Does.Contain(IdentityText));
        Assert.That(result.VolatilePrompt, Does.Contain(MemoryText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_SkipsSentimentAndMemory_ForShortUtterance()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "ja");

        Assert.That(result.StablePrompt, Does.Contain(IdentityText));
        Assert.That(result.StablePrompt, Does.Contain(OntologyText));
        Assert.That(result.VolatilePrompt, Does.Not.Contain(MemoryText));
        await _sentiment.DidNotReceive().AnalyzeSentimentAsync(Arg.Any<string>());
        await _memory.DidNotReceive().RetrieveRelevantMemoriesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_RunsSentimentAndMemory_ForLongerUtterance()
    {
        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "please show me my open shifts for tomorrow");

        await _sentiment.Received(1).AnalyzeSentimentAsync(Arg.Any<string>());
        await _memory.Received(1).RetrieveRelevantMemoriesAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_CarriesInjectedMemoryIds_FromMemoryRetrievalResult()
    {
        var injectedId = Guid.NewGuid();
        _memory.RetrieveRelevantMemoriesAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<ContextBudgetProfile?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryRetrievalResult(MemoryText, new[] { injectedId }));

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "please show me my open shifts for tomorrow");

        result.InjectedMemoryIds.ShouldNotBeNull();
        result.InjectedMemoryIds.ShouldContain(injectedId);
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsSchedulingNudge_WhenSchedulingSkillInScope()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "cover Anna's absence next week", null, new[] { "cover_absence", "get_user_context" });

        Assert.That(result.StablePrompt, Does.Contain(SchedulingMarker));
        var ontologyIdx = result.StablePrompt.IndexOf(OntologyText, StringComparison.Ordinal);
        var nudgeIdx = result.StablePrompt.IndexOf(SchedulingMarker, StringComparison.Ordinal);
        Assert.That(nudgeIdx, Is.GreaterThan(ontologyIdx));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoSchedulingNudge_WhenNoSchedulingSkillInScope()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "what is my name and email address", null, new[] { "get_user_context", "search_employees" });

        Assert.That(result.StablePrompt, Does.Not.Contain(SchedulingMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoSchedulingNudge_WhenNoSkillsPassed()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result.StablePrompt, Does.Not.Contain(SchedulingMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsSchedulingNudge_EvenForShortUtterance()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "ok", null, new[] { "place_work" });

        Assert.That(result.StablePrompt, Does.Contain(SchedulingMarker));
        Assert.That(result.VolatilePrompt, Does.Not.Contain(MemoryText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_OmitsWorldModel_OnConversationalTurn_WhenNoDomainSkillContext()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "thanks, that is good to know", hasDomainSkillContext: false);

        Assert.That(result.StablePrompt, Does.Not.Contain(OntologyText));
        Assert.That(result.StablePrompt, Does.Contain(IdentityText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_KeepsWorldModel_WhenSchedulingSkill_DespiteNoDomainSkillContext()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "ok do it now please", null, new[] { "place_work" }, hasDomainSkillContext: false);

        Assert.That(result.StablePrompt, Does.Contain(OntologyText));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsPendingNotesHint_WhenUndeliveredNotesExist()
    {
        var userId = Guid.NewGuid();
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>()).Returns(2);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Contain("[PENDING_NOTES: 2]"));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoPendingNotesHint_WhenNoneExist()
    {
        var userId = Guid.NewGuid();
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Not.Contain("PENDING_NOTES"));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoPendingNotesHint_WhenUserIdMissing()
    {
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(5);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result.VolatilePrompt, Does.Not.Contain("PENDING_NOTES"));
        await _pendingNotes.DidNotReceive().CountPendingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_InjectsRecentEntitiesBlock_WhenPresent()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _recentEntities.GetRecentAsync(userId, "conv-1", Arg.Any<CancellationToken>()).Returns(new List<RecentEntityRow>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ConversationId = "conv-1",
                EntityType = "shift",
                EntityId = shiftId,
                DisplayName = "Frühdienst",
                Action = "created",
                CreatedAtUtc = DateTime.UtcNow
            }
        });

        // A terse follow-up ("del it" = 6 chars) is below MinLengthForSemanticEnrichment and hits the
        // early return; the block must still be present, which asserts it is placed before that return.
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "del it", userId: userId, conversationId: "conv-1");

        Assert.That(result.VolatilePrompt, Does.Contain("[RECENTLY_TOUCHED]"));
        Assert.That(result.VolatilePrompt, Does.Contain(shiftId.ToString()));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoRecentEntitiesBlock_WhenConversationIdMissing()
    {
        var userId = Guid.NewGuid();

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Not.Contain("[RECENTLY_TOUCHED]"));
        await _recentEntities.DidNotReceive().GetRecentAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_StableSegment_NeverContainsVolatileMarkers()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>()).Returns(3);
        _recentEntities.GetRecentAsync(userId, "conv-1", Arg.Any<CancellationToken>()).Returns(new List<RecentEntityRow>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ConversationId = "conv-1",
                EntityType = "shift",
                EntityId = shiftId,
                DisplayName = "Frühdienst",
                Action = "created",
                CreatedAtUtc = DateTime.UtcNow
            }
        });
        _sentiment.AnalyzeSentimentAsync(Arg.Any<string>())
            .Returns(new SentimentResult(SentimentMood.Frustrated, 0.9f));
        SetUpOpenFinding(userId);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "please show me my open shifts for tomorrow", userId: userId, conversationId: "conv-1");

        Assert.That(result.StablePrompt, Does.Not.Contain("PENDING_NOTES"));
        Assert.That(result.StablePrompt, Does.Not.Contain("[RECENTLY_TOUCHED]"));
        Assert.That(result.StablePrompt, Does.Not.Contain("USER_MOOD"));
        Assert.That(result.StablePrompt, Does.Not.Contain(MemoryText));
        Assert.That(result.StablePrompt, Does.Not.Contain(OpenFindingsMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_VolatileSegment_ContainsAllPerTurnMarkers()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>()).Returns(3);
        _recentEntities.GetRecentAsync(userId, "conv-1", Arg.Any<CancellationToken>()).Returns(new List<RecentEntityRow>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ConversationId = "conv-1",
                EntityType = "shift",
                EntityId = shiftId,
                DisplayName = "Frühdienst",
                Action = "created",
                CreatedAtUtc = DateTime.UtcNow
            }
        });
        _sentiment.AnalyzeSentimentAsync(Arg.Any<string>())
            .Returns(new SentimentResult(SentimentMood.Frustrated, 0.9f));
        SetUpOpenFinding(userId);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(
            Guid.NewGuid(), "please show me my open shifts for tomorrow", userId: userId, conversationId: "conv-1");

        Assert.That(result.VolatilePrompt, Does.Contain("PENDING_NOTES"));
        Assert.That(result.VolatilePrompt, Does.Contain("[RECENTLY_TOUCHED]"));
        Assert.That(result.VolatilePrompt, Does.Contain("USER_MOOD"));
        Assert.That(result.VolatilePrompt, Does.Contain(MemoryText));
        Assert.That(result.VolatilePrompt, Does.Contain(OpenFindingsMarker));
    }

    private void SetUpOpenFinding(Guid userId)
    {
        _conditionScope.ResolveAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository.GetTopForContextAsync(
                Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TriggerKind = "open_order",
                    Fingerprint = $"open_order:{Guid.NewGuid()}",
                    Severity = "high",
                    Status = AgentConditionStatus.Detected,
                    DetectedAtUtc = DateTime.UtcNow,
                    LastSeenAtUtc = DateTime.UtcNow,
                    PayloadJson = "{}"
                }
            });
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_IncludesOpenFindingsBlock_WhenPlannerHasFindings()
    {
        var userId = Guid.NewGuid();
        SetUpOpenFinding(userId);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Contain(OpenFindingsMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoOpenFindingsBlock_WhenRepositoryReturnsNone()
    {
        var userId = Guid.NewGuid();
        _conditionScope.ResolveAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.Unrestricted());
        _conditionRepository.GetTopForContextAsync(
                Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Not.Contain(OpenFindingsMarker));
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoOpenFindingsBlock_WhenUserIsNotAPlanner()
    {
        var userId = Guid.NewGuid();
        _conditionScope.ResolveAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(AgentConditionVisibilityScope.NotAPlanner());

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Not.Contain(OpenFindingsMarker));
        await _conditionRepository.DidNotReceive().GetTopForContextAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_NoOpenFindingsBlock_WhenUserIdMissing()
    {
        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there");

        Assert.That(result.VolatilePrompt, Does.Not.Contain(OpenFindingsMarker));
        await _conditionScope.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_PassesParsedSelectedGroupId_AsPreferredGroup()
    {
        var userId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        SetUpOpenFinding(userId);
        var pageContext = new AssistantPageContext { SelectedGroupId = groupId.ToString() };

        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there", userId: userId, pageContext: pageContext);

        await _conditionRepository.Received(1).GetTopForContextAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), groupId, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_CapsFindingsRequestAtThree()
    {
        var userId = Guid.NewGuid();
        SetUpOpenFinding(userId);

        await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "hello there", userId: userId);

        await _conditionRepository.Received(1).GetTopForContextAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<Guid?>(), 3, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AssembleSoulAndMemoryPromptAsync_OpenFindingsBlock_SurvivesShortUtteranceEarlyReturn()
    {
        // A terse follow-up ("del it" = 6 chars) is below MinLengthForSemanticEnrichment and hits the
        // early return; the block must still be present, which pins it before that return (mirrors
        // AssembleSoulAndMemoryPromptAsync_InjectsRecentEntitiesBlock_WhenPresent for RECENTLY_TOUCHED).
        var userId = Guid.NewGuid();
        SetUpOpenFinding(userId);

        var result = await _sut.AssembleSoulAndMemoryPromptAsync(Guid.NewGuid(), "del it", userId: userId);

        Assert.That(result.VolatilePrompt, Does.Contain(OpenFindingsMarker));
    }
}
