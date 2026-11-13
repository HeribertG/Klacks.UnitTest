// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// BorrowedAgentFilter cuts the people who are only borrowed into the planning group out of the agents of a run: it asks
/// for them over the whole subtree of the run's group (the requested one, else the group of the source scenario), keeps
/// the order of the rest (the planner's roster order is a rank, so the cut only leaves gaps) and leaves a run without a
/// group and without a source scenario alone.
/// </summary>

using Klacks.Api.Application.Services.Schedules;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class BorrowedAgentFilterTests
{
    private readonly Guid _group = Guid.NewGuid();
    private readonly Guid _subgroup = Guid.NewGuid();
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClient = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private BorrowedAgentFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _groupClient.GetAllGroupIdsIncludingSubgroups(_group).Returns(new HashSet<Guid> { _group, _subgroup });
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _filter = new BorrowedAgentFilter(_scenarioRepository, _groupClient, _groupItemRepository);
    }

    [Test]
    public async Task BorrowedAgents_AreLeftOut_AndTheRestKeepsTheRosterOrder()
    {
        var fourth = Guid.NewGuid();
        var borrowedA = Guid.NewGuid();
        var second = Guid.NewGuid();
        var borrowedB = Guid.NewGuid();
        var first = Guid.NewGuid();
        IReadOnlyList<Guid> roster = [fourth, borrowedA, second, borrowedB, first];
        BorrowedInSubtree(borrowedB, borrowedA);

        var planned = await _filter.RemoveBorrowedAsync(roster, _group, analyseToken: null, CancellationToken.None);

        planned.ShouldBe([fourth, second, first]);
    }

    [Test]
    public async Task TheBorrowedPeople_AreAskedForInTheWholeSubtreeOfTheGroup()
    {
        var agent = Guid.NewGuid();
        BorrowedInSubtree();

        await _filter.RemoveBorrowedAsync([agent], _group, analyseToken: null, CancellationToken.None);

        await _groupItemRepository.Received(1).GetBorrowedOnlyClientIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { agent })),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { _group, _subgroup })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunWithoutGroup_OnAScenario_UsesTheGroupOfTheScenario()
    {
        var token = Guid.NewGuid();
        var borrowed = Guid.NewGuid();
        var member = Guid.NewGuid();
        _scenarioRepository.GetByTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = token, GroupId = _group });
        BorrowedInSubtree(borrowed);

        var planned = await _filter.RemoveBorrowedAsync([borrowed, member], groupId: null, token, CancellationToken.None);

        planned.ShouldBe([member]);
    }

    [Test]
    public async Task RequestedGroup_WinsOverTheGroupOfTheScenario()
    {
        var token = Guid.NewGuid();
        var agent = Guid.NewGuid();
        BorrowedInSubtree(agent);

        var planned = await _filter.RemoveBorrowedAsync([agent], _group, token, CancellationToken.None);

        planned.ShouldBeEmpty();
        await _scenarioRepository.DidNotReceiveWithAnyArgs().GetByTokenAsync(default, default);
    }

    [Test]
    public async Task RunWithoutGroupAndWithoutScenario_IsLeftAlone()
    {
        IReadOnlyList<Guid> roster = [Guid.NewGuid(), Guid.NewGuid()];

        var planned = await _filter.RemoveBorrowedAsync(roster, groupId: null, analyseToken: null, CancellationToken.None);

        planned.ShouldBe(roster);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().GetBorrowedOnlyClientIdsAsync(default!, default!, default);
    }

    [Test]
    public async Task ScenarioWithoutGroup_IsLeftAlone()
    {
        var token = Guid.NewGuid();
        IReadOnlyList<Guid> roster = [Guid.NewGuid()];
        _scenarioRepository.GetByTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = token, GroupId = null });

        var planned = await _filter.RemoveBorrowedAsync(roster, groupId: null, token, CancellationToken.None);

        planned.ShouldBe(roster);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().GetBorrowedOnlyClientIdsAsync(default!, default!, default);
    }

    private void BorrowedInSubtree(params Guid[] borrowed)
        => _groupItemRepository.GetBorrowedOnlyClientIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { _group, _subgroup })),
                Arg.Any<CancellationToken>())
            .Returns(borrowed.ToHashSet());
}
