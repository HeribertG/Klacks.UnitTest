// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

/// <summary>
/// Pins the difference between "the model says it is done" and "the model produced nothing usable".
/// Only an explicitly empty batches array is a convergence signal; everything else is a dud iteration.
/// </summary>
[TestFixture]
public sealed class HarmonyJsonParserExplicitEmptyTests
{
    private const int MaxStepsPerBatch = 4;
    private const int IterationIndex = 0;

    private static (int BatchCount, string? Error, bool ExplicitlyEmpty) Parse(string raw)
    {
        var batches = HarmonyJsonParser.TryParseBatches(
            raw, MaxStepsPerBatch, IterationIndex, NullLogger.Instance, out var error, out var explicitlyEmpty);
        return (batches.Count, error, explicitlyEmpty);
    }

    [Test]
    public void EmptyBatchesArray_IsExplicitlyEmpty()
    {
        var (count, error, explicitlyEmpty) = Parse("{\"batches\":[]}");

        count.ShouldBe(0);
        error.ShouldBeNull();
        explicitlyEmpty.ShouldBeTrue();
    }

    [Test]
    public void BatchWithoutSteps_IsDiscardedButNotExplicitlyEmpty()
    {
        var (count, error, explicitlyEmpty) = Parse("{\"batches\":[{\"intent\":\"enlarge_pause\",\"steps\":[]}]}");

        count.ShouldBe(0);
        error.ShouldBeNull();
        explicitlyEmpty.ShouldBeFalse();
    }

    [Test]
    public void StepsWithWrongFieldNames_AreDiscardedButNotExplicitlyEmpty()
    {
        var raw = "{\"batches\":[{\"intent\":\"enlarge_pause\",\"steps\":[{\"fromRow\":1,\"toRow\":2,\"day\":3}]}]}";

        var (count, error, explicitlyEmpty) = Parse(raw);

        count.ShouldBe(0);
        error.ShouldBeNull();
        explicitlyEmpty.ShouldBeFalse();
    }

    [Test]
    public void NoJsonAtAll_ReportsErrorAndIsNotExplicitlyEmpty()
    {
        var (count, error, explicitlyEmpty) = Parse("I am afraid I cannot do that.");

        count.ShouldBe(0);
        error.ShouldNotBeNull();
        explicitlyEmpty.ShouldBeFalse();
    }

    [Test]
    public void MissingBatchesProperty_ReportsErrorAndIsNotExplicitlyEmpty()
    {
        var (count, error, explicitlyEmpty) = Parse("{\"result\":\"done\"}");

        count.ShouldBe(0);
        error.ShouldNotBeNull();
        explicitlyEmpty.ShouldBeFalse();
    }
}
