// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Meta-tests over the regression fixtures. Stage 0 gates the qualification, blacklist and
/// restricted-window checks behind Guid.TryParse on the shift id - with non-Guid ids those three
/// rules are silent no-ops and every metric built on the fixtures measures an engine that never
/// evaluates them. These tests pin the wire format and prove the checks really fire on it.
/// </summary>
[TestFixture]
public sealed class WizardFixtureGuardTests
{
    private const int NightShiftTypeIndex = 2;
    private const double SlotHours = 8;

    private static IEnumerable<TestCaseData> AllFixtures()
    {
        yield return new TestCaseData((Func<CoreWizardContext>)WizardScenarioFixtures.BernFiveFullTimeHomogeneous)
            .SetName(nameof(WizardScenarioFixtures.BernFiveFullTimeHomogeneous));
        yield return new TestCaseData((Func<CoreWizardContext>)WizardScenarioFixtures.HeterogeneousMix)
            .SetName(nameof(WizardScenarioFixtures.HeterogeneousMix));
        yield return new TestCaseData((Func<CoreWizardContext>)WizardScenarioFixtures.BoundaryWithPriorWorks)
            .SetName(nameof(WizardScenarioFixtures.BoundaryWithPriorWorks));
        yield return new TestCaseData((Func<CoreWizardContext>)WizardScenarioFixtures.HeterogeneousMixWithEligibilityData)
            .SetName(nameof(WizardScenarioFixtures.HeterogeneousMixWithEligibilityData));
        yield return new TestCaseData((Func<CoreWizardContext>)WizardScenarioFixtures.HeterogeneousMixWithRestrictedNightWindow)
            .SetName(nameof(WizardScenarioFixtures.HeterogeneousMixWithRestrictedNightWindow));
    }

    [TestCaseSource(nameof(AllFixtures))]
    public void AllFixtures_ShiftIds_AreGuidParseable(Func<CoreWizardContext> factory)
    {
        var context = factory();

        context.Shifts.ShouldNotBeEmpty();
        foreach (var shift in context.Shifts)
        {
            Guid.TryParse(shift.Id, out _).ShouldBeTrue(
                $"non-Guid shift id '{shift.Id}' silently disables qualification/blacklist/restricted-window checks");
        }
    }

    [Test]
    public void Stage0_BlacklistCheck_FiresOnFixtureWireFormat()
    {
        var context = WizardScenarioFixtures.HeterogeneousMixWithEligibilityData();
        var agent = context.Agents.Single(a => a.Id == "E-NightOnly");
        var slot = FirstDaySlot(context, WizardScenarioFixtures.EarlyShiftId);

        var verdict = new Stage0HardConstraintChecker().Check(agent, slot, [], context);

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("BlacklistedShift");
    }

    [Test]
    public void Stage0_QualificationCheck_FiresOnFixtureWireFormat()
    {
        var context = WizardScenarioFixtures.HeterogeneousMixWithEligibilityData();
        var agent = context.Agents.Single(a => a.Id == "C-PT1");
        var slot = FirstDaySlot(context, WizardScenarioFixtures.NightShiftId);

        var verdict = new Stage0HardConstraintChecker().Check(agent, slot, [], context);

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("MissingQualification");
    }

    [Test]
    public void Stage0_RestrictedWindow_FiresOnFixtureWireFormat()
    {
        var context = WizardScenarioFixtures.HeterogeneousMixWithRestrictedNightWindow();
        var agent = context.Agents.Single(a => a.Id == "A-FT1");
        var slot = FirstDaySlot(context, WizardScenarioFixtures.NightShiftId);

        var verdict = new Stage0HardConstraintChecker().Check(agent, slot, [], context);

        verdict.ShouldNotBeNull();
        verdict!.RuleName.ShouldBe("RestrictedTimeWindow");
    }

    [Test]
    public void Stage0_EligibleAgentOnFixtureSlot_PassesWithoutVeto()
    {
        var context = WizardScenarioFixtures.HeterogeneousMixWithEligibilityData();
        var agent = context.Agents.Single(a => a.Id == "A-FT1");
        var slot = FirstDaySlot(context, WizardScenarioFixtures.EarlyShiftId);

        new Stage0HardConstraintChecker().Check(agent, slot, [], context).ShouldBeNull();
    }

    [Test]
    public void TokenConstraintChecker_CountsQualificationViolation_OnFixtureContext()
    {
        var context = WizardScenarioFixtures.HeterogeneousMixWithEligibilityData();
        var date = context.PeriodFrom;
        var start = date.ToDateTime(new TimeOnly(22, 0));
        var token = new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: NightShiftTypeIndex,
            Date: date,
            TotalHours: (decimal)SlotHours,
            StartAt: start,
            EndAt: start.AddHours(SlotHours),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: null,
            ShiftRefId: WizardScenarioFixtures.NightShiftId,
            AgentId: "C-PT1");
        var scenario = new CoreScenario { Id = "guard", Tokens = [token] };

        var violations = new TokenConstraintChecker().Check(scenario, context);

        violations.ShouldContain(v => v.Kind == ViolationKind.QualificationMissing);
    }

    private static CoreShift FirstDaySlot(CoreWizardContext context, Guid shiftId)
    {
        var iso = context.PeriodFrom.ToString("yyyy-MM-dd");
        return context.Shifts.Single(s => s.Id == shiftId.ToString() && s.Date == iso);
    }
}
