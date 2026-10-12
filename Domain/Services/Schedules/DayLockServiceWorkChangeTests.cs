// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Schedules;

/// <summary>
/// The work-change guard checks the day the schedule shows the change on, for the work's employee and the replacement
/// employee: the post-midnight end of a night shift is checked on the following day. Scenario writes stay exempt. The
/// delete guard checks scenario works too.
/// </summary>
[TestFixture]
public sealed class DayLockServiceWorkChangeTests
{
    private static readonly DateOnly Day = new(2026, 5, 11);
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid ReplacerId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private ISealedDayRepository _repository = null!;
    private IShiftRepository _shiftRepository = null!;
    private DayLockService _sut = null!;
    private List<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>> _queried = null!;

    [SetUp]
    public void SetUp()
    {
        _queried = [];
        _repository = Substitute.For<ISealedDayRepository>();
        _repository.GetLockedPairsAsync(
                Arg.Do<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(p => _queried.Add(p)), Arg.Any<CancellationToken>())
            .Returns([]);
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId, StartShift = new TimeOnly(22, 0), EndShift = new TimeOnly(6, 0)
        });
        _sut = new DayLockService(_repository, _shiftRepository);
    }

    [Test]
    public async Task ReplacementAtTheEndOfANightShift_IsCheckedOnTheFollowingDay_ForBothEmployees()
    {
        await _sut.EnsureWorkChangeNotLockedAsync(NightWork(null), Replacement(WorkChangeType.ReplacementEnd, 2m));

        _queried.ShouldHaveSingleItem().ShouldBe(new[] { (Day.AddDays(1), WorkerId), (Day.AddDays(1), ReplacerId) });
    }

    [Test]
    public async Task ReplacementAtTheStartOfANightShift_IsCheckedOnTheWorksDay()
    {
        await _sut.EnsureWorkChangeNotLockedAsync(NightWork(null), Replacement(WorkChangeType.ReplacementStart, 2m));

        _queried.ShouldHaveSingleItem().ShouldBe(new[] { (Day, WorkerId), (Day, ReplacerId) });
    }

    [Test]
    public async Task SealedFollowingDay_RefusesTheReplacement()
    {
        _repository.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([(Day.AddDays(1), ReplacerId)]);

        await Should.ThrowAsync<InvalidRequestException>(
            () => _sut.EnsureWorkChangeNotLockedAsync(NightWork(null), Replacement(WorkChangeType.ReplacementEnd, 2m)));
    }

    [Test]
    public async Task ScenarioWork_IsExempt()
    {
        await _sut.EnsureWorkChangeNotLockedAsync(NightWork(Guid.NewGuid()), Replacement(WorkChangeType.ReplacementEnd, 2m));

        _queried.ShouldBeEmpty();
    }

    [Test]
    public async Task EnsureDeletable_ChecksEveryWork_AlsoWhenItIsAScenarioWork()
    {
        _repository.GetLockedPairsAsync(Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns([(Day, WorkerId)]);

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.EnsureDeletableAsync([(Day, WorkerId)]));
    }

    private static Work NightWork(Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = WorkerId,
        ShiftId = ShiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(22, 0),
        EndTime = new TimeOnly(6, 0),
        AnalyseToken = token
    };

    private static WorkChange Replacement(WorkChangeType type, decimal hours) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        ChangeTime = hours,
        ReplaceClientId = ReplacerId
    };
}
