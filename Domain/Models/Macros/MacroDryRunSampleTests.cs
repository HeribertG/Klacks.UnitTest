// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroDryRunSample.Changes: a side without a value (no macro, deleted, does not compile, fails on the
/// entry, or an undo that removes the reference) keeps the stored value in production, so it is compared as the stored
/// value; equal effective values do not count as a change, and a directly recorded duration never changes.
/// </summary>

using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Domain.Models.Macros;

[TestFixture]
public class MacroDryRunSampleTests
{
    private const decimal Stored = 5m;
    private const decimal Computed = 4m;

    private static MacroDryRunSample Sample(decimal? current, decimal? next, bool keepsRecordedValue = false) =>
        new(Guid.NewGuid(), new DateOnly(2026, 6, 10), Stored, current, next, keepsRecordedValue);

    [Test]
    public void NoCurrentValue_NewValueEqualToTheStoredOne_DoesNotChange()
    {
        Sample(null, Stored).Changes.ShouldBeFalse();
    }

    [Test]
    public void NoCurrentValue_NewValueDifferentFromTheStoredOne_Changes()
    {
        Sample(null, Computed).Changes.ShouldBeTrue();
    }

    [Test]
    public void NoNewValue_CurrentValueEqualToTheStoredOne_DoesNotChange()
    {
        Sample(Stored, null).Changes.ShouldBeFalse();
    }

    [Test]
    public void NoNewValue_CurrentValueDifferentFromTheStoredOne_Changes()
    {
        Sample(Computed, null).Changes.ShouldBeTrue();
    }

    [Test]
    public void NoValueOnEitherSide_DoesNotChange()
    {
        Sample(null, null).Changes.ShouldBeFalse();
    }

    [Test]
    public void DirectlyRecordedDuration_NeverChanges()
    {
        Sample(Computed, Computed + 1, keepsRecordedValue: true).Changes.ShouldBeFalse();
    }
}
