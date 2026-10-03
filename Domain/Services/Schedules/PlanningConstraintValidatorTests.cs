// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class PlanningConstraintValidatorTests
{
    private const string MaxRunNight = """{"schemaVersion":1,"kind":"Night","maxRun":3}""";
    private const string NightThenEarly = """{"schemaVersion":1,"from":"Night","to":"early","withinDays":1}""";
    private const string RestAfterNight = """{"schemaVersion":1,"kind":"Night","freeDays":2}""";
    private const string FairWeekends = """{"schemaVersion":1,"metric":"WeekendDays","window":"Month","maxSpread":1.5,"weekendDays":["Saturday","Sunday"]}""";

    private readonly PlanningConstraintValidator _validator = new();

    [Test]
    public void ValidMaxConsecutive_ParsesTypedParameters()
    {
        var result = _validator.Validate(Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight));

        result.IsValid.ShouldBeTrue(string.Join(" ", result.Errors));
        result.Parameters.ShouldBe(new MaxConsecutiveOfKindParameters(PlanningShiftKind.Night, 3));
    }

    [Test]
    public void ValidForbiddenTransition_AcceptsCaseInsensitiveNames()
    {
        var result = _validator.Validate(Constraint(PlanningConstraintKind.ForbiddenTransition, NightThenEarly));

        result.IsValid.ShouldBeTrue(string.Join(" ", result.Errors));
        result.Parameters.ShouldBe(new ForbiddenTransitionParameters(PlanningShiftKind.Night, PlanningShiftKind.Early, 1));
    }

    [Test]
    public void ValidRestAfterKind_Parses()
    {
        var result = _validator.Validate(Constraint(PlanningConstraintKind.RestAfterKind, RestAfterNight));

        result.Parameters.ShouldBe(new RestAfterKindParameters(PlanningShiftKind.Night, 2));
    }

    [Test]
    public void TeamFairness_SoftGroupScoped_IsValid_AndProRataDefaultsToTrue()
    {
        var result = _validator.Validate(Fairness(PlanningConstraintSeverity.Soft, PlanningConstraintScopeType.Group, FairWeekends));

        result.IsValid.ShouldBeTrue(string.Join(" ", result.Errors));
        var parameters = result.Parameters.ShouldBeOfType<TeamFairnessParameters>();
        parameters.ProRata.ShouldBeTrue();
        parameters.MaxSpread.ShouldBe(1.5m);
        parameters.WeekendDays.ShouldBe(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, ignoreOrder: true);
    }

    [Test]
    public void TeamFairness_Hard_IsRejected()
    {
        var result = _validator.Validate(Fairness(PlanningConstraintSeverity.Hard, PlanningConstraintScopeType.Group, FairWeekends));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("soft", StringComparison.OrdinalIgnoreCase));
    }

    [TestCase(PlanningConstraintScopeType.Global)]
    [TestCase(PlanningConstraintScopeType.Client)]
    [TestCase(PlanningConstraintScopeType.SchedulingRule)]
    public void TeamFairness_NotGroupScoped_IsRejected(PlanningConstraintScopeType scopeType)
    {
        var result = _validator.Validate(Fairness(PlanningConstraintSeverity.Soft, scopeType, FairWeekends));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("group", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void TeamFairness_WeekendMetricWithoutWeekendDays_IsRejected()
    {
        const string json = """{"schemaVersion":1,"metric":"WeekendDays","window":"Month","maxSpread":1}""";

        _validator.Validate(Fairness(PlanningConstraintSeverity.Soft, PlanningConstraintScopeType.Group, json)).IsValid.ShouldBeFalse();
    }

    [TestCase("""{"schemaVersion":2,"kind":"Night","maxRun":3}""")]
    [TestCase("""{"kind":"Night","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Night","maxRun":0}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Night","maxRun":2.5}""")]
    [TestCase("""{"schemaVersion":1,"kind":"4","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Midday","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Work,Early","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Night ","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":" Night","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"2","maxRun":3}""")]
    [TestCase("""{"schemaVersion":"1","kind":"Night","maxRun":3}""")]
    [TestCase("""{"schemaVersion":0,"kind":"Night","maxRun":3}""")]
    [TestCase("""{"schemaVersion":1,"kind":"Night","maxRun":3,"typo":1}""")]
    [TestCase("""[1,2]""")]
    [TestCase("""not json""")]
    [TestCase("")]
    public void MalformedParameters_AreRejected(string json)
    {
        var result = _validator.Validate(Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, json));

        result.IsValid.ShouldBeFalse();
        result.Parameters.ShouldBeNull();
        result.Errors.ShouldNotBeEmpty();
    }

    [TestCase("""["Saturday,Sunday"]""")]
    [TestCase("""["Saturday "]""")]
    [TestCase("""["6"]""")]
    [TestCase("""[6]""")]
    public void WeekendDays_AcceptOnlyExactDayNames(string weekendDays)
    {
        var json = """{"schemaVersion":1,"metric":"WeekendDays","window":"Month","maxSpread":1,"weekendDays":""" + weekendDays + "}";

        _validator.Validate(Fairness(PlanningConstraintSeverity.Soft, PlanningConstraintScopeType.Group, json)).IsValid.ShouldBeFalse();
    }

    [Test]
    public void NewerSchemaVersion_IsRefusedAsWrittenByANewerKlacks()
    {
        var json = """{"schemaVersion":""" + (PlanningConstraintDefaults.CurrentParametersSchemaVersion + 1) + ""","kind":"Night","maxRun":3}""";

        var result = _validator.Validate(Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, json));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("newer Klacks", StringComparison.Ordinal));
    }

    [Test]
    public void EverySupportedOlderSchemaVersion_HasAnUpgradeStep()
    {
        for (var version = PlanningConstraintDefaults.MinimumSupportedParametersSchemaVersion;
             version < PlanningConstraintDefaults.CurrentParametersSchemaVersion;
             version++)
        {
            PlanningConstraintParametersUpgrader.Steps.ShouldContainKey(
                version, $"Bumping the parameters schema needs an upgrade step from version {version}.");
        }

        PlanningConstraintDefaults.MinimumSupportedParametersSchemaVersion
            .ShouldBeLessThanOrEqualTo(PlanningConstraintDefaults.CurrentParametersSchemaVersion);
    }

    [Test]
    public void ParametersOfAnotherKind_AreRejected()
    {
        _validator.Validate(Constraint(PlanningConstraintKind.RestAfterKind, MaxRunNight)).IsValid.ShouldBeFalse();
    }

    [Test]
    public void GlobalScopeWithId_And_ScopedWithoutId_AreRejected()
    {
        var global = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight);
        global.ScopeId = Guid.NewGuid();
        var client = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight);
        client.ScopeType = PlanningConstraintScopeType.Client;

        _validator.Validate(global).IsValid.ShouldBeFalse();
        _validator.Validate(client).IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidUntilBeforeValidFrom_IsRejected()
    {
        var constraint = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight);
        constraint.ValidFrom = new DateOnly(2026, 10, 10);
        constraint.ValidUntil = new DateOnly(2026, 10, 9);

        _validator.Validate(constraint).IsValid.ShouldBeFalse();
    }

    [TestCase(PlanningConstraintSeverity.Soft, 0d, false)]
    [TestCase(PlanningConstraintSeverity.Soft, -1d, false)]
    [TestCase(PlanningConstraintSeverity.Soft, double.NaN, false)]
    [TestCase(PlanningConstraintSeverity.Hard, 0d, true)]
    [TestCase(PlanningConstraintSeverity.Soft, 2.5d, true)]
    [TestCase(PlanningConstraintSeverity.Soft, PlanningConstraintDefaults.MaxWeight, true)]
    [TestCase(PlanningConstraintSeverity.Soft, PlanningConstraintDefaults.MaxWeight + 1, false)]
    public void Weight_IsChecked(PlanningConstraintSeverity severity, double weight, bool expectedValid)
    {
        var constraint = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight);
        constraint.Severity = severity;
        constraint.Weight = weight;

        _validator.Validate(constraint).IsValid.ShouldBe(expectedValid);
    }

    [Test]
    public void UndefinedEnums_AreRejected()
    {
        var constraint = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, MaxRunNight);
        constraint.Severity = (PlanningConstraintSeverity)0;
        _validator.Validate(constraint).IsValid.ShouldBeFalse();

        var unknownKind = Constraint((PlanningConstraintKind)99, MaxRunNight);
        _validator.Validate(unknownKind).IsValid.ShouldBeFalse();
    }

    private static PlanningConstraint Constraint(PlanningConstraintKind kind, string json) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Severity = PlanningConstraintSeverity.Hard,
        Weight = 1d,
        ScopeType = PlanningConstraintScopeType.Global,
        ParametersJson = json,
    };

    private static PlanningConstraint Fairness(PlanningConstraintSeverity severity, PlanningConstraintScopeType scopeType, string json) => new()
    {
        Id = Guid.NewGuid(),
        Kind = PlanningConstraintKind.TeamFairness,
        Severity = severity,
        Weight = 1d,
        ScopeType = scopeType,
        ScopeId = scopeType == PlanningConstraintScopeType.Global ? null : Guid.NewGuid(),
        ParametersJson = json,
    };
}
