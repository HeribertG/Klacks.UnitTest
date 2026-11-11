// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Pure-computation checks of the two package-length helpers the A5 specification target reads:
/// <see cref="AutofillPlanAnalyzer.LengthHistogramOf"/> and <see cref="AutofillPlanAnalyzer.LengthModeRivals"/>.
/// No engine run is involved; these tests pin the measuring instrument so a red A5 is attributable to the
/// plan and not to the analyzer.
/// </summary>
[TestFixture]
public sealed class PackageLengthModeTests
{
    private const string Employee = "MA-1";
    private const int IdealLength = 5;
    private const int ShortLength = 2;
    private const int OverflowLength = 8;
    private const string OverflowBucket = "7+";
    private const string ShortBucket = "2";
    private const string IdealBucket = "5";
    private const int NamedBucketCount = 6;
    private const int BucketCount = NamedBucketCount + 1;

    private static readonly DateOnly Start = new(2026, 3, 1);

    [Test]
    public void LengthHistogramOf_UsesTheBucketsOfTheMetricObject()
    {
        var histogram = AutofillPlanAnalyzer.LengthHistogramOf(
            [Package(IdealLength), Package(ShortLength), Package(OverflowLength)]);

        histogram.Count.ShouldBe(BucketCount, "The histogram must carry the buckets 1 to 6 and 7+, nothing else.");
        histogram[IdealBucket].ShouldBe(1);
        histogram[ShortBucket].ShouldBe(1);
        histogram[OverflowBucket].ShouldBe(1, "A package longer than six days belongs to the 7+ bucket.");
    }

    [Test]
    public void LengthModeRivals_AreEmptyWhenFiveIsTheSingleMostFrequentLength()
    {
        var histogram = AutofillPlanAnalyzer.LengthHistogramOf(
            [Package(IdealLength), Package(IdealLength), Package(ShortLength)]);

        AutofillPlanAnalyzer.LengthModeRivals(histogram, IdealLength).ShouldBeEmpty();
    }

    [Test]
    public void LengthModeRivals_ReportATieAtTheTop()
    {
        var histogram = AutofillPlanAnalyzer.LengthHistogramOf([Package(IdealLength), Package(ShortLength)]);

        var rivals = AutofillPlanAnalyzer.LengthModeRivals(histogram, IdealLength);

        rivals.Count.ShouldBe(
            1,
            "A tie is not a single mode: length 2 is as frequent as length 5 and must be reported as a rival.");
        rivals[0].Key.ShouldBe(ShortBucket);
    }

    [Test]
    public void LengthModeRivals_ReportEveryOtherBucketForAPlanWithoutPackages()
    {
        var histogram = AutofillPlanAnalyzer.LengthHistogramOf([]);

        AutofillPlanAnalyzer.LengthModeRivals(histogram, IdealLength).Count.ShouldBe(
            BucketCount - 1,
            "An empty plan must never pass as peaking at five days.");
    }

    private static WorkPackage Package(int lengthDays)
        => new(
            Employee,
            Start,
            Start.AddDays(lengthDays - 1),
            lengthDays,
            AutofillShiftKind.Early,
            false,
            Start.ToDateTime(TimeOnly.MinValue),
            Start.AddDays(lengthDays).ToDateTime(TimeOnly.MinValue));
}
