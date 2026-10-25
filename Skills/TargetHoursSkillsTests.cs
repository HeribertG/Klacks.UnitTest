// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// get_target_hours_calculation and evaluate_target_hours_setup: a hidden person reads like an unknown one, the month
/// defaults to the company's current month, the view names the way and the formula, the advice without a name runs in
/// company mode, and invalid input is refused before anything runs.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class TargetHoursSkillsTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);
    private static readonly DateOnly Start = new(2026, 11, 1);

    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _visibility = null!;
    private ITargetHoursCalculationService _service = null!;
    private ICompanyClock _clock = null!;
    private Client _mueller = null!;

    [SetUp]
    public void SetUp()
    {
        _mueller = new Client { Id = Guid.NewGuid(), FirstName = "Anna", Name = "Müller" };
        _search = Substitute.For<IClientSearchRepository>();
        _search.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = [new ClientSearchItem { Id = _mueller.Id, FirstName = "Anna", LastName = "Müller", IdNumber = 7 }] });
        _clients = Substitute.For<IClientRepository>();
        _clients.Get(_mueller.Id).Returns(_mueller);
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _visibility.IsVisibleAsync(_mueller.Id, Arg.Any<CancellationToken>()).Returns(true);
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);

        _service = Substitute.For<ITargetHoursCalculationService>();
        _service.CalculateAsync(_mueller.Id, Arg.Any<DateOnly>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(Calculation());
        _service.LoadCompanyFactsAsync(2026, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<int, decimal> { [1] = 168m }, Workdays(), new AbsenceHoursSettingsSummary(170m, 180m, 8m, (int)PaymentInterval.Monthly)));
    }

    [Test]
    public async Task Get_MissingMonth_UsesTheCompanyMonth_AndNamesWayAndFormula()
    {
        var result = await GetSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller" });

        result.Success.ShouldBeTrue(result.Message);
        await _service.Received(1).CalculateAsync(_mueller.Id, Start, null, Arg.Any<CancellationToken>());
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Way").GetString().ShouldBe(nameof(TargetHoursWay.NoContract));
        data.GetProperty("GuaranteedHoursFormula").GetString().ShouldBe("monthly row 168 h");
        data.GetProperty("PeriodTargetHours").GetDecimal().ShouldBe(168m);
        result.Message.ShouldContain("no contract, company value at 100 %");
        result.Message.ShouldContain("never money");
    }

    [Test]
    public async Task Get_YearMonthParameter_IsAccepted()
    {
        var result = await GetSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller", ["month"] = "2026-11" });

        result.Success.ShouldBeTrue(result.Message);
        await _service.Received(1).CalculateAsync(_mueller.Id, Start, null, Arg.Any<CancellationToken>());
    }

    [TestCase("month", "kein monat")]
    [TestCase("paymentInterval", "Fortnightly")]
    public async Task Get_InvalidInput_IsRefused(string parameter, string value)
    {
        var result = await GetSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller", [parameter] = value });

        result.Success.ShouldBeFalse();
        await _service.DidNotReceiveWithAnyArgs().CalculateAsync(default, default, default, default);
    }

    [Test]
    public async Task Get_HiddenPerson_IsAnsweredLikeAnUnknownOne()
    {
        _visibility.IsVisibleAsync(_mueller.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await GetSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientResolver.NotFoundMessage(null, "Müller"));
    }

    [Test]
    public async Task Evaluate_WithoutAName_RunsInCompanyMode()
    {
        var result = await EvaluateSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["month"] = "2026-03" });

        result.Success.ShouldBeTrue(result.Message);
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Calculation").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("Findings").EnumerateArray().Single().GetProperty("Code").GetInt32()
            .ShouldBe((int)TargetHoursSetupFindingCode.MonthlyRowsIncomplete);
        result.Message.ShouldStartWith("Company-wide view");
        await _service.DidNotReceiveWithAnyArgs().CalculateAsync(default, default, default, default);
    }

    [Test]
    public async Task Evaluate_ForAPerson_KeepsFactsApartFromFindings_AndHandsTheDesiredShareOn()
    {
        var result = await EvaluateSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller", ["desiredWorkloadPercent"] = "60" });

        result.Success.ShouldBeTrue(result.Message);
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Calculation").GetProperty("Way").GetString().ShouldBe(nameof(TargetHoursWay.NoContract));
        data.GetProperty("DesiredWorkloadPercent").GetDecimal().ShouldBe(60m);
        data.GetProperty("Findings").EnumerateArray().Select(finding => finding.GetProperty("Code").GetInt32())
            .ShouldContain((int)TargetHoursSetupFindingCode.NoContractButNotFullWorkload);
        data.GetProperty("Recommendation").GetString().ShouldBe(result.Message);
    }

    [Test]
    public async Task Evaluate_NegativeDesiredShare_IsRefused()
    {
        var result = await EvaluateSut().ExecuteAsync(Context(), new Dictionary<string, object> { ["lastName"] = "Müller", ["desiredWorkloadPercent"] = "-5" });

        result.Success.ShouldBeFalse();
    }

    private static IReadOnlyDictionary<int, int> Workdays() => Enumerable.Range(1, 12).ToDictionary(month => month, _ => 21);

    private TargetHoursCalculation Calculation() => new()
    {
        ClientId = _mueller.Id,
        PeriodStart = Start,
        PeriodEnd = new DateOnly(2026, 11, 30),
        ViewInterval = (int)PaymentInterval.Monthly,
        ContractData = new EffectiveContractData
        {
            HasActiveContract = false,
            DefaultWorkingHours = 8m,
            GuaranteedHours = 168m,
            GuaranteedHoursSource = ContractValueSource.MonthlyTargetHours,
            FullTime = 180m,
            WorkloadPercent = 100m,
            WorkloadPercentSource = WorkloadPercentSource.MonthlyTargetHoursFullWorkload,
            PaymentInterval = (int)PaymentInterval.Monthly
        },
        Way = TargetHoursWay.NoContract,
        ContractsActiveAtPeriodStart = [],
        Settings = new AbsenceHoursSettingsSummary(170m, 180m, 8m, (int)PaymentInterval.Monthly),
        MonthlyTargetHoursRow = 168m,
        MonthlyTargetHoursOfYear = Enumerable.Range(1, 12).ToDictionary(month => month, _ => 168m),
        WorkdaysPerMonth = Workdays(),
        PeriodTargetHours = 168m
    };

    private GetTargetHoursCalculationSkill GetSut() => new(_search, _clients, _visibility, _service, _clock);

    private EvaluateTargetHoursSetupSkill EvaluateSut() => new(_search, _clients, _visibility, _service, _clock);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        UserPermissions = Permissions.ExpandRoles([Roles.Admin]),
    };
}
