// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Why one employee's night count differs from the baseline run. The members are the order the
/// attribution is attempted in, most specific first.
/// </summary>
public enum BaselineDiffAttribution
{
    /// <summary>No cause found. The count of these is the number the specification asks about.</summary>
    Unexplained = 0,

    /// <summary>The count is identical; nothing to explain.</summary>
    Unchanged,

    /// <summary>
    /// The employee lost night shifts he held in the baseline on days that are now absence days. The
    /// absence is a hard block, so those shifts could not survive.
    /// </summary>
    AbsenceRemoved,

    /// <summary>
    /// The employee gained night shifts, and the absent employees together lost at least as many. The
    /// work the absence removed has to land somewhere, and this is where it landed.
    /// </summary>
    AbsenceRedistributed,
}
