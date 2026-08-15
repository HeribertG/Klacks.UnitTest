// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// Why one slot is staffed differently in the treated run than in the control run. The order of the
/// members is the order the attribution is attempted in, most specific first.
/// </summary>
public enum DiffAttribution
{
    /// <summary>No cause found. The count of these is the number the specification asks about.</summary>
    Unexplained = 0,

    /// <summary>A schedule command of the control run's employee forbids him this very slot on this day.</summary>
    KeywordRemovedEmployee,

    /// <summary>A schedule command of the treated run's employee forbids him this very slot on this day.</summary>
    KeywordWouldForbidReplacement,

    /// <summary>The treated run's employee is blacklisted from this slot; the treatment let him have it anyway.</summary>
    BlacklistWouldForbidReplacement,

    /// <summary>A blacklist entry forbids the control run's employee this slot, so the treatment had to replace him.</summary>
    BlacklistRemovedEmployee,

    /// <summary>The treated run's employee holds a preference for this slot.</summary>
    PreferenceGained,

    /// <summary>The control run's employee holds a preference for this slot and lost it in the treatment.</summary>
    PreferenceLost,

    /// <summary>
    /// Neither employee is named by any preference or command on this slot, but at least one of the
    /// two is constrained SOMEWHERE in the period, so this change can be a knock-on of a directly
    /// attributed one — the displaced work has to land somewhere.
    /// </summary>
    KnockOnOfConstrainedEmployee,
}
