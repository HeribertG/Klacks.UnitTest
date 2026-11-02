// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shift ids of a route request keep what the caller may read: a visible real shift and the clone of a visible
/// scenario. A hidden real shift, a shift of a key and the clone of a hidden scenario are left out; the answer is silent,
/// so a hidden shift looks like a missing one.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class RouteShiftVisibilityFilterTests
{
    private IShiftVisibilityReader _reader = null!;
    private IScenarioTokenAccessGuard _tokenGuard = null!;

    [SetUp]
    public void SetUp()
    {
        _reader = Substitute.For<IShiftVisibilityReader>();
        _tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ShiftWorld>());
    }

    private RouteShiftVisibilityFilter Filter(IShiftVisibilityGuard shiftGuard) => new(shiftGuard, _reader, _tokenGuard);

    [Test]
    public async Task VisibleRealShifts_StayInRequestOrder_EachOnce()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var allowed = await Filter(ShiftVisibilityGuards.AllowAll()).FilterAsync([second, first, second], CancellationToken.None);

        allowed.ShouldBe([second, first]);
        await _tokenGuard.DidNotReceiveWithAnyArgs().IsVisibleAsync(default, default);
    }

    [Test]
    public async Task AHiddenRealShift_IsLeftOut()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();

        var allowed = await Filter(ShiftVisibilityGuards.Hiding(hidden)).FilterAsync([visible, hidden], CancellationToken.None);

        allowed.ShouldBe([visible]);
    }

    [Test]
    public async Task AKeyShift_IsLeftOut_BecauseTheTokenGuardRefusesAKeyForEveryCaller()
    {
        var real = Guid.NewGuid();
        var keyShift = Guid.NewGuid();
        var keyToken = Guid.NewGuid();
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ShiftWorld(keyShift, keyToken, true) });
        _tokenGuard.IsVisibleAsync(keyToken, Arg.Any<CancellationToken>()).Returns(false);

        var allowed = await Filter(ShiftVisibilityGuards.AllowAll()).FilterAsync([real, keyShift], CancellationToken.None);

        allowed.ShouldBe([real]);
    }

    [Test]
    public async Task AClone_StaysOnlyWhenItsScenarioIsVisible()
    {
        var ownClone = Guid.NewGuid();
        var foreignClone = Guid.NewGuid();
        var ownToken = Guid.NewGuid();
        var foreignToken = Guid.NewGuid();
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ShiftWorld(ownClone, ownToken, false), new ShiftWorld(foreignClone, foreignToken, false) });
        _tokenGuard.IsVisibleAsync(ownToken, Arg.Any<CancellationToken>()).Returns(true);
        _tokenGuard.IsVisibleAsync(foreignToken, Arg.Any<CancellationToken>()).Returns(false);

        var allowed = await Filter(ShiftVisibilityGuards.AllowAll()).FilterAsync([ownClone, foreignClone], CancellationToken.None);

        allowed.ShouldBe([ownClone]);
    }

    [Test]
    public async Task EveryCloneOfOneScenario_AsksTheTokenGuardOnce()
    {
        var token = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ShiftWorld(first, token, false), new ShiftWorld(second, token, false) });
        _tokenGuard.IsVisibleAsync(token, Arg.Any<CancellationToken>()).Returns(true);

        await Filter(ShiftVisibilityGuards.AllowAll()).FilterAsync([first, second], CancellationToken.None);

        await _tokenGuard.Received(1).IsVisibleAsync(token, Arg.Any<CancellationToken>());
    }
}
