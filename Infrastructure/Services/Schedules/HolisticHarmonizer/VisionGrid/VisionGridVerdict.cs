// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

/// <summary>
/// Outcome of evaluating one grid vision answer.
/// </summary>
/// <param name="Outcome">Passed = every asked cell read correctly; Misread = answered, at least one cell wrong; Inconclusive = the answer says nothing about vision</param>
/// <param name="CorrectAnswers">Number of asked cells read correctly</param>
/// <param name="Error">Why the read did not pass; null when passed</param>
public sealed record VisionGridVerdict(VisionCapabilityOutcome Outcome, int CorrectAnswers, string? Error);
