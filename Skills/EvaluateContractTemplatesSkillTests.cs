// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the evaluate_contract_templates skill: only active (not deleted, not expired) templates take
/// part, the wishes are parsed strictly, a region without a calendar yields a warning and region differences
/// instead of an error, and the result carries the ranked candidates plus the computed recommendation. The
/// ranking rules themselves are covered by ContractTemplateComparerTests.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateContractTemplatesSkillTests
{
    private static readonly ICompanyClock CompanyClock =
        new FixedCompanyClock(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private IContractRepository _contractRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private EvaluateContractTemplatesSkill _skill = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Permissions.CanViewClients }
    };

    private static Contract Template(string name, decimal hours, DateTime? validUntil = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        GuaranteedHours = hours,
        MinimumHours = hours,
        MaximumHours = hours,
        FullTime = 180m,
        PaymentInterval = PaymentInterval.Monthly,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ValidUntil = validUntil
    };

    private void Setup(
        IEnumerable<Contract> contracts,
        IEnumerable<CalendarSelection>? calendars = null,
        IEnumerable<Guid>? regionIds = null)
    {
        _contractRepository = Substitute.For<IContractRepository>();
        _contractRepository.List().Returns(contracts.ToList());

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository.List().Returns((calendars ?? []).ToList());
        _calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((regionIds ?? []).ToList());

        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries { Abbreviation = "CH" });

        _skill = new EvaluateContractTemplatesSkill(
            _contractRepository, _calendarSelectionRepository, _countryResolver, CompanyClock);
    }

    private Task<SkillResult> Run(Dictionary<string, object>? parameters = null) =>
        _skill.ExecuteAsync(Ctx(), parameters ?? new Dictionary<string, object>());

    [Test]
    public async Task OnlyActiveTemplatesTakePart_ExpiredAreIgnored()
    {
        Setup(
        [
            Template("Active open", 160m),
            Template("Active until later", 160m, validUntil: new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            Template("Ends today", 160m, validUntil: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            Template("Expired", 160m, validUntil: new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc))
        ]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.GuaranteedHours] = 160m });

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.ActiveTemplateCount.ShouldBe(3);
        data.Candidates.Select(c => c.Name).ShouldBe(new[] { "Active open", "Active until later", "Ends today" });
    }

    [Test]
    public async Task ReturnsAtMostTheConfiguredNumberOfCandidates_ClosestFirst()
    {
        Setup(Enumerable.Range(1, 6).Select(i => Template($"Contract {i}", 100m + i)).ToList());

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.GuaranteedHours] = 104m });

        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.Candidates.Count.ShouldBe(ContractTemplateAdvisoryDefaults.MaxCandidates);
        data.Candidates[0].Name.ShouldBe("Contract 4");
        data.Candidates[0].DifferenceCount.ShouldBe(0);
        result.Message.ShouldBe(data.Recommendation);
    }

    [Test]
    public async Task NoContractsAtAll_RecommendsCreatingFromScratch()
    {
        Setup([]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Percent] = 80m });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("from scratch");
        result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>().Candidates.ShouldBeEmpty();
    }

    [Test]
    public async Task NoWishes_StillSucceeds_AndAsksForTheMissingFacts()
    {
        Setup([Template("A", 160m)]);

        var result = await Run();

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("no wishes were given");
    }

    [Test]
    public async Task Region_WithOneMatchingCalendar_MatchesTheTemplateThatUsesIt()
    {
        var calendarId = Guid.NewGuid();
        var withCalendar = Template("Zurich contract", 160m);
        withCalendar.CalendarSelectionId = calendarId;
        var withoutCalendar = Template("Aaa contract", 160m);
        Setup(
            [withoutCalendar, withCalendar],
            calendars: [new CalendarSelection { Id = calendarId, Name = "Zürich" }],
            regionIds: [calendarId]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "zh" });

        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.Candidates[0].Name.ShouldBe("Zurich contract");
        data.Candidates[0].DifferenceCount.ShouldBe(0);
        data.Candidates[0].HolidayCalendarName.ShouldBe("Zürich");
        data.Candidates[1].DifferenceCount.ShouldBe(1);
        data.Warnings.ShouldBeEmpty();
        await _calendarSelectionRepository.Received(1).GetIdsByStateAsync("CH", "ZH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Region_WithoutAnyCalendar_IsAWarningWithDifferences_NotAnError()
    {
        Setup([Template("A", 160m)], calendars: [new CalendarSelection { Id = Guid.NewGuid(), Name = "Bern" }]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Region] = "XX" });

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.Candidates.Count.ShouldBe(1);
        data.Candidates[0].DifferenceCount.ShouldBe(1);
        data.Warnings.Count.ShouldBe(1);
        data.Warnings[0].ShouldContain("XX");
        data.Warnings[0].ShouldContain("Bern");
    }

    [Test]
    public async Task GuaranteedHoursAndPercentTogether_AreComparedIndependently_WithAWarning()
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.GuaranteedHours] = 160m,
            [ContractFieldNames.Percent] = 80m
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.WishCount.ShouldBe(2);
        data.Candidates[0].DifferenceCount.ShouldBe(1);
        data.Warnings.ShouldContain(warning => warning.Contains("mutually exclusive"));
    }

    [Test]
    public async Task ParsedWishes_PaymentIntervalWorkdaysShiftWorkAndFullTime_ReachTheComparison()
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object>
        {
            [ContractFieldNames.PaymentInterval] = "biweekly",
            [ContractFieldNames.Workdays] = "Mon,Tue",
            [ContractFieldNames.PerformsShiftWork] = true,
            [ContractFieldNames.FullTime] = 168m
        });

        var data = result.Data.ShouldBeOfType<ContractTemplateEvaluationResult>();
        data.WishCount.ShouldBe(4);
        data.Candidates[0].Differences.Select(d => d.Field).ShouldBe(
            new[]
            {
                ContractFieldNames.PaymentInterval, ContractFieldNames.FullTime,
                ContractFieldNames.PerformsShiftWork, ContractFieldNames.Workdays
            },
            ignoreOrder: true);
    }

    [Test]
    public async Task InvalidPaymentInterval_ReturnsError()
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.PaymentInterval] = "Weekly,Monthly" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid paymentInterval");
    }

    [Test]
    public async Task InvalidWorkdays_ReturnsError()
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object> { [ContractFieldNames.Workdays] = "Mon,Someday" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Someday");
    }

    [TestCase(ContractFieldNames.GuaranteedHours, "20h")]
    [TestCase(ContractFieldNames.Percent, "viel")]
    [TestCase(ContractFieldNames.FullTime, "20,5")]
    [TestCase(ContractFieldNames.PerformsShiftWork, "ja")]
    public async Task UnreadableWish_ReturnsErrorNamingTheParameter(string parameter, string raw)
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object> { [parameter] = raw });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{parameter}'");
        result.Message.ShouldContain("unreadable");
    }

    [TestCase(ContractFieldNames.GuaranteedHours)]
    [TestCase(ContractFieldNames.Percent)]
    [TestCase(ContractFieldNames.FullTime)]
    public async Task NegativeWish_ReturnsError(string parameter)
    {
        Setup([Template("A", 160m)]);

        var result = await Run(new Dictionary<string, object> { [parameter] = -5m });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be negative");
    }

    [Test]
    public async Task TheSkill_NeverWrites()
    {
        Setup([Template("A", 160m)]);

        await Run(new Dictionary<string, object> { [ContractFieldNames.Percent] = 80m });

        await _contractRepository.DidNotReceive().Add(Arg.Any<Contract>());
        await _contractRepository.DidNotReceive().Put(Arg.Any<Contract>());
        await _contractRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }
}
