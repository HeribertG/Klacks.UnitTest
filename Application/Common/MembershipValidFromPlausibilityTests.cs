// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MembershipValidFromPlausibility: a date within the ±window and not before the
/// birthdate is accepted (null reason), while a date before the birthdate, more than the configured
/// years in the past, or more than the configured years in the future returns an English reason.
/// </summary>

using Klacks.Api.Application.Common;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Common;

[TestFixture]
public class MembershipValidFromPlausibilityTests
{
    private static readonly DateTime Reference = new(2026, 7, 22);

    [Test]
    public void PlausibleDate_ReturnsNull()
    {
        var reason = MembershipValidFromPlausibility.Evaluate(
            new DateTime(2026, 3, 1), new DateTime(1990, 1, 1), Reference);

        Assert.That(reason, Is.Null);
    }

    [Test]
    public void UnknownBirthdate_WithinWindow_ReturnsNull()
    {
        var reason = MembershipValidFromPlausibility.Evaluate(
            new DateTime(2026, 3, 1), null, Reference);

        Assert.That(reason, Is.Null);
    }

    [Test]
    public void BeforeBirthdate_ReturnsReason()
    {
        var reason = MembershipValidFromPlausibility.Evaluate(
            new DateTime(2000, 1, 1), new DateTime(2005, 6, 15), Reference);

        Assert.That(reason, Does.Contain("before the employee's birthdate"));
    }

    [Test]
    public void MoreThanMaxYearsInPast_ReturnsReason()
    {
        var reason = MembershipValidFromPlausibility.Evaluate(
            Reference.AddYears(-MembershipPlausibilityDefaults.MaxYearsInPast).AddDays(-1), null, Reference);

        Assert.That(reason, Does.Contain("years in the past"));
    }

    [Test]
    public void MoreThanMaxYearsInFuture_ReturnsReason()
    {
        var reason = MembershipValidFromPlausibility.Evaluate(
            Reference.AddYears(MembershipPlausibilityDefaults.MaxYearsInFuture).AddDays(1), null, Reference);

        Assert.That(reason, Does.Contain("years in the future"));
    }

    [Test]
    public void ExactlyAtWindowBoundary_ReturnsNull()
    {
        var earliest = MembershipValidFromPlausibility.Evaluate(
            Reference.AddYears(-MembershipPlausibilityDefaults.MaxYearsInPast), null, Reference);
        var latest = MembershipValidFromPlausibility.Evaluate(
            Reference.AddYears(MembershipPlausibilityDefaults.MaxYearsInFuture), null, Reference);

        Assert.That(earliest, Is.Null);
        Assert.That(latest, Is.Null);
    }
}
