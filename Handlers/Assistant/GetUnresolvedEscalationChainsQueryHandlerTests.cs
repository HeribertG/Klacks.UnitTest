// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GetUnresolvedEscalationChainsQueryHandler: the one-week window is anchored on the TimeProvider,
/// visibility goes through the ended-chain rule (FilterVisibleEndedAsync), the free-text cancel reason reaches only an
/// administrator, a supervisor or the canceller (everybody else gets null plus CancelReasonHidden), and the scenario id
/// is handed out only while the scenario is still Active.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetUnresolvedEscalationChainsQueryHandlerTests
{
    private const string CallerId = "planner-caller";
    private const string CancellerId = "planner-canceller";
    private const string Reason = "covered by phone";

    private static readonly DateTime NowUtc = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private IEscalationChainRepository _chainRepository = null!;
    private IEscalationChainVisibilityFilter _visibilityFilter = null!;
    private IUserService _userService = null!;
    private GetUnresolvedEscalationChainsQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainRepository = Substitute.For<IEscalationChainRepository>();
        _chainRepository.GetUnresolvedChainsSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain>());
        UseDisplayContext(new Dictionary<Guid, AnalyseScenarioStatus>());

        _visibilityFilter = Substitute.For<IEscalationChainVisibilityFilter>();
        _visibilityFilter.FilterVisibleEndedAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<EscalationChain>>().ToList()));

        _userService = Substitute.For<IUserService>();
        _userService.IsAdmin().Returns(false);
        _userService.GetRights().Returns(new List<string>());

        _sut = new GetUnresolvedEscalationChainsQueryHandler(
            _chainRepository, _visibilityFilter, _userService, new SettableTimeProvider(NowUtc));
    }

    [Test]
    public async Task Handle_AsksTheRepositoryForTheLastSevenDays()
    {
        await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        await _chainRepository.Received(1).GetUnresolvedChainsSinceAsync(NowUtc.AddDays(-7), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UsesTheEndedChainVisibilityRule_NotTheRunningOne()
    {
        var visible = CancelledChain();
        var hidden = CancelledChain();
        UseChains(visible, hidden);
        _visibilityFilter.FilterVisibleEndedAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), CallerId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { visible });

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { visible.Id }));
        await _visibilityFilter.DidNotReceiveWithAnyArgs().FilterVisibleAsync(default!, default!, default);
    }

    [Test]
    public async Task Handle_Stranger_GetsNoReasonButLearnsThatOneExists()
    {
        UseChains(CancelledChain());

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].CancelReason, Is.Null);
        Assert.That(result[0].CancelReasonHidden, Is.True);
    }

    [Test]
    public async Task Handle_Admin_ReadsTheReason()
    {
        _userService.IsAdmin().Returns(true);
        UseChains(CancelledChain());

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].CancelReason, Is.EqualTo(Reason));
        Assert.That(result[0].CancelReasonHidden, Is.False);
    }

    [Test]
    public async Task Handle_Supervisor_ReadsTheReason()
    {
        _userService.GetRights().Returns(new List<string> { Roles.Authorised });
        UseChains(CancelledChain());

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].CancelReason, Is.EqualTo(Reason));
        Assert.That(result[0].CancelReasonHidden, Is.False);
    }

    [Test]
    public async Task Handle_Canceller_ReadsTheirOwnReason_CaseInsensitively()
    {
        UseChains(CancelledChain());

        var result = await _sut.Handle(
            new GetUnresolvedEscalationChainsQuery(CancellerId.ToUpperInvariant()), CancellationToken.None);

        Assert.That(result[0].CancelReason, Is.EqualTo(Reason));
        Assert.That(result[0].CancelReasonHidden, Is.False);
    }

    [Test]
    public async Task Handle_ExhaustedChainWithoutReason_IsNotFlaggedAsHidden()
    {
        var exhaustedAtUtc = NowUtc.AddDays(-1);
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            Purpose = EscalationChainPurpose.AbsenceCoverage,
            Status = EscalationChainStatus.Exhausted,
            ExhaustedAtUtc = exhaustedAtUtc
        };
        UseChains(chain);

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].CancelReason, Is.Null);
        Assert.That(result[0].CancelReasonHidden, Is.False);
        Assert.That(result[0].Status, Is.EqualTo(nameof(EscalationChainStatus.Exhausted)));
        Assert.That(result[0].EndedAtUtc, Is.EqualTo(exhaustedAtUtc));
    }

    [Test]
    public async Task Handle_MapsTheCancelledChain_AndCountsOnlyTheStagesThatWereWoken()
    {
        var chain = CancelledChain();
        chain.Stages = new List<EscalationStage>
        {
            new() { Rank = 1, UserId = "a", Status = EscalationStageStatus.Expired, NotifiedAtUtc = NowUtc.AddDays(-2) },
            new() { Rank = 2, UserId = "b", Status = EscalationStageStatus.Cancelled, NotifiedAtUtc = NowUtc.AddDays(-2) },
            new() { Rank = 3, UserId = "c", Status = EscalationStageStatus.Cancelled }
        };
        UseChains(chain);

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        var resource = result.Single();
        Assert.That(resource.NotifiedStageCount, Is.EqualTo(2));
        Assert.That(resource.Status, Is.EqualTo(nameof(EscalationChainStatus.Cancelled)));
        Assert.That(resource.Purpose, Is.EqualTo(nameof(EscalationChainPurpose.AbsenceCoverage)));
        Assert.That(resource.EndedAtUtc, Is.EqualTo(chain.CancelledAtUtc));
        Assert.That(resource.CancelledByUserName, Is.EqualTo("Canceller"));
        Assert.That(resource.AbsentClientName, Is.EqualTo("Absent Employee"));
    }

    [TestCase(AnalyseScenarioStatus.Active, true)]
    [TestCase(AnalyseScenarioStatus.Accepted, false)]
    [TestCase(AnalyseScenarioStatus.Rejected, false)]
    [TestCase(AnalyseScenarioStatus.Superseded, false)]
    public async Task Handle_ScenarioStatusIsReported_ButTheIdOnlyWhileActive(AnalyseScenarioStatus status, bool expectId)
    {
        var chain = CancelledChain();
        chain.ScenarioId = Guid.NewGuid();
        UseChains(chain);
        UseDisplayContext(new Dictionary<Guid, AnalyseScenarioStatus> { [chain.ScenarioId.Value] = status });

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].ScenarioStatus, Is.EqualTo(status.ToString()));
        Assert.That(result[0].ScenarioId, expectId ? Is.EqualTo(chain.ScenarioId) : Is.Null);
    }

    [Test]
    public async Task Handle_DeletedScenario_HasNeitherStatusNorId()
    {
        var chain = CancelledChain();
        chain.ScenarioId = Guid.NewGuid();
        UseChains(chain);

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].ScenarioStatus, Is.Null);
        Assert.That(result[0].ScenarioId, Is.Null);
    }

    [Test]
    public async Task Handle_MapsGroupNameAndFindingKindFromTheDisplayContext()
    {
        var groupId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var chain = CancelledChain();
        chain.GroupId = groupId;
        chain.ConditionId = conditionId;
        UseChains(chain);
        _chainRepository.GetDisplayContextAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationChainDisplayContext(
                new Dictionary<Guid, string> { [groupId] = "Ward 3" },
                new Dictionary<Guid, string> { [conditionId] = "empty_container" },
                new Dictionary<Guid, AnalyseScenarioStatus>()));

        var result = await _sut.Handle(new GetUnresolvedEscalationChainsQuery(CallerId), CancellationToken.None);

        Assert.That(result[0].GroupId, Is.EqualTo(groupId));
        Assert.That(result[0].GroupName, Is.EqualTo("Ward 3"));
        Assert.That(result[0].FindingKind, Is.EqualTo("empty_container"));
    }

    private static EscalationChain CancelledChain() => new()
    {
        Id = Guid.NewGuid(),
        Purpose = EscalationChainPurpose.AbsenceCoverage,
        Status = EscalationChainStatus.Cancelled,
        AbsentClientName = "Absent Employee",
        CancelledAtUtc = NowUtc.AddDays(-1),
        CancelledByUserId = CancellerId,
        CancelledByUserName = "Canceller",
        CancelReason = Reason
    };

    private void UseChains(params EscalationChain[] chains) =>
        _chainRepository.GetUnresolvedChainsSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(chains.ToList());

    private void UseDisplayContext(Dictionary<Guid, AnalyseScenarioStatus> scenarioStatuses) =>
        _chainRepository.GetDisplayContextAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationChainDisplayContext(
                new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), scenarioStatuses));
}
