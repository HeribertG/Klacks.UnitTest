// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateContractSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly Guid CurrentCalendarId = Guid.NewGuid();

    private static UpdateContractSkill NewSkill(
        IMediator mediator,
        ICompanyClock? clock = null,
        ICalendarSelectionRepository? calendars = null,
        ICountryResolver? countries = null,
        IContractRepository? contracts = null) =>
        new(
            mediator,
            clock ?? CompanyClock,
            calendars ?? Substitute.For<ICalendarSelectionRepository>(),
            countries ?? Substitute.For<ICountryResolver>(),
            contracts ?? Substitute.For<IContractRepository>());

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditContracts" }
    };

    private static ContractResource Contract(Guid id) => new()
    {
        Id = id,
        Name = "Vollzeit 160",
        GuaranteedHours = 160m,
        MinimumHours = 140m,
        MaximumHours = 180m,
        FullTime = 160m,
        ValidFrom = new DateTime(2026, 1, 1),
        ValidUntil = null
    };

    [Test]
    public async Task ValidFrom_TodayWord_ResolvesToCompanyLocalDay_NotUtcDay()
    {
        // Pacific/Auckland edge case: 23:30 UTC on 27.06 is already 11:30 NZST (+12, no June DST) on
        // 28.06 - "heute" must resolve to the company's local calendar day, not the UTC day.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var aucklandClock = new FixedCompanyClock(new DateTimeOffset(2026, 6, 27, 23, 30, 0, TimeSpan.Zero), auckland);
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator, aucklandClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "heute"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.ValidFrom == new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidFrom_UnreadableValue_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "not-a-date"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateNameAndHours_DispatchesPutCommand_WithMergedValues()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["name"] = "Vollzeit 170",
            ["guaranteedHours"] = 170m,
            ["maximumHours"] = 190m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.Id == contractId &&
                c.Resource.Name == "Vollzeit 170" &&
                c.Resource.GuaranteedHours == 170m &&
                c.Resource.MaximumHours == 190m &&
                c.Resource.MinimumHours == 140m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValidFrom_PersistsKindUtc_SoNpgsqlAcceptsTheTimestamptzWrite()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["validFrom"] = "2026-04-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.ValidFrom.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinimumAboveMaximum_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["minimumHours"] = 200m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativeHours_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["guaranteedHours"] = -5m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetPercent_DispatchesPutCommand_WithPercent()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearGuaranteedHoursWithPercent_SetsContractToInheriting()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["clearGuaranteedHours"] = true,
            ["percent"] = 80m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.GuaranteedHours == null &&
                c.Resource.Percent == 80m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearPercent_RemovesPercent()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        var existing = Contract(contractId);
        existing.Percent = 60m;
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["clearPercent"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.Percent == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoFieldsSupplied_ReturnsSuccess_WithoutPut()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    private static async Task<(SkillResult Result, IMediator Mediator)> RunAsync(
        ContractResource existing, Dictionary<string, object> parameters)
    {
        var (result, mediator, _) = await RunWithCalendarsAsync(existing, parameters);
        return (result, mediator);
    }

    private static async Task<(SkillResult Result, IMediator Mediator, ICalendarSelectionRepository Calendars)> RunWithCalendarsAsync(
        ContractResource existing,
        Dictionary<string, object> parameters,
        IEnumerable<CalendarSelection>? calendars = null,
        IEnumerable<Guid>? regionIds = null)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);

        var calendarRepository = Substitute.For<ICalendarSelectionRepository>();
        calendarRepository.List().Returns((calendars ?? []).ToList());
        calendarRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((regionIds ?? []).ToList());

        var countryResolver = Substitute.For<ICountryResolver>();
        countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries { Abbreviation = "CH" });

        var skill = NewSkill(mediator, calendars: calendarRepository, countries: countryResolver);
        parameters["contractId"] = existing.Id.ToString();
        var result = await skill.ExecuteAsync(Ctx(), parameters);
        return (result, mediator, calendarRepository);
    }

    private static IReadOnlyList<string> ChangedFields(SkillResult result) =>
        ((IEnumerable<string>)result.Data!.GetType().GetProperty("ChangedFields")!.GetValue(result.Data)!).ToList();

    private static async Task AssertNotPut(IMediator mediator) =>
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());

    [Test]
    public async Task ExplicitZeroNightRate_OnStandardContract_IsStoredAsZero()
    {
        var existing = Contract(Guid.NewGuid());

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { ["nightRate"] = 0m });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.NightRate == 0m && c.Resource.HolidayRate == null),
            Arg.Any<CancellationToken>());
    }

    [TestCase("clearNightRate")]
    [TestCase("clearHolidayRate")]
    [TestCase("clearSaRate")]
    [TestCase("clearSoRate")]
    public async Task ClearRate_ResetsTheRateToStandard(string clearFlag)
    {
        var existing = Contract(Guid.NewGuid());
        existing.NightRate = 0.1m;
        existing.HolidayRate = 0.2m;
        existing.WE1Rate = 0.3m;
        existing.WE2Rate = 0.4m;

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { [clearFlag] = true });

        result.Success.ShouldBeTrue(result.Message);
        var expectedNight = clearFlag == "clearNightRate" ? (decimal?)null : 0.1m;
        var expectedHoliday = clearFlag == "clearHolidayRate" ? (decimal?)null : 0.2m;
        var expectedSa = clearFlag == "clearSaRate" ? (decimal?)null : 0.3m;
        var expectedSo = clearFlag == "clearSoRate" ? (decimal?)null : 0.4m;
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.NightRate == expectedNight &&
                c.Resource.HolidayRate == expectedHoliday &&
                c.Resource.WE1Rate == expectedSa &&
                c.Resource.WE2Rate == expectedSo),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativeNightRate_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object> { ["nightRate"] = -0.1m });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PerformsShiftWorkFalse_IsStoredExplicitly()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object> { ["performsShiftWork"] = false });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.PerformsShiftWork == false),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearPerformsShiftWork_ResetsTheFlagToStandard()
    {
        var existing = Contract(Guid.NewGuid());
        existing.PerformsShiftWork = true;

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object> { ["clearPerformsShiftWork"] = true });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.PerformsShiftWork == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RateTogetherWithItsClearFlag_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { ["nightRate"] = 0.2m, ["clearNightRate"] = true });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ShiftWorkTogetherWithItsClearFlag_ReturnsError_NoMutation()
    {
        var (result, mediator) = await RunAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { ["performsShiftWork"] = true, ["clearPerformsShiftWork"] = true });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Workdays_ListedDaysAreWorked_OthersAreNot_AndReportedAsChanged()
    {
        var existing = Contract(Guid.NewGuid());

        var (result, mediator) = await RunAsync(existing, new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = "tue, Wed,SAT"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                !c.Resource.WorkOnMonday && c.Resource.WorkOnTuesday && c.Resource.WorkOnWednesday &&
                !c.Resource.WorkOnThursday && !c.Resource.WorkOnFriday && c.Resource.WorkOnSaturday &&
                !c.Resource.WorkOnSunday),
            Arg.Any<CancellationToken>());
        ChangedFields(result).ShouldBe([ContractFieldNames.Workdays]);
        result.Message.ShouldContain(ContractFieldNames.Workdays);
    }

    [Test]
    public async Task Workdays_SameAsCurrent_IsNotAChange_AndSkipsThePut()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = "Fri,Thu,Wed,Tue,Mon"
        });

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [TestCase("Mon,Funday")]
    [TestCase("Samstag")]
    [TestCase("Mon,,Tue")]
    [TestCase("Mon,Tue,")]
    [TestCase("[\"Mon\",\"Tue\"]")]
    public async Task Workdays_UnknownOrMalformedToken_IsRejected_NoMutation(string raw)
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = raw,
            ["name"] = "Should not be written"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{ContractFieldNames.Workdays}'");
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Workdays_BlankValue_IsTreatedAsNotGiven()
    {
        var (result, mediator) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = "  "
        });

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Workdays_OnContractWithSchedulingRule_ReportsTheRuleOverrideWarning()
    {
        var existing = Contract(Guid.NewGuid());
        existing.SchedulingRuleId = Guid.NewGuid();

        var (result, _) = await RunAsync(existing, new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = "Mon,Tue"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("scheduling rule");
    }

    [Test]
    public async Task Workdays_OnContractWithoutSchedulingRule_HasNoWarning()
    {
        var (result, _) = await RunAsync(Contract(Guid.NewGuid()), new Dictionary<string, object>
        {
            [ContractFieldNames.Workdays] = "Mon,Tue"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("Warning");
    }

    [Test]
    public async Task Region_ExactlyOneCalendar_SetsTheCalendarSelection_AndIsReportedAsChanged()
    {
        var calendarId = Guid.NewGuid();
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator, calendars) = await RunWithCalendarsAsync(
            existing,
            new Dictionary<string, object> { [ContractFieldNames.Region] = " zh " },
            regionIds: [calendarId]);

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.CalendarSelectionId == calendarId),
            Arg.Any<CancellationToken>());
        await calendars.Received(1).GetIdsByStateAsync("CH", "ZH", Arg.Any<CancellationToken>());
        ChangedFields(result).ShouldBe([ContractFieldNames.Region]);
    }

    [Test]
    public async Task Region_ResolvingToTheCurrentCalendar_IsNotAChange()
    {
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator, _) = await RunWithCalendarsAsync(
            existing,
            new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" },
            regionIds: [CurrentCalendarId]);

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Region_NoCalendar_IsRejected_ListingTheRealCalendarNames_NoMutation()
    {
        var calendars = new[]
        {
            new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern" },
            new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich" }
        };

        var (result, mediator, _) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { [ContractFieldNames.Region] = "XX" },
            calendars: calendars,
            regionIds: []);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No holiday calendar");
        result.Message.ShouldContain("Bern");
        result.Message.ShouldContain("Zürich");
        result.Message.ShouldContain("contract's calendar");
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Region_SeveralCalendars_IsRejected_ListingOnlyTheMatchingOnes()
    {
        var first = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich 2026" };
        var second = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich Schulferien" };
        var unrelated = new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern" };

        var (result, mediator, _) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" },
            calendars: [first, second, unrelated],
            regionIds: [first.Id, second.Id]);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("several holiday calendars");
        result.Message.ShouldContain("Zürich 2026");
        result.Message.ShouldContain("Zürich Schulferien");
        result.Message.ShouldNotContain("Bern");
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Region_SeveralCalendars_IncludingTheCurrentCalendar_KeepsIt_WithoutError()
    {
        var current = new CalendarSelection { Id = CurrentCalendarId, Name = "Zürich 2026" };
        var other = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich Schulferien" };
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator, _) = await RunWithCalendarsAsync(
            existing,
            new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" },
            calendars: [current, other],
            regionIds: [other.Id, CurrentCalendarId]);

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task Region_SeveralCalendars_ContractWithoutCalendar_IsRejected()
    {
        var first = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich 2026" };
        var second = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich Schulferien" };

        var (result, mediator, _) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" },
            calendars: [first, second],
            regionIds: [first.Id, second.Id]);

        result.Success.ShouldBeFalse();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task ClearRegion_RemovesTheContractCalendar_AndIsReportedAsChanged()
    {
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator, calendars) = await RunWithCalendarsAsync(
            existing, new Dictionary<string, object> { [ContractFieldNames.ClearRegion] = true });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c => c.Resource.CalendarSelectionId == null),
            Arg.Any<CancellationToken>());
        ChangedFields(result).ShouldBe([ContractFieldNames.Region]);
        await calendars.DidNotReceive().GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearRegion_OnContractWithoutCalendar_IsNotAChange()
    {
        var (result, mediator) = await RunAsync(
            Contract(Guid.NewGuid()), new Dictionary<string, object> { [ContractFieldNames.ClearRegion] = true });

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task ClearRegion_False_LeavesTheCalendarUntouched()
    {
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator) = await RunAsync(
            existing, new Dictionary<string, object> { [ContractFieldNames.ClearRegion] = false });

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task RegionTogetherWithClearRegion_ReturnsError_NoMutation()
    {
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator, _) = await RunWithCalendarsAsync(
            existing,
            new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH", [ContractFieldNames.ClearRegion] = true },
            regionIds: [Guid.NewGuid()]);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{ContractFieldNames.Region}'");
        result.Message.ShouldContain($"'{ContractFieldNames.ClearRegion}'");
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task ClearRegion_Unreadable_IsRejected_NamingTheParameter()
    {
        var existing = Contract(Guid.NewGuid());
        existing.CalendarSelectionId = CurrentCalendarId;

        var (result, mediator) = await RunAsync(
            existing, new Dictionary<string, object> { [ContractFieldNames.ClearRegion] = "ja" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{ContractFieldNames.ClearRegion}'");
        result.Message.ShouldContain("unreadable");
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task BlankRegion_IsTreatedAsNotGiven_WithoutCalendarLookup()
    {
        var (result, mediator, calendars) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()), new Dictionary<string, object> { [ContractFieldNames.Region] = "   " });

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBeEmpty();
        await calendars.DidNotReceive().GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task WorkdaysRegionAndOtherFields_TogetherAreAllReportedInChangedFields()
    {
        var calendarId = Guid.NewGuid();

        var (result, mediator, _) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object>
            {
                ["name"] = "Teilzeit 80",
                [ContractFieldNames.Workdays] = "Mon,Tue,Wed",
                [ContractFieldNames.Region] = "BE"
            },
            regionIds: [calendarId]);

        result.Success.ShouldBeTrue(result.Message);
        ChangedFields(result).ShouldBe(["name", ContractFieldNames.Workdays, ContractFieldNames.Region]);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.Name == "Teilzeit 80" &&
                c.Resource.CalendarSelectionId == calendarId &&
                !c.Resource.WorkOnThursday),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidWorkdays_BlocksTheRegionLookup_AndNothingIsWritten()
    {
        var (result, mediator, calendars) = await RunWithCalendarsAsync(
            Contract(Guid.NewGuid()),
            new Dictionary<string, object> { [ContractFieldNames.Workdays] = "Mon,Funday", [ContractFieldNames.Region] = "ZH" },
            regionIds: [Guid.NewGuid()]);

        result.Success.ShouldBeFalse();
        await calendars.DidNotReceive().GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await AssertNotPut(mediator);
    }

    private static List<Contract> NamedContracts() =>
    [
        new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Vollzeit 160 BE" },
        new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Vollzeit 180 BE" },
        new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Teilzeit 0 Std BE" },
        new() { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = "Gelöscht", IsDeleted = true }
    ];

    private static async Task<(SkillResult Result, IMediator Mediator)> RunByReferenceAsync(string reference)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call => Contract(call.Arg<GetQuery<ContractResource>>().Id));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var contracts = Substitute.For<IContractRepository>();
        contracts.List().Returns(NamedContracts());

        var skill = NewSkill(mediator, contracts: contracts);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = reference,
            ["name"] = "Renamed"
        });

        return (result, mediator);
    }

    [Test]
    public async Task ContractId_AsGuid_IsUsedDirectly_WithoutListingContracts()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>()).Returns(Contract(id));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var contracts = Substitute.For<IContractRepository>();
        var skill = NewSkill(mediator, contracts: contracts);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = id.ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeTrue(result.Message);
        await contracts.DidNotReceive().List();
        await mediator.Received(1).Send(
            Arg.Is<GetQuery<ContractResource>>(q => q.Id == id), Arg.Any<CancellationToken>());
    }

    [TestCase("Vollzeit 160 BE", "11111111-1111-1111-1111-111111111111")]
    [TestCase("vollzeit 160 be", "11111111-1111-1111-1111-111111111111")]
    [TestCase("Teilzeit", "33333333-3333-3333-3333-333333333333")]
    public async Task ContractId_AsName_ResolvesExactCaseInsensitiveAndUniquePartialNames(string reference, string expectedId)
    {
        var (result, mediator) = await RunByReferenceAsync(reference);

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<GetQuery<ContractResource>>(q => q.Id == Guid.Parse(expectedId)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ContractId_AsName_MessageNamesTheResolvedContractWithItsId()
    {
        var (result, _) = await RunByReferenceAsync("vollzeit 160 be");

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith("Contract 'Vollzeit 160' (id 11111111-1111-1111-1111-111111111111) updated");
    }

    [Test]
    public async Task ContractId_WithATypo_IsRejected_NoFuzzyMatchOnAWritingSkill()
    {
        var (result, mediator) = await RunByReferenceAsync("Volzeit 160 BE");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No contract found matching 'Volzeit 160 BE'");
        result.Message.ShouldContain("Vollzeit 160 BE");
        await mediator.DidNotReceive().Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>());
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task ContractId_AsAmbiguousName_ReturnsErrorListingTheCandidates_NoMutation()
    {
        var (result, mediator) = await RunByReferenceAsync("Vollzeit");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Multiple contracts match");
        result.Message.ShouldContain("Vollzeit 160 BE");
        result.Message.ShouldContain("Vollzeit 180 BE");
        await AssertNotPut(mediator);
    }

    [TestCase("Unbekannt AG")]
    [TestCase("Gelöscht")]
    [TestCase("123e4567-e89b-12d3-a456-42661417400Z")]
    public async Task ContractId_AsUnknownNameOrMalformedGuid_ReturnsErrorListingRealNames_NoException(string reference)
    {
        var (result, mediator) = await RunByReferenceAsync(reference);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No contract found matching");
        result.Message.ShouldContain("Vollzeit 160 BE");
        result.Message[result.Message.IndexOf("Available contracts:", StringComparison.Ordinal)..].ShouldNotContain("Gelöscht");
        await mediator.DidNotReceive().Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>());
        await AssertNotPut(mediator);
    }

    [Test]
    public async Task ContractId_Missing_ReturnsCleanError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = NewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["name"] = "Renamed" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("contractId");
        await AssertNotPut(mediator);
    }
}
