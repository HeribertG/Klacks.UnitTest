// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The reason vocabulary of a rotation transition, exactly the six words of the specification. The
/// analyzer itself assigns only three of them: <see cref="None"/> for a forward transition,
/// <see cref="KeywordIneligible"/> when the ban list proves the forward successor kind was closed to
/// the employee on every day of the following package, and <see cref="Unexplained"/> for every other
/// deviation. <see cref="Coverage"/>, <see cref="HoursCap"/> and <see cref="PackageBoundary"/> are
/// part of the vocabulary for the phase-D hand classification: a finished plan does not record
/// whether a coverage hole, an hours cap or a boundary constraint stood in the way (finding K8b), so
/// the analyzer must not guess them.
/// </summary>
public static class RotationTransitionReason
{
    /// <summary>The transition follows the forward rotation; there is no deviation to explain.</summary>
    public const string None = "none";

    /// <summary>The ban list provably closed the forward successor kind for the whole next package.</summary>
    public const string KeywordIneligible = "keywordIneligible";

    /// <summary>Reserved for hand classification: the forward successor was taken by coverage needs.</summary>
    public const string Coverage = "coverage";

    /// <summary>Reserved for hand classification: an hours cap blocked the forward successor.</summary>
    public const string HoursCap = "hoursCap";

    /// <summary>Reserved for hand classification: a package-boundary constraint blocked the forward successor.</summary>
    public const string PackageBoundary = "packageBoundary";

    /// <summary>A deviation the analyzer cannot attribute to any provable cause.</summary>
    public const string Unexplained = "unexplained";
}
