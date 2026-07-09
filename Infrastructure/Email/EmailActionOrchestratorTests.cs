// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailActionOrchestrator — verifies the email-flow autonomy mapping fixed by the
/// user (FullyAutonomous executes everything, Autonomous executes only the cover scenario, lower
/// levels only suggest), the minimum-over-admins level resolution, and that ambiguous groups or
/// absence types degrade to a suggestion instead of a wrong automatic write.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailActionOrchestratorTests
{
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private ISkillExecutor _skillExecutor = null!;
    private IGroupMembershipService _groupMembershipService = null!;
    private Klacks.Api.Application.Interfaces.IAbsenceRepository _absenceRepository = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private EmailActionOrchestrator _orchestrator = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid AdminGuid = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _groupMembershipService = Substitute.For<IGroupMembershipService>();
        _absenceRepository = Substitute.For<Klacks.Api.Application.Interfaces.IAbsenceRepository>();
        _contractDataProvider = Substitute.For<IClientContractDataProvider>();

        SetContract(new EffectiveContractData { HasActiveContract = true, GuaranteedHours = 0 });
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { AdminGuid.ToString() });
        _groupMembershipService.GetClientGroupsAsync(ClientId)
            .Returns([new Group { Id = GroupId, Name = "Bern" }]);
        _absenceRepository.List().Returns(
        [
            AbsenceType("Krankheit", "Sickness"),
            AbsenceType("Ferien", "Vacation")
        ]);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "done"));

        _orchestrator = new EmailActionOrchestrator(
            _autonomyPreferences, _audienceResolver, _skillExecutor,
            _groupMembershipService, _absenceRepository, _contractDataProvider,
            Substitute.For<ILogger<EmailActionOrchestrator>>());
    }

    private void AdminLevel(AutonomyLevel level)
    {
        _autonomyPreferences.GetAsync(AdminGuid.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminGuid.ToString(), Level = level });
    }

    private void SetContract(EffectiveContractData contract)
    {
        _contractDataProvider.GetEffectiveContractDataAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contract);
    }

    private static Absence AbsenceType(string de, string en) => new()
    {
        Id = Guid.NewGuid(),
        Name = new MultiLanguage { De = de, En = en }
    };

    private static ReceivedEmail Email() => new() { Id = Guid.NewGuid(), Subject = "Test" };

    private static EmailAnalysis Analysis(EmailIntent intent, EntityTypeEnum type = EntityTypeEnum.Employee) => new()
    {
        ClientId = ClientId,
        ClientType = type,
        Intent = intent,
        FromDate = new DateOnly(2026, 7, 10),
        UntilDate = new DateOnly(2026, 7, 12)
    };

    private static EmailAnalysis AvailabilityAnalysis(
        DateOnly fromDate, DateOnly untilDate, int? startHour = null, int? endHour = null, string? weekdays = null) => new()
    {
        ClientId = ClientId,
        ClientType = EntityTypeEnum.Employee,
        Intent = EmailIntent.AvailabilityAnnouncement,
        FromDate = fromDate,
        UntilDate = untilDate,
        StartHour = startHour,
        EndHour = endHour,
        Weekdays = weekdays
    };

    private static EmailAnalysis ShiftPreferenceAnalysis(
        DateOnly fromDate, DateOnly untilDate, string? scheduleCommands, string? weekdays = null) => new()
    {
        ClientId = ClientId,
        ClientType = EntityTypeEnum.Employee,
        Intent = EmailIntent.ShiftPreference,
        FromDate = fromDate,
        UntilDate = untilDate,
        ScheduleCommands = scheduleCommands,
        Weekdays = weekdays
    };

    private List<SkillInvocation> CaptureSkillInvocations()
    {
        var captured = new List<SkillInvocation>();
        _skillExecutor.ExecuteAsync(
                Arg.Do<SkillInvocation>(captured.Add),
                Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "done"));
        return captured;
    }

    private Task<int> ExecutedSkillCallsAsync() =>
        Task.FromResult(_skillExecutor.ReceivedCalls().Count());

    [Test]
    public async Task CustomerIntent_ReturnsNull()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.CustomerMessage, EntityTypeEnum.Customer));

        outcome.ShouldBeNull();
    }

    [Test]
    public async Task MissingDate_ReturnsSuggestionWithoutExecution()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var analysis = Analysis(EmailIntent.VacationRequest);
        analysis.FromDate = null;

        var outcome = await _orchestrator.ExecuteAsync(Email(), analysis);

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    public async Task LowLevels_OnlySuggest_ForAllIntents(AutonomyLevel level)
    {
        AdminLevel(level);

        foreach (var intent in new[] { EmailIntent.WorkCancellation, EmailIntent.VacationRequest, EmailIntent.DayOffWish })
        {
            var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(intent));
            outcome.ShouldNotBeNull();
            outcome!.Executed.ShouldBeFalse(intent.ToString());
        }

        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Autonomous_ExecutesCoverScenario_ButOnlySuggestsVacationAndDayOff()
    {
        AdminLevel(AutonomyLevel.Autonomous);

        var cancellation = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.WorkCancellation));
        cancellation!.Executed.ShouldBeTrue();

        var vacation = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.VacationRequest));
        vacation!.Executed.ShouldBeFalse();

        var dayOff = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.DayOffWish));
        dayOff!.Executed.ShouldBeFalse();

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "cover_absence"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FullyAutonomous_ExecutesAllThreeIntents()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        (await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.WorkCancellation)))!.Executed.ShouldBeTrue();
        (await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.VacationRequest)))!.Executed.ShouldBeTrue();
        (await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.DayOffWish)))!.Executed.ShouldBeTrue();

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "cover_absence"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "add_break_placeholder"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == "add_schedule_commands_range"
                && (string)i.Parameters["commandKeyword"] == "FREE"),
            Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EffectiveLevel_IsMinimumOverAdmins()
    {
        var secondAdmin = Guid.NewGuid();
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { AdminGuid.ToString(), secondAdmin.ToString() });
        AdminLevel(AutonomyLevel.FullyAutonomous);
        _autonomyPreferences.GetAsync(secondAdmin.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = secondAdmin.ToString(), Level = AutonomyLevel.Propose });

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.WorkCancellation));

        outcome!.Executed.ShouldBeFalse();
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task NoAdmins_OnlySuggests()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.WorkCancellation));

        outcome!.Executed.ShouldBeFalse();
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task MultipleGroups_DegradesToSuggestion()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        _groupMembershipService.GetClientGroupsAsync(ClientId)
            .Returns([new Group { Id = GroupId, Name = "Bern" }, new Group { Id = Guid.NewGuid(), Name = "Basel" }]);

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.WorkCancellation));

        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("2 groups");
    }

    [Test]
    public async Task AmbiguousAbsenceType_DegradesToSuggestion()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        _absenceRepository.List().Returns(
        [
            AbsenceType("Ferien", "Vacation"),
            AbsenceType("Ferien unbezahlt", "Unpaid vacation")
        ]);

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.VacationRequest));

        outcome!.Executed.ShouldBeFalse();
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task SkillFailure_ReportsFailureNotSuccess()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Error("db down"));

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.DayOffWish));

        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("db down");
    }

    [Test]
    public async Task ExecutionContext_UsesAdminIdentityWithAuditName_AndBypassesGate()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        SkillExecutionContext? captured = null;
        _skillExecutor.ExecuteAsync(
                Arg.Any<SkillInvocation>(),
                Arg.Do<SkillExecutionContext>(c => captured = c),
                Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "done"));

        await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.DayOffWish));

        captured.ShouldNotBeNull();
        captured!.UserId.ShouldBe(AdminGuid);
        captured.UserName.ShouldBe("Klacksy email-analysis");
        captured.BypassAutonomyGate.ShouldBeTrue();
    }

    [Test]
    public async Task Availability_FullyAutonomous_ExecutesSkillWithDateRangeAndHours()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var invocations = CaptureSkillInvocations();

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 12), startHour: 8, endHour: 16));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeTrue();
        invocations.Count.ShouldBe(1);
        var invocation = invocations[0];
        invocation.SkillName.ShouldBe("set_client_availability");
        invocation.Parameters["clientId"].ShouldBe(ClientId);
        invocation.Parameters["startDate"].ShouldBe(new DateOnly(2026, 7, 10));
        invocation.Parameters["endDate"].ShouldBe(new DateOnly(2026, 7, 12));
        invocation.Parameters["isAvailable"].ShouldBe(true);
        invocation.Parameters["startHour"].ShouldBe(8);
        invocation.Parameters["endHour"].ShouldBe(16);
        invocation.Parameters.ContainsKey("dates").ShouldBeFalse();
    }

    [Test]
    public async Task Availability_WithWeekdays_PassesOnlyMatchingDatesInsteadOfRange()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var invocations = CaptureSkillInvocations();

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19), weekdays: "1,2,3,4,5"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeTrue();
        invocations.Count.ShouldBe(1);
        var invocation = invocations[0];
        invocation.SkillName.ShouldBe("set_client_availability");
        invocation.Parameters["dates"].ShouldBe("2026-07-13,2026-07-14,2026-07-15,2026-07-16,2026-07-17");
        invocation.Parameters["isAvailable"].ShouldBe(true);
        invocation.Parameters.ContainsKey("startDate").ShouldBeFalse();
        invocation.Parameters.ContainsKey("endDate").ShouldBeFalse();
    }

    [Test]
    public async Task Availability_BelowFullyAutonomous_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.Autonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), startHour: 8, endHour: 16));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("set_client_availability");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Availability_RangeOverNinetyTwoDays_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 10), new DateOnly(2026, 10, 10)));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("set_client_availability");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Availability_WeekdaysOutsidePeriod_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 13), weekdays: "7"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("set_client_availability");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task ShiftPreference_ZeroHourContract_ExecutesOneCallPerKeyword()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var invocations = CaptureSkillInvocations();

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "-NIGHT"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeTrue();
        invocations.Count.ShouldBe(1);
        var invocation = invocations[0];
        invocation.SkillName.ShouldBe("add_schedule_commands_range");
        invocation.Parameters["clientId"].ShouldBe(ClientId);
        invocation.Parameters["fromDate"].ShouldBe(new DateOnly(2026, 7, 13));
        invocation.Parameters["untilDate"].ShouldBe(new DateOnly(2026, 7, 17));
        invocation.Parameters["commandKeyword"].ShouldBe("-NIGHT");
    }

    [Test]
    public async Task ShiftPreference_TwoKeywords_ExecutesTwoCalls()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var invocations = CaptureSkillInvocations();

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "EARLY,-NIGHT"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeTrue();
        invocations.Count.ShouldBe(2);
        invocations.Select(i => i.SkillName).ShouldAllBe(name => name == "add_schedule_commands_range");
        invocations.Select(i => (string)i.Parameters["commandKeyword"]).ShouldBe(new[] { "EARLY", "-NIGHT" });
    }

    [Test]
    public async Task ShiftPreference_WithWeekdays_MergesConsecutiveDaysIntoSubRanges()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        var invocations = CaptureSkillInvocations();

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19), "EARLY", weekdays: "1,2,3"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeTrue();
        invocations.Count.ShouldBe(1);
        var invocation = invocations[0];
        invocation.Parameters["fromDate"].ShouldBe(new DateOnly(2026, 7, 13));
        invocation.Parameters["untilDate"].ShouldBe(new DateOnly(2026, 7, 15));
        invocation.Parameters["commandKeyword"].ShouldBe("EARLY");
    }

    [Test]
    public async Task ShiftPreference_GuaranteedHoursContract_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        SetContract(new EffectiveContractData { HasActiveContract = true, GuaranteedHours = 20 });

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "EARLY"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("guaranteed");
        outcome.Description.ShouldContain("add_schedule_commands_range");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task ShiftPreference_NoActiveContract_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        SetContract(new EffectiveContractData { HasActiveContract = false });

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "EARLY"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("no active contract");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task DayOffWish_GuaranteedHoursContract_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        SetContract(new EffectiveContractData { HasActiveContract = true, GuaranteedHours = 20 });

        var outcome = await _orchestrator.ExecuteAsync(Email(), Analysis(EmailIntent.DayOffWish));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("guaranteed");
        outcome.Description.ShouldContain("add_schedule_commands_range");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task Availability_GuaranteedHoursContract_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);
        SetContract(new EffectiveContractData { HasActiveContract = true, GuaranteedHours = 20 });

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            AvailabilityAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17)));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("guaranteed");
        outcome.Description.ShouldContain("set_client_availability");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task ShiftPreference_ContradictingKeywords_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "EARLY,-EARLY"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("contradict");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task ShiftPreference_BelowFullyAutonomous_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.Autonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), "EARLY"));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("add_schedule_commands_range");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }

    [Test]
    public async Task ShiftPreference_NoUsableKeywords_OnlySuggests()
    {
        AdminLevel(AutonomyLevel.FullyAutonomous);

        var outcome = await _orchestrator.ExecuteAsync(Email(),
            ShiftPreferenceAnalysis(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 17), null));

        outcome.ShouldNotBeNull();
        outcome!.Executed.ShouldBeFalse();
        outcome.Description.ShouldContain("no unambiguous planning command");
        (await ExecutedSkillCallsAsync()).ShouldBe(0);
    }
}
