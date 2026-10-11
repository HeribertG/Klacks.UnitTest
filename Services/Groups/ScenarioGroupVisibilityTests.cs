// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioGroupVisibility is the single rule for scenario visibility: a group-bound scenario follows its group, a
/// group-less one (it spans every group) is only visible to an unrestricted caller, and a missing scenario is never
/// visible. Writes to a whole scope refuse a hidden group like a missing one and a group-less scope with 403.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Groups;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class ScenarioGroupVisibilityTests
{
    private IGroupVisibilityGuard _restricted = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _visibleGroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();
        _restricted = Substitute.For<IGroupVisibilityGuard>();
        _restricted.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _restricted.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) == _visibleGroupId);
    }

    private static AnalyseScenario Scenario(Guid? groupId) => new() { Id = Guid.NewGuid(), Token = Guid.NewGuid(), GroupId = groupId };

    [Test]
    public async Task IsScenarioVisible_FollowsTheScenarioGroup()
    {
        (await _restricted.IsScenarioVisibleAsync(Scenario(_visibleGroupId))).ShouldBeTrue();
        (await _restricted.IsScenarioVisibleAsync(Scenario(_hiddenGroupId))).ShouldBeFalse();
    }

    [Test]
    public async Task IsScenarioVisible_MissingScenario_IsNeverVisible()
    {
        (await TestGroupWriteVisibility.UnrestrictedGroups().IsScenarioVisibleAsync(null)).ShouldBeFalse();
    }

    [Test]
    public async Task IsScenarioVisible_GrouplessScenario_OnlyForUnrestrictedCallers()
    {
        (await _restricted.IsScenarioVisibleAsync(Scenario(null))).ShouldBeFalse();
        (await TestGroupWriteVisibility.UnrestrictedGroups().IsScenarioVisibleAsync(Scenario(null))).ShouldBeTrue();
    }

    [Test]
    public async Task EnsureScenarioVisible_HiddenScenario_ThrowsTheMissingScenarioMessage()
    {
        var scenario = Scenario(_hiddenGroupId);

        var act = () => _restricted.EnsureScenarioVisibleAsync(scenario, scenario.Id);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"AnalyseScenario with ID {scenario.Id} not found");
    }

    [Test]
    public async Task EnsureScenarioVisible_VisibleScenario_ReturnsIt()
    {
        var scenario = Scenario(_visibleGroupId);

        var result = await _restricted.EnsureScenarioVisibleAsync(scenario, scenario.Id);

        result.ShouldBeSameAs(scenario);
    }

    [Test]
    public async Task EnsureScenarioScopeWritable_HiddenGroup_ThrowsNotFound()
    {
        var act = () => _restricted.EnsureScenarioScopeWritableAsync(_hiddenGroupId);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Group with ID {_hiddenGroupId} not found");
    }

    [Test]
    public async Task EnsureScenarioScopeWritable_GrouplessScope_IsForbiddenForRestrictedAndAllowedForUnrestricted()
    {
        await Should.ThrowAsync<ForbiddenException>(() => _restricted.EnsureScenarioScopeWritableAsync(null));
        await Should.NotThrowAsync(() => TestGroupWriteVisibility.UnrestrictedGroups().EnsureScenarioScopeWritableAsync(null));
        await Should.NotThrowAsync(() => _restricted.EnsureScenarioScopeWritableAsync(_visibleGroupId));
    }
}
