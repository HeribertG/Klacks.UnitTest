// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>How far one employee's preferred shifts were actually granted.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="PreferredAssignments">In-period shifts of his that he holds a Preferred entry for</param>
/// <param name="TotalAssignments">All his in-period shifts</param>
/// <param name="Share">Preferred assignments divided by all of them; 0 for an employee without shifts</param>
public sealed record PreferenceSatisfaction(
    string Employee,
    int PreferredAssignments,
    int TotalAssignments,
    double Share);
