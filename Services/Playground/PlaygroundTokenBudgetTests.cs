// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundTokenBudget: independent global, IP and device buckets, atomic reserve, reconcile of the
/// estimate against the reported usage, estimate kept when nothing is settled, the 02:00 UTC day boundary and
/// the fail-closed handling of non-positive limits.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundTokenBudgetTests
{
    private const int GlobalLimit = 1000;
    private const int IpLimit = 400;
    private const int DeviceLimit = 200;
    private const string IpA = "203.0.113.1";
    private const string IpB = "203.0.113.2";
    private const string DeviceA = "device-a";
    private const string DeviceB = "device-b";

    private SettableTimeProvider _clock = null!;
    private PlaygroundTokenBudget _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new SettableTimeProvider(new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc));
        _sut = Create(GlobalLimit, IpLimit, DeviceLimit);
    }

    private PlaygroundTokenBudget Create(int global, int perIp, int perDevice) =>
        new(_clock, Options.Create(new PlaygroundTokenLimitOptions
        {
            GlobalDailyTokens = global,
            PerIpDailyTokens = perIp,
            PerDeviceDailyTokens = perDevice,
        }));

    private static PlaygroundVisitor Visitor(string? ip, string? device) => new(ip, device);

    [Test]
    public void TryReserve_WithRoom_ReturnsReservationAndBooksTheEstimate()
    {
        var reservation = _sut.TryReserve(Visitor(IpA, DeviceA), 150);

        reservation.ShouldNotBeNull();
        reservation.ChargedTokens.ShouldBe(150);
        _sut.IsExhausted(Visitor(IpA, DeviceA)).ShouldBeFalse();
    }

    [Test]
    public void DeviceBucket_BlocksOnItsOwn_WhileIpAndGlobalStillHaveRoom()
    {
        _sut.TryReserve(Visitor(IpA, DeviceA), DeviceLimit).ShouldNotBeNull();

        _sut.IsExhausted(Visitor(IpA, DeviceA)).ShouldBeTrue();
        _sut.TryReserve(Visitor(IpA, DeviceA), 1).ShouldBeNull();
        _sut.TryReserve(Visitor(IpA, DeviceB), 1).ShouldNotBeNull();
    }

    [Test]
    public void IpBucket_BlocksOnItsOwn_ForEveryDeviceBehindThatIp()
    {
        _sut.TryReserve(Visitor(IpA, DeviceA), IpLimit).ShouldNotBeNull();

        _sut.TryReserve(Visitor(IpA, DeviceB), 1).ShouldBeNull();
        _sut.TryReserve(Visitor(IpB, DeviceB), 1).ShouldNotBeNull();
    }

    [Test]
    public void IpBucket_IsNotEscapedByMintingNewDevices()
    {
        var reservations = new List<PlaygroundBudgetReservation?>();
        for (var i = 0; i < 10; i++)
        {
            reservations.Add(_sut.TryReserve(Visitor(IpA, $"fresh-{i}"), DeviceLimit));
        }

        reservations.Count(r => r is not null).ShouldBe(IpLimit / DeviceLimit);
    }

    [Test]
    public void GlobalBucket_BlocksEveryone_EvenWithoutVisitor()
    {
        _sut.TryReserve(null, GlobalLimit).ShouldNotBeNull();

        _sut.IsExhausted(null).ShouldBeTrue();
        _sut.IsExhausted(Visitor(IpB, DeviceB)).ShouldBeTrue();
        _sut.TryReserve(Visitor(IpB, DeviceB), 1).ShouldBeNull();
    }

    [Test]
    public void TryReserve_WithoutVisitor_ChargesOnlyTheGlobalBucket()
    {
        _sut.TryReserve(null, IpLimit).ShouldNotBeNull();

        _sut.TryReserve(Visitor(IpA, DeviceA), IpLimit).ShouldNotBeNull();
    }

    [Test]
    public void TryReserve_VisitorWithoutDevice_ChargesGlobalAndIp()
    {
        _sut.TryReserve(Visitor(IpA, null), IpLimit).ShouldNotBeNull();

        _sut.TryReserve(Visitor(IpA, null), 1).ShouldBeNull();
    }

    [Test]
    public void TryReserve_RefusedBecauseOfOneBucket_BooksNothingInTheOthers()
    {
        _sut.TryReserve(Visitor(IpA, DeviceA), DeviceLimit).ShouldNotBeNull();

        _sut.TryReserve(Visitor(IpA, DeviceA), 50).ShouldBeNull();

        _sut.TryReserve(Visitor(IpA, DeviceB), IpLimit - DeviceLimit - 1).ShouldNotBeNull();
        _sut.IsExhausted(Visitor(IpA, DeviceB)).ShouldBeFalse();
    }

    [Test]
    public void TryReserve_TheCallThatCrossesTheLine_IsLetThroughOnce()
    {
        _sut.TryReserve(Visitor(IpA, DeviceA), DeviceLimit - 1).ShouldNotBeNull();

        _sut.TryReserve(Visitor(IpA, DeviceA), DeviceLimit).ShouldNotBeNull();
        _sut.TryReserve(Visitor(IpA, DeviceA), 1).ShouldBeNull();
    }

    [Test]
    public void IsExhausted_AgreesWithTryReserve()
    {
        var visitor = Visitor(IpA, DeviceA);

        _sut.IsExhausted(visitor).ShouldBeFalse();
        _sut.TryReserve(visitor, DeviceLimit);

        _sut.IsExhausted(visitor).ShouldBeTrue();
        _sut.TryReserve(visitor, 1).ShouldBeNull();
    }

    [Test]
    public void Settle_BelowTheEstimate_GivesTokensBack()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, DeviceLimit)!;
        _sut.IsExhausted(visitor).ShouldBeTrue();

        _sut.Settle(reservation, 50);

        _sut.IsExhausted(visitor).ShouldBeFalse();
        _sut.TryReserve(visitor, DeviceLimit - 50).ShouldNotBeNull();
        _sut.TryReserve(visitor, 1).ShouldBeNull();
    }

    [Test]
    public void Settle_AboveTheEstimate_AddsTheDifference()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, 10)!;

        _sut.Settle(reservation, DeviceLimit);

        _sut.IsExhausted(visitor).ShouldBeTrue();
    }

    [Test]
    public void Settle_IsIdempotent_LastReportedValueWins()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, 100)!;

        _sut.Settle(reservation, 60);
        _sut.Settle(reservation, 60);
        _sut.Settle(reservation, 80);

        reservation.ChargedTokens.ShouldBe(80);
        _sut.TryReserve(visitor, DeviceLimit - 80).ShouldNotBeNull();
        _sut.TryReserve(visitor, 1).ShouldBeNull();
    }

    [Test]
    public void UnsettledReservation_KeepsItsEstimate_SoAnAbortedTurnDoesNotRefund()
    {
        var visitor = Visitor(IpA, DeviceA);
        _sut.TryReserve(visitor, DeviceLimit);

        _sut.IsExhausted(visitor).ShouldBeTrue();
    }

    [Test]
    public void Rollover_HappensAtTwoUtc_NotAtMidnight()
    {
        _clock.Now = new DateTime(2026, 10, 28, 2, 30, 0, DateTimeKind.Utc);
        var visitor = Visitor(IpA, DeviceA);
        _sut.TryReserve(visitor, DeviceLimit);
        _sut.IsExhausted(visitor).ShouldBeTrue();

        _clock.Now = new DateTime(2026, 10, 29, 0, 30, 0, DateTimeKind.Utc);
        _sut.IsExhausted(visitor).ShouldBeTrue();

        _clock.Now = new DateTime(2026, 10, 29, 1, 59, 59, DateTimeKind.Utc);
        _sut.IsExhausted(visitor).ShouldBeTrue();

        _clock.Now = new DateTime(2026, 10, 29, PlaygroundTokenLimitConstants.DayBoundaryHourUtc, 0, 0, DateTimeKind.Utc);
        _sut.IsExhausted(visitor).ShouldBeFalse();
        _sut.TryReserve(visitor, DeviceLimit).ShouldNotBeNull();
    }

    [Test]
    public void Rollover_ResetsAllThreeBuckets()
    {
        _sut.TryReserve(null, GlobalLimit);
        _sut.IsExhausted(null).ShouldBeTrue();

        _clock.Now = _clock.Now.AddDays(1);

        _sut.IsExhausted(null).ShouldBeFalse();
        _sut.TryReserve(Visitor(IpA, DeviceA), DeviceLimit).ShouldNotBeNull();
    }

    [Test]
    public void Settle_AfterTheDayRolledOver_DoesNotTouchTheNewDay()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, 100)!;
        _clock.Now = _clock.Now.AddDays(1);
        _sut.TryReserve(visitor, 50).ShouldNotBeNull();

        _sut.Settle(reservation, DeviceLimit * 10);

        _sut.IsExhausted(visitor).ShouldBeFalse();
        _sut.TryReserve(visitor, DeviceLimit - 50 - 1).ShouldNotBeNull();
        _sut.IsExhausted(visitor).ShouldBeFalse();
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void NonPositiveLimits_FallBackToTheDefaults_NeverToUnlimited(int configured)
    {
        var globalProbe = Create(configured, configured, configured);
        globalProbe.TryReserve(null, PlaygroundTokenLimitConstants.DefaultGlobalDailyTokens).ShouldNotBeNull();
        globalProbe.IsExhausted(null).ShouldBeTrue();

        var deviceProbe = Create(configured, configured, configured);
        var visitor = Visitor(IpA, DeviceA);
        deviceProbe.TryReserve(visitor, PlaygroundTokenLimitConstants.DefaultPerDeviceDailyTokens).ShouldNotBeNull();
        deviceProbe.TryReserve(visitor, 1).ShouldBeNull();

        var ipProbe = Create(configured, configured, configured);
        ipProbe.TryReserve(Visitor(IpA, null), PlaygroundTokenLimitConstants.DefaultPerIpDailyTokens).ShouldNotBeNull();
        ipProbe.TryReserve(Visitor(IpA, null), 1).ShouldBeNull();
    }

    [Test]
    public void DefaultOptions_UseTheDocumentedDailyLimits()
    {
        var options = new PlaygroundTokenLimitOptions();

        options.GlobalDailyTokens.ShouldBe(1_500_000);
        options.PerIpDailyTokens.ShouldBe(150_000);
        options.PerDeviceDailyTokens.ShouldBe(75_000);
        PlaygroundTokenLimitConstants.DayBoundaryHourUtc.ShouldBe(2);
    }

    [Test]
    public void TryReserve_UnderParallelLoad_GrantsExactlyWhatTheGlobalLimitAllows()
    {
        const int Attempts = 500;
        const int Estimate = 10;
        var granted = 0;

        Parallel.For(0, Attempts, _ =>
        {
            if (_sut.TryReserve(null, Estimate) is not null)
            {
                Interlocked.Increment(ref granted);
            }
        });

        granted.ShouldBe(GlobalLimit / Estimate);
    }

    [Test]
    public void Release_GivesTheWholeEstimateBackToEveryBucket()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, DeviceLimit)!;
        _sut.IsExhausted(visitor).ShouldBeTrue();

        _sut.Release(reservation);

        reservation.ChargedTokens.ShouldBe(0);
        _sut.IsExhausted(visitor).ShouldBeFalse();
        _sut.TryReserve(null, GlobalLimit).ShouldNotBeNull();
    }

    [Test]
    public void Release_IsIdempotent_AndDoesNotGoBelowOtherCalls()
    {
        var visitor = Visitor(IpA, DeviceA);
        var kept = _sut.TryReserve(visitor, 100)!;
        var refused = _sut.TryReserve(visitor, 80)!;

        _sut.Release(refused);
        _sut.Release(refused);

        kept.ChargedTokens.ShouldBe(100);
        _sut.TryReserve(visitor, DeviceLimit - 100).ShouldNotBeNull();
        _sut.IsExhausted(visitor).ShouldBeTrue();
    }

    [Test]
    public void Release_AfterTheDayRolledOver_LeavesTheNewDayAlone()
    {
        var visitor = Visitor(IpA, DeviceA);
        var reservation = _sut.TryReserve(visitor, 100)!;
        _clock.Now = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        var today = _sut.TryReserve(visitor, 150)!;

        _sut.Release(reservation);

        today.ChargedTokens.ShouldBe(150);
        _sut.TryReserve(visitor, DeviceLimit - 150).ShouldNotBeNull();
        _sut.IsExhausted(visitor).ShouldBeTrue();
    }
}
