// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>One seed and what the engine produced with it.</summary>
/// <param name="Seed">Random seed of the run</param>
/// <param name="Values">The eight baseline numbers measured on that run</param>
public sealed record AutofillBandSample(int Seed, AutofillBandValues Values);
