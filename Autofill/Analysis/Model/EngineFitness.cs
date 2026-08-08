// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The engine's own five-stage score, copied through unchanged. Recorded for diagnosis only: the
/// selection order is lexicographic over the stages, so the aggregate is NOT what the engine
/// optimises and must never be used as a test oracle.
/// </summary>
/// <param name="Stage0">Hard-constraint violations; lower is better, 0 is clean</param>
/// <param name="Stage1">Guaranteed-hours coverage, rank-weighted</param>
/// <param name="Stage2">Full-time coverage, rank-weighted</param>
/// <param name="Stage3">Soft constraints: block ordering, blacklist, location, gap</param>
/// <param name="Stage4">Cosmetic: quantity fairness, minimum hours, block symmetry</param>
/// <param name="Aggregate">Stage-weighted scalar the engine carries as telemetry</param>
public sealed record EngineFitness(
    int Stage0,
    double Stage1,
    double Stage2,
    double Stage3,
    double Stage4,
    double Aggregate);
