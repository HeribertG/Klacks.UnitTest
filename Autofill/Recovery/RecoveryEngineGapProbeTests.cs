// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// Self-test of the recovery probe: on mini plans of one Monday it must report a direct cover, a two-move chain and a
/// three-move chain as CLOSED and a slot nobody may take as OPEN, deterministically. Without this proof an OPEN of the
/// probe on a real plan would say nothing.
/// </summary>
[TestFixture]
public sealed class RecoveryEngineGapProbeTests
{
    private const double RestHours = 11;
    private const double ShiftHours = 8;
    private const double ContractDailyCapHours = 4;

    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly Guid Early = new("00000000-0000-0000-0000-00000000e001");
    private static readonly Guid Late = new("00000000-0000-0000-0000-00000000e002");
    private static readonly Guid Day = new("00000000-0000-0000-0000-00000000e003");

    [Test]
    public void Free_agent_closes_the_open_head_directly()
    {
        var context = Context([Shift(Early, "Early", "06:00", "14:00", 2)], []);
        var plan = Plan(Token("A", Early, 6, 14, 0));

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=standard CLOSED moves=1 ", StringComparison.Ordinal));
        lines.ShouldContain("RECOVERY-PROBE RECHECK #1 ruleset=standard round=0 autofill-filter=ok");
        lines.ShouldContain("RECOVERY-PROBE AUTOFILL-LEGAL #1 ruleset=standard CLOSED moves=1 after 0 retry rounds");
    }

    [Test]
    public void Early_holder_takes_the_late_and_hands_its_early_on()
    {
        var context = Context(
            [Shift(Early, "Early", "06:00", "14:00", 1), Shift(Late, "Late", "14:00", "22:00", 1)],
            [("A", Late), ("C", Late)]);
        var plan = Plan(Token("B", Early, 6, 14, 0));

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        var result = lines.Single(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=standard ", StringComparison.Ordinal));
        result.ShouldContain("CLOSED moves=2 ");
        result.ShouldContain("B takes 2026-03-02 Late 14:00-22:00");
        result.ShouldContain("from " + RecoveryProbeFormats.OpenSlotHolder);
        lines.ShouldContain("RECOVERY-PROBE RECHECK #1 ruleset=standard round=0 autofill-filter=ok");
    }

    [Test]
    public void Three_moves_close_the_slot_when_no_shorter_chain_exists()
    {
        var context = Context(
            [
                Shift(Early, "Early", "06:00", "14:00", 1),
                Shift(Late, "Late", "14:00", "22:00", 1),
                Shift(Day, "Day", "08:00", "16:00", 1),
            ],
            [("A", Late), ("C", Late), ("A", Early)]);
        var plan = Plan(Token("B", Early, 6, 14, 0), Token("C", Day, 8, 16, 0));

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=standard CLOSED moves=3 ", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE ALL ruleset=standard open=1 closed=1 still-open=0 moves=3 ", StringComparison.Ordinal));
    }

    [Test]
    public void Recheck_reports_a_cover_the_autofill_filter_refuses_and_the_retry_finds_none()
    {
        var context = ContextWithDailyCapOnB([("C", Early)]);
        var plan = Plan(Token("A", Early, 6, 14, 0));

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=standard CLOSED moves=1 ", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RECHECK #1 ruleset=standard round=0 autofill-filter=refused B 2026-03-02 06:00-14:00", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("(daily-hours(contract day))", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RETRY #1 ruleset=standard round=1 gated=1 OPEN reason=no-eligible-candidate", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE AUTOFILL-LEGAL #1 ruleset=standard OPEN after 1 retry rounds", StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("refused-by-autofill-filter-first-round=1 autofill-legal-closed=0", StringComparison.Ordinal));
    }

    [Test]
    public void Retry_finds_the_autofill_legal_cover_behind_a_refused_one()
    {
        var context = ContextWithDailyCapOnB([]);
        var plan = Plan(Token("A", Early, 6, 14, 0));

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RECHECK #1 ruleset=standard round=0 autofill-filter=refused B ", StringComparison.Ordinal));
        var retry = lines.Single(l => l.StartsWith("RECOVERY-PROBE RETRY #1 ruleset=standard round=1 gated=1 ", StringComparison.Ordinal));
        retry.ShouldContain("CLOSED moves=1 ");
        retry.ShouldContain("C takes 2026-03-02 Early 06:00-14:00");
        lines.ShouldContain("RECOVERY-PROBE RECHECK #1 ruleset=standard round=1 autofill-filter=ok");
        lines.ShouldContain("RECOVERY-PROBE AUTOFILL-LEGAL #1 ruleset=standard CLOSED moves=1 after 1 retry rounds");
    }

    [Test]
    public void Slot_nobody_may_take_stays_open_with_the_engine_reason()
    {
        var context = Context([Shift(Late, "Late", "14:00", "22:00", 1)], [("A", Late), ("B", Late), ("C", Late)]);
        var plan = Plan();

        var lines = RecoveryEngineGapProbe.Describe(plan, context);

        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=standard OPEN reason=no-eligible-candidate", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE RESULT #1 ruleset=deep-budget-x10 OPEN ", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("RECOVERY-PROBE BUDGET #1 exhausted=unknown ", StringComparison.Ordinal));
    }

    [Test]
    public void Output_is_deterministic_prefixed_and_free_of_errors()
    {
        var context = Context(
            [Shift(Early, "Early", "06:00", "14:00", 1), Shift(Late, "Late", "14:00", "22:00", 1)],
            [("A", Late), ("C", Late)]);
        var plan = Plan(Token("B", Early, 6, 14, 0));

        var first = RecoveryEngineGapProbe.Describe(plan, context);
        var second = RecoveryEngineGapProbe.Describe(plan, context);

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, first));
        first.ShouldBe(second);
        first.ShouldAllBe(l => l.StartsWith(RecoveryProbeFormats.Prefix, StringComparison.Ordinal));
        first.ShouldNotContain(l => l.Contains(" ERROR ", StringComparison.Ordinal));
    }

    private static CoreWizardContext Context(IReadOnlyList<CoreShift> shifts, IReadOnlyList<(string Agent, Guid Shift)> banned)
        => new()
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(4),
            Agents = [Agent("A"), Agent("B"), Agent("C")],
            Shifts = shifts,
            IneligibleAssignments = banned.Select(b => (b.Agent, b.Shift, Monday)).ToHashSet(),
        };

    private static CoreWizardContext ContextWithDailyCapOnB(IReadOnlyList<(string Agent, Guid Shift)> banned)
        => new()
        {
            PeriodFrom = Monday,
            PeriodUntil = Monday.AddDays(4),
            Agents = [Agent("A"), Agent("B"), Agent("C")],
            Shifts = [Shift(Early, "Early", "06:00", "14:00", 2)],
            IneligibleAssignments = banned.Select(b => (b.Agent, b.Shift, Monday)).ToHashSet(),
            ContractDays = [new CoreContractDay("B", Monday, true, true, 1, ContractDailyCapHours, Guid.Empty)],
        };

    private static CoreAgent Agent(string id) => new(id, 0, 0, 0, RestHours, 0, 0, 0, 0);

    private static CoreShift Shift(Guid id, string name, string start, string end, int capacity)
        => new(id.ToString(), name, Monday.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture), start, end, ShiftHours, capacity, 0);

    private static CoreToken Token(string agent, Guid shift, int startHour, int endHour, int shiftTypeIndex)
        => new(
            [agent + shift],
            shiftTypeIndex,
            Monday,
            (decimal)ShiftHours,
            Monday.ToDateTime(new TimeOnly(startHour, 0)),
            Monday.ToDateTime(new TimeOnly(endHour, 0)),
            Guid.Empty,
            0,
            false,
            null,
            shift,
            agent);

    private static CoreScenario Plan(params CoreToken[] tokens) => new() { Id = "probe-self-test", Tokens = [.. tokens] };
}
