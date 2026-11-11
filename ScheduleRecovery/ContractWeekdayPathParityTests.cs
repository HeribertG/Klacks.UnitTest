// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Parity of the weekday rule of a contract over the four paths that judge it, each asked for the same contract on all
/// seven weekdays: Wizard 1 (the contract days of WizardAgentSnapshotBuilder), the harmonizer (the contract days of
/// HarmonizerContextBuilder), the absence cover (the availability of RecoverySnapshotBuilder) and the list of
/// alternatives (IsContractDayOff of FindReplacementQueryHandler, the opposite of "works on that day"). Compares the
/// results of the paths with the expected week and with each other.
/// </summary>
[TestFixture]
public sealed class ContractWeekdayPathParityTests
{
    private static readonly Guid PersonId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateOnly Sunday = new(2026, 6, 7);
    private static readonly TimeOnly SlotStart = new(8, 0);
    private static readonly TimeOnly SlotEnd = new(16, 0);

    private static readonly EffectiveContractData MixedWeek = new()
    {
        WorkOnMonday = true,
        WorkOnTuesday = false,
        WorkOnWednesday = true,
        WorkOnThursday = false,
        WorkOnFriday = false,
        WorkOnSaturday = true,
        WorkOnSunday = false,
    };

    private static readonly bool[] MixedWeekWorks = [true, false, true, false, false, true, false];
    private static readonly bool[] DefaultWeekWorks = [true, true, true, true, true, false, false];

    private static IEnumerable<TestCaseData> Contracts()
    {
        yield return new TestCaseData(MixedWeek, MixedWeekWorks).SetName("MixedWeek");
        yield return new TestCaseData(new EffectiveContractData(), DefaultWeekWorks).SetName("DefaultWeek");
    }

    private static List<DateOnly> Week()
        => Enumerable.Range(0, Sunday.DayNumber - Monday.DayNumber + 1).Select(Monday.AddDays).ToList();

    [TestCaseSource(nameof(Contracts))]
    public async Task TheFourPaths_AgreeOnEveryWeekday(EffectiveContractData contract, bool[] expectedWorks)
    {
        // Arrange
        var week = Week();

        // Act
        var wizard = await WizardWorksAsync(contract);
        var harmonizer = HarmonizerWorks(contract);
        var recovery = RecoveryWorks(contract);
        var alternatives = new List<bool>();
        foreach (var day in week)
        {
            alternatives.Add(!await AlternativeHasDayOffAsync(contract, day));
        }

        // Assert
        week.Select(day => wizard[day]).ShouldBe(expectedWorks);
        week.Select(day => harmonizer[day]).ShouldBe(expectedWorks);
        week.Select(day => recovery[day]).ShouldBe(expectedWorks);
        alternatives.ShouldBe(expectedWorks);
    }

    private static async Task<Dictionary<DateOnly, bool>> WizardWorksAsync(EffectiveContractData contract)
    {
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider.GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Monday, Sunday, Arg.Any<int?>())
            .Returns(ContractPerDay(contract));
        var membershipWindowReader = Substitute.For<IMembershipWindowReader>();
        membershipWindowReader.GetWindowsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, MembershipWindow>());
        var builder = new WizardAgentSnapshotBuilder(contractProvider, membershipWindowReader);

        var result = await builder.BuildAsync(
            [PersonId], Monday, Sunday, Monday, Sunday, new Dictionary<Guid, double>(), null, CancellationToken.None);

        return result.ContractDays.ToDictionary(day => day.Date, day => day.WorksOnDay);
    }

    private static Dictionary<DateOnly, bool> HarmonizerWorks(EffectiveContractData contract)
        => HarmonizerContextBuilder.BuildContractDays(
                [PersonId], Monday, Sunday, ContractPerDay(contract), new Dictionary<Guid, MembershipWindow>(),
                new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>(), CancellationToken.None)
            .ToDictionary(entry => entry.Key.Date, entry => entry.Value);

    private static Dictionary<DateOnly, bool> RecoveryWorks(EffectiveContractData contract)
        => RecoverySnapshotBuilder.BuildAvailability(
                [PersonId],
                new Dictionary<Guid, EffectiveContractData> { [PersonId] = contract },
                RecoveryBreakDays.Empty(),
                new Dictionary<(Guid AgentId, DateOnly Date), Klacks.ScheduleOptimizer.Models.ScheduleCommandKeyword>(),
                Monday,
                Sunday)
            .ToDictionary(entry => entry.Key.Date, entry => entry.Value.WorksOnDay);

    private static Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>> ContractPerDay(EffectiveContractData contract)
        => Week().ToDictionary(day => day, _ => new Dictionary<Guid, EffectiveContractData> { [PersonId] = contract });

    private static async Task<bool> AlternativeHasDayOffAsync(EffectiveContractData contract, DateOnly day)
    {
        var poolResolver = Substitute.For<IReplacementCandidatePoolResolver>();
        poolResolver.ResolveAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(new ReplacementCandidatePool(
                GroupId, [new Client { Id = PersonId, Name = "Person", FirstName = string.Empty }], []));
        var checker = Substitute.For<IPreCommitConflictChecker>();
        checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        var preferences = Substitute.For<IClientShiftPreferenceRepository>();
        preferences.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(new List<ClientShiftPreference>());
        var scheduleEntries = Substitute.For<IScheduleEntriesService>();
        ScheduleEntriesStub.ServeNothing(scheduleEntries);
        var availability = Substitute.For<IClientAvailabilityRepository>();
        availability.GetByDateRange(Arg.Any<DateOnly>(), Arg.Any<DateOnly>()).Returns(new List<ClientAvailability>());
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((Monday, Sunday));
        periodHours.GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, Klacks.Api.Domain.DTOs.Schedules.PeriodHoursResource>());
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), day, Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData> { [PersonId] = contract });
        var absences = Substitute.For<IAbsenceRepository>();
        absences.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsGroupVisibleAsync(GroupId, Arg.Any<CancellationToken>()).Returns(true);
        var clientGuard = Substitute.For<IClientVisibilityGuard>();
        clientGuard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Client>>(), Arg.Any<Func<Client, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Client>>().ToList());

        var handler = new FindReplacementQueryHandler(
            poolResolver, checker, preferences, scheduleEntries, availability, periodHours, contractProvider,
            Substitute.For<ISupervisorOverrideAuthorizer>(), Substitute.For<IHttpContextAccessor>(), absences,
            groupGuard, clientGuard, TestScenarioTokenAccess.AllVisible(),
            NullLogger<FindReplacementQueryHandler>.Instance);

        var result = await handler.Handle(
            new FindReplacementQuery(ShiftId, day, SlotStart, SlotEnd, GroupId, null), CancellationToken.None);

        return result.Eligible.ShouldHaveSingleItem().IsContractDayOff;
    }
}
