// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GetRunningEscalationChainsQueryHandler — verifies the field-for-field mapping from
/// EscalationChain/EscalationStage to EscalationChainSummaryResource/EscalationStageSummaryResource,
/// the per-requesting-user CanAcknowledge computation, and that stages come back ordered by Rank
/// regardless of storage order, plus the display context of the intervention list: group name, finding kind,
/// the scenario id only while the scenario is Active, the wake instant of a registered chain and the start
/// instant (first notification, else the wake instant, else the creation time).
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.Escalation;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetRunningEscalationChainsQueryHandlerTests
{
    private IEscalationChainRepository _chainRepository = null!;
    private IEscalationChainVisibilityFilter _visibilityFilter = null!;
    private IUserService _userService = null!;
    private GetRunningEscalationChainsQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainRepository = Substitute.For<IEscalationChainRepository>();
        _visibilityFilter = Substitute.For<IEscalationChainVisibilityFilter>();
        _visibilityFilter.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<EscalationChain>>().ToList()));
        _userService = Substitute.For<IUserService>();
        _userService.GetRights().Returns(new List<string>());
        UseDisplayContext(new EscalationChainDisplayContext(
            new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, AnalyseScenarioStatus>()));
        _sut = new GetRunningEscalationChainsQueryHandler(_chainRepository, _visibilityFilter, _userService);
    }

    [Test]
    public async Task Handle_ReturnsOnlyChainsTheVisibilityFilterKeeps()
    {
        var visible = new EscalationChain { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() };
        var hidden = new EscalationChain { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() };
        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { visible, hidden });
        _visibilityFilter.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), "planner-a", Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { visible });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("planner-a"), CancellationToken.None);

        Assert.That(result.Select(r => r.Id), Is.EqualTo(new[] { visible.Id }));
    }

    [Test]
    public async Task Handle_CanCancel_FollowsTheCancelPolicy()
    {
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Stages = new List<EscalationStage> { new() { Rank = 1, UserId = "holder", Status = EscalationStageStatus.Notified } }
        };
        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { chain });

        var holder = await _sut.Handle(new GetRunningEscalationChainsQuery("holder"), CancellationToken.None);
        var stranger = await _sut.Handle(new GetRunningEscalationChainsQuery("stranger"), CancellationToken.None);
        _userService.GetRights().Returns(new List<string> { Klacks.Api.Domain.Constants.Roles.Authorised });
        var supervisor = await _sut.Handle(new GetRunningEscalationChainsQuery("stranger"), CancellationToken.None);

        Assert.That(holder[0].CanCancel, Is.True);
        Assert.That(stranger[0].CanCancel, Is.False);
        Assert.That(supervisor[0].CanCancel, Is.True);
    }

    [Test]
    public async Task Handle_MapsChainAndStageFieldsFaithfully()
    {
        var chainId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var shiftStartUtc = new DateTime(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc);
        var deadlineUtc = new DateTime(2026, 8, 16, 4, 0, 0, DateTimeKind.Utc);
        var notifiedAtUtc = new DateTime(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);
        var dueAtUtc = new DateTime(2026, 8, 16, 3, 20, 0, DateTimeKind.Utc);
        var respondedAtUtc = new DateTime(2026, 8, 16, 3, 5, 0, DateTimeKind.Utc);

        var chain = new EscalationChain
        {
            Id = chainId,
            WorkId = workId,
            AbsentClientName = "Absent Employee",
            ShiftStartUtc = shiftStartUtc,
            DeadlineUtc = deadlineUtc,
            Stages = new List<EscalationStage>
            {
                new()
                {
                    Rank = 1,
                    UserId = "planner-a",
                    UserDisplayName = "Planner A",
                    Status = EscalationStageStatus.Acknowledged,
                    NotifiedAtUtc = notifiedAtUtc,
                    DueAtUtc = dueAtUtc,
                    RespondedAtUtc = respondedAtUtc
                },
                new()
                {
                    Rank = 2,
                    UserId = "planner-b",
                    UserDisplayName = "Planner B",
                    Status = EscalationStageStatus.Cancelled
                }
            }
        };

        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { chain });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone-else"), CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        var resource = result[0];
        Assert.That(resource.Id, Is.EqualTo(chainId));
        Assert.That(resource.WorkId, Is.EqualTo(workId));
        Assert.That(resource.AbsentClientName, Is.EqualTo("Absent Employee"));
        Assert.That(resource.ShiftStartUtc, Is.EqualTo(shiftStartUtc));
        Assert.That(resource.DeadlineUtc, Is.EqualTo(deadlineUtc));
        Assert.That(resource.Stages, Has.Count.EqualTo(2));

        var stageA = resource.Stages[0];
        Assert.That(stageA.Rank, Is.EqualTo(1));
        Assert.That(stageA.UserId, Is.EqualTo("planner-a"));
        Assert.That(stageA.UserDisplayName, Is.EqualTo("Planner A"));
        Assert.That(stageA.Status, Is.EqualTo(EscalationStageStatus.Acknowledged.ToString()));
        Assert.That(stageA.NotifiedAtUtc, Is.EqualTo(notifiedAtUtc));
        Assert.That(stageA.DueAtUtc, Is.EqualTo(dueAtUtc));
        Assert.That(stageA.RespondedAtUtc, Is.EqualTo(respondedAtUtc));

        var stageB = resource.Stages[1];
        Assert.That(stageB.Rank, Is.EqualTo(2));
        Assert.That(stageB.UserId, Is.EqualTo("planner-b"));
        Assert.That(stageB.Status, Is.EqualTo(EscalationStageStatus.Cancelled.ToString()));
        Assert.That(stageB.NotifiedAtUtc, Is.Null);
        Assert.That(stageB.DueAtUtc, Is.Null);
        Assert.That(stageB.RespondedAtUtc, Is.Null);
    }

    [Test]
    public async Task Handle_CanAcknowledge_TrueOnlyForUserWithNotifiedStageOnThisChain()
    {
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Stages = new List<EscalationStage>
            {
                new() { Rank = 1, UserId = "planner-x", Status = EscalationStageStatus.Notified },
                new() { Rank = 2, UserId = "planner-y", Status = EscalationStageStatus.Pending }
            }
        };

        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { chain });

        var resultForHolder = await _sut.Handle(new GetRunningEscalationChainsQuery("planner-x"), CancellationToken.None);
        var resultForOther = await _sut.Handle(new GetRunningEscalationChainsQuery("planner-y"), CancellationToken.None);

        Assert.That(resultForHolder[0].CanAcknowledge, Is.True, "planner-x holds the Notified stage on this chain.");
        Assert.That(resultForOther[0].CanAcknowledge, Is.False, "planner-y's stage is only Pending, not Notified.");
    }

    [Test]
    public async Task Handle_OrdersStagesByRank_RegardlessOfStorageOrder()
    {
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            WorkId = Guid.NewGuid(),
            Stages = new List<EscalationStage>
            {
                new() { Rank = 3, UserId = "planner-c" },
                new() { Rank = 1, UserId = "planner-a" },
                new() { Rank = 2, UserId = "planner-b" }
            }
        };

        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { chain });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("nobody"), CancellationToken.None);

        Assert.That(result[0].Stages.Select(s => s.UserId), Is.EqualTo(new[] { "planner-a", "planner-b", "planner-c" }));
    }

    [Test]
    public async Task Handle_MapsTheDisplayContext_GroupNameFindingKindAndGroupId()
    {
        var groupId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();
        var chain = new EscalationChain { Id = Guid.NewGuid(), GroupId = groupId, ConditionId = conditionId };
        var unknownGroupChain = new EscalationChain { Id = Guid.NewGuid(), GroupId = Guid.NewGuid() };
        UseChains(chain, unknownGroupChain);
        UseDisplayContext(new EscalationChainDisplayContext(
            new Dictionary<Guid, string> { [groupId] = "Ward 3" },
            new Dictionary<Guid, string> { [conditionId] = "empty_container" },
            new Dictionary<Guid, AnalyseScenarioStatus>()));

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        var known = result.Single(r => r.Id == chain.Id);
        Assert.That(known.GroupName, Is.EqualTo("Ward 3"));
        Assert.That(known.FindingKind, Is.EqualTo("empty_container"));
        Assert.That(known.GroupId, Is.EqualTo(groupId));
        var unknown = result.Single(r => r.Id == unknownGroupChain.Id);
        Assert.That(unknown.GroupName, Is.Null);
        Assert.That(unknown.FindingKind, Is.Null);
    }

    [Test]
    public async Task Handle_AsksTheDisplayContextWithTheDistinctIdsOfTheVisibleChainsOnly()
    {
        var groupId = Guid.NewGuid();
        var scenarioId = Guid.NewGuid();
        var visibleA = new EscalationChain { Id = Guid.NewGuid(), GroupId = groupId, ScenarioId = scenarioId };
        var visibleB = new EscalationChain { Id = Guid.NewGuid(), GroupId = groupId };
        var hidden = new EscalationChain { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), ConditionId = Guid.NewGuid() };
        UseChains(visibleA, visibleB, hidden);
        _visibilityFilter.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), "planner-a", Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { visibleA, visibleB });

        await _sut.Handle(new GetRunningEscalationChainsQuery("planner-a"), CancellationToken.None);

        await _chainRepository.Received(1).GetDisplayContextAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { groupId })),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { scenarioId })),
            Arg.Any<CancellationToken>());
    }

    [TestCase(AnalyseScenarioStatus.Active, true)]
    [TestCase(AnalyseScenarioStatus.Accepted, false)]
    [TestCase(AnalyseScenarioStatus.Rejected, false)]
    [TestCase(AnalyseScenarioStatus.Superseded, false)]
    public async Task Handle_ScenarioId_OnlyWhileTheScenarioIsActive(AnalyseScenarioStatus status, bool expectId)
    {
        var scenarioId = Guid.NewGuid();
        UseChains(new EscalationChain { Id = Guid.NewGuid(), ScenarioId = scenarioId });
        UseDisplayContext(new EscalationChainDisplayContext(
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, AnalyseScenarioStatus> { [scenarioId] = status }));

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        Assert.That(result[0].ScenarioId, expectId ? Is.EqualTo(scenarioId) : Is.Null);
    }

    [Test]
    public async Task Handle_ScenarioId_IsNullWhenTheScenarioIsGone()
    {
        UseChains(new EscalationChain { Id = Guid.NewGuid(), ScenarioId = Guid.NewGuid() });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        Assert.That(result[0].ScenarioId, Is.Null);
    }

    [Test]
    public async Task Handle_StartedAtUtc_IsTheEarliestNotification_WhenAStageWasWoken()
    {
        var firstNotifiedUtc = new DateTime(2026, 10, 3, 6, 0, 0, DateTimeKind.Utc);
        UseChains(new EscalationChain
        {
            Id = Guid.NewGuid(),
            CreateTime = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            WakeNotBeforeUtc = new DateTime(2026, 10, 3, 5, 0, 0, DateTimeKind.Utc),
            Stages = new List<EscalationStage>
            {
                new() { Rank = 1, UserId = "a", Status = EscalationStageStatus.Expired, NotifiedAtUtc = firstNotifiedUtc.AddMinutes(30) },
                new() { Rank = 2, UserId = "b", Status = EscalationStageStatus.Notified, NotifiedAtUtc = firstNotifiedUtc },
                new() { Rank = 3, UserId = "c", Status = EscalationStageStatus.Pending }
            }
        });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        Assert.That(result[0].StartedAtUtc, Is.EqualTo(firstNotifiedUtc));
    }

    [Test]
    public async Task Handle_RegisteredChainNotYetWoken_StartsAtItsWakeInstantAndReportsIt()
    {
        var wakeUtc = new DateTime(2026, 11, 2, 4, 30, 0, DateTimeKind.Utc);
        UseChains(new EscalationChain
        {
            Id = Guid.NewGuid(),
            CreateTime = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
            WakeNotBeforeUtc = wakeUtc,
            Stages = new List<EscalationStage> { new() { Rank = 1, UserId = "a", Status = EscalationStageStatus.Pending } }
        });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        Assert.That(result[0].WakeNotBeforeUtc, Is.EqualTo(wakeUtc));
        Assert.That(result[0].StartedAtUtc, Is.EqualTo(wakeUtc));
    }

    [Test]
    public async Task Handle_ChainWokenAtCreation_StartsAtItsCreationTimeWithoutWakeInstant()
    {
        var createdUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
        UseChains(new EscalationChain
        {
            Id = Guid.NewGuid(),
            CreateTime = createdUtc,
            Stages = new List<EscalationStage> { new() { Rank = 1, UserId = "a", Status = EscalationStageStatus.Pending } }
        });

        var result = await _sut.Handle(new GetRunningEscalationChainsQuery("someone"), CancellationToken.None);

        Assert.That(result[0].StartedAtUtc, Is.EqualTo(createdUtc));
        Assert.That(result[0].WakeNotBeforeUtc, Is.Null);
    }

    private void UseChains(params EscalationChain[] chains) =>
        _chainRepository.GetRunningChainsWithStagesAsync(Arg.Any<CancellationToken>())
            .Returns(chains.ToList());

    private void UseDisplayContext(EscalationChainDisplayContext context) =>
        _chainRepository.GetDisplayContextAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(context);
}
