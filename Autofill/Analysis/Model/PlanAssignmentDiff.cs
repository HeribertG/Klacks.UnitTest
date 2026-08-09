// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The assignment-level difference between a baseline plan and a treatment plan over the same
/// demanded slots — the control-group comparison of the carry-in suite, where every deviation of the
/// treatment must be attributable to the treatment's restriction. Carried-in shifts are outside the
/// comparison: they are fixed input to both runs, not output of either.
/// </summary>
/// <param name="ChangedAssignments">Every slot the two plans staff differently, ordered by date and kind</param>
/// <param name="ChangedCount">Number of changed slots</param>
public sealed record PlanAssignmentDiff(
    IReadOnlyList<ChangedAssignment> ChangedAssignments,
    int ChangedCount);
