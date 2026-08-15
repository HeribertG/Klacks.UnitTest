// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// How much of a stored baseline measurement a diff could actually use. It is written into the
/// artifact because the answer changes what the diff can prove, and a reader must not have to guess
/// which of the two they are looking at.
/// </summary>
public enum BaselineDiffMode
{
    /// <summary>No baseline artifact was found. Nothing was compared and nothing may be concluded.</summary>
    NotAvailable = 0,

    /// <summary>
    /// The baseline artifact carries aggregates only — it was written before the assignment list was
    /// added to the schema. Per-employee counts can be compared; which SLOT moved cannot.
    /// </summary>
    AggregateOnly,

    /// <summary>The baseline artifact carries its assignment list, so slots can be compared one by one.</summary>
    SlotLevel,
}
