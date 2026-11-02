// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one field list that keeps the sealed order and the plannable copy of a shift in a variant (key) equal.
/// Copy must carry every mirrored field and nothing else; the list must cover exactly the scalar fields of Shift that are
/// not hierarchy, status, token, scenario or ERP bookkeeping, so a new Shift field fails here until it is mirrored or
/// excluded on purpose; ShiftCloner must copy the same fields; the cut-relevant comparison ignores what the edit form
/// rounds (seconds, sub-minute WorkTime) and sees what a cut copies onto its pieces.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Domain.Services.Shifts;

[TestFixture]
public class VariantShiftMirrorTests
{
    private static readonly string[] ExcludedFieldNames =
    [
        nameof(Shift.Status),
        nameof(Shift.OriginalId),
        nameof(Shift.ParentId),
        nameof(Shift.RootId),
        nameof(Shift.Lft),
        nameof(Shift.Rgt),
        nameof(Shift.AnalyseToken),
        nameof(Shift.ScenarioSourceShiftId),
        nameof(Shift.SourceChildCountSnapshot),
        nameof(Shift.CopiedFromShiftId),
        nameof(Shift.SourceSystemId),
        nameof(Shift.ExternalOrderReference),
        nameof(Shift.SupersedesOrderId)
    ];

    private static readonly string[] ClonerBookkeepingNames =
    [
        nameof(Shift.ScenarioSourceShiftId),
        nameof(Shift.SourceChildCountSnapshot),
        nameof(Shift.CopiedFromShiftId),
        nameof(Shift.SourceSystemId),
        nameof(Shift.ExternalOrderReference),
        nameof(Shift.SupersedesOrderId)
    ];

    private static IEnumerable<PropertyInfo> ScalarShiftProperties() =>
        typeof(Shift).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => !typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                || property.PropertyType == typeof(string))
            .Where(property => property.PropertyType != typeof(Client));

    private static object DistinctValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(string)) return "mirror";
        if (underlying == typeof(Guid)) return Guid.NewGuid();
        if (underlying == typeof(TimeOnly)) return new TimeOnly(5, 7);
        if (underlying == typeof(DateOnly)) return new DateOnly(2099, 1, 2);
        if (underlying == typeof(int)) return 7;
        if (underlying == typeof(decimal)) return 3.5m;
        if (underlying.IsEnum) return Enum.GetValues(underlying).Cast<object>().Last();
        throw new NotSupportedException(underlying.Name);
    }

    private static Shift FullyPopulated(IEnumerable<string> names)
    {
        var shift = new Shift();
        foreach (var name in names)
        {
            var property = typeof(Shift).GetProperty(name)!;
            property.SetValue(shift, DistinctValue(property.PropertyType));
        }

        return shift;
    }

    [Test]
    public void MirroredFields_AreExactlyTheScalarShiftFieldsThatAreNotExcluded()
    {
        var scalarNames = ScalarShiftProperties().Select(property => property.Name).ToList();

        VariantShiftMirror.MirroredFieldNames.ShouldBe(scalarNames.Except(ExcludedFieldNames), ignoreOrder: true);
        VariantShiftMirror.MirroredFieldNames.Distinct().Count().ShouldBe(VariantShiftMirror.MirroredFieldNames.Count);
    }

    [Test]
    public void Copy_CarriesEveryMirroredField_AndLeavesTheRestOfTheTargetAlone()
    {
        var source = FullyPopulated(VariantShiftMirror.MirroredFieldNames);
        var target = new Shift { Status = ShiftStatus.OriginalShift, OriginalId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid(), Lft = 3 };
        var targetBefore = (target.Status, target.OriginalId, target.AnalyseToken, target.Lft, target.Id);

        VariantShiftMirror.Copy(source, target);

        foreach (var name in VariantShiftMirror.MirroredFieldNames)
        {
            var property = typeof(Shift).GetProperty(name)!;
            property.GetValue(target).ShouldBe(property.GetValue(source), name);
            property.GetValue(target).ShouldNotBe(property.GetValue(new Shift()), $"{name} must differ from the default or the test proves nothing");
        }

        (target.Status, target.OriginalId, target.AnalyseToken, target.Lft, target.Id).ShouldBe(targetBefore);
    }

    [Test]
    public void ShiftCloner_CopiesEveryMirroredField()
    {
        var source = FullyPopulated(VariantShiftMirror.MirroredFieldNames);

        var clone = ShiftCloner.Clone(source);

        foreach (var name in VariantShiftMirror.MirroredFieldNames)
        {
            var property = typeof(Shift).GetProperty(name)!;
            property.GetValue(clone).ShouldBe(property.GetValue(source), name);
        }
    }

    [Test]
    public void ShiftCloner_CopiesNothingOutsideTheMirrorExceptHierarchyStatusTokenAndTheClonerBookkeeping()
    {
        var everyScalar = ScalarShiftProperties().Select(property => property.Name).ToList();
        var source = FullyPopulated(everyScalar);

        var clone = ShiftCloner.Clone(source);

        var copiedOutsideMirror = everyScalar
            .Except(VariantShiftMirror.MirroredFieldNames)
            .Where(name =>
            {
                var property = typeof(Shift).GetProperty(name)!;
                return Equals(property.GetValue(clone), property.GetValue(source));
            })
            .ToList();

        copiedOutsideMirror.ShouldBe(
            ExcludedFieldNames.Except(ClonerBookkeepingNames),
            ignoreOrder: true,
            "a field ShiftCloner copies must be mirrored or listed as excluded here on purpose");
    }

    [Test]
    public void CutRelevantFields_AndCutIndependentFields_DoNotOverlap()
    {
        VariantShiftMirror.CutRelevantFieldNames.Intersect(VariantShiftMirror.CutIndependentFieldNames).ShouldBeEmpty();
        VariantShiftMirror.CutIndependentFieldNames.ShouldBe(
            [nameof(Shift.Name), nameof(Shift.Abbreviation), nameof(Shift.Description)], ignoreOrder: true);
    }

    [Test]
    public void FindCutRelevantChanges_IgnoresNameAbbreviationAndDescription()
    {
        var stored = new Shift { Name = "A", Abbreviation = "A", Description = "A" };
        var incoming = new Shift { Name = "B", Abbreviation = "B", Description = "B" };

        VariantShiftMirror.FindCutRelevantChanges(stored, incoming).ShouldBeEmpty();
    }

    [Test]
    public void FindCutRelevantChanges_IgnoresWhatTheEditFormRounds()
    {
        var stored = new Shift { StartShift = new TimeOnly(8, 0, 0), WorkTime = 8.0333m };
        var incoming = new Shift { StartShift = new TimeOnly(8, 0, 40), WorkTime = 8.03m };

        VariantShiftMirror.FindCutRelevantChanges(stored, incoming).ShouldBeEmpty();
    }

    [Test]
    public void FindCutRelevantChanges_NamesTimeDateWeekdayStaffingAndCustomerChanges()
    {
        var customer = Guid.NewGuid();
        var stored = new Shift { StartShift = new TimeOnly(8, 0), FromDate = new DateOnly(2099, 8, 1), IsMonday = true, Quantity = 1, ClientId = customer };
        var incoming = new Shift { StartShift = new TimeOnly(8, 1), FromDate = new DateOnly(2099, 8, 2), IsMonday = false, Quantity = 2, ClientId = Guid.NewGuid() };

        VariantShiftMirror.FindCutRelevantChanges(stored, incoming).ShouldBe(
            [nameof(Shift.StartShift), nameof(Shift.FromDate), nameof(Shift.IsMonday), nameof(Shift.Quantity), nameof(Shift.ClientId)],
            ignoreOrder: true);
    }

    [Test]
    public void FindCutRelevantChanges_SeesAnEndDateThatIsSetOrRemoved()
    {
        var withEnd = new Shift { UntilDate = new DateOnly(2099, 8, 31) };

        VariantShiftMirror.FindCutRelevantChanges(new Shift(), withEnd).ShouldBe(new[] { nameof(Shift.UntilDate) });
        VariantShiftMirror.FindCutRelevantChanges(withEnd, new Shift()).ShouldBe(new[] { nameof(Shift.UntilDate) });
    }

    [Test]
    public void FindCutRelevantChanges_SeesAWorkTimeChangeOfMoreThanAMinute()
    {
        VariantShiftMirror.FindCutRelevantChanges(new Shift { WorkTime = 8m }, new Shift { WorkTime = 8.05m })
            .ShouldBe(new[] { nameof(Shift.WorkTime) });
    }
}
