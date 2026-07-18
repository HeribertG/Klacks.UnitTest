// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCapRuleValidation: the two mutually exclusive modes (fixed CapHours vs
/// rolling-average RollingWindowWeeks + MaxAverageWeeklyHours), the CustomWeeks window bounds, the
/// optional WarnAtPercent range and the defined-enum checks for Period/Scope.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.PeriodCapRules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;

namespace Klacks.UnitTest.Application.Handlers.PeriodCapRules;

[TestFixture]
public class PeriodCapRuleValidationTests
{
    private static PeriodCapRuleResource ValidFixed() => new()
    {
        Period = PeriodCapPeriod.Month,
        Scope = PeriodCapScope.TotalHours,
        CapHours = 45m,
    };

    private static PeriodCapRuleResource ValidRolling() => new()
    {
        Period = PeriodCapPeriod.Month,
        Scope = PeriodCapScope.TotalHours,
        CapHours = 0m,
        RollingWindowWeeks = 17,
        MaxAverageWeeklyHours = 48m,
    };

    [Test]
    public void Validate_NullResource_Throws()
    {
        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(null));
        ex.Message.ShouldBe("Period cap rule data is required.");
    }

    [Test]
    public void Validate_ValidFixed_DoesNotThrow()
    {
        Should.NotThrow(() => PeriodCapRuleValidation.Validate(ValidFixed()));
    }

    [Test]
    public void Validate_ValidRolling_DoesNotThrow()
    {
        Should.NotThrow(() => PeriodCapRuleValidation.Validate(ValidRolling()));
    }

    [Test]
    public void Validate_UndefinedPeriod_Throws()
    {
        var resource = ValidFixed();
        resource.Period = (PeriodCapPeriod)999;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("Period must be a defined period cap period.");
    }

    [Test]
    public void Validate_UndefinedScope_Throws()
    {
        var resource = ValidFixed();
        resource.Scope = (PeriodCapScope)999;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("Scope must be a defined period cap scope.");
    }

    [Test]
    public void Validate_BothModes_Throws()
    {
        var resource = ValidFixed();
        resource.RollingWindowWeeks = 17;
        resource.MaxAverageWeeklyHours = 48m;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("A period cap rule is either fixed-period (CapHours) or rolling-average (RollingWindowWeeks + MaxAverageWeeklyHours), not both.");
    }

    [Test]
    public void Validate_NeitherMode_Throws()
    {
        var resource = ValidFixed();
        resource.CapHours = 0m;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("A period cap rule must define either a fixed-period cap (CapHours) or a rolling-average cap (RollingWindowWeeks + MaxAverageWeeklyHours).");
    }

    [Test]
    public void Validate_FixedWithZeroCapHours_Throws()
    {
        var resource = ValidFixed();
        resource.CapHours = 0m;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("A period cap rule must define either a fixed-period cap (CapHours) or a rolling-average cap (RollingWindowWeeks + MaxAverageWeeklyHours).");
    }

    [Test]
    public void Validate_RollingWithZeroWindowWeeks_Throws()
    {
        var resource = ValidRolling();
        resource.RollingWindowWeeks = 0;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("RollingWindowWeeks must be greater than zero in rolling-average mode.");
    }

    [Test]
    public void Validate_RollingWithZeroMaxAverageWeeklyHours_Throws()
    {
        var resource = ValidRolling();
        resource.MaxAverageWeeklyHours = 0m;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("MaxAverageWeeklyHours must be greater than zero in rolling-average mode.");
    }

    [Test]
    public void Validate_WarnAtPercentNull_DoesNotThrow()
    {
        var resource = ValidFixed();
        resource.WarnAtPercent = null;

        Should.NotThrow(() => PeriodCapRuleValidation.Validate(resource));
    }

    [TestCase(1)]
    [TestCase(100)]
    public void Validate_WarnAtPercentWithinBounds_DoesNotThrow(int percent)
    {
        var resource = ValidFixed();
        resource.WarnAtPercent = percent;

        Should.NotThrow(() => PeriodCapRuleValidation.Validate(resource));
    }

    [TestCase(0)]
    [TestCase(101)]
    public void Validate_WarnAtPercentOutOfBounds_Throws(int percent)
    {
        var resource = ValidFixed();
        resource.WarnAtPercent = percent;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("WarnAtPercent must be between 1 and 100 when set.");
    }

    [Test]
    public void Validate_CustomWeeksWithNullCustomPeriodWeeks_Throws()
    {
        var resource = ValidFixed();
        resource.Period = PeriodCapPeriod.CustomWeeks;
        resource.CustomPeriodWeeks = null;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("CustomPeriodWeeks is required and must be between 1 and 104 when Period is CustomWeeks.");
    }

    [TestCase(0)]
    [TestCase(105)]
    public void Validate_CustomWeeksOutOfBounds_Throws(int weeks)
    {
        var resource = ValidFixed();
        resource.Period = PeriodCapPeriod.CustomWeeks;
        resource.CustomPeriodWeeks = weeks;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("CustomPeriodWeeks is required and must be between 1 and 104 when Period is CustomWeeks.");
    }

    [TestCase(1)]
    [TestCase(104)]
    public void Validate_CustomWeeksWithinBounds_DoesNotThrow(int weeks)
    {
        var resource = ValidFixed();
        resource.Period = PeriodCapPeriod.CustomWeeks;
        resource.CustomPeriodWeeks = weeks;

        Should.NotThrow(() => PeriodCapRuleValidation.Validate(resource));
    }

    [Test]
    public void Validate_CustomPeriodWeeksWithNonCustomPeriod_Throws()
    {
        var resource = ValidFixed();
        resource.Period = PeriodCapPeriod.Month;
        resource.CustomPeriodWeeks = 4;

        var ex = Should.Throw<InvalidRequestException>(() => PeriodCapRuleValidation.Validate(resource));
        ex.Message.ShouldBe("CustomPeriodWeeks may only be set when Period is CustomWeeks.");
    }
}
