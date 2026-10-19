// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves the erp_import_file ledger mapping against the real Npgsql model (no database needed): the
/// pinned table name (a DbSet name would pluralise it), the fixed-length SHA-256 column, the partial
/// unique index on the storage key, the descending listing index and the nullable link from an import
/// exception to its file that survives the file's deletion.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Persistence;

using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ErpImportFileMappingTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin";

    private static DataBaseContext NewContext() => new(
        new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options,
        Substitute.For<IHttpContextAccessor>());

    private static IModel DesignModel(DataBaseContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    private static IEntityType FileEntity(DataBaseContext context) =>
        DesignModel(context).FindEntityType(typeof(ErpImportFile))!;

    [Test]
    public void Table_IsTheSingularLedgerName()
    {
        using var context = NewContext();

        FileEntity(context).GetTableName().ShouldBe("erp_import_file");
    }

    [Test]
    public void Sha256_IsAFixedLengthCharColumn()
    {
        using var context = NewContext();
        var property = FileEntity(context).FindProperty(nameof(ErpImportFile.Sha256))!;

        property.GetColumnName().ShouldBe("sha256");
        property.GetColumnType().ShouldBe("character(64)");
        property.IsNullable.ShouldBeFalse();
    }

    [Test]
    public void StorageKey_HasAUniqueIndexOnlyOverNonDeletedRows()
    {
        using var context = NewContext();
        var index = FileEntity(context).GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(ErpImportFile.StorageKey)]));

        index.IsUnique.ShouldBeTrue();
        index.GetFilter().ShouldBe("is_deleted = false");
    }

    [Test]
    public void ListingIndex_IsDropPointAscendingAndReceivedAtDescending()
    {
        using var context = NewContext();
        var index = FileEntity(context).GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(ErpImportFile.DropPointId), nameof(ErpImportFile.ReceivedAt)]));

        index.IsDescending.ShouldBe([false, true]);
    }

    [Test]
    public void StatusAndChannel_AreStoredAsIntegers()
    {
        using var context = NewContext();
        var entity = FileEntity(context);

        entity.FindProperty(nameof(ErpImportFile.Status))!.GetColumnType().ShouldBe("integer");
        entity.FindProperty(nameof(ErpImportFile.Channel))!.GetColumnType().ShouldBe("integer");
    }

    [Test]
    public void ExceptionImportFileId_IsANullableIndexedForeignKeyThatIsClearedOnDelete()
    {
        using var context = NewContext();
        var exceptionEntity = DesignModel(context).FindEntityType(typeof(ErpImportException))!;
        var property = exceptionEntity.FindProperty(nameof(ErpImportException.ImportFileId))!;

        property.GetColumnName().ShouldBe("import_file_id");
        property.IsNullable.ShouldBeTrue();

        var foreignKey = exceptionEntity.GetForeignKeys()
            .Single(fk => fk.Properties.Single().Name == nameof(ErpImportException.ImportFileId));
        foreignKey.PrincipalEntityType.ClrType.ShouldBe(typeof(ErpImportFile));
        foreignKey.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);

        exceptionEntity.GetIndexes()
            .ShouldContain(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(ErpImportException.ImportFileId));
    }

    [Test]
    public void DuplicateOfFileId_IsASelfReferenceThatIsClearedOnDelete()
    {
        using var context = NewContext();
        var foreignKey = FileEntity(context).GetForeignKeys()
            .Single(fk => fk.Properties.Single().Name == nameof(ErpImportFile.DuplicateOfFileId));

        foreignKey.PrincipalEntityType.ClrType.ShouldBe(typeof(ErpImportFile));
        foreignKey.DeleteBehavior.ShouldBe(DeleteBehavior.SetNull);
    }
}
