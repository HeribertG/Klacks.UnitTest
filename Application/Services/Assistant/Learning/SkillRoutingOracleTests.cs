// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for oracle O1. It is the loop's only judge, so the two things it must never get wrong are: it
/// asks the production toolset assembler rather than a private ranking of its own, and it asks it with
/// administrator rights and no user identity - otherwise a missing permission or a stranger's open draft
/// would decide whether a phrase counts as learned.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillRoutingOracleTests
{
    private static readonly Agent DefaultAgent = new() { Id = Guid.NewGuid(), Name = "Klacksy" };

    private IAgentRepository _agents = null!;
    private ISkillToolsetAssembler _assembler = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private SkillRoutingOracle _oracle = null!;

    [SetUp]
    public void SetUp()
    {
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(DefaultAgent);
        _assembler = Substitute.For<ISkillToolsetAssembler>();
        GivenToolset();
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        GivenReachable();

        _oracle = new SkillRoutingOracle(
            _agents, _assembler, _retrieval, Substitute.For<ILogger<SkillRoutingOracle>>());
    }

    private void GivenToolset(params string[] names) =>
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(),
                Arg.Any<List<string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult
            {
                Functions = names.Select(name => new LLMFunction { Name = name }).ToList()
            });

    [Test]
    public async Task ATargetInTheAssembledToolset_IsFound()
    {
        GivenToolset("list_clients", "revenue_per_client");

        var probe = await _oracle.ProbeAsync("umsatz pro kunde", "de", "revenue_per_client");

        probe.TargetFound.ShouldBeTrue();
        probe.TopSkills.ShouldBe(["list_clients", "revenue_per_client"]);
    }

    [Test]
    public async Task ATargetOutsideTheToolset_IsNotFoundButTheOfferedNamesAreReported()
    {
        GivenToolset("list_clients");

        var probe = await _oracle.ProbeAsync("umsatz pro kunde", "de", "revenue_per_client");

        probe.TargetFound.ShouldBeFalse();
        probe.TopSkills.ShouldBe(["list_clients"]);
    }

    // The classifier needs the candidate list before any target is known, so an empty target may not be
    // an error - it just means "assemble and tell me what you offered".
    [Test]
    public async Task WithoutATarget_TheProbeStillReportsWhatWasOffered()
    {
        GivenToolset("list_clients", "list_absences");

        var probe = await _oracle.ProbeAsync("umsatz pro kunde", "de", string.Empty);

        probe.TargetFound.ShouldBeFalse();
        probe.TopSkills.Count.ShouldBe(2);
    }

    // The learned-phrase guarantee is switched off here, and that argument is the load-bearing one:
    // PhraseLearner probes with the wording it has just written, so a guarantee keyed on that wording
    // would make every generated phrase reach its own target and leave O1 with nothing left to judge.
    [Test]
    public async Task TheProbeRunsAsAdministratorWithoutAUserIdentityOrTheLearnedPhraseGuarantee()
    {
        await _oracle.ProbeAsync("umsatz pro kunde", "de", "revenue_per_client");

        await _assembler.Received(1).AssembleAsync(
            DefaultAgent,
            Arg.Is<List<string>>(rights => rights.Contains(Roles.Admin)),
            "umsatz pro kunde",
            null,
            null,
            Guid.Empty.ToString(),
            "de",
            SkillLearningDefaults.RoutingProbeTopK,
            false,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutADefaultAgent_NothingIsAssembled()
    {
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        var probe = await _oracle.ProbeAsync("umsatz pro kunde", "de", "revenue_per_client");

        probe.TargetFound.ShouldBeFalse();
        await _assembler.DidNotReceive().AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GoldenCasesThatStillRoute_AreNotReported()
    {
        GivenToolset("revenue_per_client");

        var failing = await _oracle.FindFailingGoldenCasesAsync(
            [GoldenCase("umsatz pro kunde", "revenue_per_client")]);

        failing.ShouldBeEmpty();
    }

    [Test]
    public async Task AGoldenCaseThatNoLongerRoutes_IsReportedWithItsQueryAndTarget()
    {
        GivenToolset("list_clients");

        var failing = await _oracle.FindFailingGoldenCasesAsync(
            [GoldenCase("umsatz pro kunde", "revenue_per_client")]);

        var message = failing.ShouldHaveSingleItem();
        message.ShouldContain("umsatz pro kunde");
        message.ShouldContain("revenue_per_client");
    }

    private static SkillLearningGoldenCase GoldenCase(string query, string expected) =>
        new() { Id = Guid.NewGuid(), Query = query, Locale = "de", ExpectedSourceId = expected };

    private void GivenReachable(params string[] names) =>
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(
                [.. names.Select(name => new RetrievalCandidate(
                    new KnowledgeEntry
                    {
                        Id = Guid.NewGuid(),
                        Kind = KnowledgeEntryKind.Skill,
                        SourceId = name,
                        Text = $"{name}. A skill."
                    },
                    0.9))]));

    /// <summary>
    /// The reachable list must come from the RAW retrieval, not from a second assembly: assembly is what
    /// cuts the candidate list down to DefaultTopK, and this method exists to see past that cut.
    /// </summary>
    [Test]
    public async Task TheReachableListComesFromRetrievalAtTheFullRerankerDepth()
    {
        GivenReachable("list_clients", "set_client_availability");

        var reachable = await _oracle.ListReachableSkillsAsync("kunde erreichbar");

        reachable.ShouldBe(["list_clients", "set_client_availability"]);
        await _retrieval.Received(1).RetrieveAsync(
            "kunde erreichbar",
            Arg.Is<IReadOnlyCollection<string>>(rights => rights.Contains(Roles.Admin)),
            true,
            KnowledgeIndexConstants.MaxRerankerCandidates,
            null,
            Arg.Any<CancellationToken>(),
            KnowledgeEntryKind.Skill);
    }

    // Degrading to an empty list restores exactly the behaviour this method replaced - the classifier then
    // sees only what was offered. Taking the whole learning round down over it would be worse.
    [Test]
    public async Task WhenRetrievalFails_TheReachableListIsEmptyRatherThanAnError()
    {
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns<RetrievalResult>(_ => throw new InvalidOperationException("embedding backend down"));

        (await _oracle.ListReachableSkillsAsync("kunde erreichbar")).ShouldBeEmpty();
    }

    [Test]
    public async Task AnEmptyUtterance_RetrievesNothingAtAll()
    {
        (await _oracle.ListReachableSkillsAsync("   ")).ShouldBeEmpty();

        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default);
    }
}
