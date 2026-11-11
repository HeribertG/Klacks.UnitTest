// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for the ineligibility merge of <see cref="RecoverySnapshotBuilder"/>: availability stays hard on keyword
/// days (owner decision 2026-11-05, as in the wizards) and qualification is sharp on every day, so both sources are
/// kept in full; plus the keyword-day extraction. The relief-day tests build a whole snapshot over substituted
/// readers: a movable work of an in-group member on a day within the relief reach of the absence is gated for a
/// candidate who lacks its qualification, while a work nobody may take from its holder, a work of a borrowable
/// candidate and a work outside the reach are not asked about at all.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderIneligibleTests
{
    private static readonly Guid AgentA = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AgentB = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Shift1 = new("00000000-0000-0000-0000-000000000101");
    private static readonly Guid Shift2 = new("00000000-0000-0000-0000-000000000102");
    private static readonly DateOnly KeywordDay = new(2026, 6, 10);
    private static readonly DateOnly OpenDay = new(2026, 6, 11);

    private static readonly ScheduleCommandKeywordSet DefaultKeywords = ScheduleCommandKeywordTestFactory.Default;

    private static readonly IReadOnlyDictionary<string, Klacks.ScheduleOptimizer.Models.ScheduleCommandKeyword> DefaultKeywordMap =
        ScheduleCommandKeywordMapper.BuildMap(DefaultKeywords);

    [Test]
    public void AvailabilityVeto_OnKeywordDay_IsKept()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible([], availability);

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, KeywordDay));
    }

    [Test]
    public void AvailabilityVeto_OnDayWithoutKeyword_IsKept()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, OpenDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible([], availability);

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, OpenDay));
    }

    [Test]
    public void QualificationAndAvailability_AreBothKept()
    {
        var qualification = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };
        var availability = new[] { new IneligibleKey(AgentA, Shift2, KeywordDay), new IneligibleKey(AgentB, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(qualification, availability);

        result.ShouldBe(
            [
                new IneligibleKey(AgentA, Shift1, KeywordDay),
                new IneligibleKey(AgentA, Shift2, KeywordDay),
                new IneligibleKey(AgentB, Shift1, KeywordDay),
            ],
            ignoreOrder: true);
    }

    [Test]
    public void ExtractKeywordDays_RecognizesMappedKeywords_AndIgnoresUnmapped()
    {
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
            new() { ClientId = AgentA, CurrentDate = OpenDay, CommandKeyword = "FREE" },
            new() { ClientId = AgentB, CurrentDate = KeywordDay, CommandKeyword = "not-a-keyword" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days.ShouldContainKey((AgentA, KeywordDay));
        days.ShouldContainKey((AgentA, OpenDay));
        days.ShouldNotContainKey((AgentB, KeywordDay));
    }

    [Test]
    public void ExtractKeywordDays_KeepsTheKeywordItself_NotJustTheDay()
    {
        // The day alone only suppressed the availability veto. The category gates need to know WHICH
        // keyword governs the day, otherwise an ONLY-LATE restriction stays invisible to recovery.
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days[(AgentA, KeywordDay)].ShouldBe(ScheduleCommandKeyword.OnlyLate);
    }

    [Test]
    public void ExtractKeywordDays_SeveralCommandsOnOneDay_FreeWins()
    {
        // Otherwise the answer would depend on the order the rows came back in.
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "FREE" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days[(AgentA, KeywordDay)].ShouldBe(ScheduleCommandKeyword.Free);
    }

    private static ScheduleCommandKeyword ExtractOne(params string[] keywords)
    {
        var commands = keywords
            .Select(k => new ScheduleCommand { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = k })
            .ToList();

        return RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap)[(AgentA, KeywordDay)];
    }

    [Test]
    public void ExtractKeywordDays_NoEarlyAndNoNight_LeavesOnlyLate()
    {
        ExtractOne("-EARLY", "-NIGHT").ShouldBe(ScheduleCommandKeyword.OnlyLate);
        ExtractOne("-NIGHT", "-EARLY").ShouldBe(ScheduleCommandKeyword.OnlyLate);
    }

    [Test]
    public void ExtractKeywordDays_NotFreeAndNoNight_KeepsNoNight()
    {
        ExtractOne("-FREE", "-NIGHT").ShouldBe(ScheduleCommandKeyword.NoNight);
    }

    [Test]
    public void ExtractKeywordDays_OnlyEarlyAndOnlyLate_ClosesDay()
    {
        ExtractOne("EARLY", "LATE").ShouldBe(ScheduleCommandKeyword.Free);
    }

    [Test]
    public void ExtractKeywordDays_OnlyNotFree_StaysAKeywordDay()
    {
        ExtractOne("-FREE").ShouldBe(ScheduleCommandKeyword.NotFree);
    }

    private static readonly Guid ReceivingGroup = new("00000000-0000-0000-0000-000000000201");
    private static readonly Guid Absent = new("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid Holder = AgentA;
    private static readonly Guid Candidate = AgentB;
    private static readonly Guid Borrowable = new("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid DemandShift = Shift1;
    private static readonly Guid ReliefShift = Shift2;
    private static readonly Guid DemandWorkId = new("00000000-0000-0000-0000-000000000301");
    private static readonly Guid ReliefWorkId = new("00000000-0000-0000-0000-000000000302");
    private static readonly DateOnly AbsenceDay = new(2026, 6, 10);
    private static readonly DateOnly PeriodStart = new(2026, 6, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 6, 30);
    private static readonly TimeSpan ShiftStart = new(8, 0, 0);
    private static readonly TimeSpan ShiftEnd = new(16, 0, 0);
    private const int DaysToTheRelief = 3;

    private static ScheduleCell WorkCell(Guid clientId, DateOnly day, Guid shiftId, Guid workId, WorkLockLevel lockLevel = WorkLockLevel.None)
        => new()
        {
            EntryType = (int)ScheduleEntryType.Work,
            ClientId = clientId,
            SourceId = workId,
            EntryId = shiftId,
            EntryDate = day.ToDateTime(TimeOnly.MinValue),
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            LockLevel = (int)lockLevel,
        };

    /// <summary>
    /// Builds a snapshot for an absence of <see cref="Absent"/> on <see cref="AbsenceDay"/>. Absent, Holder and Candidate
    /// are members of the receiving group, Borrowable is not. The candidate lacks the qualification of the relief
    /// shift: the substituted matrix reports it for every slot of that shift the builder asks about, and for none else.
    /// </summary>
    private static async Task<(RecoverySnapshot Snapshot, IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> Incumbents)> BuildAsync(
        ScheduleCell reliefWork, IReadOnlySet<Guid>? clonedWorkIds = null, IReadOnlySet<Guid>? swapLockedWorkIds = null)
    {
        Guid[] inGroup = [Absent, Holder, Candidate];
        Guid[] everybody = [.. inGroup, Borrowable];
        IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> incumbents = new HashSet<(string, Guid, DateOnly)>();

        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(everybody.Select(id => new Client { Id = id, Name = id.ToString(), Type = EntityTypeEnum.Employee }).ToList());

        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        var periodHoursService = Substitute.For<IPeriodHoursService>();
        periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((PeriodStart, PeriodEnd));
        periodHoursService.GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, PeriodHoursResource>());

        var preferenceRepository = Substitute.For<IClientShiftPreferenceRepository>();
        preferenceRepository.GetScopedByClientIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScopedShiftPreference>());

        var eligibilityMatrixBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        eligibilityMatrixBuilder.BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                incumbents = call.ArgAt<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(2) ?? incumbents;
                return new EligibilityMatrix
                {
                    Ineligible = call.ArgAt<IReadOnlyCollection<EligibilitySlot>>(1)
                        .Where(slot => slot.ShiftId == ReliefShift)
                        .Select(slot => (Candidate.ToString(), slot.ShiftId, slot.Date))
                        .ToHashSet(),
                    Gaps = new Dictionary<(string AgentId, Guid ShiftId, DateOnly Date), IReadOnlyList<QualificationGap>>(),
                    QualificationInfo = new Dictionary<Guid, QualificationInfo>(),
                    ShiftNames = new Dictionary<Guid, string>(),
                };
            });

        var availabilityRepository = Substitute.For<IClientAvailabilityRepository>();
        availabilityRepository.GetByDateRange(Arg.Any<DateOnly>(), Arg.Any<DateOnly>()).Returns(new List<ClientAvailability>());

        var scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        ScheduleEntriesStub.Serve(
            scheduleEntriesService, [WorkCell(Absent, AbsenceDay, DemandShift, DemandWorkId), reliefWork]);

        var scheduleCommandRepository = Substitute.For<IScheduleCommandRepository>();
        scheduleCommandRepository.GetByClientsAndDateRangeAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand>());

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.Get(ReceivingGroup).Returns((Group?)null);

        var groupClientService = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        groupClientService.GetAllClientIdsFromGroupAndSubgroups(ReceivingGroup).Returns(inGroup.ToList());

        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords);

        var absenceRepository = Substitute.For<IAbsenceRepository>();
        absenceRepository.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());

        var sealedDayRepository = Substitute.For<ISealedDayRepository>();
        sealedDayRepository.GetLockedClientDaysAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(Guid ClientId, DateOnly Date)>());

        var membershipWindowReader = Substitute.For<IMembershipWindowReader>();
        membershipWindowReader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MembershipWindow>());

        var builder = new RecoverySnapshotBuilder(
            clientRepository, contractProvider, periodHoursService, preferenceRepository, eligibilityMatrixBuilder,
            availabilityRepository, scheduleEntriesService, scheduleCommandRepository, groupRepository, groupClientService,
            keywordProvider, absenceRepository, sealedDayRepository, membershipWindowReader,
            NullLogger<RecoverySnapshotBuilder>.Instance);

        var cloned = clonedWorkIds ?? new HashSet<Guid> { DemandWorkId, ReliefWorkId };
        var snapshot = await builder.BuildAsync(
            ReceivingGroup, Absent, [AbsenceDay], null, cloned, cloned, swapLockedWorkIds ?? new HashSet<Guid>(),
            CancellationToken.None);
        return (snapshot, incumbents);
    }

    [Test]
    public async Task AMovableWorkOfAMemberWithinTheReliefReach_GatesTheCandidateWhoLacksItsQualification()
    {
        // Arrange
        var reliefDay = AbsenceDay.AddDays(DaysToTheRelief);

        // Act
        var (snapshot, incumbents) = await BuildAsync(WorkCell(Holder, reliefDay, ReliefShift, ReliefWorkId));

        // Assert: the slot was asked about, the unqualified candidate is gated and the holder stays its incumbent.
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeTrue();
        snapshot.IsIneligible(Holder, ReliefShift, reliefDay).ShouldBeFalse();
        snapshot.IsIneligible(Candidate, ReliefShift, AbsenceDay).ShouldBeFalse();
        incumbents.ShouldContain((Holder.ToString(), ReliefShift, reliefDay));
    }

    [TestCase(-RulesetDefaults.ReliefReachDays)]
    [TestCase(-1)]
    [TestCase(1)]
    [TestCase(RulesetDefaults.ReliefReachDays)]
    public async Task AMovableWorkOnAnyDayOfTheReliefReach_IsGated(int dayOffset)
    {
        // Arrange
        var reliefDay = AbsenceDay.AddDays(dayOffset);

        // Act
        var (snapshot, _) = await BuildAsync(WorkCell(Holder, reliefDay, ReliefShift, ReliefWorkId));

        // Assert
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeTrue();
    }

    [TestCase(-RulesetDefaults.ReliefReachDays - 1)]
    [TestCase(RulesetDefaults.ReliefReachDays + 1)]
    public async Task AMovableWorkOutsideTheReliefReach_IsNotGated(int dayOffset)
    {
        // Arrange: the day lies inside the window the snapshot reads, but no chain can take a work from there.
        var farDay = AbsenceDay.AddDays(dayOffset);

        // Act
        var (snapshot, incumbents) = await BuildAsync(WorkCell(Holder, farDay, ReliefShift, ReliefWorkId));

        // Assert
        snapshot.GetWorks(Holder, farDay).ShouldHaveSingleItem().IsLocked.ShouldBeFalse();
        snapshot.IsIneligible(Candidate, ReliefShift, farDay).ShouldBeFalse();
        incumbents.ShouldNotContain((Holder.ToString(), ReliefShift, farDay));
    }

    [Test]
    public async Task ALockedWorkWithinTheReliefReach_IsNotGated()
    {
        // Arrange
        var reliefDay = AbsenceDay.AddDays(DaysToTheRelief);

        // Act
        var (snapshot, incumbents) = await BuildAsync(
            WorkCell(Holder, reliefDay, ReliefShift, ReliefWorkId, WorkLockLevel.Confirmed));

        // Assert
        snapshot.GetWorks(Holder, reliefDay).ShouldHaveSingleItem().IsLocked.ShouldBeTrue();
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeFalse();
        incumbents.ShouldNotContain((Holder.ToString(), ReliefShift, reliefDay));
    }

    [Test]
    public async Task AWorkWithoutACloneWithinTheReliefReach_IsNotGated()
    {
        // Arrange: a work the scenario did not clone cannot be written, so no chain takes it.
        var reliefDay = AbsenceDay.AddDays(DaysToTheRelief);

        // Act
        var (snapshot, _) = await BuildAsync(
            WorkCell(Holder, reliefDay, ReliefShift, ReliefWorkId), clonedWorkIds: new HashSet<Guid> { DemandWorkId });

        // Assert
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeFalse();
    }

    [Test]
    public async Task ASwapLockedWorkWithinTheReliefReach_IsNotGated()
    {
        // Arrange
        var reliefDay = AbsenceDay.AddDays(DaysToTheRelief);

        // Act
        var (snapshot, _) = await BuildAsync(
            WorkCell(Holder, reliefDay, ReliefShift, ReliefWorkId), swapLockedWorkIds: new HashSet<Guid> { ReliefWorkId });

        // Assert
        snapshot.GetWorks(Holder, reliefDay).ShouldHaveSingleItem().IsSwapLocked.ShouldBeTrue();
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeFalse();
    }

    [Test]
    public async Task AWorkOfABorrowableCandidateWithinTheReliefReach_IsNotGated()
    {
        // Arrange: a borrowed candidate only ever ends a chain, its own works are never handed on.
        var reliefDay = AbsenceDay.AddDays(DaysToTheRelief);

        // Act
        var (snapshot, incumbents) = await BuildAsync(WorkCell(Borrowable, reliefDay, ReliefShift, ReliefWorkId));

        // Assert
        snapshot.FindAgent(Borrowable).ShouldNotBeNull().IsInGroup.ShouldBeFalse();
        snapshot.GetWorks(Borrowable, reliefDay).ShouldHaveSingleItem().IsLocked.ShouldBeFalse();
        snapshot.IsIneligible(Candidate, ReliefShift, reliefDay).ShouldBeFalse();
        incumbents.ShouldNotContain((Borrowable.ToString(), ReliefShift, reliefDay));
    }
}
