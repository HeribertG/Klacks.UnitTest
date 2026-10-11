// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A schedule row addressed by id is visible only when its owning client AND its scenario are visible; the main plan
/// (no token) depends on the client alone.
/// </summary>

using Klacks.Api.Application.Services.Groups;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class ScheduleRowAccessGuardTests
{
    private readonly Guid _hiddenClient = Guid.NewGuid();
    private readonly Guid _hiddenToken = Guid.NewGuid();

    private ScheduleRowAccessGuard Guard()
    {
        var clients = Substitute.For<IClientVisibilityGuard>();
        clients.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClient);
        clients.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(_hiddenClient));
        return new ScheduleRowAccessGuard(clients, TestScenarioTokenAccess.Hiding(_hiddenToken));
    }

    [Test]
    public async Task VisibleClientInTheMainPlan_IsVisible()
    {
        (await Guard().IsVisibleAsync(Guid.NewGuid(), null)).ShouldBeTrue();
    }

    [Test]
    public async Task VisibleClientInAHiddenScenario_IsHidden()
    {
        (await Guard().IsVisibleAsync(Guid.NewGuid(), _hiddenToken)).ShouldBeFalse();
        (await Guard().AreAllVisibleAsync([Guid.NewGuid()], _hiddenToken)).ShouldBeFalse();
    }

    [Test]
    public async Task HiddenClientInAVisibleScenario_IsHidden()
    {
        (await Guard().IsVisibleAsync(_hiddenClient, Guid.NewGuid())).ShouldBeFalse();
        (await Guard().AreAllVisibleAsync([Guid.NewGuid(), _hiddenClient], Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task NoClientsInAVisibleScenario_IsVisible()
    {
        (await Guard().AreAllVisibleAsync([], Guid.NewGuid())).ShouldBeTrue();
    }
}
