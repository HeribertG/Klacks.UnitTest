// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetRunningEscalationChainsQueryHandler — verifies the field-for-field mapping from
/// EscalationChain/EscalationStage to EscalationChainSummaryResource/EscalationStageSummaryResource,
/// the per-requesting-user CanAcknowledge computation, and that stages come back ordered by Rank
/// regardless of storage order.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetRunningEscalationChainsQueryHandlerTests
{
    private IEscalationChainRepository _chainRepository = null!;
    private GetRunningEscalationChainsQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _chainRepository = Substitute.For<IEscalationChainRepository>();
        _sut = new GetRunningEscalationChainsQueryHandler(_chainRepository);
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
}
