// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The per-turn hand-over of the tool search: each name is accepted once per turn, no more than
/// ToolSearchConstants.MaxAddedToolsPerTurn names per turn, every offered function is taken exactly once, and offers
/// never cross from one turn into another.
/// </summary>

using Klacks.Api.Application.Services.Assistant.ToolSearch;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant.ToolSearch;

[TestFixture]
public class TurnToolsetExtensionTests
{
    private TurnToolsetExtension _extension = null!;

    [SetUp]
    public void SetUp()
    {
        _extension = new TurnToolsetExtension();
    }

    private static List<LLMFunction> Functions(params string[] names) =>
        names.Select(n => new LLMFunction { Name = n }).ToList();

    private static List<string> Names(IEnumerable<LLMFunction> functions) => functions.Select(f => f.Name).ToList();

    [Test]
    public void Offer_AcceptsNewNamesAndDropsDuplicatesWithinTheTurn()
    {
        var turnId = Guid.NewGuid();

        var first = _extension.Offer(turnId, Functions("assign_contract", "add_clients_to_group", "ASSIGN_CONTRACT"));
        var second = _extension.Offer(turnId, Functions("add_clients_to_group", "list_contracts"));

        Names(first).ShouldBe(new[] { "assign_contract", "add_clients_to_group" });
        Names(second).ShouldBe(new[] { "list_contracts" });
        Names(_extension.TakeOffered(turnId)).ShouldBe(new[] { "assign_contract", "add_clients_to_group", "list_contracts" });
    }

    [Test]
    public void Offer_StopsAtTheCapPerTurn_AcrossSeveralCalls()
    {
        var turnId = Guid.NewGuid();
        var names = Enumerable.Range(0, ToolSearchConstants.MaxAddedToolsPerTurn + 3).Select(i => $"skill_{i}").ToArray();

        var first = _extension.Offer(turnId, Functions(names.Take(ToolSearchConstants.MaxAddedToolsPerTurn - 1).ToArray()));
        var second = _extension.Offer(turnId, Functions(names.Skip(ToolSearchConstants.MaxAddedToolsPerTurn - 1).ToArray()));

        first.Count.ShouldBe(ToolSearchConstants.MaxAddedToolsPerTurn - 1);
        Names(second).ShouldBe(new[] { names[ToolSearchConstants.MaxAddedToolsPerTurn - 1] });
        _extension.TakeOffered(turnId).Count.ShouldBe(ToolSearchConstants.MaxAddedToolsPerTurn);
        _extension.Offer(turnId, Functions("one_more")).ShouldBeEmpty();
    }

    [Test]
    public void TakeOffered_ReturnsEachOfferedFunctionOnce()
    {
        var turnId = Guid.NewGuid();
        _extension.Offer(turnId, Functions("assign_contract"));

        Names(_extension.TakeOffered(turnId)).ShouldBe(new[] { "assign_contract" });
        _extension.TakeOffered(turnId).ShouldBeEmpty();

        _extension.Offer(turnId, Functions("assign_contract", "list_contracts"));

        Names(_extension.TakeOffered(turnId)).ShouldBe(new[] { "list_contracts" });
        _extension.TakeOffered(turnId).ShouldBeEmpty();
    }

    [Test]
    public void TakeOffered_ForAnotherTurn_IsEmpty()
    {
        _extension.Offer(Guid.NewGuid(), Functions("assign_contract"));

        _extension.TakeOffered(Guid.NewGuid()).ShouldBeEmpty();
        _extension.TakeOffered(null).ShouldBeEmpty();
    }

    [Test]
    public void TakeOffered_BeforeAnyOffer_IsEmpty()
    {
        _extension.TakeOffered(Guid.NewGuid()).ShouldBeEmpty();
    }

    [Test]
    public void OfferingForANewTurn_ForgetsTheOldTurn()
    {
        var oldTurn = Guid.NewGuid();
        var newTurn = Guid.NewGuid();
        _extension.Offer(oldTurn, Functions("assign_contract"));

        var accepted = _extension.Offer(newTurn, Functions("assign_contract", "list_contracts"));

        Names(accepted).ShouldBe(new[] { "assign_contract", "list_contracts" });
        _extension.TakeOffered(oldTurn).ShouldBeEmpty();
        Names(_extension.TakeOffered(newTurn)).ShouldBe(new[] { "assign_contract", "list_contracts" });
    }

    [Test]
    public void ANewTurn_StartsWithAFreshCap()
    {
        var oldTurn = Guid.NewGuid();
        var names = Enumerable.Range(0, ToolSearchConstants.MaxAddedToolsPerTurn).Select(i => $"skill_{i}").ToArray();
        _extension.Offer(oldTurn, Functions(names));

        _extension.Offer(Guid.NewGuid(), Functions(names)).Count.ShouldBe(ToolSearchConstants.MaxAddedToolsPerTurn);
    }
}
