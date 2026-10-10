// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for create_contract_from_template: a reflection guard that every template value is copied except
/// the identity and the explicitly overridden fields, then one test per override rule (validFrom, the two
/// workload paths and their band defaults, the path conflict, validation, payment-interval and individual
/// period handling, strict weekdays, region resolution, duplicate names, missing template) and the
/// adapted/copied field report.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Services.Contracts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateContractFromTemplateSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly Guid TemplateId = Guid.NewGuid();
    private static readonly Guid TemplateCalendarId = Guid.NewGuid();
    private static readonly Guid IndividualPeriodId = Guid.NewGuid();
    private static readonly Guid SchedulingRuleId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IContractRepository _contractRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private CreateContractFromTemplateSkill _skill = null!;
    private ContractResource? _posted;
    private Guid _postedIdAtCall;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Permissions.CanCreateContracts }
    };

    private static ContractResource FixedHoursTemplate() => new()
    {
        Id = TemplateId,
        Name = "Vollzeit 160 BE",
        GuaranteedHours = 160m,
        MinimumHours = 120m,
        MaximumHours = 200m,
        FullTime = 180m,
        NightRate = 0.1m,
        HolidayRate = 0.15m,
        WE1Rate = 0.1m,
        WE2Rate = 0.12m,
        WE3Rate = 0.05m,
        NightStart = "22:00",
        NightEnd = "06:00",
        PaymentInterval = PaymentInterval.Monthly,
        Percent = null,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ValidUntil = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        CalendarSelectionId = TemplateCalendarId,
        PerformsShiftWork = true,
        SchedulingRuleId = SchedulingRuleId
    };

    private static ContractResource InheritedTemplate() => new()
    {
        Id = TemplateId,
        Name = "Inherited 80",
        GuaranteedHours = null,
        MinimumHours = 0m,
        MaximumHours = 0m,
        FullTime = 0m,
        PaymentInterval = PaymentInterval.Monthly,
        Percent = 80m,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private void Setup(
        ContractResource template,
        IEnumerable<Contract>? existing = null,
        IEnumerable<CalendarSelection>? calendars = null,
        IEnumerable<Guid>? regionIds = null)
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>()).Returns(template);
        _mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _posted = call.Arg<PostCommand<ContractResource>>().Resource;
                _postedIdAtCall = _posted.Id;
                var created = ContractTemplateCopier.CopyAsNew(_posted);
                created.Id = Guid.NewGuid();
                return created;
            });

        _contractRepository = Substitute.For<IContractRepository>();
        _contractRepository.List().Returns((existing ?? []).ToList());

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository.List().Returns((calendars ?? []).ToList());
        _calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((regionIds ?? []).ToList());

        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new Countries { Abbreviation = "CH" });

        _skill = new CreateContractFromTemplateSkill(
            _mediator, CompanyClock, _contractRepository, _calendarSelectionRepository, _countryResolver);
    }

    private Task<SkillResult> Run(Dictionary<string, object>? extra = null, string name = "Vollzeit 120 BE")
    {
        var parameters = new Dictionary<string, object>
        {
            [ContractFieldNames.TemplateContractId] = TemplateId.ToString(),
            [ContractFieldNames.Name] = name,
            [ContractFieldNames.ValidFrom] = "2026-07-01"
        };
        foreach (var (key, value) in extra ?? [])
        {
            parameters[key] = value;
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    private async Task AssertNotPosted() =>
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());

    [Test]
    public async Task OnlyRequiredParameters_CopiesEveryTemplateValue_ExceptIdentityAndValidity()
    {
        var template = DistinctiveTemplate();
        Setup(template);

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        _posted.ShouldNotBeNull();

        var skipped = new HashSet<string>
        {
            nameof(ContractResource.Id),
            nameof(ContractResource.Name),
            nameof(ContractResource.ValidFrom),
            nameof(ContractResource.ValidUntil),
            nameof(ContractResource.CalendarSelection)
        };
        var mismatches = typeof(ContractResource).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !skipped.Contains(property.Name))
            .Where(property => !Equals(property.GetValue(template), property.GetValue(_posted)))
            .Select(property => property.Name)
            .ToList();

        mismatches.ShouldBeEmpty("A ContractResource property was not carried over from the template.");
    }

    [Test]
    public async Task Posted_HasNoIdentity_NoNavigationObject_AndNoValidUntil_ButTheNewNameAndValidFrom()
    {
        var template = FixedHoursTemplate();
        template.CalendarSelection = new CalendarSelectionResource { Id = TemplateCalendarId, Name = "Bern" };
        Setup(template);

        var result = await Run(name: "  Vollzeit 120 BE  ");

        result.Success.ShouldBeTrue(result.Message);
        _postedIdAtCall.ShouldBe(Guid.Empty);
        _posted!.CalendarSelection.ShouldBeNull();
        _posted.CalendarSelectionId.ShouldBe(TemplateCalendarId);
        _posted.ValidUntil.ShouldBeNull();
        _posted.Name.ShouldBe("Vollzeit 120 BE");
        _posted.ValidFrom.ShouldBe(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        _posted.ValidFrom.Kind.ShouldBe(DateTimeKind.Utc);
        template.Id.ShouldBe(TemplateId);
        template.ValidUntil.ShouldNotBeNull();
    }

    [Test]
    public async Task ValidFrom_AlwaysComesFromTheParameter_NotFromTheTemplate()
    {
        Setup(FixedHoursTemplate());

        await Run(new Dictionary<string, object> { [ContractFieldNames.ValidFrom] = "2026-09-15" });

        _posted!.ValidFrom.ShouldBe(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GuaranteedHours_SwitchesToFixedPath_ClearsPercent_AndBandDefaultsToTheNewHours()
    {
        var template = InheritedTemplate();
        Setup(template);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.GuaranteedHours] = 120m });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.GuaranteedHours.ShouldBe(120m);
        _posted.Percent.ShouldBeNull();
        _posted.MinimumHours.ShouldBe(120m);
        _posted.MaximumHours.ShouldBe(120m);
    }

    [Test]
    public async Task GuaranteedHours_OnAFixedHoursTemplate_CollapsesTheOldBandUnlessBandIsGiven()
    {
        Setup(FixedHoursTemplate());

        await Run(new Dictionary<string, object> { [ContractFieldNames.GuaranteedHours] = 140m });

        _posted!.MinimumHours.ShouldBe(140m);
        _posted.MaximumHours.ShouldBe(140m);
    }

    [Test]
    public async Task GuaranteedHours_WithExplicitBand_UsesTheGivenBand()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 140m,
            [ContractFieldNames.MinimumHours] = 100m,
            [ContractFieldNames.MaximumHours] = 180m
        });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.MinimumHours.ShouldBe(100m);
        _posted.MaximumHours.ShouldBe(180m);
    }

    [Test]
    public async Task Percent_SwitchesToInheritedPath_ClearsGuaranteedHours_AndResetsTheBandToZero()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Percent] = 80m });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.GuaranteedHours.ShouldBeNull();
        _posted.Percent.ShouldBe(80m);
        _posted.MinimumHours.ShouldBe(decimal.Zero);
        _posted.MaximumHours.ShouldBe(decimal.Zero);
        _posted.FullTime.ShouldBe(180m);
    }

    [Test]
    public async Task Percent_WithExplicitBand_KeepsTheGivenBand()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.Percent] = 80m,
            [ContractFieldNames.MinimumHours] = 10m,
            [ContractFieldNames.MaximumHours] = 150m
        });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.MinimumHours.ShouldBe(10m);
        _posted.MaximumHours.ShouldBe(150m);
    }

    [Test]
    public async Task Percent_WithOnlyAMinimum_IsRejectedBeforeThePostHandlerCouldThrow()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.Percent] = 80m,
            [ContractFieldNames.MinimumHours] = 10m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ContractFieldNames.MinimumHours);
        await AssertNotPosted();
    }

    [Test]
    public async Task GuaranteedHoursAndPercentTogether_AreRejected()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 120m,
            [ContractFieldNames.Percent] = 80m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("mutually exclusive");
        await AssertNotPosted();
    }

    [TestCase(ContractFieldNames.GuaranteedHours)]
    [TestCase(ContractFieldNames.Percent)]
    [TestCase(ContractFieldNames.MinimumHours)]
    [TestCase(ContractFieldNames.MaximumHours)]
    [TestCase(ContractFieldNames.FullTime)]
    public async Task NegativeValues_AreRejected(string parameter)
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [parameter] = -1m });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be negative");
        await AssertNotPosted();
    }

    [Test]
    public async Task MinimumAboveMaximum_IsRejected()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.MinimumHours] = 190m,
            [ContractFieldNames.MaximumHours] = 150m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not exceed");
        await AssertNotPosted();
    }

    [Test]
    public async Task GuaranteedHoursOutsideTheGivenBand_AreRejected()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 90m,
            [ContractFieldNames.MinimumHours] = 100m,
            [ContractFieldNames.MaximumHours] = 150m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must be between");
        await AssertNotPosted();
    }

    [Test]
    public async Task PaymentInterval_NonIndividual_ClearsTheIndividualPeriod()
    {
        var template = FixedHoursTemplate();
        template.PaymentInterval = PaymentInterval.Individual;
        template.IndividualPeriodId = IndividualPeriodId;
        Setup(template);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PaymentInterval] = "weekly" });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.PaymentInterval.ShouldBe(PaymentInterval.Weekly);
        _posted.IndividualPeriodId.ShouldBeNull();
    }

    [Test]
    public async Task PaymentInterval_IndividualWithoutATemplatePeriod_IsRejected()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PaymentInterval] = "Individual" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("individual pay period");
        await AssertNotPosted();
    }

    [Test]
    public async Task PaymentInterval_IndividualWithATemplatePeriod_KeepsThePeriod()
    {
        var template = FixedHoursTemplate();
        template.IndividualPeriodId = IndividualPeriodId;
        Setup(template);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PaymentInterval] = "Individual" });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.PaymentInterval.ShouldBe(PaymentInterval.Individual);
        _posted.IndividualPeriodId.ShouldBe(IndividualPeriodId);
    }

    [Test]
    public async Task PaymentInterval_WithoutOverride_KeepsTheTemplateIntervalAndPeriod()
    {
        var template = FixedHoursTemplate();
        template.PaymentInterval = PaymentInterval.Individual;
        template.IndividualPeriodId = IndividualPeriodId;
        Setup(template);

        await Run();

        _posted!.PaymentInterval.ShouldBe(PaymentInterval.Individual);
        _posted.IndividualPeriodId.ShouldBe(IndividualPeriodId);
    }

    [TestCase("Weekly,Monthly")]
    [TestCase("3")]
    [TestCase("Quarterly")]
    public async Task PaymentInterval_Malformed_IsRejected(string raw)
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PaymentInterval] = raw });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid paymentInterval");
        await AssertNotPosted();
    }

    [Test]
    public async Task Workdays_ListedDaysTrue_OthersFalse()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Workdays] = "tue, Wed,SAT" });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.WorkOnMonday.ShouldBeFalse();
        _posted.WorkOnTuesday.ShouldBeTrue();
        _posted.WorkOnWednesday.ShouldBeTrue();
        _posted.WorkOnThursday.ShouldBeFalse();
        _posted.WorkOnFriday.ShouldBeFalse();
        _posted.WorkOnSaturday.ShouldBeTrue();
        _posted.WorkOnSunday.ShouldBeFalse();
    }

    [Test]
    public async Task Workdays_UnknownToken_IsRejected()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Workdays] = "Mon,Funday" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Funday");
        await AssertNotPosted();
    }

    [Test]
    public async Task Region_ExactlyOneCalendar_SetsTheCalendarSelection()
    {
        var calendarId = Guid.NewGuid();
        Setup(FixedHoursTemplate(), regionIds: [calendarId]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "zh" });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.CalendarSelectionId.ShouldBe(calendarId);
        await _calendarSelectionRepository.Received(1)
            .GetIdsByStateAsync("CH", "ZH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Region_NoCalendar_IsRejected_ListingTheRealCalendarNames()
    {
        var calendars = new[]
        {
            new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern" },
            new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich" }
        };
        Setup(FixedHoursTemplate(), calendars: calendars, regionIds: []);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "XX" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No holiday calendar");
        result.Message.ShouldContain("Bern");
        result.Message.ShouldContain("Zürich");
        await AssertNotPosted();
    }

    [Test]
    public async Task Region_SeveralCalendars_IsRejected_ListingOnlyTheMatchingOnes()
    {
        var first = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich 2026" };
        var second = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich Schulferien" };
        var unrelated = new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern" };
        Setup(FixedHoursTemplate(), calendars: [first, second, unrelated], regionIds: [first.Id, second.Id]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("several holiday calendars");
        result.Message.ShouldContain("Zürich 2026");
        result.Message.ShouldContain("Zürich Schulferien");
        result.Message.ShouldNotContain("Bern");
        await AssertNotPosted();
    }

    [Test]
    public async Task Region_SeveralCalendars_IncludingTheTemplateCalendar_KeepsTheTemplateCalendar()
    {
        var other = new CalendarSelection { Id = Guid.NewGuid(), Name = "Zürich Schulferien" };
        var templateCalendar = new CalendarSelection { Id = TemplateCalendarId, Name = "Zürich 2026" };
        Setup(FixedHoursTemplate(), calendars: [templateCalendar, other], regionIds: [other.Id, TemplateCalendarId]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "ZH" });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.CalendarSelectionId.ShouldBe(TemplateCalendarId);
        var adapted = ((IReadOnlyList<string>)result.Data!.GetType().GetProperty("AdaptedFields")!.GetValue(result.Data)!).ToList();
        adapted.ShouldNotContain(ContractFieldNames.Region);
    }

    [TestCase(ContractFieldNames.GuaranteedHours, "20h")]
    [TestCase(ContractFieldNames.Percent, "abc")]
    [TestCase(ContractFieldNames.MinimumHours, "20,5")]
    [TestCase(ContractFieldNames.MaximumHours, "1,000")]
    [TestCase(ContractFieldNames.FullTime, "40 hours")]
    [TestCase(ContractFieldNames.PerformsShiftWork, "ja")]
    public async Task UnreadableOptionalValue_IsRejected_NamingTheParameter_InsteadOfKeepingTheTemplateValue(
        string parameter, string raw)
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [parameter] = raw });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{parameter}'");
        result.Message.ShouldContain("unreadable");
        await AssertNotPosted();
    }

    [Test]
    public async Task NumericStringsAndBlankValues_AreReadWithTheInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Setup(InheritedTemplate());

            var result = await Run(new Dictionary<string, object>
            {
                [ContractFieldNames.Percent] = "80.5",
                [ContractFieldNames.FullTime] = " ",
                [ContractFieldNames.PerformsShiftWork] = "true"
            });

            result.Success.ShouldBeTrue(result.Message);
            _posted!.Percent.ShouldBe(80.5m);
            _posted.FullTime.ShouldBe(0m);
            _posted.PerformsShiftWork.ShouldBe(true);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task JsonElementArguments_AreRead()
    {
        Setup(FixedHoursTemplate());
        using var document = System.Text.Json.JsonDocument.Parse("{\"g\":140,\"s\":false}");

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = document.RootElement.GetProperty("g").Clone(),
            [ContractFieldNames.PerformsShiftWork] = document.RootElement.GetProperty("s").Clone()
        });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.GuaranteedHours.ShouldBe(140m);
        _posted.PerformsShiftWork.ShouldBe(false);
    }

    [Test]
    public async Task SchedulingRule_WithAdaptedRuleOverridableValues_AddsAWarningListingExactlyThose()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 140m,
            [ContractFieldNames.Workdays] = "Mon,Tue"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("The copied scheduling rule may override these adapted values: " +
            "guaranteedHours, minimumHours, maximumHours, workdays.");
    }

    [Test]
    public async Task SchedulingRule_PercentPath_DoesNotListGuaranteedHours()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Percent] = 80m });

        result.Message.ShouldContain("scheduling rule may override these adapted values: minimumHours, maximumHours.");
    }

    [Test]
    public async Task SchedulingRule_NothingRuleOverridableAdapted_NoWarning()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PerformsShiftWork] = false });

        result.Message.ShouldNotContain("scheduling rule");
    }

    [Test]
    public async Task SchedulingRule_TemplateWithoutRule_NoWarning()
    {
        var template = FixedHoursTemplate();
        template.SchedulingRuleId = null;
        Setup(template);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.GuaranteedHours] = 140m });

        result.Message.ShouldNotContain("scheduling rule");
    }

    [Test]
    public async Task ExpiredTemplate_AddsAWarning_ActiveTemplateDoesNot()
    {
        var expired = FixedHoursTemplate();
        expired.ValidUntil = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc);
        Setup(expired);
        var expiredResult = await Run();

        Setup(FixedHoursTemplate());
        var activeResult = await Run();

        expiredResult.Success.ShouldBeTrue(expiredResult.Message);
        expiredResult.Message.ShouldContain("The template expired on 2026-05-31");
        activeResult.Message.ShouldNotContain("expired");
    }

    [Test]
    public async Task DuplicateName_CaseInsensitive_IsRejected_WithoutAutoSuffix()
    {
        Setup(FixedHoursTemplate(), existing: [new Contract { Id = Guid.NewGuid(), Name = "vollzeit 120 be" }]);

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("already exists");
        await AssertNotPosted();
    }

    [Test]
    public async Task UnknownTemplate_ReturnsError()
    {
        Setup(FixedHoursTemplate());
        _mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await AssertNotPosted();
    }

    [TestCase("")]
    [TestCase("not-a-guid")]
    public async Task InvalidTemplateId_ReturnsErrorInsteadOfThrowing(string raw)
    {
        Setup(FixedHoursTemplate());

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            [ContractFieldNames.TemplateContractId] = raw,
            [ContractFieldNames.Name] = "X",
            [ContractFieldNames.ValidFrom] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(ContractFieldNames.TemplateContractId);
        await AssertNotPosted();
    }

    [Test]
    public async Task MissingNameOrValidFrom_ReturnsError()
    {
        Setup(FixedHoursTemplate());

        var noName = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            [ContractFieldNames.TemplateContractId] = TemplateId.ToString(),
            [ContractFieldNames.ValidFrom] = "2026-07-01"
        });
        var noDate = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            [ContractFieldNames.TemplateContractId] = TemplateId.ToString(),
            [ContractFieldNames.Name] = "X"
        });
        var badDate = await Run(new Dictionary<string, object> { [ContractFieldNames.ValidFrom] = "not-a-date" });

        noName.Success.ShouldBeFalse();
        noDate.Success.ShouldBeFalse();
        badDate.Success.ShouldBeFalse();
        await AssertNotPosted();
    }

    [Test]
    public async Task ShiftWorkAndFullTime_AreOverridden_WhenGiven()
    {
        Setup(FixedHoursTemplate());

        await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.PerformsShiftWork] = false,
            [ContractFieldNames.FullTime] = 168m
        });

        _posted!.PerformsShiftWork.ShouldBe(false);
        _posted.FullTime.ShouldBe(168m);
    }

    [Test]
    public async Task Result_ExposesTheCreatedId_AndSplitsAdaptedFromCopiedFields()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 140m,
            [ContractFieldNames.Workdays] = "Mon,Tue,Wed,Thu"
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data!;
        data.GetType().GetProperty("Id")!.GetValue(data).ShouldBeOfType<Guid>().ShouldNotBe(Guid.Empty);

        var adapted = ((IReadOnlyList<string>)data.GetType().GetProperty("AdaptedFields")!.GetValue(data)!).ToList();
        var copied = ((IReadOnlyList<string>)data.GetType().GetProperty("CopiedFields")!.GetValue(data)!).ToList();

        adapted.ShouldBe(
            new[]
            {
                ContractFieldNames.Name, ContractFieldNames.ValidFrom, ContractFieldNames.ValidUntil,
                ContractFieldNames.GuaranteedHours, ContractFieldNames.MinimumHours, ContractFieldNames.MaximumHours,
                ContractFieldNames.Workdays
            },
            ignoreOrder: true);
        copied.ShouldContain(ContractFieldNames.NightRate);
        copied.ShouldContain(ContractFieldNames.We3Rate);
        copied.ShouldContain(ContractFieldNames.NightWindow);
        copied.ShouldContain(ContractFieldNames.SchedulingRule);
        copied.ShouldContain(ContractFieldNames.Region);
        copied.ShouldNotContain(ContractFieldNames.GuaranteedHours);
        result.Message.ShouldContain("Surcharges are time credits, never money");
    }

    [Test]
    public async Task MonthlyTargetHoursInterval_WithGuaranteedHours_IsCreatedButWarned()
    {
        Setup(FixedHoursTemplate());

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 120m,
            [ContractFieldNames.PaymentInterval] = "MonthlyTargetHours"
        });

        result.Success.ShouldBeTrue(result.Message);
        _posted!.PaymentInterval.ShouldBe(PaymentInterval.MonthlyTargetHours);
        result.Message.ShouldContain("only applies in months without a row");
    }

    [Test]
    public async Task PostReturningNull_ReturnsError()
    {
        Setup(FixedHoursTemplate());
        _mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns((ContractResource?)null);

        var result = await Run();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be created");
    }

    private static ContractResource DistinctiveTemplate()
    {
        var template = new ContractResource();
        var index = 0;

        foreach (var property in typeof(ContractResource).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            index++;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (type == typeof(Guid))
            {
                property.SetValue(template, Guid.NewGuid());
            }
            else if (type == typeof(string))
            {
                property.SetValue(template, $"value-{property.Name}");
            }
            else if (type == typeof(decimal))
            {
                property.SetValue(template, 10m + index);
            }
            else if (type == typeof(bool))
            {
                var defaultValue = property.GetValue(new ContractResource());
                property.SetValue(template, defaultValue is bool flag ? !flag : true);
            }
            else if (type == typeof(DateTime))
            {
                property.SetValue(template, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index));
            }
            else if (type.IsEnum)
            {
                property.SetValue(template, Enum.ToObject(type, 3));
            }
        }

        template.GuaranteedHours = 100m;
        template.MinimumHours = 80m;
        template.MaximumHours = 120m;
        template.Percent = 70m;
        return template;
    }
}
