// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// NUnit category names of the autofill scenario suite.
/// </summary>
public static class AutofillTestCategories
{
    /// <summary>
    /// Marks the spec-first assertions that measure the binding specification (tests/autofill/SPEC.md)
    /// against the current engine and are DELIBERATELY red until the engine closes the gap. CI excludes
    /// this category from the deploy gate (filter "TestCategory!=SpecFirstRed" in the Klacks.Api
    /// workflows deploy.yml and tests.yml); full local runs keep reporting them. The exact set of
    /// marked tests is pinned by <see cref="SpecFirstRedRegistryTests"/> — extend the list there
    /// (and the documentation in tests/autofill/INFRA.md) when a test is added to or removed from
    /// this category.
    /// </summary>
    public const string SpecFirstRed = "SpecFirstRed";
}
