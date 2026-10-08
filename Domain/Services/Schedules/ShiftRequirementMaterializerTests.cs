// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ShiftRequirementMaterializerTests
{
    private static readonly Guid Piece = Guid.NewGuid();
    private static readonly Guid Order = Guid.NewGuid();

    private static ShiftRequiredQualification Row(Guid shiftId, Guid qualificationId, bool mandatory = true,
        QualificationLevel level = QualificationLevel.Basic) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        QualificationId = qualificationId,
        IsMandatory = mandatory,
        MinLevel = level,
    };

    [Test]
    public void InheritedAsOwn_Copies_The_Inherited_Rows_Onto_The_Shift_With_New_Ids()
    {
        var q1 = Row(Order, Guid.NewGuid(), true, QualificationLevel.Expert);
        var q2 = Row(Order, Guid.NewGuid(), false);
        var effective = new[]
        {
            new EffectiveShiftRequirement(Piece, "piece", Order, q1),
            new EffectiveShiftRequirement(Piece, "piece", Order, q2),
        };

        var own = ShiftRequirementMaterializer.InheritedAsOwn(effective, Piece);

        own.Count.ShouldBe(2);
        own.ShouldAllBe(row => row.ShiftId == Piece && row.Id != q1.Id && row.Id != q2.Id);
        own.Single(row => row.QualificationId == q1.QualificationId).MinLevel.ShouldBe(QualificationLevel.Expert);
        own.Single(row => row.QualificationId == q2.QualificationId).IsMandatory.ShouldBeFalse();
    }

    [Test]
    public void InheritedAsOwn_Is_Empty_When_The_Shift_Has_Rows_Of_Its_Own()
    {
        var effective = new[] { new EffectiveShiftRequirement(Piece, "piece", Piece, Row(Piece, Guid.NewGuid())) };

        ShiftRequirementMaterializer.InheritedAsOwn(effective, Piece).ShouldBeEmpty();
    }

    [Test]
    public void InheritedAsOwn_Ignores_Entries_Of_Other_Shifts()
    {
        var other = Guid.NewGuid();
        var effective = new[] { new EffectiveShiftRequirement(other, "other", Order, Row(Order, Guid.NewGuid())) };

        ShiftRequirementMaterializer.InheritedAsOwn(effective, Piece).ShouldBeEmpty();
    }

    [Test]
    public void CopyAll_Keeps_One_Row_Per_Qualification()
    {
        var qualificationId = Guid.NewGuid();

        var copies = ShiftRequirementMaterializer.CopyAll([Row(Order, qualificationId), Row(Piece, qualificationId)], Piece);

        copies.Count.ShouldBe(1);
        copies[0].ShiftId.ShouldBe(Piece);
    }
}
