// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RevalidationOnActiveIndustriesChangedHandler: an industry switch re-validates the whole
/// unlocked real-mode work window so the error list stops showing the previous state. The contracts
/// left on an inactive industry are only reported, never migrated, and their number does not gate the
/// re-validation - the window does. A failure of the read side stays inside the handler, because the
/// setting change it reacts to is already committed.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Interfaces.Scheduling;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class RevalidationOnActiveIndustriesChangedHandlerTests
{
    private static readonly DateOnly WindowFrom = new(2026, 1, 1);
    private static readonly DateOnly WindowUntil = new(2026, 12, 31);

    private IIndustryMigrationReader _migrationReader = null!;
    private ISurchargeRecalculationScope _scope = null!;
    private IScheduleTimelineService _timelineService = null!;
    private RevalidationOnActiveIndustriesChangedHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _migrationReader = Substitute.For<IIndustryMigrationReader>();
        _scope = Substitute.For<ISurchargeRecalculationScope>();
        _timelineService = Substitute.For<IScheduleTimelineService>();

        _migrationReader.GetContractsOnInactiveIndustriesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IndustryMigrationCandidate>());

        _sut = new RevalidationOnActiveIndustriesChangedHandler(
            _migrationReader,
            _scope,
            _timelineService,
            NullLogger<RevalidationOnActiveIndustriesChangedHandler>.Instance);
    }

    [Test]
    public async Task HandleAsync_ContractsAwaitMigrationAndWorkExists_QueuesTheUnlockedRealWorkWindow()
    {
        _migrationReader.GetContractsOnInactiveIndustriesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new IndustryMigrationCandidate(
                    Guid.NewGuid(), "Contract A", Guid.NewGuid(), "Security day", IndustrySlugs.Security, 3),
            });
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkRecalculationWindow(WindowFrom, WindowUntil));

        await _sut.HandleAsync(new ActiveIndustriesChangedEvent(IndustrySlugs.Security, IndustrySlugs.Healthcare));

        _timelineService.Received(1).QueueRangeCheck(WindowFrom, WindowUntil, null);
    }

    [Test]
    public async Task HandleAsync_NoContractAwaitsMigration_StillRevalidatesTheWindow()
    {
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkRecalculationWindow(WindowFrom, WindowUntil));

        await _sut.HandleAsync(new ActiveIndustriesChangedEvent(null, IndustrySlugs.Healthcare));

        _timelineService.Received(1).QueueRangeCheck(WindowFrom, WindowUntil, null);
    }

    [Test]
    public async Task HandleAsync_NoUnlockedRealWork_DoesNotQueueAnything()
    {
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns((WorkRecalculationWindow?)null);

        await _sut.HandleAsync(new ActiveIndustriesChangedEvent(IndustrySlugs.Security, IndustrySlugs.Custom));

        _timelineService.DidNotReceiveWithAnyArgs().QueueRangeCheck(default, default, null);
    }

    [Test]
    public async Task HandleAsync_MigrationReaderThrows_SwallowsAndDoesNotQueue()
    {
        _migrationReader.GetContractsOnInactiveIndustriesAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<IndustryMigrationCandidate>>>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(
            new ActiveIndustriesChangedEvent(IndustrySlugs.Security, IndustrySlugs.Healthcare)));

        _timelineService.DidNotReceiveWithAnyArgs().QueueRangeCheck(default, default, null);
        await _scope.DidNotReceive().GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_ScopeThrows_SwallowsAndDoesNotQueue()
    {
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns<Task<WorkRecalculationWindow?>>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(
            new ActiveIndustriesChangedEvent(IndustrySlugs.Security, IndustrySlugs.Healthcare)));

        _timelineService.DidNotReceiveWithAnyArgs().QueueRangeCheck(default, default, null);
    }
}
