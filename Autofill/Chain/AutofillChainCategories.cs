// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// NUnit category and explicit reason of the chain quality measurement (production chain Wizard 1 -> Wizard 3, with Wizard 2
/// alone and the former chain as comparison, on the autofill scenarios). Every fixture of the category is [Explicit]: it runs Wizard 1 for minutes and needs the run1
/// artifacts of the scenario suite, so it runs only when selected with TestCategory=AutofillChainQuality. The large
/// scenarios (15 or 17 employees) have their own category, <see cref="ChainQualityLarge"/>, so the small step never
/// starts them.
/// </summary>
public static class AutofillChainCategories
{
    public const string ChainQuality = "AutofillChainQuality";

    public const string ChainQualityLarge = "AutofillChainQualityLarge";

    public const string ExplicitReason =
        "Measurement of the autofill chain Wizard 1 -> 3 (Wizard 2 for comparison); runs for minutes and needs the run1 artifacts of the "
        + "scenario suite; select it with TestCategory=AutofillChainQuality.";

    public const string ExplicitReasonLarge =
        "Measurement of the autofill chain Wizard 1 -> 2 -> 3 on a large scenario (15 or 17 employees, 279 or 372 shifts); "
        + "runs for an hour or more and needs the run1 artifact of the scenario suite; select it with "
        + "TestCategory=AutofillChainQualityLarge.";
}
