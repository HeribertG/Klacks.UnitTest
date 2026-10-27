// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the observation-only SkillToolsetTrace of SkillToolsetAssembler: the retrieved names in rank order, the
/// selection before the cap and the names the cap removed, with and without truncation, and that the trace changes
/// nothing about which functions the toolset offers.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerTraceTests
{
    private const string AlwaysOnSkill = "navigate_to";
    private const string Alpha = "alpha_skill";
    private const string Bravo = "bravo_skill";
    private const string Charlie = "charlie_skill";
    private const string NotRetrieved = "delta_skill";
    private const string UserMessage = "something unrelated";
    private const int AmpleCap = 10;
    private const int CapOneBelowSelection = 3;
    private const int TightCap = 2;
    private const int SelectionSize = 4;

    private Agent _agent = null!;
    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private Task<TurnIntentPrediction?> _pendingTurnIntent = null!;
    private SkillToolsetAssembler _assembler = null!;

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
                Skill(AlwaysOnSkill, alwaysOn: true), Skill(Charlie), Skill(Alpha), Skill(Bravo), Skill(NotRetrieved)
            });

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        RetrievalReturns(Candidate(Charlie, 0.3), Candidate(Alpha, 0.9), Candidate(Bravo, 0.5));

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        _pendingTurnIntent = Task.FromResult<TurnIntentPrediction?>(null);
        var resolver = Substitute.For<ISkillToolsetGuaranteeResolver>();
        resolver.StartTurnIntent(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_pendingTurnIntent);
        resolver.ResolveAsync(Arg.Any<SkillToolsetGuaranteeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SkillToolsetGuaranteeResult(
                new HashSet<AgentSkill>(),
                new Dictionary<string, ToolsetSkillSource>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                false)));

        _assembler = new SkillToolsetAssembler(
            _skillCache, _retrieval, queryBuilder, expander, resolver, Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    [TearDown]
    public void TearDown() => _pendingTurnIntent.Dispose();

    private void RetrievalReturns(params RetrievalCandidate[] candidates) =>
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(candidates));

    private Task<SkillToolsetResult> Assemble(int maxTools) =>
        _assembler.AssembleAsync(
            _agent, new List<string>(), UserMessage, conversationId: "conv", currentRoute: "/route", "user-1", language: "de",
            maxTools, applyLearnedPhraseGuarantee: true, excludedSkillNames: null, pinnedSkillNames: null,
            previousTurnSkillNames: null, continuationSkillNames: null, CancellationToken.None);

    private static List<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Test]
    public async Task WithoutTruncation_TraceListsRetrievedInRankOrderAndNothingTruncated()
    {
        var result = await Assemble(AmpleCap);

        result.Trace.ShouldNotBeNull();
        result.Trace!.RetrievedNames.ShouldBe([Alpha, Bravo, Charlie]);
        Sorted(result.Trace.PreCapNames).ShouldBe(Sorted([Alpha, AlwaysOnSkill, Bravo, Charlie]));
        result.Trace.TruncatedNames.ShouldBeEmpty();
        result.Functions.Count.ShouldBe(SelectionSize);
    }

    [Test]
    public async Task WhenTheSelectionExceedsTheCap_TraceNamesTheSkillsTheCapRemoved()
    {
        var result = await Assemble(CapOneBelowSelection);

        result.Trace.ShouldNotBeNull();
        result.Trace!.PreCapNames.Count.ShouldBe(SelectionSize);
        result.Trace.TruncatedNames.ShouldBe([Charlie]);
        result.Trace.RetrievedNames.ShouldBe([Alpha, Bravo, Charlie]);
        result.Functions.Select(f => f.Name).ShouldNotContain(Charlie);
        result.Functions.Count.ShouldBe(CapOneBelowSelection);
    }

    [Test]
    public async Task TruncatedNames_AreExactlyThePreCapNamesMissingFromTheFunctions()
    {
        var result = await Assemble(TightCap);

        var offered = result.Functions.Select(f => f.Name).ToHashSet();
        Sorted(result.Trace!.TruncatedNames)
            .ShouldBe(Sorted(result.Trace.PreCapNames.Where(n => !offered.Contains(n))));
        result.Trace.TruncatedNames.Count.ShouldBe(SelectionSize - TightCap);
    }

    [Test]
    public async Task ASkillRetrievalNeverSurfaced_IsNotInTheTrace()
    {
        var result = await Assemble(AmpleCap);

        result.Trace!.RetrievedNames.ShouldNotContain(NotRetrieved);
        result.Trace.PreCapNames.ShouldNotContain(NotRetrieved);
    }

    [Test]
    public async Task WhenNothingWasRetrieved_TraceHoldsTheAlwaysOnSkillsOnly()
    {
        RetrievalReturns();

        var result = await Assemble(AmpleCap);

        result.Trace.ShouldNotBeNull();
        result.Trace!.RetrievedNames.ShouldBeEmpty();
        result.Trace.PreCapNames.ShouldBe([AlwaysOnSkill]);
        result.Trace.TruncatedNames.ShouldBeEmpty();
    }
}
