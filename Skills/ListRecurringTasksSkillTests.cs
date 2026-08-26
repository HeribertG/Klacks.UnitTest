// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ListRecurringTasksSkill. The point under test is the paused state: a task refused by
/// the unattended policy for a cause its owner can fix keeps IsEnabled true, so reporting "enabled" alone
/// would show the owner a task they were just told is paused as if it were still running. The listing has
/// to carry the paused flag, its reason and the per-task irreversible opt-in, and the summary line - the
/// part a shallow model summary actually relays - has to name how many tasks are paused.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListRecurringTasksSkillTests
{
    private const string PausedReasonText = "irreversible skill without the opt-in";

    private IScheduledTaskRepository _repository = null!;
    private ListRecurringTasksSkill _skill = null!;

    private static readonly Guid Owner = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skill = new ListRecurringTasksSkill(_repository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Owner,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanPlan" }
    };

    private static ScheduledTask Task(string name, bool paused = false, bool optIn = false)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            CronExpression = "0 8 * * 1",
            TimeZoneId = "Europe/Zurich",
            ActionType = ScheduledTaskActionTypes.Skill,
            SkillName = "update_client",
            OwnerUserId = Owner,
            OwnerUserName = "tester",
            IsEnabled = true,
            NextRunUtc = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc),
            AllowIrreversibleUnattended = optIn
        };

        if (paused)
        {
            task.Pause(PausedReasonText);
        }

        return task;
    }

    private void Owned(params ScheduledTask[] tasks) =>
        _repository.GetByOwnerAsync(Owner, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(tasks.ToList());

    private static string Json(SkillResult result) => JsonSerializer.Serialize(result.Data);

    [Test]
    public async Task Execute_ReportsThePausedFlagAndItsReason()
    {
        Owned(Task("paused task", paused: true));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var json = Json(result);
        json.ShouldContain("\"paused\":true");
        json.ShouldContain(PausedReasonText);
    }

    [Test]
    public async Task Execute_StillReportsAPausedTaskAsEnabled_BecauseTheOwnersIntentIsUntouched()
    {
        Owned(Task("paused task", paused: true));

        var json = Json(await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));

        json.ShouldContain("\"enabled\":true");
        json.ShouldContain("\"paused\":true");
    }

    [Test]
    public async Task Execute_ReportsTheIrreversibleOptIn()
    {
        Owned(Task("opted-in task", optIn: true));

        var json = Json(await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));

        json.ShouldContain("\"allowIrreversibleUnattended\":true");
    }

    [Test]
    public async Task Execute_RunningTaskOnly_ReportsNoPause()
    {
        Owned(Task("running task"));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Json(result).ShouldContain("\"paused\":false");
        result.Message!.ShouldBe("You have 1 scheduled task(s).");
    }

    [Test]
    public async Task Execute_SummaryLine_NamesHowManyTasksArePaused()
    {
        Owned(Task("running task"), Task("paused task", paused: true), Task("second paused", paused: true));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message!.ShouldContain("3 scheduled task(s)");
        result.Message!.ShouldContain("2 of them paused");
        result.Message!.ShouldContain("same name");
    }

    [Test]
    public async Task Execute_ListsDisabledTasksToo_SoAPausedOrStoppedTaskIsNeverHidden()
    {
        Owned(Task("running task"));

        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await _repository.Received(1).GetByOwnerAsync(Owner, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_NoTasks_SaysSo()
    {
        Owned();

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message!.ShouldBe("You have no scheduled tasks.");
    }
}
