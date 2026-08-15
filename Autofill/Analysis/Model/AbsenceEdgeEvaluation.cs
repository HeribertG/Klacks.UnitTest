// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Which end of an assignment lies inside an absence window. The engine only ever tests the START
/// date (<c>Stage0HardConstraintChecker.cs:104</c> reads <c>token.Date</c>, <c>:164</c> tests it), so
/// the three members are not three engine behaviours — they are three FACTS about a shift, and the
/// difference between what the fact says and what the engine did is the measurement.
/// </summary>
public enum AbsenceEdgeEvaluation
{
    /// <summary>Neither end lies in the window; the shift does not touch the absence at all.</summary>
    None = 0,

    /// <summary>The shift starts inside the window and ends outside it — a night shift on the last absent day.</summary>
    Start,

    /// <summary>The shift starts outside the window and ends inside it — a night shift on the eve of the absence.</summary>
    End,

    /// <summary>Both ends lie inside the window.</summary>
    Both,
}
