// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// How much the rows of a plan look like shifted copies of one cycle (the staircase picture of a harmonious plan).
/// </summary>
/// <param name="CycleSimilarity">
/// Mean over all pairs of rows of the best share of equal cells when one row is shifted against the other
/// </param>
/// <param name="ChanceSimilarity">
/// The same value after the days of every row were shuffled; what the measure reads from rows with the same
/// content and no order
/// </param>
/// <param name="StepRegularity">Share of neighbouring rows whose best shift is the most frequent best shift</param>
/// <param name="ModalStepDays">The most frequent best shift between neighbouring rows, in days</param>
/// <param name="GridDays">Days of the grid, the carried-in days of the previous period included</param>
/// <param name="CarryInDays">Days of the grid before the planned period</param>
public sealed record StaircaseHarmony(
    double CycleSimilarity,
    double ChanceSimilarity,
    double StepRegularity,
    int ModalStepDays,
    int GridDays,
    int CarryInDays);
