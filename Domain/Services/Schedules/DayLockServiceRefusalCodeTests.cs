// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// Every day-lock refusal carries the stable code DAY_SEALED and the refused date next to the unchanged English text,
/// on all four throwing entry points; it stays an InvalidRequestException (400). A scenario write is still exempt.
/// </summary>
[TestFixture]
public sealed class DayLockServiceRefusalCodeTests
{
    private static readonly DateOnly Day = new(2026, 5, 11);
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private const string SealedText = "Day 2026-05-11 is sealed and cannot be modified.";

    private ISealedDayRepository _repository = null!;
    private DayLockService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISealedDayRepository>();
        _repository.IsDayLockedAsync(Day, ClientId, Arg.Any<CancellationToken>()).Returns(true);
        _repository.IsDayLockedForShiftAsync(Day, ShiftId, Arg.Any<CancellationToken>()).Returns(true);
        _repository.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([(Day, ClientId)]);
        _sut = new DayLockService(_repository, Substitute.For<IShiftRepository>());
    }

    [Test]
    public async Task EnsureNotLockedAsync_SealedDay_CarriesTheCodeAndTheDate()
        => AssertSealed(await Should.ThrowAsync<WorkBookingRefusedException>(
            () => _sut.EnsureNotLockedAsync(Day, ClientId, null)));

    [Test]
    public async Task EnsureNotLockedForShiftAsync_SealedDay_CarriesTheCodeAndTheDate()
        => AssertSealed(await Should.ThrowAsync<WorkBookingRefusedException>(
            () => _sut.EnsureNotLockedForShiftAsync(Day, ShiftId, null)));

    [Test]
    public async Task EnsureNoneLockedAsync_SealedPair_CarriesTheCodeAndTheDate()
        => AssertSealed(await Should.ThrowAsync<WorkBookingRefusedException>(
            () => _sut.EnsureNoneLockedAsync([(Day, ClientId, null)])));

    [Test]
    public async Task EnsureDeletableAsync_SealedPair_CarriesTheCodeAndTheDate()
        => AssertSealed(await Should.ThrowAsync<WorkBookingRefusedException>(
            () => _sut.EnsureDeletableAsync([(Day, ClientId)])));

    [Test]
    public async Task EnsureNotLockedAsync_ScenarioWrite_IsStillExempt()
    {
        await Should.NotThrowAsync(() => _sut.EnsureNotLockedAsync(Day, ClientId, Guid.NewGuid()));
    }

    [Test]
    public async Task EnsureNotLockedAsync_OpenDay_Passes()
    {
        await Should.NotThrowAsync(() => _sut.EnsureNotLockedAsync(Day.AddDays(1), ClientId, null));
    }

    private static void AssertSealed(WorkBookingRefusedException refusal)
    {
        refusal.ShouldBeAssignableTo<InvalidRequestException>();
        refusal.Message.ShouldBe(SealedText);
        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        refusal.Details.Keys.ShouldBe([WorkBookingRefusalCodes.DateField]);
        refusal.Details[WorkBookingRefusalCodes.DateField].ShouldBe(Day);
    }
}
