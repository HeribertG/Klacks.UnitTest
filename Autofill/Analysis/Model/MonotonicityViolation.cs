// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A list rank whose fulfilment is higher than the rank above it — the top-down rule 6 expects the
/// fulfilment to never rise as the list goes down.
/// </summary>
/// <param name="Rank">List rank that breaks the order, 1-based</param>
/// <param name="PrevPct">Fulfilment of the rank above</param>
/// <param name="ThisPct">Fulfilment of this rank</param>
public sealed record MonotonicityViolation(int Rank, double PrevPct, double ThisPct);
