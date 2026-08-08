// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// One employee of a scenario, in the order the list shows them. The position in the builder's
/// employee list IS the list rank — rank 1 is the first entry.
/// </summary>
/// <param name="Id">Employee identifier, also the agent id handed to the engine</param>
/// <param name="GuaranteedHours">Contractual guaranteed hours for the planning period</param>
public sealed record AutofillEmployeeSpec(string Id, double GuaranteedHours);
