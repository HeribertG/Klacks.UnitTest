// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CompanyRuleParameterCatalog: verifies the required/optional flags per rule kind, that
/// surcharge parameters are all optional, that the counter-rule hours threshold is not unconditionally
/// required, and that parameter lookup returns definitions with the expected type and range metadata.
/// </summary>

using System.Linq;
using Shouldly;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Services.Settings;

[TestFixture]
public class CompanyRuleParameterCatalogTests
{
    private CompanyRuleParameterCatalog _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new CompanyRuleParameterCatalog();
    }

    [Test]
    public void Surcharge_Parameters_Are_All_Optional()
    {
        var parameters = _sut.GetParameters(CompanyRuleKind.SurchargeSettings);

        parameters.ShouldNotBeEmpty();
        parameters.ShouldAllBe(p => !p.Required);
    }

    [Test]
    public void Surcharge_Contains_NightWindow_And_Rate_Parameters()
    {
        var names = _sut.GetParameters(CompanyRuleKind.SurchargeSettings).Select(p => p.Name).ToList();

        names.ShouldContain(CompanyRuleParameterNames.NightStart);
        names.ShouldContain(CompanyRuleParameterNames.NightEnd);
        names.ShouldContain(CompanyRuleParameterNames.NightRate);
        names.ShouldContain(CompanyRuleParameterNames.StackingMode);
        names.ShouldContain(CompanyRuleParameterNames.OvertimeTier3Rate);
    }

    [Test]
    public void CounterRule_Requires_EventType_Period_Threshold_Enforcement()
    {
        var required = _sut.GetParameters(CompanyRuleKind.CounterRule)
            .Where(p => p.Required)
            .Select(p => p.Name)
            .ToList();

        required.ShouldBe(new[]
        {
            CompanyRuleParameterNames.EventType,
            CompanyRuleParameterNames.Period,
            CompanyRuleParameterNames.Threshold,
            CompanyRuleParameterNames.Enforcement
        }, ignoreOrder: true);
    }

    [Test]
    public void CounterRule_HoursThreshold_Is_Not_Statically_Required()
    {
        var hoursThreshold = _sut.FindParameter(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.HoursThreshold);

        hoursThreshold.ShouldNotBeNull();
        hoursThreshold!.Required.ShouldBeFalse();
        hoursThreshold.DataType.ShouldBe(CompanyRuleParameterDataType.Decimal);
    }

    [Test]
    public void CounterRule_Threshold_Is_Integer_With_Minimum_One()
    {
        var threshold = _sut.FindParameter(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.Threshold);

        threshold.ShouldNotBeNull();
        threshold!.DataType.ShouldBe(CompanyRuleParameterDataType.Integer);
        threshold.Min.ShouldBe(1m);
    }

    [Test]
    public void CounterRule_EventType_EnumValues_Match_Domain_Enum()
    {
        var eventType = _sut.FindParameter(CompanyRuleKind.CounterRule, CompanyRuleParameterNames.EventType);

        eventType.ShouldNotBeNull();
        eventType!.DataType.ShouldBe(CompanyRuleParameterDataType.Enum);
        eventType.EnumValues.ShouldBe(System.Enum.GetNames(typeof(CounterEventType)), ignoreOrder: true);
    }

    [Test]
    public void CustomMacro_Requires_Name_And_Script_Only()
    {
        var required = _sut.GetParameters(CompanyRuleKind.CustomMacro)
            .Where(p => p.Required)
            .Select(p => p.Name)
            .ToList();

        required.ShouldBe(new[]
        {
            CompanyRuleParameterNames.MacroName,
            CompanyRuleParameterNames.MacroScript
        }, ignoreOrder: true);
    }

    [Test]
    public void FindParameter_Returns_Null_For_Unknown_Name()
    {
        _sut.FindParameter(CompanyRuleKind.CustomMacro, "doesNotExist").ShouldBeNull();
    }
}
