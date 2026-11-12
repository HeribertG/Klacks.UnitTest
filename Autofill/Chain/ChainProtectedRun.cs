// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>One Wizard 3 run of the comparison: the production run from the Wizard 1 plan, or the former chain's run from the Wizard 2 plan.</summary>
/// <param name="Label">Stage label of the run in the comparison table</param>
/// <param name="Description">How the run was started and protected</param>
/// <param name="Works">Works after the run</param>
/// <param name="Search">Search result of the run</param>
/// <param name="Start">Works of the plan the run started from</param>
/// <param name="StartLabel">Name of the stage that plan comes from, as shown in the comparison lines</param>
public sealed record ChainProtectedRun(
    string Label,
    string Description,
    IReadOnlyList<ChainWork> Works,
    DeterministicSearchResult Search,
    IReadOnlyList<ChainWork> Start,
    string StartLabel);
