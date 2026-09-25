// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the ownership and lifetime rules of the active turn registry behind the cancel endpoint: only the
/// user who registered a turn can stop it, every other case answers NotFound without telling foreign,
/// unknown and finished turns apart, a repeated stop is harmless, Complete cleans up, and a stop racing a
/// Complete never throws.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class ActiveTurnRegistryTests
{
    private const string Owner = "user-owner";
    private const string Stranger = "user-stranger";
    private const int RaceIterations = 1000;

    private ActiveTurnRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = new ActiveTurnRegistry();
    }

    [Test]
    public void TheRegistry_IsADependencyFreeLeaf()
    {
        typeof(ActiveTurnRegistry).GetConstructors().ShouldAllBe(c => c.GetParameters().Length == 0);
    }

    [Test]
    public void RequestStop_ByTheOwner_CancelsTheTokenAndIsAccepted()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);

        var outcome = _registry.RequestStop(turnId, Owner);

        outcome.ShouldBe(StopRequestOutcome.Accepted);
        token.IsCancellationRequested.ShouldBeTrue();
        _registry.IsStopRequested(turnId).ShouldBeTrue();
    }

    [Test]
    public void Register_ReturnsATokenThatIsNotCancelledUntilAStopIsRequested()
    {
        var turnId = Guid.NewGuid();

        var token = _registry.Register(turnId, Owner);

        token.CanBeCanceled.ShouldBeTrue();
        token.IsCancellationRequested.ShouldBeFalse();
        _registry.IsStopRequested(turnId).ShouldBeFalse();
    }

    [Test]
    public void RequestStop_ByAnotherUser_IsNotFoundAndLeavesTheTokenAlone()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);

        var outcome = _registry.RequestStop(turnId, Stranger);

        outcome.ShouldBe(StopRequestOutcome.NotFound);
        token.IsCancellationRequested.ShouldBeFalse();
        _registry.IsStopRequested(turnId).ShouldBeFalse();
    }

    [Test]
    public void RequestStop_ComparesTheOwnerOrdinally()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, "User-Owner");

        _registry.RequestStop(turnId, "user-owner").ShouldBe(StopRequestOutcome.NotFound);
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void RequestStop_ForAnUnknownTurn_IsNotFound()
    {
        _registry.RequestStop(Guid.NewGuid(), Owner).ShouldBe(StopRequestOutcome.NotFound);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void RequestStop_WithAnEmptyUserId_IsNotFoundEvenForATurnRegisteredWithOne(string userId)
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);

        _registry.RequestStop(turnId, userId).ShouldBe(StopRequestOutcome.NotFound);
        token.IsCancellationRequested.ShouldBeFalse();
    }

    [Test]
    public void RequestStop_Twice_IsAcceptedBothTimes()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);

        _registry.RequestStop(turnId, Owner).ShouldBe(StopRequestOutcome.Accepted);
        _registry.RequestStop(turnId, Owner).ShouldBe(StopRequestOutcome.Accepted);

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void Complete_RemovesTheTurn()
    {
        var turnId = Guid.NewGuid();
        _registry.Register(turnId, Owner);

        _registry.Complete(turnId);

        _registry.RequestStop(turnId, Owner).ShouldBe(StopRequestOutcome.NotFound);
        _registry.IsStopRequested(turnId).ShouldBeFalse();
        _registry.ActiveCount.ShouldBe(0);
    }

    [Test]
    public void Complete_ForAnUnknownTurn_DoesNotThrow()
    {
        Should.NotThrow(() => _registry.Complete(Guid.NewGuid()));
    }

    [Test]
    public void Complete_Twice_DoesNotThrow()
    {
        var turnId = Guid.NewGuid();
        _registry.Register(turnId, Owner);

        _registry.Complete(turnId);

        Should.NotThrow(() => _registry.Complete(turnId));
    }

    [Test]
    public void Complete_KeepsTheTokenOfAnEarlierStopCancelled()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);
        _registry.RequestStop(turnId, Owner);

        _registry.Complete(turnId);

        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void Register_ForTwoTurns_KeepsThemIndependent()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstToken = _registry.Register(first, Owner);
        var secondToken = _registry.Register(second, Stranger);

        _registry.RequestStop(first, Owner);

        firstToken.IsCancellationRequested.ShouldBeTrue();
        secondToken.IsCancellationRequested.ShouldBeFalse();
        _registry.ActiveCount.ShouldBe(2);
    }

    [Test]
    public void Register_WithAnEmptyTurnId_Throws()
    {
        Should.Throw<ArgumentException>(() => _registry.Register(Guid.Empty, Owner));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Register_WithAnEmptyUserId_Throws(string userId)
    {
        Should.Throw<ArgumentException>(() => _registry.Register(Guid.NewGuid(), userId));
        _registry.ActiveCount.ShouldBe(0);
    }

    [Test]
    public void Register_WithNullUserId_Throws()
    {
        Should.Throw<ArgumentException>(() => _registry.Register(Guid.NewGuid(), null!));
    }

    [Test]
    public void Register_TheSameTurnIdTwice_Throws()
    {
        var turnId = Guid.NewGuid();
        var token = _registry.Register(turnId, Owner);

        Should.Throw<InvalidOperationException>(() => _registry.Register(turnId, Owner));

        _registry.RequestStop(turnId, Owner).ShouldBe(StopRequestOutcome.Accepted);
        token.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public async Task RequestStopRacingComplete_NeverThrows()
    {
        for (var i = 0; i < RaceIterations; i++)
        {
            var turnId = Guid.NewGuid();
            var token = _registry.Register(turnId, Owner);

            var stop = Task.Run(() => _registry.RequestStop(turnId, Owner));
            var complete = Task.Run(() => _registry.Complete(turnId));

            await Should.NotThrowAsync(() => Task.WhenAll(stop, complete));

            var outcome = await stop;
            (outcome is StopRequestOutcome.Accepted or StopRequestOutcome.NotFound).ShouldBeTrue();
            if (outcome == StopRequestOutcome.Accepted)
            {
                token.IsCancellationRequested.ShouldBeTrue();
            }
        }

        _registry.ActiveCount.ShouldBe(0);
    }
}
