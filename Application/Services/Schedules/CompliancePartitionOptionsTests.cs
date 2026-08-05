// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// A swap consists of a relocation hop and a cover hop. Partitioning them as independent rows let one
/// half through while the other was blocked - the plan ended up in a state the engine never proposed.
/// </summary>
[TestFixture]
public sealed class CompliancePartitionOptionsTests
{
    private static readonly DateOnly Day = new(2026, 4, 20);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private IPreCommitConflictChecker _checker = null!;
    private ISupervisorOverrideAuthorizer _authorizer = null!;
    private CompliancePartitionService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _checker = Substitute.For<IPreCommitConflictChecker>();
        _authorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _authorizer.IsAuthorizedAsync(Arg.Any<bool>()).Returns(false);
        _sut = new CompliancePartitionService(
            _checker,
            _authorizer,
            Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            NullLogger<CompliancePartitionService>.Instance);
    }

    private static PlannedOption Swap(Guid coverAgent, Guid relocatedAgent) => new(
        [
            new PlannedWorkRow(coverAgent, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), Guid.NewGuid()),
            new PlannedWorkRow(relocatedAgent, Day, new TimeOnly(16, 0), new TimeOnly(22, 0), Guid.NewGuid()),
        ],
        [
            new PlannedRemovalRow(relocatedAgent, Day, new TimeOnly(8, 0), new TimeOnly(16, 0)),
        ]);

    /// <summary>A block that a supervisor may override (Block-mode escalation, not a structural error).</summary>
    private static PreCommitCheckResult Blocking(Guid clientId) => new(
    [
        new ScheduleValidationNotificationDto
        {
            ClientId = clientId,
            Type = ScheduleValidationType.Error,
            Comment = "MaxConsecutiveDays exceeded",
            CommentParams = new Dictionary<string, string> { ["enforcementRule"] = "maxConsecutiveDays" },
        },
    ]);

    private void SetCheck(PreCommitCheckResult result)
        => _checker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Test]
    public async Task NoConflicts_AcceptsEveryOption()
    {
        SetCheck(PreCommitCheckResult.Empty);

        var result = await _sut.PartitionOptionsAsync(
            [Swap(AgentA, AgentB), Swap(AgentB, AgentA)], null, false, CancellationToken.None);

        result.AcceptedOptionIndexes.ShouldBe(new[] { 0, 1 });
        result.BlockedOptions.ShouldBeEmpty();
    }

    [Test]
    public async Task BlockingConflict_RefusesTheWholeOption_NotJustOneHalf()
    {
        SetCheck(Blocking(AgentB));

        var result = await _sut.PartitionOptionsAsync(
            [Swap(AgentA, AgentB)], null, false, CancellationToken.None);

        // Either both halves land or neither does - a half-applied swap is worse than no swap.
        result.AcceptedOptionIndexes.ShouldBeEmpty();
        result.BlockedOptions.Count.ShouldBe(1);
        result.BlockedOptions[0].Index.ShouldBe(0);
        result.BlockedOptions[0].ReasonKey.ShouldBe("MaxConsecutiveDays exceeded");
    }

    [Test]
    public async Task OptionOfAnUnaffectedClient_IsTakenWholesale()
    {
        var unaffected = Guid.NewGuid();
        SetCheck(Blocking(AgentB));

        var result = await _sut.PartitionOptionsAsync(
            [new PlannedOption([new PlannedWorkRow(unaffected, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), Guid.NewGuid())], []),
             Swap(AgentA, AgentB)],
            null, false, CancellationToken.None);

        result.AcceptedOptionIndexes.ShouldContain(0);
        result.BlockedOptions.Select(b => b.Index).ShouldContain(1);
    }

    [Test]
    public async Task AuthorizedOverride_LetsTheOptionThrough()
    {
        SetCheck(Blocking(AgentB));
        _authorizer.IsAuthorizedAsync(true).Returns(true);

        var result = await _sut.PartitionOptionsAsync(
            [Swap(AgentA, AgentB)], null, overrideBlockRequested: true, CancellationToken.None);

        result.AcceptedOptionIndexes.ShouldBe(new[] { 0 });
        result.OverrideApplied.ShouldBeTrue();
    }

    [Test]
    public async Task EmptyInput_IsANoOp()
    {
        var result = await _sut.PartitionOptionsAsync([], null, false, CancellationToken.None);

        result.AcceptedOptionIndexes.ShouldBeEmpty();
        result.BlockedOptions.ShouldBeEmpty();
        await _checker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default, default);
    }

    [Test]
    public async Task TheCheckSeesTheRemovals_NotJustTheAdditions()
    {
        SetCheck(PreCommitCheckResult.Empty);

        await _sut.PartitionOptionsAsync([Swap(AgentA, AgentB)], null, false, CancellationToken.None);

        // Without the removals the relocation half looks like a plain double booking.
        await _checker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(r => r.Count == 2),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(r => r.Count == 1),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }
}
