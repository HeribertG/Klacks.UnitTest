// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// Stage 3 runs the deterministic local search without any LLM or API key. A successful run lands in the shared result
/// cache together with the snapshot marker of the plan it started from; an engine exception becomes a failure outcome
/// and caches nothing.
/// </summary>
[TestFixture]
public sealed class HolisticHarmonizerRunServiceTests
{
    private const int FixtureSeed = 11;
    private static readonly DateOnly PeriodFrom = new(2026, 3, 2);
    private static readonly DateOnly PeriodUntil = new(2026, 3, 22);

    private IHarmonizerContextBuilder _contextBuilder = null!;
    private IScheduleSnapshotMarkerService _snapshotMarkerService = null!;
    private HarmonizerResultCache _resultCache = null!;
    private HolisticHarmonizerRunService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        _contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => DeterministicSearchFixture.BuildInput(FixtureSeed));

        _snapshotMarkerService = Substitute.For<IScheduleSnapshotMarkerService>();
        _resultCache = new HarmonizerResultCache();

        _sut = new HolisticHarmonizerRunService(
            new HolisticHarmonizerDeterministicEngine(_contextBuilder, NullLogger<HolisticHarmonizerDeterministicEngine>.Instance),
            _resultCache,
            _snapshotMarkerService,
            NullLogger<HolisticHarmonizerRunService>.Instance);
    }

    [Test]
    public async Task RunAsync_ImprovesThePlan_AndCachesTheResultWithTheSnapshotMarker()
    {
        // Arrange
        var input = BuildInput();
        var marker = new ScheduleSnapshotMarker(PeriodFrom, PeriodUntil, input.AgentIds, input.AnalyseToken, 3, 0, "fingerprint");
        _snapshotMarkerService
            .ComputeAsync(PeriodFrom, PeriodUntil, input.AgentIds, input.AnalyseToken, Arg.Any<CancellationToken>())
            .Returns(marker);

        // Act
        var outcome = await _sut.RunAsync(input, CancellationToken.None);

        // Assert
        outcome.IsSuccess.ShouldBeTrue();
        outcome.Result!.LlmModelId.ShouldBe(HolisticHarmonizerDeterministicEngine.EngineLabel);
        outcome.Result.FitnessAfter.ShouldBeGreaterThan(outcome.Result.FitnessBefore);
        outcome.Result.Iterations.ShouldNotBeEmpty();
        outcome.Result.Iterations.ShouldAllBe(i => i.Result == BatchAcceptance.Accepted);
        _resultCache.TryGet(outcome.JobId!.Value, out _, out var cachedFinal, out var cachedToken, out _, out _, out var cachedMarker)
            .ShouldBeTrue();
        cachedFinal.ShouldBeSameAs(outcome.Result.FinalBitmap);
        cachedToken.ShouldBe(input.AnalyseToken);
        cachedMarker.ShouldBeSameAs(marker);
    }

    [Test]
    public async Task RunAsync_WithAJobId_CachesTheResultUnderThatJobId()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        // Act
        var outcome = await _sut.RunAsync(BuildInput(), jobId, progress: null, CancellationToken.None);

        // Assert
        outcome.IsSuccess.ShouldBeTrue();
        outcome.JobId.ShouldBe(jobId);
        _resultCache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
    }

    [Test]
    public async Task RunAsync_HandsTheGroupOfTheRunToTheContextBuilder()
    {
        // Arrange
        var groupId = Guid.NewGuid();

        // Act
        await _sut.RunAsync(BuildInput() with { GroupId = groupId }, CancellationToken.None);

        // Assert
        await _contextBuilder.Received().BuildContextAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.GroupId == groupId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_EngineThrows_ReportsFailureAndCachesNothing()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BitmapInput>(new InvalidOperationException("context unavailable")));

        // Act
        var outcome = await _sut.RunAsync(BuildInput(), jobId, progress: null, CancellationToken.None);

        // Assert
        outcome.IsSuccess.ShouldBeFalse();
        outcome.JobId.ShouldBeNull();
        outcome.FailureMessage.ShouldBe("Holistic Harmonizer engine failed: context unavailable");
        _resultCache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_Cancelled_Throws()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _sut.RunAsync(BuildInput(), cts.Token));
    }

    private static HolisticHarmonizerRunInput BuildInput()
        => new(PeriodFrom, PeriodUntil, [Guid.NewGuid()], AnalyseToken: Guid.NewGuid());
}
