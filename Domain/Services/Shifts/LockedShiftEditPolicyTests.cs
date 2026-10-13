// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for LockedShiftEditPolicy: a draft order is never locked; a sealed order, a plannable shift and a cut piece
/// refuse a change to every content field the edit form disables, accept a save that changes only what the form leaves
/// editable (groups, expenses, qualifications, macro, a first end date), tolerate the minute rounding of the form, refuse
/// a status regression and every delete, and leave scenario rows to the repository guard.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class LockedShiftEditPolicyTests
{
    private static readonly ShiftStatus[] LockedStatuses =
        [ShiftStatus.SealedOrder, ShiftStatus.OriginalShift, ShiftStatus.SplitShift];

    private static Shift Stored(ShiftStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Night patrol",
        Abbreviation = "NP",
        Description = "Patrol of the site",
        Status = status,
        FromDate = new DateOnly(2026, 1, 1),
        StartShift = new TimeOnly(22, 0),
        EndShift = new TimeOnly(6, 0),
        IsMonday = true,
        IsTuesday = true,
        Quantity = 1,
        SumEmployees = 2,
        WorkTime = 8m,
        ShiftType = ShiftType.IsTask,
        ClientId = Guid.NewGuid()
    };

    private static Shift CopyOf(Shift source)
    {
        var copy = (Shift)typeof(Shift).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(source, null)!;
        copy.GroupItems = [];
        copy.ShiftExpenses = [];
        copy.RequiredQualifications = [];
        return copy;
    }

    [Test]
    public void DraftOrder_IsNeverLocked_EvenWhenEveryFieldChanges()
    {
        var stored = Stored(ShiftStatus.OriginalOrder);
        var incoming = CopyOf(stored);
        incoming.Name = "Other";
        incoming.StartShift = new TimeOnly(1, 0);
        incoming.Status = ShiftStatus.SealedOrder;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBeEmpty();
        LockedShiftEditPolicy.IsDeletionLocked(stored).ShouldBeFalse();
    }

    [TestCaseSource(nameof(LockedStatuses))]
    public void UnchangedRoundtrip_IsAccepted(ShiftStatus status)
    {
        var stored = Stored(status);

        LockedShiftEditPolicy.FindLockedChanges(stored, CopyOf(stored)).ShouldBeEmpty();
    }

    [TestCaseSource(nameof(LockedStatuses))]
    public void ChangingOnlyEditableParts_IsAccepted(ShiftStatus status)
    {
        var stored = Stored(status);
        var incoming = CopyOf(stored);
        incoming.MacroId = Guid.NewGuid();
        incoming.UntilDate = new DateOnly(2026, 12, 31);
        incoming.GroupItems = [new GroupItem { GroupId = Guid.NewGuid() }];
        incoming.ShiftExpenses = [new ShiftExpenses { Amount = 5m }];
        incoming.RequiredQualifications = [new ShiftRequiredQualification { QualificationId = Guid.NewGuid() }];
        incoming.ParentId = Guid.NewGuid();
        incoming.RootId = Guid.NewGuid();
        incoming.OriginalId = Guid.NewGuid();
        incoming.Lft = 7;
        incoming.Rgt = 9;
        incoming.SourceSystemId = null;
        incoming.ExternalOrderReference = null;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBeEmpty();
    }

    private static IEnumerable<TestCaseData> LockedFieldMutations()
    {
        yield return Mutation(nameof(Shift.Name), s => s.Name = "Renamed");
        yield return Mutation(nameof(Shift.Abbreviation), s => s.Abbreviation = "XX");
        yield return Mutation(nameof(Shift.Description), s => s.Description = "Changed");
        yield return Mutation(nameof(Shift.FromDate), s => s.FromDate = new DateOnly(2026, 2, 1));
        yield return Mutation(nameof(Shift.StartShift), s => s.StartShift = new TimeOnly(21, 0));
        yield return Mutation(nameof(Shift.EndShift), s => s.EndShift = new TimeOnly(7, 0));
        yield return Mutation(nameof(Shift.BeforeShift), s => s.BeforeShift = new TimeOnly(0, 30));
        yield return Mutation(nameof(Shift.AfterShift), s => s.AfterShift = new TimeOnly(0, 30));
        yield return Mutation(nameof(Shift.BriefingTime), s => s.BriefingTime = new TimeOnly(0, 15));
        yield return Mutation(nameof(Shift.DebriefingTime), s => s.DebriefingTime = new TimeOnly(0, 15));
        yield return Mutation(nameof(Shift.TravelTimeBefore), s => s.TravelTimeBefore = new TimeOnly(0, 20));
        yield return Mutation(nameof(Shift.TravelTimeAfter), s => s.TravelTimeAfter = new TimeOnly(0, 20));
        yield return Mutation(nameof(Shift.WorkTime), s => s.WorkTime = 9m);
        yield return Mutation(nameof(Shift.IsMonday), s => s.IsMonday = false);
        yield return Mutation(nameof(Shift.IsWednesday), s => s.IsWednesday = true);
        yield return Mutation(nameof(Shift.IsHoliday), s => s.IsHoliday = true);
        yield return Mutation(nameof(Shift.IsWeekdayAndHoliday), s => s.IsWeekdayAndHoliday = true);
        yield return Mutation(nameof(Shift.IsSporadic), s => s.IsSporadic = true);
        yield return Mutation(nameof(Shift.SporadicScope), s => s.SporadicScope = ShiftSporadic.Month);
        yield return Mutation(nameof(Shift.IsTimeRange), s => s.IsTimeRange = true);
        yield return Mutation(nameof(Shift.Quantity), s => s.Quantity = 3);
        yield return Mutation(nameof(Shift.SumEmployees), s => s.SumEmployees = 5);
        yield return Mutation(nameof(Shift.ShiftType), s => s.ShiftType = ShiftType.IsContainer);
        yield return Mutation(nameof(Shift.ClientId), s => s.ClientId = Guid.NewGuid());
        yield return Mutation(nameof(Shift.CuttingAfterMidnight), s => s.CuttingAfterMidnight = true);
        yield return Mutation(nameof(Shift.Status), s => s.Status = ShiftStatus.OriginalOrder);
    }

    private static TestCaseData Mutation(string field, Action<Shift> mutate) =>
        new TestCaseData(field, mutate).SetName($"Changing_{field}_IsLocked");

    [TestCaseSource(nameof(LockedFieldMutations))]
    public void ChangingAContentField_IsLockedForEveryNonDraftStatus(string field, Action<Shift> mutate)
    {
        foreach (var status in LockedStatuses)
        {
            var stored = Stored(status);
            var incoming = CopyOf(stored);
            mutate(incoming);

            LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldContain(field, $"status {status}");
        }
    }

    [Test]
    public void StatusRegression_FromSealedOrderToDraft_IsReported()
    {
        var stored = Stored(ShiftStatus.SealedOrder);
        var incoming = CopyOf(stored);
        incoming.Status = ShiftStatus.OriginalOrder;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBe([nameof(Shift.Status)]);
    }

    [Test]
    public void UntilDate_CanBeSetOnce_ButNotChangedOrClearedAfterwards()
    {
        var empty = Stored(ShiftStatus.SealedOrder);
        var setOnce = CopyOf(empty);
        setOnce.UntilDate = new DateOnly(2026, 6, 30);
        LockedShiftEditPolicy.FindLockedChanges(empty, setOnce).ShouldBeEmpty();

        var stored = Stored(ShiftStatus.SealedOrder);
        stored.UntilDate = new DateOnly(2026, 6, 30);
        var sameValue = CopyOf(stored);
        LockedShiftEditPolicy.FindLockedChanges(stored, sameValue).ShouldBeEmpty();

        var moved = CopyOf(stored);
        moved.UntilDate = new DateOnly(2026, 7, 31);
        LockedShiftEditPolicy.FindLockedChanges(stored, moved).ShouldBe([nameof(Shift.UntilDate)]);

        var cleared = CopyOf(stored);
        cleared.UntilDate = null;
        LockedShiftEditPolicy.FindLockedChanges(stored, cleared).ShouldBe([nameof(Shift.UntilDate)]);
    }

    [Test]
    public void SubMinuteTimeAndWorkTimeRounding_OfTheEditForm_IsTolerated()
    {
        var stored = Stored(ShiftStatus.OriginalShift);
        stored.StartShift = new TimeOnly(22, 0, 30);
        stored.WorkTime = 7.8333333333m;
        var incoming = CopyOf(stored);
        incoming.StartShift = new TimeOnly(22, 0, 0);
        incoming.WorkTime = 7.833333333333333m + 0.004m;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBeEmpty();
    }

    [Test]
    public void WorkTimeChangeOfMoreThanAMinute_IsLocked()
    {
        var stored = Stored(ShiftStatus.OriginalShift);
        var incoming = CopyOf(stored);
        incoming.WorkTime = stored.WorkTime + 0.05m;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBe([nameof(Shift.WorkTime)]);
    }

    [Test]
    public void NullAndEmptyText_AreTheSame()
    {
        var stored = Stored(ShiftStatus.SplitShift);
        stored.Description = string.Empty;
        var incoming = CopyOf(stored);
        incoming.Description = null!;

        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBeEmpty();
    }

    [TestCaseSource(nameof(LockedStatuses))]
    public void Deletion_IsLockedForEveryNonDraftStatus(ShiftStatus status)
    {
        LockedShiftEditPolicy.IsDeletionLocked(Stored(status)).ShouldBeTrue();
    }

    [Test]
    public void ScenarioRow_IsLeftToTheRepositoryGuard()
    {
        var stored = Stored(ShiftStatus.SealedOrder);
        stored.AnalyseToken = Guid.NewGuid();
        var incoming = CopyOf(stored);
        incoming.Name = "Renamed";

        LockedShiftEditPolicy.Applies(stored).ShouldBeFalse();
        LockedShiftEditPolicy.FindLockedChanges(stored, incoming).ShouldBeEmpty();
        LockedShiftEditPolicy.IsDeletionLocked(stored).ShouldBeFalse();
    }

    [Test]
    public void OnlyTheSealedOrderItself_QualifiesForTheAdministratorFamilyDelete()
    {
        LockedShiftEditPolicy.IsOrderFamilyDeletable(Stored(ShiftStatus.SealedOrder)).ShouldBeTrue();
        LockedShiftEditPolicy.IsOrderFamilyDeletable(Stored(ShiftStatus.OriginalShift)).ShouldBeFalse();
        LockedShiftEditPolicy.IsOrderFamilyDeletable(Stored(ShiftStatus.SplitShift)).ShouldBeFalse();
        LockedShiftEditPolicy.IsOrderFamilyDeletable(Stored(ShiftStatus.OriginalOrder)).ShouldBeFalse();
    }

    [Test]
    public void AScenarioCloneOfASealedOrder_DoesNotQualifyForTheFamilyDelete()
    {
        var clone = Stored(ShiftStatus.SealedOrder);
        clone.AnalyseToken = Guid.NewGuid();
        clone.ScenarioSourceShiftId = Guid.NewGuid();

        LockedShiftEditPolicy.IsOrderFamilyDeletable(clone).ShouldBeFalse();
    }

    private static readonly IReadOnlyDictionary<string, string> PropertiesOutsideTheLock =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Shift.MacroId)] = "Editable on a locked shift: the edit form keeps the macro selectable.",
            [nameof(Shift.GroupItems)] = "Editable on a locked shift: the group assignment stays editable.",
            [nameof(Shift.ShiftExpenses)] = "Editable on a locked shift: the default expenses stay editable.",
            [nameof(Shift.RequiredQualifications)] = "Editable on a locked shift: the required qualifications stay editable.",
            [nameof(Shift.Client)] = "Navigation of ClientId, which is locked.",
            [nameof(Shift.OriginalId)] = "Cut hierarchy; the repository preserves it on a form save and only the cut editor writes it.",
            [nameof(Shift.ParentId)] = "Cut hierarchy; the repository preserves it on a form save and only the cut editor writes it.",
            [nameof(Shift.RootId)] = "Cut hierarchy; the repository preserves it on a form save and only the cut editor writes it.",
            [nameof(Shift.Lft)] = "Cut hierarchy; the repository preserves it on a form save and only the cut editor writes it.",
            [nameof(Shift.Rgt)] = "Cut hierarchy; the repository preserves it on a form save and only the cut editor writes it.",
            [nameof(Shift.AnalyseToken)] = "Scenario marker; a scenario row is left to the repository guard.",
            [nameof(Shift.ScenarioSourceShiftId)] = "Scenario clone tracking; the repository keeps the stored value on a form save.",
            [nameof(Shift.SourceChildCountSnapshot)] = "Scenario clone tracking; the repository keeps the stored value on a form save.",
            [nameof(Shift.SourceSystemId)] = "ERP reference; the repository keeps the stored value on a form save.",
            [nameof(Shift.ExternalOrderReference)] = "ERP reference; the repository keeps the stored value on a form save.",
            [nameof(Shift.SupersedesOrderId)] = "ERP reference; the repository keeps the stored value on a form save.",
            [nameof(Shift.Id)] = "Identity of the row.",
            [nameof(Shift.CreateTime)] = "Audit field written by the database context.",
            [nameof(Shift.CurrentUserCreated)] = "Audit field written by the database context.",
            [nameof(Shift.UpdateTime)] = "Audit field written by the database context.",
            [nameof(Shift.CurrentUserUpdated)] = "Audit field written by the database context.",
            [nameof(Shift.IsDeleted)] = "Soft-delete marker written by the database context.",
            [nameof(Shift.DeletedTime)] = "Audit field written by the database context.",
            [nameof(Shift.CurrentUserDeleted)] = "Audit field written by the database context."
        };

    [Test]
    public void EveryShiftProperty_IsLockedOrExplicitlyExempt()
    {
        var undecided = typeof(Shift).GetProperties()
            .Select(property => property.Name)
            .Where(name => !LockedShiftEditPolicy.LockedFieldNames.Contains(name) && !PropertiesOutsideTheLock.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        undecided.ShouldBeEmpty(
            "These Shift properties are neither compared by LockedShiftEditPolicy nor listed as exempt with a reason. "
            + "A new content column would otherwise be editable on a sealed order, a plannable shift and a cut piece: "
            + string.Join(", ", undecided));
    }

    [Test]
    public void TheExemptionList_HasNoStaleOrDoubleEntries()
    {
        var shiftProperties = typeof(Shift).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        var stale = PropertiesOutsideTheLock.Keys.Where(name => !shiftProperties.Contains(name)).ToList();
        var doubled = PropertiesOutsideTheLock.Keys.Where(LockedShiftEditPolicy.LockedFieldNames.Contains).ToList();

        stale.ShouldBeEmpty("Not a Shift property any more: " + string.Join(", ", stale));
        doubled.ShouldBeEmpty("Both compared and exempt: " + string.Join(", ", doubled));
        LockedShiftEditPolicy.LockedFieldNames.Where(name => !shiftProperties.Contains(name)).ShouldBeEmpty();
    }
}
