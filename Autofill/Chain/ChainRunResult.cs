// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>Outcome of Wizard 2 and Wizard 3, each started from the same Wizard 1 plan.</summary>
/// <param name="Wizard2">Works after Wizard 2 (conductor-only harmonizer, a single stage outside the production chain)</param>
/// <param name="Wizard3">Works after Wizard 3 (deterministic search) started from the Wizard 1 plan, as the production chain runs it</param>
/// <param name="Wizard3Search">Search result of Wizard 3, for the stop reason and the clock check</param>
/// <param name="SofteningHintCount">Softening hints Wizard 2 received from the Wizard 1 auction</param>
public sealed record ChainRunResult(
    IReadOnlyList<ChainWork> Wizard2,
    IReadOnlyList<ChainWork> Wizard3,
    DeterministicSearchResult Wizard3Search,
    int SofteningHintCount);
