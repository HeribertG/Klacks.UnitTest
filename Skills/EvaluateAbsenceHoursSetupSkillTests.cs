// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// evaluate_absence_hours_setup: read-only advice built from the real calculation - a missing date falls back to the
/// company clock, the findings and the recommendation come from the advisor, a hidden person reads like an unknown
/// one, and a negative desired-hours value is refused.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateAbsenceHoursSetupSkillTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _visibility = null!;
    private IAbsenceHoursCalculationService _service = null!;
    private ICompanyClock _clock = null!;
    private Client _mueller = null!;
    private Absence _vacation = null!;

    [SetUp]
    public void SetUp()
    {
        _mueller = new Client { Id = Guid.NewGuid(), FirstName = "Anna", Name = "Müller" };
        _vacation = new Absence { Id = Guid.NewGuid(), MacroId = Guid.NewGuid(), Name = new MultiLanguage { De = "Ferien", En = "Vacation" } };

        _search = Substitute.For<IClientSearchRepository>();
        _search.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = _mueller.Id, FirstName = "Anna", LastName = "Müller", IdNumber = 7 }],
            });
        _clients = Substitute.For<IClientRepository>();
        _clients.Get(_mueller.Id).Returns(_mueller);
        _visibility = Substitute.For<IClientVisibilityGuard>();
        _visibility.IsVisibleAsync(_mueller.Id, Arg.Any<CancellationToken>()).Returns(true);
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);

        _service = Substitute.For<IAbsenceHoursCalculationService>();
        _service.ResolveAbsenceAsync(Arg.Any<string?>(), _mueller.Id, Today, Arg.Any<CancellationToken>())
            .Returns((_vacation, (string?)null));
        _service.CalculateAsync(_mueller.Id, Today, _vacation, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(NoContractCalculation());
    }

    [Test]
    public async Task MissingDate_UsesTheCompanyToday()
    {
        var result = await Sut().ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await _service.Received(1).CalculateAsync(_mueller.Id, Today, _vacation, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoContract_RecommendsAssigningAContract_AndKeepsFactsApartFromFindings()
    {
        var result = await Sut().ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Calculation").GetProperty("HasActiveContract").GetBoolean().ShouldBeFalse();
        data.GetProperty("Findings").EnumerateArray().Single().GetProperty("Code").GetInt32()
            .ShouldBe((int)AbsenceHoursSetupFindingCode.NoContract);
        data.GetProperty("Recommendation").GetString().ShouldBe(result.Message);
        result.Message.ShouldContain("Nothing has been changed");
    }

    [Test]
    public async Task DesiredHours_AreHandedToTheAdvisor()
    {
        var parameters = Parameters();
        parameters["desiredHoursPerDay"] = "6";

        var result = await Sut().ExecuteAsync(Context(), parameters);

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("DesiredHoursPerDay").GetDecimal().ShouldBe(6m);
        data.GetProperty("Findings").EnumerateArray()
            .Select(finding => finding.GetProperty("Code").GetInt32())
            .ShouldContain((int)AbsenceHoursSetupFindingCode.DesiredHoursNeedChange);
    }

    [Test]
    public async Task NegativeDesiredHours_AreRefused()
    {
        var parameters = Parameters();
        parameters["desiredHoursPerDay"] = "-1";

        var result = await Sut().ExecuteAsync(Context(), parameters);

        result.Success.ShouldBeFalse();
        await _service.DidNotReceiveWithAnyArgs().CalculateAsync(default, default, default!, default, default);
    }

    [Test]
    public async Task HiddenPerson_IsAnsweredLikeAnUnknownOne()
    {
        _visibility.IsVisibleAsync(_mueller.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Sut().ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientResolver.NotFoundMessage("Anna", "Müller"));
    }

    private AbsenceHoursCalculation NoContractCalculation() => new()
    {
        ClientId = _mueller.Id,
        Date = Today,
        AbsenceId = _vacation.Id,
        AbsenceName = _vacation.Name,
        MacroId = _vacation.MacroId,
        MacroName = "Vacation",
        PaymentIntervalUsed = (int)PaymentInterval.Monthly,
        ContractData = new EffectiveContractData
        {
            HasActiveContract = false,
            DefaultWorkingHours = 8m,
            GuaranteedHours = 170m,
            FullTime = 180m,
            WorkloadPercent = 100m,
            WorkloadPercentSource = WorkloadPercentSource.NoContractFullWorkload
        },
        ContractsActiveOnDate = [],
        Settings = new AbsenceHoursSettingsSummary(170m, 180m, 8m, (int)PaymentInterval.Monthly),
        MacroInput = new MacroData { Hour = 8m, Weekday = 3, GuaranteedHours = 170m, FullTime = 180m, WorkloadPercent = 100m, WeekendDay1 = 6, WeekendDay2 = 7 },
        IsWeekendDay = false,
        MacroExecuted = true,
        ResultHours = 8m
    };

    private EvaluateAbsenceHoursSetupSkill Sut() => new(_search, _clients, _visibility, _service, _clock);

    private static Dictionary<string, object> Parameters() => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
    };

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = "de",
        UserPermissions = Permissions.ExpandRoles([Roles.Admin]),
    };
}
