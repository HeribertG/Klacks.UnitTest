// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CompanyRuleDraftValidator: value validation for enums, integer ranges, decimals and
/// HH:mm times, rejection of unknown and empty parameters, and the missing-required reporting including
/// the conditional hours-threshold rule and the "at least one surcharge parameter" rule.
/// </summary>

using Shouldly;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Services.Settings;

[TestFixture]
public class CompanyRuleDraftValidatorTests
{
    private CompanyRuleDraftValidator _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new CompanyRuleDraftValidator(new CompanyRuleParameterCatalog());
    }

    [Test]
    public void Validate_Unknown_Parameter_Is_Invalid()
    {
        var result = _sut.Validate(CompanyRuleKind.CounterRule, "nope", "x");

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Unknown parameter");
    }

    [Test]
    public void Validate_Empty_Value_Is_Invalid()
    {
        var result = _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.EventType, "   ");

        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void Validate_Valid_Enum_Value_Is_Ok()
    {
        _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.EventType, "NightShift").IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_Enum_Value_Is_Case_Insensitive()
    {
        _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.EventType, "nightshift").IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_Invalid_Enum_Value_Is_Invalid()
    {
        var result = _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.Enforcement, "halt");

        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain(CompanyRuleParameterValues.EnforcementWarn);
    }

    [Test]
    public void Validate_Integer_In_Range_Is_Ok()
    {
        _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.Threshold, "25").IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_Integer_Below_Minimum_Is_Invalid()
    {
        _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.Threshold, "0").IsValid.ShouldBeFalse();
    }

    [Test]
    public void Validate_Integer_Non_Numeric_Is_Invalid()
    {
        _sut.Validate(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.Threshold, "many").IsValid.ShouldBeFalse();
    }

    [Test]
    public void Validate_Time_In_Correct_Format_Is_Ok()
    {
        _sut.Validate(CompanyRuleKind.SurchargeSettings, CompanyRuleParameterNames.NightStart, "22:00").IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_Time_In_Wrong_Format_Is_Invalid()
    {
        _sut.Validate(CompanyRuleKind.SurchargeSettings, CompanyRuleParameterNames.NightStart, "10 pm").IsValid.ShouldBeFalse();
    }

    [Test]
    public void Validate_Time_Out_Of_Range_Is_Invalid()
    {
        _sut.Validate(CompanyRuleKind.SurchargeSettings, CompanyRuleParameterNames.NightStart, "25:99").IsValid.ShouldBeFalse();
    }

    [Test]
    public void Validate_Decimal_Is_Ok()
    {
        _sut.Validate(CompanyRuleKind.SurchargeSettings, CompanyRuleParameterNames.NightRate, "1.5").IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_Negative_Decimal_Below_Minimum_Is_Invalid()
    {
        _sut.Validate(CompanyRuleKind.SurchargeSettings, CompanyRuleParameterNames.NightRate, "-1").IsValid.ShouldBeFalse();
    }

    [Test]
    public void GetMissingRequired_Empty_CounterRule_Lists_Static_Required_Without_HoursThreshold()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule };

        var missing = _sut.GetMissingRequired(draft);

        missing.ShouldBe(new[]
        {
            CompanyRuleParameterNames.EventType,
            CompanyRuleParameterNames.Period,
            CompanyRuleParameterNames.Threshold,
            CompanyRuleParameterNames.Enforcement
        }, ignoreOrder: true);
    }

    [Test]
    public void GetMissingRequired_ShiftExceedingHours_Without_HoursThreshold_Reports_It()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule };
        draft.Parameters[CompanyRuleParameterNames.EventType] = nameof(CounterEventType.ShiftExceedingHours);
        draft.Parameters[CompanyRuleParameterNames.Period] = nameof(CounterPeriod.Month);
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "3";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = CompanyRuleParameterValues.EnforcementWarn;

        _sut.GetMissingRequired(draft).ShouldBe(new[] { CompanyRuleParameterNames.HoursThreshold });
    }

    [Test]
    public void GetMissingRequired_NonShift_Event_Does_Not_Require_HoursThreshold()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule };
        draft.Parameters[CompanyRuleParameterNames.EventType] = nameof(CounterEventType.NightShift);
        draft.Parameters[CompanyRuleParameterNames.Period] = nameof(CounterPeriod.Year);
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = CompanyRuleParameterValues.EnforcementBlock;

        _sut.GetMissingRequired(draft).ShouldBeEmpty();
    }

    [Test]
    public void GetMissingRequired_Empty_Surcharge_Reports_AtLeastOneParameter()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings };

        _sut.GetMissingRequired(draft).ShouldBe(new[] { CompanyRuleParameterNames.AtLeastOneSurchargeParameter });
    }

    [Test]
    public void GetMissingRequired_Surcharge_With_One_Parameter_Is_Complete()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings };
        draft.Parameters[CompanyRuleParameterNames.NightRate] = "1.5";

        _sut.GetMissingRequired(draft).ShouldBeEmpty();
    }

    [Test]
    public void GetMissingRequired_Empty_CustomMacro_Requires_Name_And_Script()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CustomMacro };

        _sut.GetMissingRequired(draft).ShouldBe(new[]
        {
            CompanyRuleParameterNames.MacroName,
            CompanyRuleParameterNames.MacroScript
        }, ignoreOrder: true);
    }
}
