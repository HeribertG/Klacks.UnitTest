// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>A night shift on <paramref name="Date"/> followed by an early shift on the next day.</summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Day the night shift starts on</param>
public sealed record NightToEarlyViolation(string Employee, DateOnly Date);
