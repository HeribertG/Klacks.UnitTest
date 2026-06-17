// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer;

[TestFixture]
public sealed class HarmonizerEligibilityTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    // --- Wizard 2: DomainAwareReplaceValidator (same-day replace) ---

    [Test]
    public void ReplaceValidator_IneligibleReceivingAgent_Rejected()
    {
        var shiftId = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, WorkCell(shiftId, Day0.AddDays(1)));

        // agent-1 would receive shiftId on Day0+1 and is not qualified.
        var ineligible = new HashSet<(string, Guid, DateOnly)> { ("agent-1", shiftId, Day0.AddDays(1)) };
        var validator = new DomainAwareReplaceValidator(null, null, ineligible);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void ReplaceValidator_IneligibleForOtherAgent_Passes()
    {
        var shiftId = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, WorkCell(shiftId, Day0.AddDays(1)));

        // Only agent-0 is gated; agent-1 (the receiver) is eligible.
        var ineligible = new HashSet<(string, Guid, DateOnly)> { ("agent-0", shiftId, Day0.AddDays(1)) };
        var validator = new DomainAwareReplaceValidator(null, null, ineligible);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeTrue();
    }

    [Test]
    public void ReplaceValidator_EmptyMatrix_Passes()
    {
        var shiftId = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, WorkCell(shiftId, Day0.AddDays(1)));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeTrue();
    }

    // --- Wizard 3: PlanMutationValidator cross-day branch (discriminating test) ---

    [Test]
    public void PlanMutationValidator_CrossDaySwap_IneligibleAgent_Rejected()
    {
        var shiftId = Guid.NewGuid();
        var otherShift = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, WorkCell(shiftId, Day0));
        bitmap.SetCell(1, 1, WorkCell(otherShift, Day0.AddDays(1)));

        // Cross-day swap: agent-1 (row 1) would receive shiftId on Day0+1 and is not qualified.
        // This only fails if the cross-day branch applies the gate — the same-day domain validator
        // is never consulted for cross-day swaps.
        var ineligible = new HashSet<(string, Guid, DateOnly)> { ("agent-1", shiftId, Day0.AddDays(1)) };
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null, null, ineligible));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(0, 0, 1, 1, "test"));

        rejection.ShouldNotBeNull();
        rejection!.Reason.ShouldBe(PlanMutationRejectionReason.HardConstraintViolation);
    }

    [Test]
    public void PlanMutationValidator_CrossDaySwap_EligibleAgents_Passes()
    {
        var shiftId = Guid.NewGuid();
        var otherShift = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, WorkCell(shiftId, Day0));
        bitmap.SetCell(1, 1, WorkCell(otherShift, Day0.AddDays(1)));

        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(null));

        validator.Validate(bitmap, new PlanCellSwap(0, 0, 1, 1, "test")).ShouldBeNull();
    }

    private static Cell WorkCell(Guid shiftId, DateOnly date) => new(
        CellSymbol.Early,
        shiftId,
        [Guid.NewGuid()],
        false,
        date.ToDateTime(new TimeOnly(7, 0)),
        date.ToDateTime(new TimeOnly(15, 0)),
        8m);

    private static HarmonyBitmap BuildBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"agent-{r}", 100m, new HashSet<CellSymbol>()));
        }

        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
