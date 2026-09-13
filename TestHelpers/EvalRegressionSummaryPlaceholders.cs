// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The parameter names the two eval sentences are built from: the regression alert of its own, and the
/// three eval figures the weekly learning digest gained. Shared by the gate, which pins that every
/// catalogue interpolates exactly these placeholders, and by the gate's own event assertions, which pin
/// that the trigger events really supply them. One list rather than two, because a renamed parameter that
/// only one half knows about renders as a literal "{{retrieval}}" in the user's language while both halves
/// stay green.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

public static class EvalRegressionSummaryPlaceholders
{
    private const string PlaceholderPrefix = "{{";
    private const string PlaceholderSuffix = "}}";

    public static readonly IReadOnlyList<string> AlertNames =
        ["goldset", "model", "retrieval", "selection", "retrievalDelta", "selectionDelta"];

    public static readonly IReadOnlyList<string> DigestNames =
        ["evalRetrieval", "evalSelection", "evalItems"];

    public static string Placeholder(string name) => PlaceholderPrefix + name + PlaceholderSuffix;
}
