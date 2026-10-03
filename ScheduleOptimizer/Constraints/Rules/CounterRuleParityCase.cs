// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.ScheduleOptimizer.Constraints.Rules;

/// <summary>
/// One scenario run through both the API CounterRuleEvaluator and the pure PeriodCount evaluator.
/// Works are persisted rows; PlannedSlots (optional) are the not-yet-persisted slots of EvaluatePlannedAsync.
/// </summary>
public sealed record CounterRuleParityCase(
    string Name,
    CounterEventType EventType,
    CounterPeriod Period,
    int Threshold,
    decimal? HoursThreshold,
    DateOnly AsOfDate,
    IReadOnlyList<(DateOnly Date, TimeOnly Start, TimeOnly End)> Works,
    IReadOnlyList<(DateOnly Date, TimeOnly Start, TimeOnly End)> PlannedSlots,
    (string Start, string End)? NightWindowOverride = null)
{
    public override string ToString() => Name;
}
