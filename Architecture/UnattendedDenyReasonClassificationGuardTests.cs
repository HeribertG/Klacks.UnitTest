// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard for UnattendedDenyReasonClassification, the split that decides whether a refused
/// scheduled task is PAUSED (its cause is one the owner can lift) or DISABLED (it is not). A switch with
/// a default arm would silently sort a newly added deny reason into one bucket without anyone deciding,
/// which is exactly how a user's task gets destroyed for something one setting would have fixed. The
/// guard therefore demands that the union of the two curated sets covers every declared enum value
/// except None, that nothing sits in both, and that the runtime lookup stays fail-closed - None and an
/// undeclared value are never treated as recoverable.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UnattendedDenyReasonClassificationGuardTests
{
    private const UnattendedDenyReason UndeclaredReason = (UnattendedDenyReason)int.MaxValue;

    [Test]
    public void EveryDenyReason_IsCuratedAsOwnerFixableOrTerminal()
    {
        var classified = UnattendedDenyReasonClassification.OwnerFixable
            .Concat(UnattendedDenyReasonClassification.Terminal)
            .ToHashSet();

        var unclassified = Enum.GetValues<UnattendedDenyReason>()
            .Where(reason => reason != UnattendedDenyReason.None)
            .Where(reason => !classified.Contains(reason))
            .Select(reason => reason.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unclassified.ShouldBeEmpty(
            "Every UnattendedDenyReason has to be curated in UnattendedDenyReasonClassification as either " +
            "OwnerFixable (the scheduled task is paused and can be resumed) or Terminal (the task is " +
            "disabled). An unlisted reason falls into the fail-closed Terminal behaviour, which destroys " +
            "the owner's task - decide deliberately instead. Unclassified: " + string.Join(", ", unclassified));
    }

    [Test]
    public void NoDenyReason_IsBothOwnerFixableAndTerminal()
    {
        var overlap = UnattendedDenyReasonClassification.OwnerFixable
            .Intersect(UnattendedDenyReasonClassification.Terminal)
            .Select(reason => reason.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        overlap.ShouldBeEmpty(
            "A deny reason cannot both pause and disable the task. Contradictory entries: " +
            string.Join(", ", overlap));
    }

    [Test]
    public void NeitherSet_ContainsNone()
    {
        UnattendedDenyReasonClassification.OwnerFixable.ShouldNotContain(UnattendedDenyReason.None);
        UnattendedDenyReasonClassification.Terminal.ShouldNotContain(UnattendedDenyReason.None);
    }

    [TestCase(UnattendedDenyReason.IrreversibleWithoutOptIn)]
    [TestCase(UnattendedDenyReason.AutonomyLevelTooLow)]
    public void OwnerFixableReasons_PauseTheTask(UnattendedDenyReason reason)
    {
        UnattendedDenyReasonClassification.IsOwnerFixable(reason).ShouldBeTrue();
    }

    [TestCase(UnattendedDenyReason.NoPermissions)]
    [TestCase(UnattendedDenyReason.UnknownSkill)]
    [TestCase(UnattendedDenyReason.SensitiveSkill)]
    [TestCase(UnattendedDenyReason.UnknownRiskClass)]
    public void TerminalReasons_DisableTheTask(UnattendedDenyReason reason)
    {
        UnattendedDenyReasonClassification.IsOwnerFixable(reason).ShouldBeFalse();
    }

    [Test]
    public void NoneAndAnUndeclaredValue_AreNotOwnerFixable()
    {
        UnattendedDenyReasonClassification.IsOwnerFixable(UnattendedDenyReason.None).ShouldBeFalse();
        UnattendedDenyReasonClassification.IsOwnerFixable(UndeclaredReason).ShouldBeFalse();
    }
}
