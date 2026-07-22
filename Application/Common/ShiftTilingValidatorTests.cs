// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ShiftTilingValidator: an exact tiling of the order span passes, while a gap or an
/// overlap is rejected with a message that names the offending interval. Covers 24h (start == end)
/// and bounded spans, cross-midnight parts, gaps at the start/end and parts overrunning the order end.
/// </summary>

using Klacks.Api.Application.Common;

namespace Klacks.UnitTest.Application.Common;

[TestFixture]
public class ShiftTilingValidatorTests
{
    private static TimeOnly T(string value) => TimeOnly.Parse(value);

    private static List<(TimeOnly Start, TimeOnly End)> Parts(params (string Start, string End)[] parts)
        => parts.Select(p => (T(p.Start), T(p.End))).ToList();

    [Test]
    public void ExactTiling_Of24hOrder_IsValid()
    {
        var error = ShiftTilingValidator.Validate(
            T("07:00"), T("07:00"),
            Parts(("07:00", "15:00"), ("15:00", "23:00"), ("23:00", "07:00")));

        Assert.That(error, Is.Null);
    }

    [Test]
    public void ExactTiling_OfBoundedOrder_IsValid()
    {
        var error = ShiftTilingValidator.Validate(
            T("08:00"), T("20:00"),
            Parts(("08:00", "14:00"), ("14:00", "20:00")));

        Assert.That(error, Is.Null);
    }

    [Test]
    public void ExactTiling_WithPartsSuppliedOutOfOrder_IsValid()
    {
        var error = ShiftTilingValidator.Validate(
            T("07:00"), T("07:00"),
            Parts(("23:00", "07:00"), ("07:00", "15:00"), ("15:00", "23:00")));

        Assert.That(error, Is.Null);
    }

    [Test]
    public void Gap_BetweenParts_IsRejected_WithInterval()
    {
        var error = ShiftTilingValidator.Validate(
            T("07:00"), T("07:00"),
            Parts(("07:00", "14:00"), ("15:00", "23:00"), ("23:00", "07:00")));

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("gap").And.Contain("14:00").And.Contain("15:00"));
    }

    [Test]
    public void Overlap_BetweenParts_IsRejected_WithInterval()
    {
        var error = ShiftTilingValidator.Validate(
            T("07:00"), T("07:00"),
            Parts(("07:00", "15:00"), ("14:00", "23:00"), ("23:00", "07:00")));

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("overlap").And.Contain("14:00").And.Contain("15:00"));
    }

    [Test]
    public void GapAtStart_WhenFirstPartDoesNotBeginAtOrderStart_IsRejected()
    {
        var error = ShiftTilingValidator.Validate(
            T("08:00"), T("20:00"),
            Parts(("09:00", "14:00"), ("14:00", "20:00")));

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("gap").And.Contain("08:00").And.Contain("09:00"));
    }

    [Test]
    public void GapAtEnd_WhenPartsDoNotReachOrderEnd_IsRejected()
    {
        var error = ShiftTilingValidator.Validate(
            T("08:00"), T("20:00"),
            Parts(("08:00", "14:00"), ("14:00", "18:00")));

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("gap").And.Contain("18:00").And.Contain("20:00"));
    }

    [Test]
    public void Overrun_WhenPartsExtendBeyondOrderEnd_IsRejected()
    {
        var error = ShiftTilingValidator.Validate(
            T("08:00"), T("20:00"),
            Parts(("08:00", "14:00"), ("14:00", "22:00")));

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("overrun").And.Contain("22:00"));
    }

    [Test]
    public void NoParts_IsRejected()
    {
        var error = ShiftTilingValidator.Validate(
            T("08:00"), T("20:00"),
            new List<(TimeOnly Start, TimeOnly End)>());

        Assert.That(error, Is.Not.Null);
    }
}
