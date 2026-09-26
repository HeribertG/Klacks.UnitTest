// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the Npgsql model of the pre-seal lock state a period seal records on Work and Break: the three columns
/// must stay nullable (NULL is what marks an entry sealed before the state was recorded, and what makes a reopen
/// of such an entry behave exactly like before) and keep the names the migration created. The bulk statements
/// themselves (ExecuteUpdate with a self-referencing SET) cannot run on EF InMemory; they were verified live
/// against PostgreSQL (see docs/knowledge/period-closing-day-locks.md).
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PreSealLockStateModelGuardTests
{
    private const string ModelOnlyConnectionString = "Host=localhost;Port=1;Database=model-only";

    private static readonly (string Property, string Column)[] PreSealColumns =
    {
        (nameof(ScheduleEntryBase.PreSealLockLevel), "pre_seal_lock_level"),
        (nameof(ScheduleEntryBase.PreSealSealedAt), "pre_seal_sealed_at"),
        (nameof(ScheduleEntryBase.PreSealSealedBy), "pre_seal_sealed_by")
    };

    [TestCase(typeof(Work), "work")]
    [TestCase(typeof(Break), "break")]
    public void PreSealColumns_AreNullableAndNamedAsMigrated(Type entityType, string table)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>());

        var entity = context.Model.FindEntityType(entityType)!;
        entity.GetTableName().ShouldBe(table);

        foreach (var (propertyName, column) in PreSealColumns)
        {
            var property = entity.FindProperty(propertyName);
            property.ShouldNotBeNull($"{entityType.Name}.{propertyName} is not mapped");
            property!.IsNullable.ShouldBeTrue($"{entityType.Name}.{propertyName} must stay nullable");
            property.GetColumnName().ShouldBe(column);
        }
    }

    [Test]
    public void PreSealLockLevel_IsStoredAsInteger_LikeLockLevel()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>());

        var work = context.Model.FindEntityType(typeof(Work))!;

        work.FindProperty(nameof(ScheduleEntryBase.PreSealLockLevel))!.GetColumnType()
            .ShouldBe(work.FindProperty(nameof(ScheduleEntryBase.LockLevel))!.GetColumnType());
    }
}
