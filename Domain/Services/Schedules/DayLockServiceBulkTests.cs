// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// Applying a wizard result writes hundreds of works at once and the guard used to run two queries per
/// entry. The batch variant must keep the rule identical - scenario writes stay exempt, a single sealed
/// pair still refuses the whole batch - while touching the repository exactly once.
/// </summary>
[TestFixture]
public sealed class DayLockServiceBulkTests
{
    private static readonly DateOnly Day = new(2026, 5, 11);
    private static readonly Guid ClientId = Guid.NewGuid();

    private ISealedDayRepository _repository = null!;
    private DayLockService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISealedDayRepository>();
        _repository.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sut = new DayLockService(_repository);
    }

    [Test]
    public async Task EnsureNoneLockedAsync_NothingSealed_Passes()
    {
        await _sut.EnsureNoneLockedAsync([(Day, ClientId, null)]);

        await _repository.Received(1).GetLockedPairsAsync(
            Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureNoneLockedAsync_OneSealedPair_RefusesTheWholeBatch()
    {
        _repository.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([(Day.AddDays(1), ClientId)]);

        var act = async () => await _sut.EnsureNoneLockedAsync(
            [(Day, ClientId, null), (Day.AddDays(1), ClientId, null)]);

        var exception = await act.ShouldThrowAsync<InvalidRequestException>();
        exception.Message.ShouldContain(Day.AddDays(1).ToString("yyyy-MM-dd"));
    }

    [Test]
    public async Task EnsureNoneLockedAsync_ReportsTheEarliestSealedDate()
    {
        _repository.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([(Day.AddDays(3), ClientId), (Day.AddDays(1), ClientId)]);

        var act = async () => await _sut.EnsureNoneLockedAsync([(Day, ClientId, null)]);

        var exception = await act.ShouldThrowAsync<InvalidRequestException>();
        exception.Message.ShouldContain(Day.AddDays(1).ToString("yyyy-MM-dd"));
    }

    [Test]
    public async Task EnsureNoneLockedAsync_ScenarioWrites_AreExemptAndNeverQueried()
    {
        await _sut.EnsureNoneLockedAsync([(Day, ClientId, Guid.NewGuid())]);

        await _repository.DidNotReceiveWithAnyArgs().GetLockedPairsAsync(default!, default);
    }

    [Test]
    public async Task EnsureNoneLockedAsync_DuplicateEntries_AreQueriedOnce()
    {
        await _sut.EnsureNoneLockedAsync([(Day, ClientId, null), (Day, ClientId, null)]);

        await _repository.Received(1).GetLockedPairsAsync(
            Arg.Is<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(p => p.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureNoneLockedAsync_EmptyBatch_IsANoOp()
    {
        await _sut.EnsureNoneLockedAsync([]);

        await _repository.DidNotReceiveWithAnyArgs().GetLockedPairsAsync(default!, default);
    }
}
