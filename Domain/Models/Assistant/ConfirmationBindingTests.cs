// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ConfirmationBinding: the fingerprint ignores the order of the items and changes with any added, removed
/// or swapped item and with the kind; DiffTo counts new and vanished items (a swap is one of each) and gives up when a side
/// kept no items; a set larger than the storage cap keeps only the fingerprint and still matches; the JSON round trip the
/// store relies on keeps kind, items and fingerprint; ConfirmationBindingMessages.Describe words the difference.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class ConfirmationBindingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static ConfirmationBinding Work(params string[] items) =>
        ConfirmationBinding.Of(ConfirmationBindingKinds.Work, items);

    [Test]
    public void TheFingerprint_DoesNotDependOnTheOrderOfTheItems()
    {
        Work("a", "b", "c").Matches(Work("c", "a", "b")).ShouldBeTrue();
    }

    [Test]
    public void TheItems_AreStoredOrdinalSorted()
    {
        Work("b", "B", "a").Items.ShouldBe(new[] { "B", "a", "b" });
    }

    [Test]
    public void AnAddedItem_ChangesTheFingerprint()
    {
        Work("a", "b").Matches(Work("a", "b", "c")).ShouldBeFalse();
    }

    [Test]
    public void ARemovedItem_ChangesTheFingerprint()
    {
        Work("a", "b", "c").Matches(Work("a", "b")).ShouldBeFalse();
    }

    [Test]
    public void ASwappedItem_ChangesTheFingerprint()
    {
        Work("a", "b").Matches(Work("a", "c")).ShouldBeFalse();
    }

    [Test]
    public void TheKind_FlowsIntoTheFingerprint()
    {
        var work = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { "a" });
        var macro = ConfirmationBinding.Of(ConfirmationBindingKinds.MacroSwitch, new[] { "a" });

        work.Fingerprint.ShouldNotBe(macro.Fingerprint);
        work.Matches(macro).ShouldBeFalse();
    }

    [Test]
    public void TwoBindingsWithTheSameFingerprintButAnotherKind_DoNotMatch()
    {
        var work = Work("a");
        var forged = work with { Kind = ConfirmationBindingKinds.MacroSwitch };

        work.Matches(forged).ShouldBeFalse();
    }

    [Test]
    public void DiffTo_CountsAddedAndVanishedItems()
    {
        var diff = Work("a", "b", "c").DiffTo(Work("b", "c", "d", "e"));

        diff.ShouldNotBeNull();
        diff.ShownCount.ShouldBe(3);
        diff.CurrentCount.ShouldBe(4);
        diff.AddedCount.ShouldBe(2);
        diff.GoneCount.ShouldBe(1);
    }

    [Test]
    public void DiffTo_ASwapIsOneNewAndOneVanished()
    {
        var diff = Work("a", "b").DiffTo(Work("a", "c"));

        diff.ShouldNotBeNull();
        diff.AddedCount.ShouldBe(1);
        diff.GoneCount.ShouldBe(1);
    }

    [Test]
    public void ASetBeyondTheStorageCap_KeepsOnlyTheFingerprint_AndStillMatches()
    {
        var items = Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1).Select(i => $"item-{i:D3}").ToList();

        var first = Work(items.ToArray());
        var second = Work(items.AsEnumerable().Reverse().ToArray());

        first.Items.ShouldBeEmpty();
        first.Fingerprint.ShouldNotBeNullOrEmpty();
        first.Matches(second).ShouldBeTrue();
        first.Matches(Work(items.Take(ConfirmationBindingDefaults.MaxStoredItems).ToArray())).ShouldBeFalse();
    }

    [Test]
    public void ASetExactlyAtTheStorageCap_KeepsItsItems()
    {
        var items = Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems).Select(i => $"item-{i:D3}").ToArray();

        Work(items).Items.Count.ShouldBe(ConfirmationBindingDefaults.MaxStoredItems);
    }

    [Test]
    public void DiffTo_IsNull_WhenASideKeptNoItems()
    {
        var big = Work(Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1).Select(i => $"i{i}").ToArray());

        big.DiffTo(Work("a")).ShouldBeNull();
        Work("a").DiffTo(big).ShouldBeNull();
    }

    [Test]
    public void TheJsonRoundTrip_KeepsKindItemsAndFingerprint()
    {
        var binding = Work("x", "y");

        var back = JsonSerializer.Deserialize<ConfirmationBinding>(JsonSerializer.Serialize(binding, JsonOptions), JsonOptions);

        back.ShouldNotBeNull();
        back.Kind.ShouldBe(binding.Kind);
        back.Items.ShouldBe(binding.Items);
        back.Fingerprint.ShouldBe(binding.Fingerprint);
        back.Matches(binding).ShouldBeTrue();
    }

    [Test]
    public void Describe_WithItemsOnBothSides_NamesTheCounts()
    {
        var text = ConfirmationBindingMessages.Describe(Work("a", "b"), Work("a", "b", "c"));

        text.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        text.ShouldContain("Nothing was changed");
    }

    [Test]
    public void Describe_WithoutItems_FallsBackToTheScopeText()
    {
        var big = Work(Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1).Select(i => $"i{i}").ToArray());

        ConfirmationBindingMessages.Describe(big, Work("a")).ShouldBe(ConfirmationBindingMessages.ScopeChanged);
    }

    [Test]
    public void Describe_WithABindingOfAnotherKind_SaysScopeChanged_InsteadOfMisleadingCounts()
    {
        var shown = ConfirmationBinding.Of(ConfirmationBindingKinds.MacroSwitch, ["a", "b"]);

        var text = ConfirmationBindingMessages.Describe(shown, Work("a", "b", "c"));

        text.ShouldBe(ConfirmationBindingMessages.ScopeChanged);
        text.ShouldNotContain("item(s)");
    }

    [Test]
    public void TheStorageCap_CoversTheLargestBulkDeletePlan()
    {
        ConfirmationBindingDefaults.MaxStoredItems.ShouldBeGreaterThanOrEqualTo(BulkWorkDeletionDefaults.MaxWorksPerCall);
    }

    [Test]
    public void DirectRedemptionRefused_NamesTheConfirmSkillByItsConstant()
    {
        ConfirmationBindingMessages.DirectRedemptionRefused
            .ShouldContain($"'{AutonomyDefaults.ConfirmPendingActionSkillName}'");
    }
}
