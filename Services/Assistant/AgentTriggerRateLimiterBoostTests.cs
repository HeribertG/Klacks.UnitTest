// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the helpful-learned daily budget boost of AgentTriggerRateLimiter — verifies
/// that a boost raises the remaining budget and allows extra fires beyond the base budget, that
/// the boost is clamped to MaxDailyBudgetBoost and never negative, that zero clears a previous
/// boost, and that a boost applies only to the exact user and kind it was set for. The base
/// budget is read from a fresh limiter instead of being hard-coded, so these tests stay valid
/// if the default changes.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerRateLimiterBoostTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string KindA = "kind-a";
    private const string KindB = "kind-b";
    private const int Boost = 2;

    private AgentTriggerRateLimiter _sut = null!;
    private int _baseBudget;

    [SetUp]
    public void Setup()
    {
        _sut = new AgentTriggerRateLimiter(TimeProvider.System);
        _baseBudget = _sut.GetRemainingBudget(UserA, KindA);
    }

    private void ExhaustBaseBudget(string userId, string triggerKind)
    {
        for (var i = 0; i < _baseBudget; i++)
        {
            _sut.RecordFire(userId, triggerKind);
        }
    }

    [Test]
    public void SetDailyBudgetBoost_RaisesRemainingBudget()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, Boost);

        Assert.That(_sut.GetRemainingBudget(UserA, KindA), Is.EqualTo(_baseBudget + Boost));
    }

    [Test]
    public void BoostedKind_AllowsExtraFiresAfterBaseBudgetIsExhausted()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, Boost);
        ExhaustBaseBudget(UserA, KindA);

        Assert.That(_sut.ShouldFire(UserA, KindA), Is.True);
        Assert.That(_sut.GetRemainingBudget(UserA, KindA), Is.EqualTo(Boost));

        for (var i = 0; i < Boost; i++)
        {
            _sut.RecordFire(UserA, KindA);
        }

        Assert.That(_sut.ShouldFire(UserA, KindA), Is.False);
        Assert.That(_sut.GetRemainingBudget(UserA, KindA), Is.EqualTo(0));
    }

    [Test]
    public void Boost_IsClampedAtMaxDailyBudgetBoost()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, int.MaxValue);

        Assert.That(
            _sut.GetRemainingBudget(UserA, KindA),
            Is.EqualTo(_baseBudget + ProactiveHelpfulLearning.MaxDailyBudgetBoost));
    }

    [Test]
    public void NegativeBoost_IsTreatedAsZero()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, -1);

        Assert.That(_sut.GetRemainingBudget(UserA, KindA), Is.EqualTo(_baseBudget));
    }

    [Test]
    public void ZeroBoost_ClearsAPreviousBoost()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, Boost);
        _sut.SetDailyBudgetBoost(UserA, KindA, 0);

        Assert.That(_sut.GetRemainingBudget(UserA, KindA), Is.EqualTo(_baseBudget));
    }

    [Test]
    public void Boost_AppliesOnlyToTheGivenUserAndKind()
    {
        _sut.SetDailyBudgetBoost(UserA, KindA, Boost);

        Assert.That(_sut.GetRemainingBudget(UserA, KindB), Is.EqualTo(_baseBudget));
        Assert.That(_sut.GetRemainingBudget(UserB, KindA), Is.EqualTo(_baseBudget));
    }
}
