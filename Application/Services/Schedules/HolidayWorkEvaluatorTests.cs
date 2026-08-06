// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the holiday-work detector: it reports a statutory holiday the client works on, stays
/// silent on ordinary days and on unofficial holidays, is suppressed by a global exemption as well as
/// by one bound to the client's scheduling rule but not by one bound to a different rule, and
/// escalates to an overridable error when the holidayWork rule is configured as Block.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.CalendarSelections;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Holidays;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class HolidayWorkEvaluatorTests
{
    private static readonly DateOnly Holiday = new(2026, 12, 25);
    private static readonly DateOnly OrdinaryDay = new(2026, 12, 23);
    private static readonly Guid ContractRuleId = Guid.NewGuid();

    private IHolidayWorkExemptionRuleRepository _exemptions = null!;
    private IClientHolidayCalendarResolver _calendarResolver = null!;
    private IClientContractDataProvider _contractData = null!;
    private IComplianceEnforcementResolver _enforcement = null!;
    private HolidayWorkEvaluator _sut = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        _clientId = Guid.NewGuid();

        _exemptions = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        _exemptions.GetAllActiveAsync().Returns(new List<HolidayWorkExemptionRule>());

        // Build the calculator BEFORE handing it to Returns: configuring one substitute inside
        // another's Returns() makes NSubstitute attribute the call to the wrong substitute.
        var calculator = BuildCalculator();
        _calendarResolver = Substitute.For<IClientHolidayCalendarResolver>();
        _calendarResolver.GetCalculatorAsync(Arg.Any<Guid?>(), Arg.Any<int>()).Returns(calculator);

        _contractData = Substitute.For<IClientContractDataProvider>();
        GivenContractRulePerDate(ContractRuleId);

        _enforcement = Substitute.For<IComplianceEnforcementResolver>();
        _enforcement.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        _sut = new HolidayWorkEvaluator(_exemptions, _calendarResolver, _contractData, _enforcement);
    }

    /// <summary>
    /// Christmas is official, the day before it is a rule that is not mandatory - so only the first
    /// one may ever be reported.
    /// </summary>
    private static IHolidaysListCalculator BuildCalculator()
    {
        var calculator = Substitute.For<IHolidaysListCalculator>();
        calculator.IsHoliday(Holiday).Returns(HolidayStatus.OfficialHoliday);
        calculator.IsHoliday(OrdinaryDay).Returns(HolidayStatus.NotAHoliday);
        calculator.IsHoliday(new DateOnly(2026, 12, 24)).Returns(HolidayStatus.UnofficialHoliday);
        calculator.GetHolidayInfo(Holiday).Returns(new HolidayDate { CurrentName = "Christmas" });
        return calculator;
    }


    /// <summary>
    /// Every evaluated day resolves to the same contract. Tests that care about a contract change
    /// inside the range override this with their own per-date map.
    /// </summary>
    private void GivenContractRulePerDate(Guid? ruleId) =>
        _contractData.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var from = ci.ArgAt<DateOnly>(1);
                var until = ci.ArgAt<DateOnly>(2);
                var map = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var d = from; d <= until; d = d.AddDays(1))
                {
                    map[d] = new Dictionary<Guid, EffectiveContractData>
                    {
                        [_clientId] = new() { SchedulingRuleId = ruleId },
                    };
                }
                return map;
            });

    private void GivenExemptions(params HolidayWorkExemptionRule[] rules) =>
        _exemptions.GetAllActiveAsync().Returns(rules.ToList());

    [Test]
    public async Task Evaluate_WorkOnStatutoryHoliday_IsReported()
    {
        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        var entry = entries.ShouldHaveSingleItem();
        entry.Comment.ShouldBe(ScheduleValidationKeys.HolidayWork);
        entry.Date.ShouldBe(Holiday);
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams["holiday"].ShouldBe("Christmas");
    }

    [Test]
    public async Task Evaluate_OrdinaryDayAndUnofficialHoliday_AreNotReported()
    {
        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [OrdinaryDay, new DateOnly(2026, 12, 24)]);

        entries.ShouldBeEmpty();
    }

    [Test]
    public async Task Evaluate_GlobalExemption_SuppressesTheFinding()
    {
        GivenExemptions(new HolidayWorkExemptionRule { SchedulingRuleId = null });

        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        entries.ShouldBeEmpty("an exemption without a scheduling rule covers everyone");
    }

    [Test]
    public async Task Evaluate_ExemptionOfTheClientsOwnRule_SuppressesTheFinding()
    {
        GivenExemptions(new HolidayWorkExemptionRule { SchedulingRuleId = ContractRuleId });

        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        entries.ShouldBeEmpty();
    }

    [Test]
    public async Task Evaluate_ExemptionOfAnotherRule_DoesNotSuppressTheFinding()
    {
        GivenExemptions(new HolidayWorkExemptionRule { SchedulingRuleId = Guid.NewGuid() });

        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        entries.ShouldHaveSingleItem("an exemption bound to a different scheduling rule must not cover this client");
    }

    [Test]
    public async Task Evaluate_BlockMode_ReportsAnOverridableError()
    {
        _enforcement.GetModeAsync(ComplianceRuleNames.HolidayWork).Returns(RuleEnforcementMode.Block);

        var entry = (await _sut.EvaluateAsync(_clientId, "Probe", [Holiday])).ShouldHaveSingleItem();

        entry.Type.ShouldBe(ScheduleValidationType.Error);
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.HolidayWork);
    }

    [Test]
    public async Task Evaluate_NoWorkDates_TouchesNothing()
    {
        var entries = await _sut.EvaluateAsync(_clientId, "Probe", []);

        entries.ShouldBeEmpty();
        await _contractData.DidNotReceiveWithAnyArgs().GetEffectiveContractDataAsync(default, default, default);
    }

    [Test]
    public async Task Evaluate_NoCalendarConfigured_ReportsNothing()
    {
        _calendarResolver.GetCalculatorAsync(Arg.Any<Guid?>(), Arg.Any<int>()).Returns((IHolidaysListCalculator?)null);

        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        entries.ShouldBeEmpty("without a calendar there is nothing to judge against");
    }

    /// <summary>
    /// The range check hands over every work date of the whole period at once. If the contract is
    /// resolved only for the first of them, a contract change inside the range is judged by the wrong
    /// scheduling rule - an exemption that no longer applies would keep suppressing findings.
    /// </summary>
    [Test]
    public async Task Evaluate_ContractChangesInsideTheRange_JudgesEachDayByItsOwnContract()
    {
        var exemptRuleId = Guid.NewGuid();
        var secondHoliday = new DateOnly(2026, 12, 26);
        var calculator = Substitute.For<IHolidaysListCalculator>();
        calculator.IsHoliday(Holiday).Returns(HolidayStatus.OfficialHoliday);
        calculator.IsHoliday(secondHoliday).Returns(HolidayStatus.OfficialHoliday);
        _calendarResolver.GetCalculatorAsync(Arg.Any<Guid?>(), Arg.Any<int>()).Returns(calculator);

        // Exempt on the first day, no longer exempt on the second.
        _contractData.GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>
            {
                [Holiday] = new() { [_clientId] = new() { SchedulingRuleId = exemptRuleId } },
                [secondHoliday] = new() { [_clientId] = new() { SchedulingRuleId = ContractRuleId } },
            });
        GivenExemptions(new HolidayWorkExemptionRule { SchedulingRuleId = exemptRuleId });

        var entries = await _sut.EvaluateAsync(_clientId, "Probe", [Holiday, secondHoliday]);

        var entry = entries.ShouldHaveSingleItem("only the day whose contract lost the exemption may be reported");
        entry.Date.ShouldBe(secondHoliday);
    }

    /// <summary>
    /// The range check calls this evaluator once per client of the period, on every hub join. Reading
    /// the exemption table per client would turn a handful of rows into one query per client.
    /// </summary>
    [Test]
    public async Task Evaluate_CalledForSeveralClients_ReadsTheExemptionsOnlyOnce()
    {
        await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);
        await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);
        await _sut.EvaluateAsync(_clientId, "Probe", [Holiday]);

        await _exemptions.Received(1).GetAllActiveAsync();
    }
}
