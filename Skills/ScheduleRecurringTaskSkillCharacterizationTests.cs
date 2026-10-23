// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for ScheduleRecurringTaskSkill.ExecuteAsync, written before its long body was split
/// into private steps: every refusal text in its precedence order (action type, time zone, cron, action target,
/// skill parameters), that the time-zone resolver, skill registry, risk classifier, MCP policy and repository are
/// consulted in this order and no further than the first refusal, the exact preview and saved texts with their
/// serialized data, the persisted task for a new reminder and a new skill action, and the re-authoring of an
/// existing task (run count reset, pause lifted, resume hint).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScheduleRecurringTaskSkillCharacterizationTests
{
    private const string Zone = "Europe/Zurich";
    private const string Cron = "0 8 * * 1";

    private IScheduledTaskRepository _repository = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IEffectiveTimeZoneResolver _timeZoneResolver = null!;
    private IMcpDelegatedSkillPolicy _mcpPolicy = null!;
    private ScheduleRecurringTaskSkill _skill = null!;
    private List<string> _calls = null!;
    private ScheduledTask? _existing;
    private SkillRiskClass _riskClass;

    [SetUp]
    public void SetUp()
    {
        _calls = [];
        _existing = null;
        _riskClass = SkillRiskClass.ReadOnly;

        _repository = Substitute.For<IScheduledTaskRepository>();
        _repository.GetByOwnerAndNameAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("get-existing"); return _existing; });
        _repository.AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("add"); return Task.CompletedTask; });
        _repository.UpdateAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("update"); return Task.CompletedTask; });

        _skillRegistry = Substitute.For<ISkillRegistry>();
        _skillRegistry.GetSkillByName(Arg.Any<string>()).Returns(call =>
        {
            var name = call.ArgAt<string>(0);
            _calls.Add("registry");
            return name == "nope"
                ? null
                : new SkillDescriptor(
                    name, "test skill", SkillCategory.Query,
                    Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null);
        });

        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(_ => { _calls.Add("classify"); return _riskClass; });

        _timeZoneResolver = Substitute.For<IEffectiveTimeZoneResolver>();
        _timeZoneResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("tz"); return (TimeZoneInfo.Utc, Zone); });

        _mcpPolicy = Substitute.For<IMcpDelegatedSkillPolicy>();
        _mcpPolicy.IsAllowed(Arg.Any<SkillDescriptor>(), Arg.Any<PersonalAccessTokenAccessMode?>())
            .Returns(_ => { _calls.Add("mcp"); return true; });

        _skill = new ScheduleRecurringTaskSkill(_repository, _skillRegistry, _riskClassifier, _timeZoneResolver, _mcpPolicy);
    }

    private static readonly Guid OwnerId = Guid.Parse("e0000000-0000-0000-0000-000000000001");

    private static SkillExecutionContext Ctx(bool withPermissions = true) => new()
    {
        UserId = OwnerId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = withPermissions ? new List<string> { "Authorised", "CanViewClients" } : new List<string>()
    };

    private static Dictionary<string, object> Reminder(params (string Key, object Value)[] overrides) =>
        With(new Dictionary<string, object>
        {
            ["name"] = "weekly check",
            ["cronExpression"] = Cron,
            ["actionType"] = "reminder",
            ["messageText"] = "check coverage",
            ["apply"] = true
        }, overrides);

    private static Dictionary<string, object> SkillAction(params (string Key, object Value)[] overrides) =>
        With(new Dictionary<string, object>
        {
            ["name"] = "weekly report",
            ["cronExpression"] = Cron,
            ["actionType"] = "skill",
            ["skillName"] = "list_clients",
            ["apply"] = true
        }, overrides);

    private static Dictionary<string, object> With(Dictionary<string, object> parameters, (string Key, object Value)[] overrides)
    {
        foreach (var (key, value) in overrides)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static string NextRunOf(SkillResult result)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        return document.RootElement.GetProperty("nextRun").GetString()!;
    }

    [Test]
    public async Task Preview_ExactMessageAndData_LooksUpTheExistingTask_ButSavesNothing()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Reminder(("apply", false), ("maxRuns", 3)));

        result.Success.ShouldBeTrue(result.Message);
        var nextRun = NextRunOf(result);
        result.Message.ShouldBe(
            $"Preview: 'weekly check' would run [{Cron}] in {Zone}. " +
            $"Next run: {nextRun}. Nothing was scheduled yet. " +
            "Ask the user to confirm, then call again with apply=true.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"name\":\"weekly check\",\"cronExpression\":\"0 8 * * 1\",\"timeZone\":\"Europe/Zurich\"," +
            "\"actionType\":\"reminder\",\"skillName\":null,\"nextRun\":\"" + nextRun + "\",\"maxRuns\":3," +
            "\"allowIrreversibleUnattended\":false}");
        _calls.ShouldBe(["tz", "get-existing"]);
    }

    [Test]
    public async Task NewReminder_ExactMessage_TrimmedInputs_AndPersistedTask()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Reminder(
            ("name", "  weekly check  "),
            ("cronExpression", "  0 8 * * 1 "),
            ("actionType", " Reminder "),
            ("skillName", "list_clients"),
            ("maxRuns", 0)));

        result.Success.ShouldBeTrue(result.Message);
        var nextRun = NextRunOf(result);
        result.Message.ShouldBe($"Scheduled 'weekly check' [{Cron}] in {Zone}. Next run: {nextRun}.");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"skillName\":null,");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"maxRuns\":null,");
        _calls.ShouldBe(["tz", "get-existing", "add"]);
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t =>
                t.Name == "weekly check"
                && t.OwnerUserId == OwnerId
                && t.CronExpression == Cron
                && t.TimeZoneId == Zone
                && t.ActionType == "reminder"
                && t.MessageText == "check coverage"
                && t.SkillName == null
                && t.ParametersJson == "{}"
                && t.NextRunUtc != null
                && t.MaxRuns == null
                && !t.AllowIrreversibleUnattended),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NewSkillAction_ConsultsRegistryClassifierAndPolicyInOrder_AndStoresTheNormalizedParameters()
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(
            ("skillName", "  list_clients  "),
            ("skillParameters", " { \"a\" : 1 } "),
            ("maxRuns", 5)));

        result.Success.ShouldBeTrue(result.Message);
        _calls.ShouldBe(["tz", "registry", "classify", "mcp", "get-existing", "add"]);
        JsonSerializer.Serialize(result.Data).ShouldContain("\"actionType\":\"skill\",\"skillName\":\"list_clients\",");
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t =>
                t.SkillName == "list_clients"
                && t.ParametersJson == "{ \"a\" : 1 }"
                && t.MaxRuns == 5
                && t.MessageText == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitTimeZone_SkipsTheResolver()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Reminder(("timeZoneId", "  America/New_York ")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith($"Scheduled 'weekly check' [{Cron}] in America/New_York. Next run: ");
        _calls.ShouldBe(["get-existing", "add"]);
    }

    [Test]
    public async Task ReAuthoringAnExistingTask_ResetsTheRunCount_AndUpdatesIt()
    {
        _existing = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = "weekly check",
            OwnerUserId = OwnerId,
            ActionType = ScheduledTaskActionTypes.Reminder,
            RunCount = 4,
            AllowIrreversibleUnattended = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), Reminder());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe($"Scheduled 'weekly check' [{Cron}] in {Zone}. Next run: {NextRunOf(result)}.");
        _calls.ShouldBe(["tz", "get-existing", "update"]);
        _existing.RunCount.ShouldBe(0);
        _existing.CronExpression.ShouldBe(Cron);
        _existing.AllowIrreversibleUnattended.ShouldBeTrue();
    }

    [Test]
    public async Task ReAuthoringAPausedTask_AppendsTheResumeHint()
    {
        _existing = new ScheduledTask { Id = Guid.NewGuid(), Name = "weekly check", OwnerUserId = OwnerId, RunCount = 2 };
        _existing.Pause("autonomy level too low");

        var result = await _skill.ExecuteAsync(Ctx(), Reminder());

        result.Message.ShouldBe(
            $"Scheduled 'weekly check' [{Cron}] in {Zone}. Next run: {NextRunOf(result)}. " +
            "The task was paused and is running again.");
        _existing.IsPaused.ShouldBeFalse();
    }

    [Test]
    public async Task UnknownActionType_IsRefusedFirst_LowercasedAndTrimmed()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Reminder(
            ("actionType", " EMAIL "), ("timeZoneId", "Not/AZone"), ("cronExpression", "bad")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Unknown actionType 'email'. Use 'reminder' or 'skill'.");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownTimeZone_IsRefusedBeforeTheCronCheck()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Reminder(("timeZoneId", "Not/AZone"), ("cronExpression", "bad")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Unknown time zone 'Not/AZone'. Use a valid IANA time zone id (e.g. 'Continent/City').");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownResolvedTimeZone_IsRefusedWithTheResolvedId()
    {
        _timeZoneResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("tz"); return (TimeZoneInfo.Utc, "Mars/Olympus"); });

        var result = await _skill.ExecuteAsync(Ctx(), Reminder());

        result.Message.ShouldBe("Unknown time zone 'Mars/Olympus'. Use a valid IANA time zone id (e.g. 'Continent/City').");
        _calls.ShouldBe(["tz"]);
    }

    [Test]
    public async Task InvalidCron_IsRefusedBeforeTheActionTarget()
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("cronExpression", " bad cron "), ("skillName", "nope")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Invalid cron expression 'bad cron'. Use standard 5-field cron, e.g. '0 8 * * 1' for Mondays at 08:00.");
        _calls.ShouldBe(["tz"]);
    }

    [Test]
    public async Task ReminderWithoutText_IsRefused()
    {
        var parameters = Reminder(("messageText", "   "));

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Message.ShouldBe("A reminder needs messageText.");
        _calls.ShouldBe(["tz"]);
    }

    [Test]
    public async Task SkillActionWithoutSkillName_IsRefused()
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("skillName", "   ")));

        result.Message.ShouldBe("A skill action needs skillName.");
        _calls.ShouldBe(["tz"]);
    }

    [TestCase("schedule_recurring_task", "Scheduling skills cannot themselves be scheduled.")]
    [TestCase("LIST_RECURRING_TASKS", "Scheduling skills cannot themselves be scheduled.")]
    [TestCase("cancel_recurring_task", "Scheduling skills cannot themselves be scheduled.")]
    [TestCase("Confirm_Pending_Action",
        "'confirm_pending_action' cannot be scheduled: a held action runs only after the user confirmed it in the " +
        "conversation, never from a background run.")]
    public async Task UnschedulableSkills_AreRefusedWithoutConsultingTheRegistry(string skillName, string expected)
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("skillName", skillName)));

        result.Message.ShouldBe(expected);
        _calls.ShouldBe(["tz"]);
    }

    [Test]
    public async Task UnknownSkill_IsRefusedBeforeTheParametersAreChecked()
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("skillName", "nope"), ("skillParameters", "{bad")));

        result.Message.ShouldBe("Unknown skill 'nope'. It cannot be scheduled.");
        _calls.ShouldBe(["tz", "registry"]);
    }

    [Test]
    public async Task SensitiveSkill_IsRefused()
    {
        _riskClass = SkillRiskClass.Sensitive;

        var result = await _skill.ExecuteAsync(Ctx(), SkillAction());

        result.Message.ShouldBe("Skill 'list_clients' is too sensitive to run unattended on a schedule.");
        _calls.ShouldBe(["tz", "registry", "classify"]);
    }

    [Test]
    public async Task McpPolicyRefusal_HasItsExactText()
    {
        _mcpPolicy.IsAllowed(Arg.Any<SkillDescriptor>(), Arg.Any<PersonalAccessTokenAccessMode?>())
            .Returns(_ => { _calls.Add("mcp"); return false; });

        var result = await _skill.ExecuteAsync(Ctx(), SkillAction());

        result.Message.ShouldBe(
            "Skill 'list_clients' cannot be scheduled by an external agent: it is not available to this caller over MCP, " +
            "so it cannot run on its behalf in the background either.");
        _calls.ShouldBe(["tz", "registry", "classify", "mcp"]);
    }

    [Test]
    public async Task MissingPermissions_HasItsExactText()
    {
        var result = await _skill.ExecuteAsync(Ctx(withPermissions: false), SkillAction());

        result.Message.ShouldBe(
            "Your permissions could not be determined, so this schedule would later run without any " +
            "permission check. Sign in again and retry.");
        _calls.ShouldBe(["tz", "registry", "classify", "mcp"]);
    }

    [TestCase("[1,2]")]
    [TestCase("{bad")]
    [TestCase("\"text\"")]
    public async Task NonObjectSkillParameters_AreRefused_BeforeTheExistingTaskIsLookedUp(string raw)
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("skillParameters", raw)));

        result.Message.ShouldBe("skillParameters must be a JSON object, e.g. {\"key\":\"value\"}.");
        _calls.ShouldBe(["tz", "registry", "classify", "mcp"]);
    }

    [Test]
    public async Task BlankSkillParameters_KeepTheEmptyObject()
    {
        var result = await _skill.ExecuteAsync(Ctx(), SkillAction(("skillParameters", "   ")));

        result.Success.ShouldBeTrue(result.Message);
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.ParametersJson == "{}"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingRequiredParameters_ThrowInDeclarationOrder()
    {
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>())))
            .Message.ShouldBe("Required parameter 'name' is missing");
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(
                Ctx(), new Dictionary<string, object> { ["name"] = "x", ["actionType"] = "email" })))
            .Message.ShouldBe("Required parameter 'cronExpression' is missing");
        _calls.ShouldBeEmpty();
    }
}
