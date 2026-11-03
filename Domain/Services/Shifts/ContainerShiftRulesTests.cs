// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shape of a container: one row, status OriginalShift, no OriginalId. Only a container that is being sealed is brought
/// into it; a task and a draft container are left alone.
/// </summary>

using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class ContainerShiftRulesTests
{
    private static Shift Row(ShiftType type, ShiftStatus status, Guid? originalId = null) =>
        new() { Id = Guid.NewGuid(), ShiftType = type, Status = status, OriginalId = originalId };

    [TestCase(ShiftType.IsContainer, ShiftStatus.SealedOrder, true)]
    [TestCase(ShiftType.IsContainer, ShiftStatus.OriginalOrder, false)]
    [TestCase(ShiftType.IsContainer, ShiftStatus.OriginalShift, false)]
    [TestCase(ShiftType.IsTask, ShiftStatus.SealedOrder, false)]
    public void IsBeingSealed_OnlyForASealedContainer(ShiftType type, ShiftStatus status, bool expected)
    {
        ContainerShiftRules.IsBeingSealed(Row(type, status)).ShouldBe(expected);
    }

    [Test]
    public void ApplyOneRowShape_SetsTheStatus_AndClearsTheOrderReference()
    {
        var container = Row(ShiftType.IsContainer, ShiftStatus.SealedOrder, Guid.NewGuid());

        ContainerShiftRules.ApplyOneRowShape(container);

        container.Status.ShouldBe(ShiftStatus.OriginalShift);
        container.OriginalId.ShouldBeNull();
        ContainerShiftRules.HasOneRowShape(container).ShouldBeTrue();
    }

    [Test]
    public void HasOneRowShape_IsFalse_ForACopyOfAnOrder_ADraft_AndATask()
    {
        ContainerShiftRules.HasOneRowShape(Row(ShiftType.IsContainer, ShiftStatus.OriginalShift, Guid.NewGuid())).ShouldBeFalse();
        ContainerShiftRules.HasOneRowShape(Row(ShiftType.IsContainer, ShiftStatus.OriginalOrder)).ShouldBeFalse();
        ContainerShiftRules.HasOneRowShape(Row(ShiftType.IsTask, ShiftStatus.OriginalShift)).ShouldBeFalse();
    }
}
