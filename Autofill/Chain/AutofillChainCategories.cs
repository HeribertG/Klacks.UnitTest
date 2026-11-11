// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// NUnit category and explicit reason of the chain quality measurement (Wizard 1 -> Wizard 2 -> Wizard 3 on the
/// autofill scenarios). Every fixture of the category is [Explicit]: it runs Wizard 1 for minutes and needs the run1
/// artifacts of the scenario suite, so it runs only when selected with TestCategory=AutofillChainQuality.
/// </summary>
public static class AutofillChainCategories
{
    public const string ChainQuality = "AutofillChainQuality";

    public const string ExplicitReason =
        "Measurement of the autofill chain Wizard 1 -> 2 -> 3; runs for minutes and needs the run1 artifacts of the "
        + "scenario suite; select it with TestCategory=AutofillChainQuality.";
}
