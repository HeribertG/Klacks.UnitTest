// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Keyword violations of the finished plan, computed by an INDEPENDENT scan of every planned and
/// carried-in shift against the fixture ban list — never from the engine's ConstraintViolation list,
/// which skips locked assignments and would therefore silently exempt exactly the carry-in stock
/// (finding K8). Only the violation list exists on engine level: the specification's boundaryCases
/// and irrelevantKeywordInfluence require the API's validity evaluation and its keyword catalogue,
/// which the engine's bare ban list has already flattened away (findings K3 and K4), so they are
/// measured one level up against the EligibilityMatrixBuilder and not here.
/// </summary>
/// <param name="Violations">Every planned or carried-in shift the ban list forbids, ordered by list rank, date and kind</param>
public sealed record KeywordMetrics(
    IReadOnlyList<KeywordViolation> Violations);
