// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioTokenAccessGuard: the main plan (no token) passes without a query; an unrestricted caller passes for a scenario
/// and for an unknown token but never for the token of a variant (key), whoever the caller is (admin, restricted user or
/// background job without a user); for a restricted caller a token of a hidden, group-less or unknown scenario is refused
/// exactly like a missing one (404 text). IsVariantTokenVisibleAsync is the opposite door: true only for the token of a
/// variant whose group the caller may see.
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

    private Guid StoredToken(Guid? groupId, AnalyseScenarioKind kind = AnalyseScenarioKind.Scenario)
    {
        var scenario = new AnalyseScenario { Id = Guid.NewGuid(), Token = Guid.NewGuid(), GroupId = groupId, Kind = kind };
        _repository.GetByTokenAsync(scenario.Token, Arg.Any<CancellationToken>()).Returns(scenario);
        return scenario.Token;
    }

    private ScenarioTokenAccessGuard RestrictedGuard() => new(_repository, _restricted);

    private ScenarioTokenAccessGuard UnrestrictedGuard() => new(_repository, TestGroupWriteVisibility.UnrestrictedGroups());

    private ScenarioTokenAccessGuard BackgroundGuard()
    {
        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns((string?)null);
        return new ScenarioTokenAccessGuard(
            _repository, new GroupVisibilityGuard(Substitute.For<IGroupVisibilityService>(), userService));
    }

    [Test]
    public async Task NoToken_IsTheMainPlanAndAlwaysAllowed()
    {
        (await RestrictedGuard().IsVisibleAsync(null)).ShouldBeTrue();
        await _repository.DidNotReceiveWithAnyArgs().GetByTokenAsync(default, default);
    }

    [Test]
    public async Task UnrestrictedCaller_IsAllowedForAnUnknownToken_AfterLoadingIt()
    {
        var token = Guid.NewGuid();

        (await UnrestrictedGuard().IsVisibleAsync(token)).ShouldBeTrue();
        await _repository.Received(1).GetByTokenAsync(token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnrestrictedCaller_IsAllowedForAScenarioToken()
    {
        (await UnrestrictedGuard().IsVisibleAsync(StoredToken(_hiddenGroupId))).ShouldBeTrue();
    }

    [Test]
    public async Task UnrestrictedCaller_VariantToken_IsRefusedLikeAMissingScenario()
    {
        var token = StoredToken(_visibleGroupId, AnalyseScenarioKind.Variant);
        var guard = UnrestrictedGuard();

        (await guard.IsVisibleAsync(token)).ShouldBeFalse();
        (await Should.ThrowAsync<KeyNotFoundException>(() => guard.EnsureVisibleAsync(token)))
            .Message.ShouldBe($"Scenario with token {token} not found");
    }

    [Test]
    public async Task BackgroundCallerWithoutUser_IsAllowedForAScenarioToken_ButNotForAVariantToken()
    {
        var guard = BackgroundGuard();

        (await guard.IsVisibleAsync(StoredToken(_visibleGroupId))).ShouldBeTrue();
        (await guard.IsVisibleAsync(StoredToken(_visibleGroupId, AnalyseScenarioKind.Variant))).ShouldBeFalse();
        (await guard.IsVisibleAsync(StoredToken(null, AnalyseScenarioKind.Variant))).ShouldBeFalse();
    }

    [Test]
    public async Task RestrictedCaller_TokenOfVisibleGroup_IsAllowed()
    {
        (await RestrictedGuard().IsVisibleAsync(StoredToken(_visibleGroupId))).ShouldBeTrue();
    }

    [Test]
    public async Task RestrictedCaller_VariantTokenOfVisibleGroup_IsRefused()
    {
        (await RestrictedGuard().IsVisibleAsync(StoredToken(_visibleGroupId, AnalyseScenarioKind.Variant))).ShouldBeFalse();
    }

    [Test]
    public async Task RestrictedCaller_TokenOfHiddenGroup_IsRefusedLikeAMissingScenario()
    {
        var token = StoredToken(_hiddenGroupId);
        var guard = RestrictedGuard();

        (await guard.IsVisibleAsync(token)).ShouldBeFalse();
        (await Should.ThrowAsync<KeyNotFoundException>(() => guard.EnsureVisibleAsync(token)))
            .Message.ShouldBe($"Scenario with token {token} not found");
    }

    [Test]
    public async Task RestrictedCaller_GrouplessOrUnknownToken_IsRefused()
    {
        var guard = RestrictedGuard();

        (await guard.IsVisibleAsync(StoredToken(null))).ShouldBeFalse();
        (await guard.IsVisibleAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task IsHiddenVariantToken_IsTrueOnlyForAVariantOfAHiddenGroup()
    {
        (await RestrictedGuard().IsHiddenVariantTokenAsync(StoredToken(_hiddenGroupId, AnalyseScenarioKind.Variant))).ShouldBeTrue();
        (await RestrictedGuard().IsHiddenVariantTokenAsync(StoredToken(_visibleGroupId, AnalyseScenarioKind.Variant))).ShouldBeFalse();
        (await RestrictedGuard().IsHiddenVariantTokenAsync(StoredToken(_hiddenGroupId))).ShouldBeFalse();
        (await RestrictedGuard().IsHiddenVariantTokenAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task IsVariantTokenVisible_VariantOfVisibleGroup_IsTrue()
    {
        (await RestrictedGuard().IsVariantTokenVisibleAsync(StoredToken(_visibleGroupId, AnalyseScenarioKind.Variant)))
            .ShouldBeTrue();
    }

    [Test]
    public async Task IsVariantTokenVisible_VariantOfHiddenGroup_IsFalse()
    {
        (await RestrictedGuard().IsVariantTokenVisibleAsync(StoredToken(_hiddenGroupId, AnalyseScenarioKind.Variant)))
            .ShouldBeFalse();
    }

    [Test]
    public async Task IsVariantTokenVisible_GrouplessVariant_NeedsAnUnrestrictedCaller()
    {
        var token = StoredToken(null, AnalyseScenarioKind.Variant);

        (await RestrictedGuard().IsVariantTokenVisibleAsync(token)).ShouldBeFalse();
        (await UnrestrictedGuard().IsVariantTokenVisibleAsync(token)).ShouldBeTrue();
    }

    [Test]
    public async Task IsVariantTokenVisible_ScenarioToken_IsFalse_EvenInAVisibleGroup()
    {
        (await RestrictedGuard().IsVariantTokenVisibleAsync(StoredToken(_visibleGroupId))).ShouldBeFalse();
        (await UnrestrictedGuard().IsVariantTokenVisibleAsync(StoredToken(_visibleGroupId))).ShouldBeFalse();
    }

    [Test]
    public async Task IsVariantTokenVisible_UnknownToken_IsFalse_EvenForAnUnrestrictedCaller()
    {
        (await UnrestrictedGuard().IsVariantTokenVisibleAsync(Guid.NewGuid())).ShouldBeFalse();
    }
}
