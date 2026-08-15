// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Rules 3 and 4: package integrity and shift-kind homogeneity inside a package.</summary>
/// <param name="Items">Every package of every employee, ordered by list rank and start date</param>
/// <param name="LengthHistogram">Package count per length bucket, "1".."6" and "7+"</param>
/// <param name="FreeBlockHistogram">Count of free blocks per length, period edges excluded</param>
/// <param name="IdealShare">Share of packages of length 5 followed by exactly 2 free days</param>
/// <param name="MixedTypeCount">Packages that change shift kind inside the package</param>
/// <param name="FreeEdges">Free days at the period boundaries, reported but not counted as free blocks</param>
/// <param name="ForcedShortenings">
/// Sub-five-day packages of a ban-restricted kind that the eligible pool provably could not avoid,
/// each carrying its capacity proof. Empty without an eligibility input
/// </param>
/// <param name="UnexplainedShortenings">
/// Sub-five-day packages of a ban-restricted kind beyond the provable budget — shortenings the pool
/// arithmetic does not force. 0 without an eligibility input; kinds the ban list never mentions are
/// outside this measure, their shortness is what the length histogram already reports
/// </param>
public sealed record PackageMetrics(
    IReadOnlyList<WorkPackage> Items,
    IReadOnlyDictionary<string, int> LengthHistogram,
    IReadOnlyDictionary<int, int> FreeBlockHistogram,
    double IdealShare,
    int MixedTypeCount,
    IReadOnlyList<EmployeeFreeEdge> FreeEdges,
    IReadOnlyList<ForcedShortening> ForcedShortenings,
    int UnexplainedShortenings)
{
    /// <summary>
    /// Leading free days of an employee that the plan offered no way to fill — see
    /// <see cref="ForcedFreeDayRun"/> for why the measure stops at the first package.
    /// </summary>
    public IReadOnlyList<ForcedFreeDayRun> ForcedExtraFreeDays { get; init; } = [];

    /// <summary>
    /// Packages an absence cut short. Empty for a scenario without absences. Every entry is forced by
    /// construction — the next day could not be worked — which is why it is declared rather than
    /// counted as an ordinary short package.
    /// </summary>
    public IReadOnlyList<AbsenceCut> AbsenceCuts { get; init; } = [];

    /// <summary>
    /// Packages that ran past the contractual maximum length — the first of the two shapes an
    /// arithmetic surplus takes in a finished plan.
    /// </summary>
    public IReadOnlyList<PackageExtension> ForcedExtensions { get; init; } = [];

    /// <summary>
    /// Gaps that granted fewer free days than the contract asks for — the second shape. Together with
    /// <see cref="ForcedExtensions"/> this is where the capacity a scenario lacks becomes visible in
    /// the PLAN rather than only in the fixture arithmetic.
    /// </summary>
    public IReadOnlyList<ShortenedFreeBlock> ShortenedFreeBlocks { get; init; } = [];
}
