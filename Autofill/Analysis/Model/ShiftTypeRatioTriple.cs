// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>One ratio per shift kind.</summary>
/// <param name="Early">Value for the early shift</param>
/// <param name="Late">Value for the late shift</param>
/// <param name="Night">Value for the night shift</param>
public sealed record ShiftTypeRatioTriple(double Early, double Late, double Night);
