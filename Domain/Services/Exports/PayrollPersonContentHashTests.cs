// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollPersonContentHash and PayrollEntriesSnapshot: the hash is stable for the documented line
/// format, independent of entry order and of the current culture, and changes whenever a day entry changes.
/// </summary>
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Domain.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Exports;

[TestFixture]
public class PayrollPersonContentHashTests
{
    private static readonly Guid AbsenceId = Guid.Parse("a1000000-0000-4000-8000-000000000002");

    private CultureInfo _originalCulture = null!;

    [SetUp]
    public void Setup()
    {
        _originalCulture = CultureInfo.CurrentCulture;
    }

    [TearDown]
    public void TearDown()
    {
        CultureInfo.CurrentCulture = _originalCulture;
    }

    [Test]
    public void Compute_FollowsTheDocumentedLineFormat()
    {
        var entries = SampleEntries();

        var hash = PayrollPersonContentHash.Compute(entries);

        var expectedPayload = PayrollPersonContentHash.HashVersionPrefix + string.Join(
            "\n",
            "2026-01-15|0|8.5|0|",
            "2026-01-15|1|1.25|0|",
            $"2026-01-20|2|1|1|{AbsenceId}");
        hash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedPayload))));
        hash.Length.ShouldBe(64);
    }

    [Test]
    public void Compute_HashesTheVersionPrefixWithThePayload()
    {
        PayrollPersonContentHash.HashVersionPrefix.ShouldNotBeNullOrEmpty();
        PayrollPersonContentHash.Compute([]).ShouldBe(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PayrollPersonContentHash.HashVersionPrefix))));
    }

    [Test]
    public void Compute_IsIndependentOfEntryOrder()
    {
        var entries = SampleEntries();
        var reversed = entries.AsEnumerable().Reverse().ToList();

        PayrollPersonContentHash.Compute(reversed).ShouldBe(PayrollPersonContentHash.Compute(entries));
    }

    [Test]
    public void Compute_SortsEntriesOfTheSameDayAndKindByAbsenceThenUnitThenQuantity()
    {
        var absenceA = Guid.Parse("00000000-0000-4000-8000-00000000000a");
        var absenceB = Guid.Parse("00000000-0000-4000-8000-00000000000b");
        var first = new List<PayrollDayEntry>
        {
            Absence(absenceB, 2m),
            Absence(absenceA, 3m),
            Absence(absenceA, 1m),
        };
        var second = new List<PayrollDayEntry> { first[2], first[0], first[1] };

        PayrollPersonContentHash.Compute(first).ShouldBe(PayrollPersonContentHash.Compute(second));
    }

    [TestCase("de-CH")]
    [TestCase("en-US")]
    [TestCase("de-DE")]
    [TestCase("tr-TR")]
    public void Compute_IsIndependentOfTheCurrentCulture(string cultureName)
    {
        var baseline = PayrollPersonContentHash.Compute(SampleEntries());

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        var underCulture = PayrollPersonContentHash.Compute(SampleEntries());

        underCulture.ShouldBe(baseline);
    }

    [Test]
    public void Compute_ChangesWhenAQuantityChanges()
    {
        var changed = SampleEntries();
        changed[1].Quantity = 8.75m;

        PayrollPersonContentHash.Compute(changed).ShouldNotBe(PayrollPersonContentHash.Compute(SampleEntries()));
    }

    [Test]
    public void Compute_ChangesWhenAnEntryIsAddedOrRemoved()
    {
        var extra = SampleEntries();
        extra.Add(new PayrollDayEntry { Date = new DateOnly(2026, 1, 21), Kind = PayrollEntryKind.WorkHours, Quantity = 4m });
        var fewer = SampleEntries().Take(2).ToList();
        var baseline = PayrollPersonContentHash.Compute(SampleEntries());

        PayrollPersonContentHash.Compute(extra).ShouldNotBe(baseline);
        PayrollPersonContentHash.Compute(fewer).ShouldNotBe(baseline);
    }

    [Test]
    public void Compute_ChangesWhenKindUnitDateOrAbsenceChanges()
    {
        var baseline = PayrollPersonContentHash.Compute(SampleEntries());

        var otherKind = SampleEntries();
        otherKind[1].Kind = PayrollEntryKind.Surcharge;
        var otherUnit = SampleEntries();
        otherUnit[0].Unit = PayrollQuantityUnit.Hours;
        var otherDate = SampleEntries();
        otherDate[0].Date = new DateOnly(2026, 1, 21);
        var otherAbsence = SampleEntries();
        otherAbsence[0].AbsenceId = Guid.NewGuid();

        new[] { otherKind, otherUnit, otherDate, otherAbsence }
            .Select(PayrollPersonContentHash.Compute)
            .ShouldAllBe(hash => hash != baseline);
    }

    [Test]
    public void Compute_TreatsEqualDecimalsWithDifferentScaleAsEqual()
    {
        var withScale = new List<PayrollDayEntry> { Work(8.50m) };
        var withoutScale = new List<PayrollDayEntry> { Work(8.5m) };

        PayrollPersonContentHash.Compute(withScale).ShouldBe(PayrollPersonContentHash.Compute(withoutScale));
    }

    [Test]
    public void Compute_IgnoresQuantityNoiseBeyondFourDecimals()
    {
        var noisy = new List<PayrollDayEntry> { Work(8.500001m) };
        var clean = new List<PayrollDayEntry> { Work(8.5m) };
        var negativeNoise = new List<PayrollDayEntry> { Work(-0.00001m) };
        var zero = new List<PayrollDayEntry> { Work(0m) };

        PayrollPersonContentHash.Compute(noisy).ShouldBe(PayrollPersonContentHash.Compute(clean));
        PayrollPersonContentHash.Compute(negativeNoise).ShouldBe(PayrollPersonContentHash.Compute(zero));
    }

    [Test]
    public void Snapshot_IsCanonicalAndIndependentOfOrderAndCulture()
    {
        var entries = SampleEntries();
        var baseline = PayrollEntriesSnapshot.ToJson(entries);

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-CH");
        var reversed = PayrollEntriesSnapshot.ToJson(entries.AsEnumerable().Reverse().ToList());

        reversed.ShouldBe(baseline);
        baseline.ShouldBe(
            "[{\"Date\":\"2026-01-15\",\"Kind\":0,\"Quantity\":8.5,\"Unit\":0,\"AbsenceId\":null},"
            + "{\"Date\":\"2026-01-15\",\"Kind\":1,\"Quantity\":1.25,\"Unit\":0,\"AbsenceId\":null},"
            + $"{{\"Date\":\"2026-01-20\",\"Kind\":2,\"Quantity\":1,\"Unit\":1,\"AbsenceId\":\"{AbsenceId}\"}}]");
        JsonDocument.Parse(baseline).RootElement.GetArrayLength().ShouldBe(3);
    }

    [Test]
    public void Snapshot_OfNoEntries_IsAnEmptyArray()
    {
        PayrollEntriesSnapshot.ToJson([]).ShouldBe("[]");
    }

    private static List<PayrollDayEntry> SampleEntries()
    {
        return
        [
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 1m, Unit = PayrollQuantityUnit.Days, AbsenceId = AbsenceId },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.Surcharge, Quantity = 1.25m },
        ];
    }

    private static PayrollDayEntry Work(decimal quantity)
    {
        return new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = quantity };
    }

    private static PayrollDayEntry Absence(Guid absenceId, decimal quantity)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = quantity,
            AbsenceId = absenceId,
        };
    }
}
