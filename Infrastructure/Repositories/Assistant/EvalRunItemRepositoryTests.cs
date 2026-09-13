// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the per-item eval store. The query the learning loop depends on is the narrow one: pure
/// selection misses (offered and not chosen) of one run that were never spent on a proposal before.
/// A retrieval miss must never appear there - narrowing a description cannot fix a tool that was not in
/// the toolset - and a watermarked item must never appear twice, or the same handful of misses would
/// justify a fresh narrowing on every run. The caller also names the item ids it may learn from, and a
/// row without a chosen tool is no evidence at all: both have to narrow the window in SQL, because a
/// filter applied after the limit leaves the window occupied by rows nobody can spend.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class EvalRunItemRepositoryTests
{
    private const int Limit = 50;
    private const string TrainItem = "ts-012";
    private const string HoldoutItem = "ts-013";

    private static readonly DateTime ConsumedAt = new(2026, 9, 13, 4, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private Guid _runId;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
        _runId = Guid.NewGuid();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private EvalRunItemRepository NewRepository() => new(CreateContext());

    [Test]
    public async Task AddRange_ThenListByRun_ReturnsEveryRowOfThatRun()
    {
        var otherRun = Guid.NewGuid();
        await NewRepository().AddRangeAsync(
        [
            Item("ts-011", retrievalHit: true, selectionHit: true),
            Item("ts-012", retrievalHit: true, selectionHit: false),
            Item("ts-013", retrievalHit: true, selectionHit: false, runId: otherRun)
        ]);

        var rows = await NewRepository().ListByRunAsync(_runId);

        rows.Count.ShouldBe(2);
        rows.Select(r => r.ItemId).ShouldContain("ts-011");
        rows.Select(r => r.ItemId).ShouldContain("ts-012");
    }

    [Test]
    public async Task UnconsumedSelectionMisses_AreOfferedAndNotChosenOnly()
    {
        await NewRepository().AddRangeAsync(
        [
            Item("ts-011", retrievalHit: true, selectionHit: true),
            Item("ts-012", retrievalHit: true, selectionHit: false),
            Item("ts-013", retrievalHit: false, selectionHit: null),
            Item("ts-014", retrievalHit: null, selectionHit: null)
        ]);

        var misses = await NewRepository().ListUnconsumedSelectionMissesAsync(
            _runId, ["ts-011", "ts-012", "ts-013", "ts-014"], Limit);

        misses.Count.ShouldBe(1);
        misses[0].ItemId.ShouldBe("ts-012");
    }

    // The train half is the only population a proposal may be built from, so the ids are a SQL filter and
    // not a filter the caller applies to whatever the limit happened to return.
    [Test]
    public async Task AnItemIdOutsideTheGivenSet_IsNeverReturned()
    {
        await NewRepository().AddRangeAsync(
        [
            Item(TrainItem, retrievalHit: true, selectionHit: false),
            Item(HoldoutItem, retrievalHit: true, selectionHit: false)
        ]);

        var misses = await NewRepository().ListUnconsumedSelectionMissesAsync(_runId, [TrainItem], Limit);

        misses.Count.ShouldBe(1);
        misses[0].ItemId.ShouldBe(TrainItem);
    }

    // A miss without a chosen tool names no skill to narrow, so it must not occupy a slot of the window.
    [Test]
    public async Task AMissWithoutAChosenTool_IsNotOfferedAsEvidence()
    {
        await NewRepository().AddRangeAsync(
        [
            ItemWithoutChosenTool(TrainItem),
            Item(HoldoutItem, retrievalHit: true, selectionHit: false)
        ]);

        var misses = await NewRepository().ListUnconsumedSelectionMissesAsync(
            _runId, [TrainItem, HoldoutItem], Limit);

        misses.Count.ShouldBe(1);
        misses[0].ItemId.ShouldBe(HoldoutItem);
    }

    [Test]
    public async Task AnEmptyIdSet_ReturnsNothingAndDoesNotQueryTheWholeRun()
    {
        await NewRepository().AddRangeAsync(
        [
            Item(TrainItem, retrievalHit: true, selectionHit: false)
        ]);

        (await NewRepository().ListUnconsumedSelectionMissesAsync(_runId, [], Limit)).ShouldBeEmpty();
    }

    [Test]
    public async Task AWatermarkedItem_IsNeverOfferedAgain()
    {
        await NewRepository().AddRangeAsync(
        [
            Item("ts-012", retrievalHit: true, selectionHit: false)
        ]);

        var first = await NewRepository().ListUnconsumedSelectionMissesAsync(_runId, ["ts-012"], Limit);
        await NewRepository().MarkConsumedAsync([first[0].Id], ConsumedAt);

        var second = await NewRepository().ListUnconsumedSelectionMissesAsync(_runId, ["ts-012"], Limit);

        second.ShouldBeEmpty();
    }

    [Test]
    public async Task MarkConsumed_StampsTheWatermarkItWasGiven()
    {
        await NewRepository().AddRangeAsync(
        [
            Item("ts-012", retrievalHit: true, selectionHit: false)
        ]);
        var row = (await NewRepository().ListUnconsumedSelectionMissesAsync(_runId, ["ts-012"], Limit))[0];

        await NewRepository().MarkConsumedAsync([row.Id], ConsumedAt);

        await using var context = CreateContext();
        var stored = await context.EvalRunItems.SingleAsync(i => i.Id == row.Id);
        stored.LearningConsumedAtUtc.ShouldBe(ConsumedAt);
    }

    [Test]
    public async Task AnEmptyBatch_WritesNothingAndDoesNotThrow()
    {
        await NewRepository().AddRangeAsync([]);
        await NewRepository().MarkConsumedAsync([], ConsumedAt);

        (await NewRepository().ListByRunAsync(_runId)).ShouldBeEmpty();
    }

    private EvalRunItem ItemWithoutChosenTool(string itemId)
    {
        var item = Item(itemId, retrievalHit: true, selectionHit: false);
        item.ChosenTool = null;
        return item;
    }

    private EvalRunItem Item(
        string itemId,
        bool? retrievalHit,
        bool? selectionHit,
        Guid? runId = null) => new()
    {
        Id = Guid.NewGuid(),
        EvalRunId = runId ?? _runId,
        ItemId = itemId,
        Locale = "de",
        ExpectedTool = "add_client_note",
        ChosenTool = selectionHit == true ? "add_client_note" : "search_employees",
        ToolsetNamesJson = "[\"add_client_note\"]",
        RetrievalHit = retrievalHit,
        SelectionHit = selectionHit,
        Passed = selectionHit == true,
        LatencyMs = 1200,
        CreateTime = new DateTime(2026, 9, 13, 3, 0, 0, DateTimeKind.Utc)
    };
}
