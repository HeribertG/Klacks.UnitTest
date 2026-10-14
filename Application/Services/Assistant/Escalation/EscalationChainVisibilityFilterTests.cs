// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for EscalationChainVisibilityFilter: the filter delegates to IPlanningAudienceResolver.MaySeeAnyGroupAsync
/// (admins true, no rows false), a chain without a group is asked with an empty group set (fail-closed), and the verdict
/// is computed once per distinct group of a list.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;

namespace Klacks.UnitTest.Application.Services.Assistant.Escalation;

[TestFixture]
public class EscalationChainVisibilityFilterTests
{
    private const string UserId = "planner-a";

    private IPlanningAudienceResolver _resolver = null!;
    private EscalationChainVisibilityFilter _sut = null!;

    [SetUp]
    public void Setup()
    {
        _resolver = Substitute.For<IPlanningAudienceResolver>();
        _sut = new EscalationChainVisibilityFilter(_resolver);
    }

    [Test]
    public async Task FilterVisibleAsync_KeepsOnlyChainsOfVisibleGroups_AndAsksOncePerGroup()
    {
        var visibleGroup = Guid.NewGuid();
        var hiddenGroup = Guid.NewGuid();
        var chains = new List<EscalationChain>
        {
            new() { GroupId = visibleGroup },
            new() { GroupId = visibleGroup },
            new() { GroupId = hiddenGroup },
        };
        _resolver.MaySeeAnyGroupAsync(UserId, Arg.Is<IReadOnlyCollection<Guid>>(g => g.Contains(visibleGroup)), Arg.Any<CancellationToken>())
            .Returns(true);
        _resolver.MaySeeAnyGroupAsync(UserId, Arg.Is<IReadOnlyCollection<Guid>>(g => g.Contains(hiddenGroup)), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.FilterVisibleAsync(chains, UserId);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(c => c.GroupId == visibleGroup), Is.True);
        await _resolver.Received(2).MaySeeAnyGroupAsync(UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsVisibleAsync_ChainWithoutGroup_AsksWithAnEmptySetSoOnlyAdminsPass()
    {
        _resolver.MaySeeAnyGroupAsync(UserId, Arg.Is<IReadOnlyCollection<Guid>>(g => g.Count == 0), Arg.Any<CancellationToken>())
            .Returns(false);

        var visible = await _sut.IsVisibleAsync(new EscalationChain { GroupId = null }, UserId);

        Assert.That(visible, Is.False);
        await _resolver.Received(1).MaySeeAnyGroupAsync(
            UserId, Arg.Is<IReadOnlyCollection<Guid>>(g => g.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsVisibleAsync_AdminResolverAnswer_IsPassedThrough()
    {
        _resolver.MaySeeAnyGroupAsync(UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);

        Assert.That(await _sut.IsVisibleAsync(new EscalationChain { GroupId = null }, UserId), Is.True);
    }

    [TestCase(EscalationStageStatus.Pending)]
    [TestCase(EscalationStageStatus.Notified)]
    public async Task StageHolder_AlwaysSeesTheirChain_EvenWithoutGroupAndWithoutVisibility(EscalationStageStatus status)
    {
        var chain = new EscalationChain
        {
            GroupId = null,
            Stages = new List<EscalationStage> { new() { UserId = UserId, Status = status } }
        };
        _resolver.MaySeeAnyGroupAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.That(await _sut.IsVisibleAsync(chain, UserId), Is.True);
        Assert.That(await _sut.FilterVisibleAsync(new[] { chain }, UserId), Has.Count.EqualTo(1));
        await _resolver.DidNotReceive().MaySeeAnyGroupAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HolderOfAnAlreadyClosedStage_GetsNoSpecialAccess()
    {
        var chain = new EscalationChain
        {
            GroupId = Guid.NewGuid(),
            Stages = new List<EscalationStage> { new() { UserId = UserId, Status = EscalationStageStatus.Expired } }
        };
        _resolver.MaySeeAnyGroupAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.That(await _sut.IsVisibleAsync(chain, UserId), Is.False);
    }

    [Test]
    public async Task OtherUsersStage_DoesNotGrantAccess()
    {
        var chain = new EscalationChain
        {
            GroupId = null,
            Stages = new List<EscalationStage> { new() { UserId = "someone-else", Status = EscalationStageStatus.Notified } }
        };
        _resolver.MaySeeAnyGroupAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.That(await _sut.IsVisibleAsync(chain, UserId), Is.False);
    }

    [Test]
    public async Task FilterVisibleAsync_ChainsWithoutGroupInTheList_DoNotThrow_AndAreHiddenFromNonAdmins()
    {
        _resolver.MaySeeAnyGroupAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.FilterVisibleAsync(new[] { new EscalationChain { GroupId = null }, new EscalationChain { GroupId = null } }, UserId);

        Assert.That(result, Is.Empty);
    }
}
