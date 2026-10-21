// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateContractSkill.ExecuteAsync, written before its long body was split into
/// private steps: the order of the changed-field entries across all edit steps, the success text (old name)
/// and serialized result data, the precedence of the refusals (each step's error stops the later steps, the
/// company clock is read after workdays/region and before validFrom, the no-op check runs before the
/// consistency checks) and the order of mediator, region lookup, clock and PutCommand.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateContractSkillCharacterizationTests
{
    private static readonly Guid ContractId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CurrentCalendarId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RegionCalendarId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SchedulingRuleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string SchedulingRuleWarning =
        "The contract has a scheduling rule that may override the working weekdays: a value the rule sets wins " +
        "over the contract's own value, so check the rule in the contract settings page.";

    private IMediator _mediator = null!;
    private ICompanyClock _clock = null!;
    private ICalendarSelectionRepository _calendars = null!;
    private ICountryResolver _countries = null!;
    private IContractRepository _contracts = null!;
    private UpdateContractSkill _skill = null!;
    private List<string> _calls = null!;
    private ContractResource _existing = null!;
    private ContractResource? _put;
    private bool _putReturnsNull;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _put = null;
        _putReturnsNull = false;
        _existing = new ContractResource
        {
            Id = ContractId,
            Name = "Vollzeit 160",
            GuaranteedHours = 160m,
            MinimumHours = 140m,
            MaximumHours = 180m,
            FullTime = 160m,
            WE2Rate = 0.5m,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CalendarSelectionId = CurrentCalendarId,
            SchedulingRuleId = SchedulingRuleId
        };

        _mediator = Substitute.For<IMediator>();
        _clock = Substitute.For<ICompanyClock>();
        _calendars = Substitute.For<ICalendarSelectionRepository>();
        _countries = Substitute.For<ICountryResolver>();
        _contracts = Substitute.For<IContractRepository>();

        _mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("get"); return _existing; });
        _mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("put");
                _put = ci.Arg<PutCommand<ContractResource>>().Resource;
                return _putReturnsNull ? null : _put;
            });
        _clock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("clock"); return new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc); });
        _countries.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("country"); return new Countries { Abbreviation = "CH" }; });
        _calendars.GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("region"); return new List<Guid> { RegionCalendarId }; });

        _skill = new UpdateContractSkill(_mediator, _clock, _calendars, _countries, _contracts);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditContracts" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { [ContractFieldNames.ContractId] = ContractId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task AllSteps_ChangedOrder_ExactMessageData_CallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("name", " Teilzeit 80 "),
            ("minimumHours", 60m),
            ("maximumHours", 100m),
            ("fullTime", 160m),
            ("guaranteedHours", 80m),
            ("percent", 50m),
            ("nightRate", 0.1m),
            ("holidayRate", 0m),
            ("performsShiftWork", false),
            ("clearSoRate", true),
            ("clearSaRate", true),
            (ContractFieldNames.Workdays, "Mon,Tue,Wed"),
            (ContractFieldNames.Region, " zh "),
            ("validFrom", "2026-07-01"),
            ("validUntil", "2026-12-31")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Contract 'Vollzeit 160' (id {ContractId}) updated (name, minimumHours, maximumHours, guaranteedHours, " +
            "percent, nightRate, holidayRate, performsShiftWork, soRate, workdays, region, validFrom, validUntil)." +
            " Warning: " + SchedulingRuleWarning);
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ContractId\":\"" + ContractId + "\",\"ChangedFields\":[\"name\",\"minimumHours\",\"maximumHours\"," +
            "\"guaranteedHours\",\"percent\",\"nightRate\",\"holidayRate\",\"performsShiftWork\",\"soRate\",\"workdays\"," +
            "\"region\",\"validFrom\",\"validUntil\"],\"Name\":\"Teilzeit 80\",\"GuaranteedHours\":80,\"Percent\":50," +
            "\"MinimumHours\":60,\"MaximumHours\":100,\"ValidFrom\":\"2026-07-01T00:00:00Z\"," +
            "\"ValidUntil\":\"2026-12-31T00:00:00Z\",\"Workdays\":\"Mon,Tue,Wed\"," +
            "\"CalendarSelectionId\":\"" + RegionCalendarId + "\",\"Warnings\":[" + JsonSerializer.Serialize(SchedulingRuleWarning) + "]}");
        _calls.ShouldBe(["get", "country", "region", "clock", "put"]);
        await _calendars.Received(1).GetIdsByStateAsync("CH", "ZH", Arg.Any<CancellationToken>());

        _put.ShouldBeSameAs(_existing);
        _put!.Name.ShouldBe("Teilzeit 80");
        _put.FullTime.ShouldBe(160m);
        _put.NightRate.ShouldBe(0.1m);
        _put.HolidayRate.ShouldBe(0m);
        _put.WE1Rate.ShouldBeNull();
        _put.WE2Rate.ShouldBeNull();
        _put.PerformsShiftWork.ShouldBe(false);
        new[] { _put.WorkOnMonday, _put.WorkOnTuesday, _put.WorkOnWednesday, _put.WorkOnThursday, _put.WorkOnFriday }
            .ShouldBe([true, true, true, false, false]);
    }

    [Test]
    public async Task NoChange_ExactMessageAndData_EvenOnAnInconsistentContract_ClockStillRead()
    {
        _existing.MinimumHours = 200m;
        _existing.ValidUntil = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Vollzeit 160"), ("fullTime", 160m)));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("No fields supplied for update — contract left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"ContractId\":\"" + ContractId + "\",\"ChangedFields\":[]}");
        _calls.ShouldBe(["get", "clock"]);
    }

    [Test]
    public async Task NegativeDecimal_WinsOverNegativeNullableDecimal_BeforeTheClock()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("fullTime", -1m), ("guaranteedHours", -1m)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Parameter 'fullTime' must not be negative.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task NegativeNullableDecimal_WinsOverAContradictoryClear()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("nightRate", -1m), ("holidayRate", 1m), ("clearHolidayRate", true)));

        result.Message.ShouldBe("Parameter 'nightRate' must not be negative.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task ContradictoryClear_WinsOverInvalidWorkdays()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("holidayRate", 1m), ("clearHolidayRate", true), (ContractFieldNames.Workdays, "Funday")));

        result.Message.ShouldBe("Parameter 'holidayRate' must not be combined with 'clearHolidayRate'.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task InvalidWorkdays_WinsOverInvalidValidFrom_BeforeTheClock()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            (ContractFieldNames.Workdays, "Mon,Funday"), ("validFrom", "irgendwann")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Unknown weekday 'Funday' in 'workdays'. Use a comma separated list of Mon,Tue,Wed,Thu,Fri,Sat,Sun.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task RegionWithClearRegion_IsRejectedBeforeTheClock()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            (ContractFieldNames.Region, "ZH"), (ContractFieldNames.ClearRegion, true), ("validFrom", "irgendwann")));

        result.Message.ShouldBe("Parameter 'region' must not be combined with 'clearRegion'.");
        _calls.ShouldBe(["get"]);
    }

    [Test]
    public async Task InvalidValidFrom_WinsOverInvalidValidUntil_AfterTheClock()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("validFrom", "irgendwann"), ("validUntil", "nie")));

        result.Message.ShouldBe(
            "Invalid validFrom value: 'irgendwann'. Please give a concrete date (for example 2026-05-01), " +
            "a relative day such as 'today' or 'tomorrow', or ask the user which date is meant.");
        _calls.ShouldBe(["get", "clock"]);
    }

    [Test]
    public async Task InvalidValidUntil_ExactText()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Neu"), ("validUntil", "nie")));

        result.Message.ShouldBe(
            "Invalid validUntil value: 'nie'. Please give a concrete date (for example 2026-05-01), " +
            "a relative day such as 'today' or 'tomorrow', or ask the user which date is meant.");
        _calls.ShouldBe(["get", "clock"]);
    }

    [Test]
    public async Task ClearValidUntil_WinsOverAnUnreadableValidUntil()
    {
        _existing.ValidUntil = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("clearValidUntil", true), ("validUntil", "nie")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe($"Contract 'Vollzeit 160' (id {ContractId}) updated (validUntil).");
        _put!.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task ClearGuaranteedHoursAndPercent_AfterValidFrom_ValueAndClearTogether_AreBothListed()
    {
        _existing.Percent = 80m;

        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("guaranteedHours", 120m), ("clearGuaranteedHours", true), ("clearPercent", true), ("validFrom", "2026-02-01")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Contract 'Vollzeit 160' (id {ContractId}) updated (guaranteedHours, validFrom, guaranteedHours, percent).");
        _put!.GuaranteedHours.ShouldBeNull();
        _put.Percent.ShouldBeNull();
    }

    [Test]
    public async Task MinimumAboveMaximum_WinsOverUntilBeforeFrom()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("minimumHours", 190m), ("validUntil", "2025-01-01")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("minimumHours must not exceed maximumHours.");
        _calls.ShouldBe(["get", "clock"]);
    }

    [Test]
    public async Task MinimumAboveAZeroMaximum_IsAllowed()
    {
        _existing.MaximumHours = 0m;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("minimumHours", 190m)));

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task UntilBeforeFrom_ExactText()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("validUntil", "2025-01-01")));

        result.Message.ShouldBe("validUntil must not be before validFrom.");
        _calls.ShouldBe(["get", "clock"]);
    }

    [Test]
    public async Task PutReturningNull_ExactText()
    {
        _putReturnsNull = true;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Neu")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Updating contract '{ContractId}' failed.");
        _calls.ShouldBe(["get", "clock", "put"]);
    }

    [Test]
    public async Task UnknownContract_ExactText()
    {
        _mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());

        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Neu")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Contract '{ContractId}' not found.");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task RenameWithoutWorkdays_HasNoWarning_EvenWithASchedulingRule()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Neu")));

        result.Message.ShouldBe($"Contract 'Vollzeit 160' (id {ContractId}) updated (name).");
        JsonSerializer.Serialize(result.Data).ShouldEndWith("\"Warnings\":[]}");
    }
}
