// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioTokenAccessGuard: the main plan (no token) and unrestricted callers pass without a query; for a restricted
/// caller a token of a hidden, group-less or unknown scenario is refused exactly like a missing one (404 text).
/// </summary>

using Klacks.Api.Application.Services.Groups;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class ScenarioTokenAccessGuardTests
{
    private IAnalyseScenarioRepository _repository = null!;
    private IGroupVisibilityGuard _restricted = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _visibleGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();
        _restricted = Substitute.For<IGroupVisibilityGuard>();
        _restricted.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _restricted.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) == _visibleGroupId);
    }

    private Guid StoredToken(Guid? groupId)
    {
        var scenario = new AnalyseScenario { Id = Guid.NewGuid(), Token = Guid.NewGuid(), GroupId = groupId };
        _repository.GetByTokenAsync(scenario.Token, Arg.Any<CancellationToken>()).Returns(scenario);
        return scenario.Token;
    }

    [Test]
    public async Task NoToken_IsTheMainPlanAndAlwaysAllowed()
    {
        (await new ScenarioTokenAccessGuard(_repository, _restricted).IsVisibleAsync(null)).ShouldBeTrue();
        await _repository.DidNotReceiveWithAnyArgs().GetByTokenAsync(default, default);
    }

    [Test]
    public async Task UnrestrictedCaller_IsAllowedWithoutQuery_EvenForAnUnknownToken()
    {
        var guard = new ScenarioTokenAccessGuard(_repository, TestGroupWriteVisibility.UnrestrictedGroups());

        (await guard.IsVisibleAsync(Guid.NewGuid())).ShouldBeTrue();
        await _repository.DidNotReceiveWithAnyArgs().GetByTokenAsync(default, default);
    }

    [Test]
    public async Task RestrictedCaller_TokenOfVisibleGroup_IsAllowed()
    {
        var token = StoredToken(_visibleGroupId);

        (await new ScenarioTokenAccessGuard(_repository, _restricted).IsVisibleAsync(token)).ShouldBeTrue();
    }

    [Test]
    public async Task RestrictedCaller_TokenOfHiddenGroup_IsRefusedLikeAMissingScenario()
    {
        var token = StoredToken(_hiddenGroupId);
        var guard = new ScenarioTokenAccessGuard(_repository, _restricted);

        (await guard.IsVisibleAsync(token)).ShouldBeFalse();
        (await Should.ThrowAsync<KeyNotFoundException>(() => guard.EnsureVisibleAsync(token)))
            .Message.ShouldBe($"Scenario with token {token} not found");
    }

    [Test]
    public async Task RestrictedCaller_GrouplessOrUnknownToken_IsRefused()
    {
        var guard = new ScenarioTokenAccessGuard(_repository, _restricted);

        (await guard.IsVisibleAsync(StoredToken(null))).ShouldBeFalse();
        (await guard.IsVisibleAsync(Guid.NewGuid())).ShouldBeFalse();
    }
}
