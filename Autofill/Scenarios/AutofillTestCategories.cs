// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// NUnit category names and explicit reasons of the autofill scenario suite.
/// </summary>
public static class AutofillTestCategories
{
    /// <summary>
    /// Marks the specification targets A4, A5, A7 and A8 of scenarios 1, 1b, 2 and 3 (owner decision
    /// 2026-11-10). They judge the binding values of tests/autofill/SPEC.md and are expected red until the
    /// engine closes the gap, so every one of them is also [Explicit]: the regular run
    /// (filter "TestCategory!=SlowModelLoad") skips them, and they run only when selected with
    /// "TestCategory=AutofillSpecTarget".
    /// </summary>
    public const string SpecTarget = "AutofillSpecTarget";

    /// <summary>The [Explicit] reason every specification-target test carries.</summary>
    public const string SpecTargetExplicitReason =
        "Specification target, expected red until the engine closes the gap; select it with TestCategory=AutofillSpecTarget.";
}
