// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The input-side view of the fixture ban list: who may hold which slot at all. These are properties
/// of the scenario, not of the plan — they exist so a red assertion can distinguish "the engine chose
/// badly" from "the input left it no choice". Without an eligibility input, or with an empty ban
/// list, all three lists stay empty: the engine short-circuits an empty ban set to "everyone is
/// eligible" (finding K3), and listing one identical full pool per slot would add bulk without
/// information.
/// </summary>
/// <param name="PoolPerShift">Eligible pool of every demanded slot, ordered by date and kind</param>
/// <param name="SingletonDays">Slots whose pool holds exactly one employee — factually determined</param>
/// <param name="EmptyPoolDays">Slots whose pool is empty — unfillable by input</param>
public sealed record EligibilityMetrics(
    IReadOnlyList<ShiftEligibilityPool> PoolPerShift,
    IReadOnlyList<SingletonPoolDay> SingletonDays,
    IReadOnlyList<EmptyPoolDay> EmptyPoolDays);
