// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the recipe-run expiry sweep (W1.5): the cutoff it hands the repository is exactly
/// RecipeRunDefaults.ExpireAfter behind the injected clock, the stamp it writes is that same clock
/// rather than an ambient DateTime.UtcNow, and the flipped count is passed through.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class RecipeRunExpirySweepTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private IRecipeRunRepository _repository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private RecipeRunExpirySweep _sweep = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IRecipeRunRepository>();
        _timeProvider = new SettableTimeProvider(NowUtc);
        _sweep = new RecipeRunExpirySweep(_repository, _timeProvider);
    }

    [Test]
    public async Task RunAsync_UsesTheInjectedClock_ForBothCutoffAndStamp()
    {
        DateTime? cutoff = null;
        DateTime? stamp = null;
        _repository.ExpireStaleAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                cutoff = callInfo.ArgAt<DateTime>(0);
                stamp = callInfo.ArgAt<DateTime>(1);
                return 0;
            });

        await _sweep.RunAsync();

        cutoff.ShouldBe(NowUtc - RecipeRunDefaults.ExpireAfter);
        stamp.ShouldBe(NowUtc);
    }

    [Test]
    public async Task RunAsync_MovesTheCutoffWithTheClock()
    {
        DateTime? cutoff = null;
        _repository.ExpireStaleAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                cutoff = callInfo.ArgAt<DateTime>(0);
                return 0;
            });

        _timeProvider.Now = NowUtc.AddDays(3);
        await _sweep.RunAsync();

        cutoff.ShouldBe(NowUtc.AddDays(3) - RecipeRunDefaults.ExpireAfter);
    }

    [Test]
    public async Task RunAsync_ReturnsHowManyRunsWereFlipped()
    {
        _repository.ExpireStaleAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(7);

        (await _sweep.RunAsync()).ShouldBe(7);
    }

    [Test]
    public void ExpireAfter_OutlivesThePendingRecipeTtl()
    {
        _timeProvider.GetUtcNow().UtcDateTime.ShouldBe(NowUtc);
        RecipeRunDefaults.ExpireAfter
            .ShouldBeGreaterThan(TimeSpan.FromMinutes(RecipeEngineDefaults.PendingRecipeTtlMinutes));
    }
}
