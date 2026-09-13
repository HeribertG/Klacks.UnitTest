// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The parameter names the aggregated target-hours-drift sentence is built from. Shared by the detector
/// test, which pins that TargetHoursDriftTriggerEvent.SummaryParams carries exactly these keys, and by the
/// i18n gate, which pins that every catalogue interpolates exactly these placeholders. One list rather than
/// two, because a renamed parameter that only one half knows about renders as a literal "{{hours}}" in the
/// user's language while both halves stay green.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

public static class TargetHoursDriftSummaryPlaceholders
{
    private const string PlaceholderPrefix = "{{";
    private const string PlaceholderSuffix = "}}";

    public static readonly IReadOnlyList<string> Names = ["count", "period", "hours", "names"];

    public static string Placeholder(string name) => PlaceholderPrefix + name + PlaceholderSuffix;
}
