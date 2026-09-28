// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the preview registry behind the apply gate of apply_grouping_plan: a preview is remembered per
/// user and full plan fingerprint together with the chat turn it was shown in, forgotten on request, and
/// stored as a size-1 entry (the host cache has a size limit) that expires after
/// GroupingFeasibilityDefaults.PreviewValidityMinutes. An apply in the same chat turn as the preview is
/// reported as SameTurn, so the user has to answer before anything is written. Real expiry is not waited
/// for; the entry options are asserted instead. Two concurrent previews of the same plan are serialised:
/// only one of them finds no earlier entry, so the first recorded turn is the one that is kept.
/// </summary>

using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingPlanPreviewRegistryTests
{
    private static readonly string Fingerprint = new('a', 64);
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromMilliseconds(300);
    private const int ConcurrentPreviews = 2;

    private static GroupingPlanPreviewRegistry NewRegistry() =>
        new(new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 }));

    [Test]
    public void RecordedPreview_IsFoundOnlyForTheSameUserAndFingerprint()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();

        registry.RecordPreview(userId, Fingerprint, null);

        registry.GetPreviewStatus(userId, Fingerprint, null).ShouldBe(GroupingPreviewStatus.Confirmable);
        registry.GetPreviewStatus(Guid.NewGuid(), Fingerprint, null).ShouldBe(GroupingPreviewStatus.Missing);
        registry.GetPreviewStatus(userId, new string('b', 64), null).ShouldBe(GroupingPreviewStatus.Missing);
    }

    [Test]
    public void ForgottenPreview_IsNoLongerFound()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        registry.RecordPreview(userId, Fingerprint, null);

        registry.Forget(userId, Fingerprint);

        registry.GetPreviewStatus(userId, Fingerprint, null).ShouldBe(GroupingPreviewStatus.Missing);
    }

    [Test]
    public void ApplyInTheTurnOfThePreview_IsSameTurn()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        var turn = Guid.NewGuid();
        registry.RecordPreview(userId, Fingerprint, turn);

        registry.GetPreviewStatus(userId, Fingerprint, turn).ShouldBe(GroupingPreviewStatus.SameTurn);
    }

    [Test]
    public void ApplyInALaterTurn_IsConfirmable()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        registry.RecordPreview(userId, Fingerprint, Guid.NewGuid());

        registry.GetPreviewStatus(userId, Fingerprint, Guid.NewGuid()).ShouldBe(GroupingPreviewStatus.Confirmable);
    }

    [Test]
    public void WithoutATurnOnEitherSide_ThePreviewIsConfirmable()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        var turn = Guid.NewGuid();
        registry.RecordPreview(userId, Fingerprint, null);
        registry.RecordPreview(userId, new string('c', 64), turn);

        registry.GetPreviewStatus(userId, Fingerprint, turn).ShouldBe(GroupingPreviewStatus.Confirmable);
        registry.GetPreviewStatus(userId, new string('c', 64), null).ShouldBe(GroupingPreviewStatus.Confirmable);
    }

    [Test]
    public void RepeatedPreviewInTheConfirmingTurn_KeepsTheEarlierTurn()
    {
        var registry = NewRegistry();
        var userId = Guid.NewGuid();
        var confirmingTurn = Guid.NewGuid();
        registry.RecordPreview(userId, Fingerprint, Guid.NewGuid());

        registry.RecordPreview(userId, Fingerprint, confirmingTurn);

        registry.GetPreviewStatus(userId, Fingerprint, confirmingTurn).ShouldBe(GroupingPreviewStatus.Confirmable);
    }

    [Test]
    public void RecordedPreview_IsASizeOneEntryWithTheConfiguredLifetime()
    {
        var cache = Substitute.For<IMemoryCache>();
        var entry = Substitute.For<ICacheEntry>();
        cache.CreateEntry(Arg.Any<object>()).Returns(entry);

        new GroupingPlanPreviewRegistry(cache).RecordPreview(Guid.NewGuid(), Fingerprint, Guid.NewGuid());

        entry.Received().Size = 1;
        entry.Received().AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(GroupingFeasibilityDefaults.PreviewValidityMinutes);
    }

    [Test]
    public async Task ConcurrentPreviewsOfTheSamePlan_OnlyOneMissesTheEarlierEntry_AndOneTurnIsKept()
    {
        using var cache = new RendezvousMemoryCache(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 }), ConcurrentPreviews, RendezvousTimeout);
        var registry = new GroupingPlanPreviewRegistry(cache);
        var userId = Guid.NewGuid();
        var turns = Enumerable.Range(0, ConcurrentPreviews).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(turns.Select(turn => Task.Run(() => registry.RecordPreview(userId, Fingerprint, turn))));

        cache.Misses.ShouldBe(1);
        turns.Count(turn => registry.GetPreviewStatus(userId, Fingerprint, turn) == GroupingPreviewStatus.SameTurn).ShouldBe(1);
    }
}
