// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>How many shifts of each kind one employee received inside the period.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="ListRank">Position in the displayed list, 1 = top</param>
/// <param name="Early">Number of early shifts</param>
/// <param name="Late">Number of late shifts</param>
/// <param name="Night">Number of night shifts</param>
public sealed record EmployeeShiftTypeCounts(string Employee, int ListRank, int Early, int Late, int Night);
