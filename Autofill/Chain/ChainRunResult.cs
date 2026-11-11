// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>Outcome of Wizard 2 and Wizard 3 started from one Wizard 1 plan.</summary>
/// <param name="Wizard2">Works after Wizard 2 (conductor-only harmonizer)</param>
/// <param name="Wizard3">Works after Wizard 3 (deterministic search)</param>
/// <param name="Wizard3Search">Search result of Wizard 3, for the stop reason and the clock check</param>
/// <param name="SofteningHintCount">Softening hints Wizard 2 received from the Wizard 1 auction</param>
public sealed record ChainRunResult(
    IReadOnlyList<ChainWork> Wizard2,
    IReadOnlyList<ChainWork> Wizard3,
    DeterministicSearchResult Wizard3Search,
    int SofteningHintCount);
