// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>One whole number per shift kind.</summary>
/// <param name="Early">Value for the early shift</param>
/// <param name="Late">Value for the late shift</param>
/// <param name="Night">Value for the night shift</param>
public sealed record ShiftTypeCountTriple(int Early, int Late, int Night);
