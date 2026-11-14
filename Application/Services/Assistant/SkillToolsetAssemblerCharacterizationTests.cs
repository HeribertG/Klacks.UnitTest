// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for SkillToolsetAssembler.AssembleAsync with a substituted guarantee resolver: the call order
/// (turn intent started before retrieval, guarantees resolved after it), the request handed to the resolver, the
/// always-on-only result when nothing was retrieved or guaranteed, a guaranteed but unretrieved skill turning the
/// result into a domain-context toolset, the turn-intent veto on both paths and the free budget given to the expander.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerCharacterizationTests
{
    private const string AlwaysOnSkill = "navigate_to";
    private const string RetrievedA = "alpha_skill";
    private const string RetrievedB = "bravo_skill";
    private const string RetrievedC = "charlie_skill";
    private const string PoolOnlySkill = "delta_skill";
    private const string GuaranteedSkill = "update_contract";
    private const string UserMessage = "something unrelated, and something else";
    private const string SinglePartMessage = "something unrelated";

    private Agent _agent = null!;
    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _queryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private ISkillToolsetGuaranteeResolver _resolver = null!;
    private Task<TurnIntentPrediction?> _pendingTurnIntent = null!;
    private SkillToolsetGuaranteeRequest? _seenRequest;

    private static AgentSkill Skill(string name, bool alwaysOn = false) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = SkillEffect.Read,
        AlwaysOn = alwaysOn,
        TriggerKeywords = "[]"
    };

    private static RetrievalCandidate Candidate(string skillName, double score) =>
        new(new KnowledgeEntry { SourceId = skillName, Kind = KnowledgeEntryKind.Skill }, score);

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                Skill(AlwaysOnSkill, alwaysOn: true), Skill(RetrievedA), Skill(RetrievedB), Skill(GuaranteedSkill),
                Skill(RetrievedC), Skill(PoolOnlySkill)
            });

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        RetrievalReturns();

        _queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        _queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        _expander = Substitute.For<ISkillRetrievalExpander>();

        _pendingTurnIntent = Task.FromResult<TurnIntentPrediction?>(null);
        _resolver = Substitute.For<ISkillToolsetGuaranteeResolver>();
        _resolver.StartTurnIntent(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_pendingTurnIntent);
        GuaranteesReturn(vetoes: false);
    }

    [TearDown]
    public void TearDown() => _pendingTurnIntent.Dispose();

    private void RetrievalReturns(params RetrievalCandidate[] candidates) =>
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(candidates));

    private void RetrievalReturnsWithPool(RetrievalCandidate[] candidates, params RetrievalCandidate[] pool) =>
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(candidates) { AssociationPool = pool });

    private void ThreeRetrievedWithADeeperPool(int slots)
    {
        _expander.AssociationSlots.Returns(slots);
        RetrievalReturnsWithPool(
            [Candidate(RetrievedA, 0.5), Candidate(RetrievedB, 0.4), Candidate(RetrievedC, 0.3)],
            Candidate(AlwaysOnSkill, 0.6), Candidate(RetrievedA, 0.5), Candidate(RetrievedB, 0.4),
            Candidate(RetrievedC, 0.3), Candidate(PoolOnlySkill, 0.2));
    }

    private void GuaranteesReturn(bool vetoes, params string[] guaranteedNames)
    {
        _resolver.ResolveAsync(Arg.Any<SkillToolsetGuaranteeRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<SkillToolsetGuaranteeRequest>();
                _seenRequest = request;
                var skills = request.PermittedSkills.Where(s => guaranteedNames.Contains(s.Name)).ToHashSet();
                var sources = new Dictionary<string, ToolsetSkillSource>(StringComparer.OrdinalIgnoreCase);
                foreach (var skill in skills)
                {
                    sources[skill.Name] = ToolsetSkillSource.Hint;
                }

                return Task.FromResult(new SkillToolsetGuaranteeResult(
                    skills, sources, new HashSet<string>(StringComparer.OrdinalIgnoreCase), vetoes));
            });
    }

    private SkillToolsetAssembler CreateAssembler() =>
        new(_skillCache, _retrieval, _queryBuilder, _expander, _resolver, Substitute.For<ILogger<SkillToolsetAssembler>>());

    private Task<SkillToolsetResult> Assemble(int maxTools = 10, string userMessage = UserMessage) =>
        CreateAssembler().AssembleAsync(
            _agent, new List<string>(), userMessage, conversationId: "conv", currentRoute: "/route", "user-1", language: "de",
            maxTools, applyLearnedPhraseGuarantee: true, excludedSkillNames: null, pinnedSkillNames: ["pin_a"],
            previousTurnSkillNames: ["prev_a"], continuationSkillNames: ["cont_a"], CancellationToken.None);

    private static List<string> Names(SkillToolsetResult result) => result.Functions.Select(f => f.Name).ToList();

    [Test]
    public async Task TurnIntentStarts_BeforeRetrieval_AndGuaranteesAreResolvedAfterIt()
    {
        RetrievalReturns(Candidate(RetrievedA, 0.5));

        await Assemble();

        Received.InOrder(() =>
        {
            _resolver.StartTurnIntent(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>());
            _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>());
            _resolver.ResolveAsync(Arg.Any<SkillToolsetGuaranteeRequest>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task TheResolver_GetsTheTurnInputs_TheRetrievedSkillsAndScores_AndThePendingTurnIntent()
    {
        RetrievalReturns(Candidate(RetrievedA, 0.5), Candidate(AlwaysOnSkill, 0.9));

        await Assemble();

        _seenRequest.ShouldNotBeNull();
        _seenRequest.AgentId.ShouldBe(_agent.Id);
        _seenRequest.PermittedSkills.Count.ShouldBe(6);
        _seenRequest.RetrievedSkills.Select(s => s.Name).ShouldBe([RetrievedA]);
        _seenRequest.UserMessage.ShouldBe(UserMessage);
        _seenRequest.ConversationId.ShouldBe("conv");
        _seenRequest.CurrentRoute.ShouldBe("/route");
        _seenRequest.UserId.ShouldBe("user-1");
        _seenRequest.Language.ShouldBe("de");
        _seenRequest.PinnedSkillNames!.ShouldBe(["pin_a"]);
        _seenRequest.PreviousTurnSkillNames!.ShouldBe(["prev_a"]);
        _seenRequest.ContinuationSkillNames!.ShouldBe(["cont_a"]);
        _seenRequest.ApplyLearnedPhraseGuarantee.ShouldBeTrue();
        _seenRequest.RetrievalScores![RetrievedA].ShouldBe(0.5);
        _seenRequest.RetrievalScores[AlwaysOnSkill].ShouldBe(0.9);
        _seenRequest.PendingTurnIntent.ShouldBeSameAs(_pendingTurnIntent);
    }

    [Test]
    public async Task NothingRetrievedOrGuaranteed_YieldsAlwaysOnOnly_WithoutDomainContext_ButWithTheVeto()
    {
        GuaranteesReturn(vetoes: true);

        var result = await Assemble();

        Names(result).ShouldBe([AlwaysOnSkill]);
        result.HasDomainSkillContext.ShouldBeFalse();
        result.TurnIntentVetoesForcedToolCall.ShouldBeTrue();
        await _expander.DidNotReceiveWithAnyArgs().AssociateAsync(default, default!, default!, default, default);
    }

    [Test]
    public async Task AGuaranteedSkillThatRetrievalMissed_IsAdded_AndMakesTheToolsetADomainContext()
    {
        GuaranteesReturn(vetoes: true, GuaranteedSkill);

        var result = await Assemble();

        Names(result).ShouldBe([AlwaysOnSkill, GuaranteedSkill], ignoreOrder: true);
        result.HasDomainSkillContext.ShouldBeTrue();
        result.TurnIntentVetoesForcedToolCall.ShouldBeTrue();
        result.Functions.Single(f => f.Name == GuaranteedSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public async Task TheBestRetrievedSkillsSeedTheAssociation_AndThePoolOffersOnlyEligibleNonSeeds()
    {
        ThreeRetrievedWithADeeperPool(slots: 1);

        await Assemble();

        await _expander.Received(1).AssociateAsync(
            _agent.Id,
            Arg.Is<IReadOnlyDictionary<string, double>>(seeds =>
                seeds.Count == 2 && seeds[RetrievedA] == 0.5 && seeds[RetrievedB] == 0.4),
            Arg.Is<IReadOnlyDictionary<string, double>>(pool =>
                pool.Count == 2 && pool[RetrievedC] == 0.3 && pool[PoolOnlySkill] == 0.2),
            1,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAssociatedSkill_TakesTheLastRetrievalPlace_WithTheExpansionSource()
    {
        ThreeRetrievedWithADeeperPool(slots: 1);
        _expander.AssociateAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, double>>(),
                Arg.Any<IReadOnlyDictionary<string, double>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { PoolOnlySkill });

        var result = await Assemble();

        Names(result).ShouldBe([AlwaysOnSkill, RetrievedA, RetrievedB, PoolOnlySkill], ignoreOrder: true);
        result.Functions.Single(f => f.Name == PoolOnlySkill).ToolsetSource.ShouldBe(ToolsetSkillSource.Expansion);
    }

    [Test]
    public async Task WhenTheAssociationPicksNothing_TheLastPlaceGoesBackToTheNextRetrievedSkill()
    {
        ThreeRetrievedWithADeeperPool(slots: 1);
        _expander.AssociateAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, double>>(),
                Arg.Any<IReadOnlyDictionary<string, double>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var result = await Assemble();

        Names(result).ShouldBe([AlwaysOnSkill, RetrievedA, RetrievedB, RetrievedC], ignoreOrder: true);
    }

    [Test]
    public async Task WithZeroSlots_TheAssociationIsNeverAsked_AndRetrievalIsUntouched()
    {
        ThreeRetrievedWithADeeperPool(slots: 0);

        var result = await Assemble();

        await _expander.DidNotReceiveWithAnyArgs().AssociateAsync(default, default!, default!, default, default);
        Names(result).ShouldBe([AlwaysOnSkill, RetrievedA, RetrievedB, RetrievedC], ignoreOrder: true);
    }

    [Test]
    public async Task ASinglePartMessage_IsNeverAssociated_AndKeepsItsRetrievedSkills()
    {
        ThreeRetrievedWithADeeperPool(slots: 1);

        var result = await Assemble(userMessage: SinglePartMessage);

        await _expander.DidNotReceiveWithAnyArgs().AssociateAsync(default, default!, default!, default, default);
        Names(result).ShouldBe([AlwaysOnSkill, RetrievedA, RetrievedB, RetrievedC], ignoreOrder: true);
    }

    [Test]
    public async Task WithoutAnAssociationPool_ARerankedPass_TheAssociationIsNeverAsked()
    {
        _expander.AssociationSlots.Returns(1);
        RetrievalReturns(Candidate(RetrievedA, 0.5), Candidate(RetrievedB, 0.4), Candidate(RetrievedC, 0.3));

        var result = await Assemble();

        await _expander.DidNotReceiveWithAnyArgs().AssociateAsync(default, default!, default!, default, default);
        Names(result).ShouldBe([AlwaysOnSkill, RetrievedA, RetrievedB, RetrievedC], ignoreOrder: true);
    }

    [Test]
    public async Task ThePoolScores_NeverReachTheGuaranteeResolver()
    {
        ThreeRetrievedWithADeeperPool(slots: 1);
        _expander.AssociateAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<string, double>>(),
                Arg.Any<IReadOnlyDictionary<string, double>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { PoolOnlySkill });

        await Assemble();

        _seenRequest.ShouldNotBeNull();
        _seenRequest!.RetrievalScores.ShouldNotContainKey(PoolOnlySkill);
        _seenRequest.RetrievedSkills.Select(s => s.Name).ShouldContain(PoolOnlySkill);
    }
}
