// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity gate for the "lay the calculation open" skill: the hours AbsenceHoursCalculationService reports for an
/// employee and a day must equal the hours BreakMacroService writes on a booked absence of the same shape, on the
/// real pipeline - real ClientContractDataProvider, real MacroDataProvider, real macro compilation of the seeded
/// vacation macro - with the same payment interval the Ui sends. It also pins the owner's case: an employee without a
/// contract and company settings 170 / 180 / 8 h credits the full 8 h per vacation day (no contract = 100 %, like an
/// inheriting contract with an empty percent), and a Monthly view sees a MonthlyTargetHours contract (Weg 1).
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Breaks;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Breaks;

[TestFixture]
public class AbsenceHoursCalculationServiceParityTests
{
    private const string VacationMacro = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate
import guaranteedhours
import fulltime
import percent
import weekendday1
import weekendday2
import weekendday3

IF Weekday = WeekendDay1 OR Weekday = WeekendDay2 OR Weekday = WeekendDay3 OR Holiday THEN
	Hour = 0
ELSE
	IF GuaranteedHours > 0 THEN
		Hour = Hour * Percent / 100
	ELSE
		Hour = 0
	END IF
END IF

OUTPUT 1, Hour";

    private const decimal SettingsGuaranteedHours = 170m;
    private const decimal SettingsFullTime = 180m;
    private const decimal SettingsDailyHours = 8m;
    private const int MonthlyInterval = (int)PaymentInterval.Monthly;

    private static readonly DateOnly Wednesday = new(2026, 11, 4);
    private static readonly DateOnly Saturday = new(2026, 11, 7);
    private static readonly Guid MacroId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private Absence _vacation = null!;
    private IClientRepository _clients = null!;
    private BreakMacroService _breakMacroService = null!;
    private AbsenceHoursCalculationService _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _vacation = new Absence
        {
            Id = Guid.NewGuid(),
            MacroId = MacroId,
            Name = new MultiLanguage { De = "Ferien", En = "Vacation" },
            Abbreviation = new MultiLanguage { De = "Fe", En = "Va" },
            Description = new MultiLanguage()
        };
        _context.Absence.Add(_vacation);
        AddSetting(SettingKeys.GuaranteedHours, SettingsGuaranteedHours);
        AddSetting(SettingKeys.FullTime, SettingsFullTime);
        AddSetting(SettingKeys.DefaultWorkingHours, SettingsDailyHours);
        AddSetting(SettingKeys.PaymentInterval, MonthlyInterval);
        await _context.SaveChangesAsync();

        var contractDataProvider = new ClientContractDataProvider(
            _context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());

        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });
        var macroDataProvider = new MacroDataProvider(
            _context,
            new ClientHolidayCalendarResolver(_context, Substitute.For<IHolidayCalculatorCache>()),
            contractDataProvider,
            Substitute.For<IWorkChangeEffectiveTimeService>(),
            weekConfiguration);

        var macroManagement = Substitute.For<IMacroManagementService>();
        macroManagement.GetMacroAsync(MacroId).Returns(new Macro { Id = MacroId, Name = "Vacation", Content = VacationMacro });
        var compilation = new MacroCompilationService(
            macroManagement, new MacroCache(), new MacroEngine(), NullLogger<MacroCompilationService>.Instance);

        _breakMacroService = new BreakMacroService(_context, macroDataProvider, compilation, NullLogger<BreakMacroService>.Instance);

        _clients = Substitute.For<IClientRepository>();
        var absences = Substitute.For<IAbsenceRepository>();
        absences.List().Returns(new List<Absence> { _vacation });
        var breaks = Substitute.For<IBreakRepository>();
        breaks.GetByClientAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, string>>(
                _context.Settings.AsNoTracking().ToDictionary(setting => setting.Type, setting => setting.Value)));

        var monthlyTargetHours = Substitute.For<IMonthlyTargetHoursRepository>();
        monthlyTargetHours.ListByYear(Arg.Any<int>()).Returns(new List<MonthlyTargetHours>());
        var facts = new EmployeeContractFactsLoader(
            _clients,
            Substitute.For<ISchedulingRuleRepository>(),
            monthlyTargetHours,
            settingsReader,
            weekConfiguration,
            new ClientHolidayCalendarResolver(_context, Substitute.For<IHolidayCalculatorCache>()));

        _sut = new AbsenceHoursCalculationService(
            facts,
            absences,
            breaks,
            contractDataProvider,
            macroDataProvider,
            compilation,
            macroManagement,
            monthlyTargetHours);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task NoContract_Weekday_MatchesTheBookedHours_AndCreditsFullWorkload()
    {
        var clientId = Guid.NewGuid();
        _clients.Get(clientId).Returns(new Client { Id = clientId, FirstName = "Hans", Name = "Muster" });

        var calculation = await _sut.CalculateAsync(clientId, Wednesday, _vacation, null);
        var booked = await BookAsync(clientId, Wednesday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        calculation.ResultHours.ShouldBe(SettingsDailyHours);
        calculation.ContractData.HasActiveContract.ShouldBeFalse();
        calculation.ContractData.WorkloadPercentSource.ShouldBe(WorkloadPercentSource.NoContractFullWorkload);
        calculation.ContractData.WorkloadRatioBasis.ShouldBeNull();
        calculation.ContractData.DefaultWorkingHoursSource.ShouldBe(ContractValueSource.Settings);
        calculation.MonthlyTargetHoursOfYear.ShouldNotBeNull();
        calculation.WorkdaysPerMonth!.Count.ShouldBe(12);
        calculation.WorkdaysPerMonth[11].ShouldBe(21);
        calculation.PaymentIntervalUsed.ShouldBe(MonthlyInterval);
        calculation.MacroName.ShouldBe("Vacation");
    }

    [Test]
    public async Task NoContract_WeekendDay_MatchesTheBookedHours_AndIsZero()
    {
        var clientId = Guid.NewGuid();
        _clients.Get(clientId).Returns(new Client { Id = clientId, FirstName = "Hans", Name = "Muster" });

        var calculation = await _sut.CalculateAsync(clientId, Saturday, _vacation, null);
        var booked = await BookAsync(clientId, Saturday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        calculation.ResultHours.ShouldBe(0m);
        calculation.IsWeekendDay.ShouldBeTrue();
    }

    [Test]
    public async Task PartTimeContract_MatchesTheBookedHours_AndNamesTheContract()
    {
        var clientId = Guid.NewGuid();
        var contract = await SeedContractAsync(clientId, "Teilzeit 160", PaymentInterval.Monthly, 160m, 180m);

        var calculation = await _sut.CalculateAsync(clientId, Wednesday, _vacation, MonthlyInterval);
        var booked = await BookAsync(clientId, Wednesday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        Math.Round(calculation.ResultHours!.Value, 2).ShouldBe(7.11m);
        calculation.ContractData.WorkloadPercentSource.ShouldBe(WorkloadPercentSource.ContractRatio);
        calculation.ContractsActiveOnDate.ShouldHaveSingleItem().ContractId.ShouldBe(contract.Id);
        calculation.ContractsActiveOnDate[0].UsedByCalculation.ShouldBeTrue();
        calculation.ContractIgnoredByPaymentInterval.ShouldBeFalse();
    }

    [Test]
    public async Task ContractOnOtherInterval_IsIgnoredLikeTheBookingPathDoes_AndReportedAsIgnored()
    {
        var clientId = Guid.NewGuid();
        await SeedContractAsync(clientId, "Weekly 40", PaymentInterval.Weekly, 40m, 42m);

        var calculation = await _sut.CalculateAsync(clientId, Wednesday, _vacation, MonthlyInterval);
        var booked = await BookAsync(clientId, Wednesday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        calculation.ResultHours.ShouldBe(SettingsDailyHours);
        calculation.ContractData.HasActiveContract.ShouldBeFalse();
        calculation.ContractIgnoredByPaymentInterval.ShouldBeTrue();
        calculation.ContractsActiveOnDate.ShouldHaveSingleItem().Name.ShouldBe("Weekly 40");
        calculation.ContractsActiveOnDate[0].FilteredByPaymentInterval.ShouldBeTrue();
    }

    [Test]
    public async Task MonthlyTargetHoursContract_IsSeenByAMonthlyView_AndMatchesTheBookedHours()
    {
        var clientId = Guid.NewGuid();
        var contract = await SeedContractAsync(clientId, "Weg 1 - 50 %", PaymentInterval.MonthlyTargetHours, 160m, 180m);

        var calculation = await _sut.CalculateAsync(clientId, Wednesday, _vacation, MonthlyInterval);
        var booked = await BookAsync(clientId, Wednesday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        Math.Round(calculation.ResultHours!.Value, 2).ShouldBe(7.11m);
        calculation.ContractData.HasActiveContract.ShouldBeTrue();
        calculation.ContractData.ContractId.ShouldBe(contract.Id);
        calculation.ContractIgnoredByPaymentInterval.ShouldBeFalse();
        calculation.ContractsActiveOnDate.ShouldHaveSingleItem().FilteredByPaymentInterval.ShouldBeFalse();
    }

    [Test]
    public async Task ZeroHoursContract_MatchesTheBookedHours_AndIsZero()
    {
        var clientId = Guid.NewGuid();
        await SeedContractAsync(clientId, "Teilzeit 0 Std", PaymentInterval.Monthly, 0m, 180m);

        var calculation = await _sut.CalculateAsync(clientId, Wednesday, _vacation, MonthlyInterval);
        var booked = await BookAsync(clientId, Wednesday, MonthlyInterval);

        calculation.ResultHours.ShouldBe(booked.WorkTime);
        calculation.ResultHours.ShouldBe(0m);
    }

    [Test]
    public async Task Calculation_NeverPersistsAnEntry()
    {
        var clientId = Guid.NewGuid();
        _clients.Get(clientId).Returns(new Client { Id = clientId, FirstName = "Hans", Name = "Muster" });

        await _sut.CalculateAsync(clientId, Wednesday, _vacation, null);

        (await _context.Break.CountAsync()).ShouldBe(0);
        _context.ChangeTracker.HasChanges().ShouldBeFalse();
    }

    private async Task<Break> BookAsync(Guid clientId, DateOnly date, int? paymentInterval)
    {
        var entry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = _vacation.Id,
            CurrentDate = date,
            StartTime = TimeOnly.MinValue,
            EndTime = DayTimeConstants.LastMinuteOfDay,
            WorkTime = decimal.Zero
        };

        await _breakMacroService.ProcessBreakMacroAsync(entry, paymentInterval);
        return entry;
    }

    private async Task<Contract> SeedContractAsync(
        Guid clientId, string name, PaymentInterval interval, decimal guaranteedHours, decimal fullTime)
    {
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = name,
            PaymentInterval = interval,
            GuaranteedHours = guaranteedHours,
            FullTime = fullTime,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var assignment = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            Contract = contract,
            FromDate = new DateOnly(2020, 1, 1),
            IsActive = true
        };
        _context.Contract.Add(contract);
        _context.ClientContract.Add(assignment);
        await _context.SaveChangesAsync();

        var client = new Client { Id = clientId, FirstName = "Anna", Name = "Müller" };
        client.ClientContracts.Add(assignment);
        _clients.Get(clientId).Returns(client);

        return contract;
    }

    private void AddSetting(string key, decimal value) =>
        _context.Settings.Add(new SettingsEntity
        {
            Id = Guid.NewGuid(),
            Type = key,
            Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
}
