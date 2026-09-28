// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>How many in-period assignments one calendar day carries, over all orders together.</summary>
/// <param name="Date">Calendar day; a night shift counts on the day it starts on</param>
/// <param name="Count">Assignments dated on that day</param>
public sealed record DayAssignmentCount(DateOnly Date, int Count);
