// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>One measured value two runs disagree on.</summary>
/// <param name="Metric">Name of the measurement, in the path form of the metrics JSON</param>
/// <param name="ValueLeft">Value of the first run, formatted invariantly</param>
/// <param name="ValueRight">Value of the second run, formatted invariantly</param>
public sealed record Scenario4MetricDifference(string Metric, string ValueLeft, string ValueRight);
