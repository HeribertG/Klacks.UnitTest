// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Reflection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Pins the exact set of tests carrying <see cref="AutofillTestCategories.SpecFirstRed"/>. The CI
/// deploy gate excludes that category (filter "TestCategory!=SpecFirstRed" in the Klacks.Api
/// workflows), so this guard is what keeps the exclusion honest: no test can silently join the
/// category (and vanish from the deploy gate) or silently leave it (and start blocking deploys)
/// without this list — and the documentation in tests/autofill/INFRA.md — being updated on purpose.
/// This fixture itself must never carry the category; it has to run inside the gate.
/// </summary>
[TestFixture]
public class SpecFirstRedRegistryTests
{
    private static readonly IReadOnlyCollection<string> ExpectedSpecFirstRedTests =
    [
        Qualified<Scenario1.Scenario1Tests>(nameof(Scenario1.Scenario1Tests.A4_ShiftKindStaysConstantInsideAPackage)),
        Qualified<Scenario1.Scenario1Tests>(nameof(Scenario1.Scenario1Tests.A5_PackageLengthsFollowTheFiveTwoIdeal)),
        Qualified<Scenario1.Scenario1Tests>(nameof(Scenario1.Scenario1Tests.A6_GuaranteedHoursAreServedTopDown)),
        Qualified<Scenario1.Scenario1Tests>(nameof(Scenario1.Scenario1Tests.A7_RotationFollowsEarlyLateNight)),
        Qualified<Scenario1.Scenario1Tests>(nameof(Scenario1.Scenario1Tests.A8_ShiftKindsAreSpreadEvenlyOverTheEmployees)),
        Qualified<Scenario1.Scenario1bTests>(nameof(Scenario1.Scenario1bTests.A4_ShiftKindStaysConstantInsideAPackage)),
        Qualified<Scenario1.Scenario1bTests>(nameof(Scenario1.Scenario1bTests.A5_PackageLengthsFollowTheFiveTwoIdeal)),
        Qualified<Scenario1.Scenario1bTests>(nameof(Scenario1.Scenario1bTests.A6_GuaranteedHoursAreServedTopDown)),
        Qualified<Scenario1.Scenario1bTests>(nameof(Scenario1.Scenario1bTests.A7_RotationFollowsEarlyLateNight)),
        Qualified<Scenario1.Scenario1bTests>(nameof(Scenario1.Scenario1bTests.A8_ShiftKindsAreSpreadEvenlyOverTheEmployees)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A4_ShiftKindStaysConstantInsideAPackage)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A5_PackageLengthsFollowTheFiveTwoIdeal)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A6_GuaranteedHoursAreServedTopDown)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A7_RotationRunsForward)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A8_ShiftKindsAreSpreadEvenlyOverTheTopRanks)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A12_TwoFreeDaysFollowTheCompletedCarryInPackage)),
        Qualified<Scenario2.Scenario2Tests>(nameof(Scenario2.Scenario2Tests.A13_ClosedPackagesRotateAcrossTheMonthBoundary)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A4_ShiftKindStaysConstantInsideAPackage)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A5_PackageLengthsFollowTheFiveTwoIdeal)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A6_GuaranteedHoursAreServedTopDown)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A7_RotationRunsForward)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A8_ShiftKindsAreSpreadEvenlyOverTheTopRanks)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A13_ClosedPackagesRotateAcrossTheMonthBoundary)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A17_EveryRotationDeviationIsExplained)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A19_PackageShorteningsStayWithinTheProvableBudget)),
        Qualified<Scenario3.Scenario3MainRunTests>(nameof(Scenario3.Scenario3MainRunTests.A25_NightSharesStayWithin20PercentOfTheCohortMean)),
    ];

    [Test]
    public void SpecFirstRedCategory_MarksExactlyTheDocumentedTests()
    {
        var actual = CollectSpecFirstRedTests();
        var expected = ExpectedSpecFirstRedTests.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var unexpected = actual.Except(expected, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var problems = new List<string>();

        if (missing.Count > 0)
        {
            problems.Add(
                "documented as SpecFirstRed but no longer carrying the category — these tests would start "
                + "BLOCKING the deploy gate again: " + string.Join(", ", missing));
        }

        if (unexpected.Count > 0)
        {
            problems.Add(
                "carrying the SpecFirstRed category without being documented — these tests would silently "
                + "VANISH from the deploy gate: " + string.Join(", ", unexpected));
        }

        problems.ShouldBeEmpty(
            $"The set of SpecFirstRed tests is pinned to the {ExpectedSpecFirstRedTests.Count} documented "
            + "spec-first assertions (tests/autofill/INFRA.md, section SpecFirstRed). Changing the set is a "
            + "deliberate decision: update this registry AND the documentation together. "
            + string.Join(" | ", problems));
    }

    private static string Qualified<TFixture>(string methodName)
    {
        return $"{typeof(TFixture).FullName}.{methodName}";
    }

    private static HashSet<string> CollectSpecFirstRedTests()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var fixtures = typeof(SpecFirstRedRegistryTests).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract);

        foreach (var fixture in fixtures)
        {
            var fixtureIsMarked = HasSpecFirstRedCategory(fixture);
            var testMethods = fixture
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.IsDefined(typeof(TestAttribute), inherit: true));

            foreach (var method in testMethods)
            {
                if (fixtureIsMarked || HasSpecFirstRedCategory(method))
                {
                    found.Add($"{fixture.FullName}.{method.Name}");
                }
            }
        }

        return found;
    }

    private static bool HasSpecFirstRedCategory(MemberInfo member)
    {
        return member
            .GetCustomAttributes<CategoryAttribute>(inherit: true)
            .Any(category => string.Equals(category.Name, AutofillTestCategories.SpecFirstRed, StringComparison.Ordinal));
    }
}
