// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>One stage of the chain, measured with the suite's analyzer.</summary>
/// <param name="Label">Stage label: w1, w2 or w3</param>
/// <param name="Plan">Plan of the stage in the token form the analyzer reads</param>
/// <param name="Metrics">Full analyzer measurement of the plan</param>
/// <param name="ChangedVsWizard1">Slots whose assignees differ from the Wizard 1 plan</param>
public sealed record ChainStageMeasurement(
    string Label,
    CoreScenario Plan,
    AutofillMetrics Metrics,
    int ChangedVsWizard1);
