// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleRecurringTaskSkill time-zone resolution: with no explicit timeZoneId the
/// schedule uses the app owner's address country (globalCalendarCountry → IANA zone); an explicit
/// timeZoneId overrides it; the user context and a hard default act as fallbacks. Also covers the pure
/// CountryTimeZones map and the guard that refuses to freeze an empty permission set for a skill action.
/// The second block covers the way OUT of a pause: the skill is the only surface that can set the
/// per-task irreversible opt-in at all, and re-applying an existing task by name has to lift the pause -
/// otherwise the note telling the owner to fix the cause points at a state nothing can leave.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScheduleRecurringTaskSkillTests
{
    private IScheduledTaskRepository _repository = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private ISettingsReader _settingsReader = null!;
    private ScheduleRecurringTaskSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _skill = new ScheduleRecurringTaskSkill(_repository, _skillRegistry, _riskClassifier, _settingsReader);
    }

    private void OwnerCountry(string? code) =>
        _settingsReader.GetSetting(SettingKeys.GlobalCalendarCountry)
            .Returns(code is null ? (SettingsModel?)null : new SettingsModel { Type = SettingKeys.GlobalCalendarCountry, Value = code });

    private static SkillExecutionContext Ctx(string? userTimezone = null, IReadOnlyList<string>? permissions = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions ?? new List<string>(),
        UserTimezone = userTimezone
    };

    private static Dictionary<string, object> SkillParams() => new()
    {
        ["name"] = "weekly report",
        ["cronExpression"] = "0 8 * * 1",
        ["actionType"] = "skill",
        ["skillName"] = "list_clients",
        ["apply"] = true
    };

    private void KnownHarmlessSkill(string name)
    {
        _skillRegistry.GetSkillByName(name).Returns(new SkillDescriptor(
            name, "test skill", SkillCategory.Query,
            Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null));
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
    }

    private static Dictionary<string, object> ReminderParams(string? timeZoneId = null)
    {
        var p = new Dictionary<string, object>
        {
            ["name"] = "weekly check",
            ["cronExpression"] = "0 8 * * 1",
            ["actionType"] = "reminder",
            ["messageText"] = "check coverage",
            ["apply"] = true
        };
        if (timeZoneId is not null)
        {
            p["timeZoneId"] = timeZoneId;
        }

        return p;
    }

    [Test]
    public async Task NoTimeZone_UsesOwnerCountry_Switzerland_ZurichZone()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Zurich"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoTimeZone_UsesOwnerCountry_Germany_BerlinZone()
    {
        OwnerCountry("DE");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Berlin"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitTimeZone_OverridesOwnerCountry()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams("America/New_York"));

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "America/New_York"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoOwnerCountry_FallsBackToUserTimezone()
    {
        OwnerCountry(null);

        var result = await _skill.ExecuteAsync(Ctx("Europe/Vienna"), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Vienna"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownOwnerCountry_NoUserTimezone_FallsBackToDefault()
    {
        OwnerCountry("US");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == TimeZoneDefaults.DefaultTimezone), Arg.Any<CancellationToken>());
    }

    [TestCase("CH", "Europe/Zurich")]
    [TestCase("ch", "Europe/Zurich")]
    [TestCase(" DE ", "Europe/Berlin")]
    [TestCase("AT", "Europe/Vienna")]
    [TestCase("LI", "Europe/Vaduz")]
    public void CountryTimeZones_Resolve_KnownCodes(string code, string expected)
    {
        CountryTimeZones.Resolve(code).ShouldBe(expected);
    }

    [TestCase("US")]
    [TestCase("")]
    [TestCase(null)]
    public void CountryTimeZones_Resolve_UnknownOrEmpty_ReturnsNull(string? code)
    {
        CountryTimeZones.Resolve(code).ShouldBeNull();
    }

    [Test]
    public async Task SkillAction_WithoutPermissions_IsRefusedAndNothingIsPersisted()
    {
        OwnerCountry("CH");
        KnownHarmlessSkill("list_clients");

        var result = await _skill.ExecuteAsync(Ctx(), SkillParams());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("permission check");
        await _repository.DidNotReceive().AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkillAction_WithPermissions_FreezesThem()
    {
        OwnerCountry("CH");
        KnownHarmlessSkill("list_clients");

        var result = await _skill.ExecuteAsync(
            Ctx(permissions: new[] { "Authorised", "CanViewClients" }), SkillParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.OwnerPermissionsCsv == "Authorised,CanViewClients"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reminder_WithoutPermissions_IsStillAllowed()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    private ScheduledTask ExistingTask(Guid ownerUserId, string name, string? pausedReason = null)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            CronExpression = "0 6 * * 1",
            TimeZoneId = "Europe/Zurich",
            ActionType = ScheduledTaskActionTypes.Reminder,
            MessageText = "old text",
            OwnerUserId = ownerUserId,
            OwnerUserName = "tester",
            IsEnabled = true,
            RunCount = 4
        };

        if (pausedReason is not null)
        {
            task.Pause(pausedReason);
        }

        _repository.GetByOwnerAndNameAsync(ownerUserId, name, Arg.Any<CancellationToken>()).Returns(task);
        return task;
    }

    [Test]
    public async Task ReApplyingAPausedTask_LiftsThePauseAndDropsItsReason()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
        existing.PausedReason.ShouldBeNull();
        await _repository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReApplyingAPausedTask_TellsTheUserItIsRunningAgain()
    {
        OwnerCountry("CH");
        var context = Ctx();
        ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Message!.ShouldContain("paused and is running again");
    }

    [Test]
    public async Task ReApplyingATaskThatWasNotPaused_SaysNothingAboutAPause()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
        result.Message!.ShouldNotContain("paused");
    }

    [Test]
    public async Task IrreversibleOptIn_DefaultsToOff_OnANewTask()
    {
        OwnerCountry("CH");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => !t.AllowIrreversibleUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_IsPersisted_OnANewTask()
    {
        OwnerCountry("CH");
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.AllowIrreversibleUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_IsPersisted_WhenAnExistingTaskIsReApplied()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
    }

    [Test]
    public async Task IrreversibleOptIn_SurvivesAReApplyThatOmitsIt_SoTheResumeAdviceDoesNotLoop()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "autonomy level too low");
        existing.AllowIrreversibleUnattended = true;

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
    }

    [Test]
    public async Task IrreversibleOptIn_CanStillBeSwitchedOffExplicitly()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");
        existing.AllowIrreversibleUnattended = true;
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = false;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeFalse();
    }

    [Test]
    public async Task Preview_OfAnExistingTask_ShowsTheOptInItActuallyCarries()
    {
        OwnerCountry("CH");
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");
        existing.AllowIrreversibleUnattended = true;
        var parameters = ReminderParams();
        parameters["apply"] = false;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(result.Data)
            .ShouldContain("\"allowIrreversibleUnattended\":true");
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_AppearsInThePreviewBeforeAnythingIsSaved()
    {
        OwnerCountry("CH");
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;
        parameters["apply"] = false;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(result.Data)
            .ShouldContain("\"allowIrreversibleUnattended\":true");
        await _repository.DidNotReceive().AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }
}
