// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// get_absence_hours_calculation: a person hidden by group visibility reads like an unknown one, the absence type is
/// resolved through the service, the view names every source and the message carries the formula; an invalid date or
/// payment interval is refused before anything runs.
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
public class GetAbsenceHoursCalculationSkillTests
{
    private static readonly DateOnly Wednesday = new(2026, 11, 4);

    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _visibility = null!;
    private IAbsenceHoursCalculationService _service = null!;
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

        _service = Substitute.For<IAbsenceHoursCalculationService>();
        _service.ResolveAbsenceAsync(Arg.Any<string?>(), _mueller.Id, Wednesday, Arg.Any<CancellationToken>())
            .Returns((_vacation, (string?)null));
        _service.CalculateAsync(_mueller.Id, Wednesday, _vacation, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Calculation());
    }

    [Test]
    public async Task HiddenPerson_IsAnsweredLikeAnUnknownOne_AndNothingIsCalculated()
    {
        _visibility.IsVisibleAsync(_mueller.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Sut().ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientResolver.NotFoundMessage("Anna", "Müller"));
        await _service.DidNotReceiveWithAnyArgs().CalculateAsync(default, default, default!, default, default);
    }

    [Test]
    public async Task Success_NamesEverySourceInTheData_AndTheFormulaInTheMessage()
    {
        var result = await Sut().ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Employee").GetString().ShouldBe("Anna Müller");
        data.GetProperty("AbsenceType").GetString().ShouldBe("Ferien");
        data.GetProperty("HasActiveContract").GetBoolean().ShouldBeFalse();
        data.GetProperty("WorkloadPercentSource").GetString().ShouldBe(nameof(WorkloadPercentSource.SettingsRatio));
        data.GetProperty("WorkloadPercentFormula").GetString().ShouldBe("170 / 180 x 100 = 94.44 %");
        data.GetProperty("DailyHoursSource").GetString().ShouldBe(nameof(ContractValueSource.Settings));
        data.GetProperty("ResultHours").GetDecimal().ShouldBe(7.5555m);
        data.GetProperty("ResultFormula").GetString().ShouldBe("8 h x 94.44 % = 7.56 h");
        result.Message.ShouldContain("7.5555 h");
        result.Message.ShouldContain("170 / 180 x 100 = 94.44 %");
        result.Message.ShouldContain("never money");
    }

    [Test]
    public async Task PaymentInterval_IsPassedToTheService()
    {
        var parameters = Parameters();
        parameters["paymentInterval"] = nameof(PaymentInterval.Weekly);

        await Sut().ExecuteAsync(Context(), parameters);

        await _service.Received(1).CalculateAsync(_mueller.Id, Wednesday, _vacation, (int)PaymentInterval.Weekly, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownAbsenceName_IsRefusedWithTheServiceAnswer()
    {
        _service.ResolveAbsenceAsync("Sabbatical", _mueller.Id, Wednesday, Arg.Any<CancellationToken>())
            .Returns(((Absence?)null, "No absence type named 'Sabbatical'."));
        var parameters = Parameters();
        parameters["absenceName"] = "Sabbatical";

        var result = await Sut().ExecuteAsync(Context(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Sabbatical");
    }

    [TestCase("date", "kein datum")]
    [TestCase("paymentInterval", "Fortnightly")]
    public async Task InvalidInput_IsRefused(string parameter, string value)
    {
        var parameters = Parameters();
        parameters[parameter] = value;

        var result = await Sut().ExecuteAsync(Context(), parameters);

        result.Success.ShouldBeFalse();
    }

    private AbsenceHoursCalculation Calculation() => new()
    {
        ClientId = _mueller.Id,
        Date = Wednesday,
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
            WorkloadPercent = 170m / 180m * 100m,
            WorkloadPercentSource = WorkloadPercentSource.SettingsRatio,
            WorkloadRatioBasis = new WorkloadRatioBasis(170m, 180m)
        },
        ContractsActiveOnDate = [],
        Settings = new AbsenceHoursSettingsSummary(170m, 180m, 8m, (int)PaymentInterval.Monthly),
        MacroInput = new MacroData { Hour = 8m, Weekday = 3, GuaranteedHours = 170m, FullTime = 180m, WorkloadPercent = 170m / 180m * 100m, WeekendDay1 = 6, WeekendDay2 = 7 },
        IsWeekendDay = false,
        MacroExecuted = true,
        ResultHours = 7.5555m
    };

    private GetAbsenceHoursCalculationSkill Sut() => new(_search, _clients, _visibility, _service);

    private static Dictionary<string, object> Parameters() => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
        ["date"] = "2026-11-04",
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
