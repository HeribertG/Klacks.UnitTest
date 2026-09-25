// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The cleanup a stopped or interrupted turn ends with: the confirmations the turn issued are dropped and
/// its dispatched UiAction rows are closed as cancelled. Each part is skipped when its key is unusable and
/// a failing part never stops the other one or escapes to the caller.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class StoppedTurnCleanupTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TurnId = Guid.NewGuid();

    private ITurnConfirmationDiscarder _discarder = null!;
    private ISkillUsageRepository _usage = null!;
    private StoppedTurnCleanup _cleanup = null!;

    [SetUp]
    public void SetUp()
    {
        _discarder = Substitute.For<ITurnConfirmationDiscarder>();
        _usage = Substitute.For<ISkillUsageRepository>();
        _cleanup = new StoppedTurnCleanup(_discarder, _usage, Substitute.For<ILogger<StoppedTurnCleanup>>());
    }

    [Test]
    public async Task ItDropsTheConfirmationsOfTheUserAndClosesTheUiActionRowsOfTheTurn()
    {
        await _cleanup.CleanUpAsync(UserId.ToString(), TurnId, CancellationToken.None);

        _discarder.Received(1).DiscardIssuedThisTurn(UserId);
        await _usage.Received(1).CancelDispatchedForTurnAsync(TurnId, Arg.Any<CancellationToken>());
    }

    [TestCase("")]
    [TestCase("not-a-guid")]
    public async Task AnUnusableUserId_SkipsTheConfirmationsButStillClosesTheRows(string userId)
    {
        await _cleanup.CleanUpAsync(userId, TurnId, CancellationToken.None);

        _discarder.DidNotReceiveWithAnyArgs().DiscardIssuedThisTurn(default);
        await _usage.Received(1).CancelDispatchedForTurnAsync(TurnId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEmptyTurnId_SkipsTheRowsButStillDropsTheConfirmations()
    {
        await _cleanup.CleanUpAsync(UserId.ToString(), Guid.Empty, CancellationToken.None);

        _discarder.Received(1).DiscardIssuedThisTurn(UserId);
        await _usage.DidNotReceiveWithAnyArgs().CancelDispatchedForTurnAsync(default, default);
    }

    [Test]
    public async Task AFailingRowWrite_IsLoggedNotThrownAndTheConfirmationsWereAlreadyDropped()
    {
        _usage.CancelDispatchedForTurnAsync(TurnId, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database down"));

        await Should.NotThrowAsync(() => _cleanup.CleanUpAsync(UserId.ToString(), TurnId, CancellationToken.None));

        _discarder.Received(1).DiscardIssuedThisTurn(UserId);
    }
}
