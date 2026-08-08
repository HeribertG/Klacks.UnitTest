// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// The floor one scenario must not fall below. Every value is a band edge, not a single measurement:
/// it is the worst result the current engine produced over the seeds of
/// <see cref="AutofillSeedBand.Seeds"/>, so a change that leaves the plan inside the spread the search
/// already covers stays green and only a change that pushes it outside turns red. Pinning a single
/// seed instead would report the trajectory noise of the genetic search as a regression — the
/// seed-to-seed spread of the unchanged engine is wider than most real code changes.
/// <para>
/// These are pins of the implementation, not expectations of the specification: they never replace an
/// A-assertion and they carry no verdict about whether the value is good — scenario 1's ideal share of
/// 0 is pinned as a floor exactly because it cannot get worse.
/// </para>
/// </summary>
/// <param name="MinForwardRate">rotation.forwardRate must stay at least this high</param>
/// <param name="MaxMixedTypeCount">packages.mixedTypeCount must stay at most this high</param>
/// <param name="MaxShortPackageShare">Share of packages of at most two days must stay at most this high</param>
/// <param name="MaxPackagesOverIdealLength">Packages longer than the five-day ideal must stay at most this many</param>
/// <param name="MinIdealShare">packages.idealShare must stay at least this high</param>
/// <param name="MaxShiftKindSpread">Widest of the three fairness.spreadPerType values must stay at most this high</param>
/// <param name="MaxMonotonicityViolations">hours.monotonicityViolations must stay at most this many</param>
/// <param name="MinTopRanksPlannedHours">
/// The planned hours of list ranks 1 to <see cref="AutofillSpecConstants.TopRankUpperBound"/> added up
/// must stay at least this high
/// </param>
public sealed record AutofillBaseline(
    double MinForwardRate,
    int MaxMixedTypeCount,
    double MaxShortPackageShare,
    int MaxPackagesOverIdealLength,
    double MinIdealShare,
    int MaxShiftKindSpread,
    int MaxMonotonicityViolations,
    double MinTopRanksPlannedHours);
