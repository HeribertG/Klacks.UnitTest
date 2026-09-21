// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The parameter names the aggregated availability-gap and missing-core-data sentences are built from.
/// Shared by the detector tests, which pin that the events' SummaryParams carry exactly these keys, and
/// by the i18n gate, which pins that every catalogue interpolates exactly these placeholders. One list
/// per sentence rather than two copies each, because a renamed parameter that only one half knows about
/// renders as a literal "{{names}}" in the user's language while both halves stay green.
/// </summary>

namespace Klacks.UnitTest.TestHelpers;

public static class ProactiveNoiseSummaryPlaceholders
{
    private const string PlaceholderPrefix = "{{";
    private const string PlaceholderSuffix = "}}";

    public static readonly IReadOnlyList<string> AvailabilityGapNames = ["count", "from", "until", "names"];

    public static readonly IReadOnlyList<string> ClientMissingCoreDataNames = ["count", "names"];

    public static string Placeholder(string name) => PlaceholderPrefix + name + PlaceholderSuffix;
}
