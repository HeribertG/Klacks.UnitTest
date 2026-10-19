// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ImportedOrderShiftMapper.DiffersFromDraft: an unchanged draft compares equal, every
/// single field ApplyToDraft writes (Name, Abbreviation and SourceSystemId included) is detected on its
/// own, and a reflection drift guard proves that every scalar Shift property ApplyToDraft writes is part of
/// the comparison -- a field added to ApplyToDraft but forgotten in DiffersFromDraft fails here instead of
/// silently turning updates into "unchanged" no-ops.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Models.Schedules;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ImportedOrderShiftMapperDiffersFromDraftTests
{
    private const int MinimumWrittenScalarFields = 21;

    private static readonly Guid ClientId = Guid.NewGuid();

    private static readonly Dictionary<string, Action<Shift>> FieldChanges = new()
    {
        [nameof(Shift.ClientId)] = s => s.ClientId = Guid.NewGuid(),
        [nameof(Shift.SourceSystemId)] = s => s.SourceSystemId = "erp-2",
        [nameof(Shift.ExternalOrderReference)] = s => s.ExternalOrderReference = "ORD-2",
        [nameof(Shift.Name)] = s => s.Name = "Renamed by a planner",
        [nameof(Shift.Abbreviation)] = s => s.Abbreviation = "XX",
        [nameof(Shift.Description)] = s => s.Description = "Evening care visit",
        [nameof(Shift.WorkTime)] = s => s.WorkTime += 1m,
        [nameof(Shift.FromDate)] = s => s.FromDate = s.FromDate.AddDays(1),
        [nameof(Shift.UntilDate)] = s => s.UntilDate = null,
        [nameof(Shift.StartShift)] = s => s.StartShift = s.StartShift.AddHours(1),
        [nameof(Shift.EndShift)] = s => s.EndShift = s.EndShift.AddHours(1),
        [nameof(Shift.IsTimeRange)] = s => s.IsTimeRange = !s.IsTimeRange,
        [nameof(Shift.IsMonday)] = s => s.IsMonday = !s.IsMonday,
        [nameof(Shift.IsTuesday)] = s => s.IsTuesday = !s.IsTuesday,
        [nameof(Shift.IsWednesday)] = s => s.IsWednesday = !s.IsWednesday,
        [nameof(Shift.IsThursday)] = s => s.IsThursday = !s.IsThursday,
        [nameof(Shift.IsFriday)] = s => s.IsFriday = !s.IsFriday,
        [nameof(Shift.IsSaturday)] = s => s.IsSaturday = !s.IsSaturday,
        [nameof(Shift.IsSunday)] = s => s.IsSunday = !s.IsSunday,
        [nameof(Shift.Quantity)] = s => s.Quantity += 1,
        [nameof(Shift.SumEmployees)] = s => s.SumEmployees += 1,
    };

    private static IEnumerable<string> ChangedFields() => FieldChanges.Keys;

    private static ImportedOrderPayload FullPayload()
    {
        return new ImportedOrderPayload
        {
            SourceSystemId = "erp-1",
            ExternalOrderReference = "ORD-1",
            Description = "Morning care visit",
            FromDate = new DateOnly(2026, 8, 1),
            UntilDate = new DateOnly(2026, 12, 31),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(15, 0),
            IsTimeRange = true,
            DurationMinutes = 20,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 2,
            SumEmployees = 3
        };
    }

    [Test]
    public void DiffersFromDraft_DraftWrittenFromTheSamePayload_ReturnsFalse()
    {
        // Arrange
        var draft = ImportedOrderShiftMapper.BuildDraft(FullPayload(), ClientId);

        // Act
        var differs = ImportedOrderShiftMapper.DiffersFromDraft(draft, FullPayload(), ClientId);

        // Assert
        differs.ShouldBeFalse();
    }

    [Test]
    public void DiffersFromDraft_IgnoresFieldsApplyToDraftDoesNotWrite()
    {
        // Arrange
        var draft = ImportedOrderShiftMapper.BuildDraft(FullPayload(), ClientId);
        draft.Id = Guid.NewGuid();
        draft.SupersedesOrderId = Guid.NewGuid();

        // Act
        var differs = ImportedOrderShiftMapper.DiffersFromDraft(draft, FullPayload(), ClientId);

        // Assert
        differs.ShouldBeFalse();
    }

    [TestCaseSource(nameof(ChangedFields))]
    public void DiffersFromDraft_SingleFieldChanged_ReturnsTrue(string field)
    {
        // Arrange
        var draft = ImportedOrderShiftMapper.BuildDraft(FullPayload(), ClientId);
        FieldChanges[field](draft);

        // Act
        var differs = ImportedOrderShiftMapper.DiffersFromDraft(draft, FullPayload(), ClientId);

        // Assert
        differs.ShouldBeTrue($"a changed {field} must be detected");
    }

    [Test]
    public void DiffersFromDraft_PayloadChangedOnly_ReturnsTrue()
    {
        // Arrange
        var draft = ImportedOrderShiftMapper.BuildDraft(FullPayload(), ClientId);
        var changed = FullPayload();
        changed.ExternalOrderReference = "ORD-9";

        // Act
        var differs = ImportedOrderShiftMapper.DiffersFromDraft(draft, changed, ClientId);

        // Assert
        differs.ShouldBeTrue("the derived Name and Abbreviation change with the reference");
    }

    [Test]
    public void DiffersFromDraft_CoversEveryScalarFieldApplyToDraftWrites()
    {
        // Arrange
        var blank = new Shift();
        var applied = new Shift();
        ImportedOrderShiftMapper.ApplyToDraft(applied, FullPayload(), ClientId);

        var writtenFields = ScalarProperties()
            .Where(p => !Equals(p.GetValue(blank), p.GetValue(applied)))
            .ToList();

        // Act + Assert
        writtenFields.Count.ShouldBeGreaterThanOrEqualTo(MinimumWrittenScalarFields,
            "the full payload must make every written field differ from a blank shift");

        foreach (var property in writtenFields)
        {
            var draft = new Shift();
            ImportedOrderShiftMapper.ApplyToDraft(draft, FullPayload(), ClientId);
            property.SetValue(draft, OtherValue(property.PropertyType, property.GetValue(draft)));

            ImportedOrderShiftMapper.DiffersFromDraft(draft, FullPayload(), ClientId)
                .ShouldBeTrue($"ApplyToDraft writes {property.Name}, so DiffersFromDraft must compare it");
        }
    }

    private static IEnumerable<PropertyInfo> ScalarProperties()
    {
        return typeof(Shift)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.PropertyType == typeof(string) || p.PropertyType.IsValueType);
    }

    private static object? OtherValue(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return current switch
        {
            null when underlying == typeof(Guid) => Guid.NewGuid(),
            null => throw new InvalidOperationException($"No alternative value defined for null {type.Name}."),
            string text => text + "-changed",
            Guid => Guid.NewGuid(),
            bool flag => !flag,
            int number => number + 1,
            decimal amount => amount + 1m,
            DateOnly date => date.AddDays(1),
            TimeOnly time => time.AddHours(1),
            _ => throw new InvalidOperationException(
                $"Extend {nameof(OtherValue)} for {underlying.Name}: ApplyToDraft writes a field of this type."),
        };
    }
}
