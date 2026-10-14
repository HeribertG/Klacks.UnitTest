// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BulkWorkDeletionBinding: the bound items are the plain ids of the planned works plus the container
/// children (child works and breaks) that the delete takes with them, each under its own prefix. A plan without container
/// children binds exactly like a plain list of work ids (kind Work); a child work, a child break and a parent with the same
/// id never collide, and a changed child set changes the fingerprint.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BulkWorkDeletionBindingTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);

    private static PlannedWorkDeletion Parent(Guid id) =>
        new(id, Day, Guid.NewGuid(), "Early", new TimeOnly(6, 0), new TimeOnly(14, 0));

    private static BulkWorkDeletionPlan Plan(
        IReadOnlyList<PlannedWorkDeletion> works, IReadOnlyList<Guid>? childWorks = null, IReadOnlyList<Guid>? childBreaks = null) =>
        new(null, "Max Müller", Day, Day, works) { ChildWorkIds = childWorks ?? [], ChildBreakIds = childBreaks ?? [] };

    [Test]
    public void APlanWithoutContainerChildren_BindsLikeAPlainListOfWorkIds()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var binding = BulkWorkDeletionBinding.Of(Plan([Parent(first), Parent(second)]));

        binding.Kind.ShouldBe(ConfirmationBindingKinds.Work);
        binding.Matches(ConfirmationBinding.Of(
            ConfirmationBindingKinds.Work, [first.ToString("N"), second.ToString("N")])).ShouldBeTrue();
        binding.Items.Count.ShouldBe(2);
    }

    [Test]
    public void ContainerChildren_AreBoundAsOwnPrefixedItems()
    {
        var parent = Guid.NewGuid();
        var childWork = Guid.NewGuid();
        var childBreak = Guid.NewGuid();

        var binding = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [childWork], [childBreak]));

        binding.Items.Count.ShouldBe(3);
        binding.Items.ShouldContain(parent.ToString("N"));
        binding.Items.ShouldContain(item => item.StartsWith("child-work:") && item.EndsWith(childWork.ToString("N")));
        binding.Items.ShouldContain(item => item.StartsWith("child-break:") && item.EndsWith(childBreak.ToString("N")));
    }

    [Test]
    public void AChildWorkAndAChildBreakWithTheSameId_AreTwoDifferentItems()
    {
        var shared = Guid.NewGuid();
        var parent = Guid.NewGuid();

        var asWork = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [shared], []));
        var asBreak = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [], [shared]));

        asWork.Matches(asBreak).ShouldBeFalse();
    }

    [Test]
    public void AChildThatWasAParentId_IsNotMistakenForTheParent()
    {
        var id = Guid.NewGuid();

        var asParent = BulkWorkDeletionBinding.Of(Plan([Parent(id)]));
        var asChild = BulkWorkDeletionBinding.Of(Plan([], [id], []));

        asParent.Matches(asChild).ShouldBeFalse();
    }

    [Test]
    public void ANewContainerChild_ChangesTheFingerprint()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();

        var before = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [child], []));
        var after = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [child, Guid.NewGuid()], []));

        before.Matches(after).ShouldBeFalse();
    }

    [Test]
    public void TheOrderOfTheChildren_DoesNotMatter()
    {
        var parent = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var one = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [first, second], []));
        var other = BulkWorkDeletionBinding.Of(Plan([Parent(parent)], [second, first], []));

        one.Matches(other).ShouldBeTrue();
    }

    [Test]
    public void ParentsAndChildrenTogetherBeyondTheStorageCap_KeepOnlyTheFingerprint()
    {
        var parents = Enumerable.Range(0, 30).Select(_ => Parent(Guid.NewGuid())).ToList();
        var children = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();

        var binding = BulkWorkDeletionBinding.Of(Plan(parents, children));

        binding.Items.ShouldBeEmpty();
        binding.Fingerprint.ShouldNotBeNullOrEmpty();
    }
}
