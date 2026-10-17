// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the PreserveLevelBlockForExistingQualifications migration: every qualification existing at
/// update time becomes level-binding, so the former manual-booking block for a too-low level is kept.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class PreserveLevelBlockForExistingQualificationsMigrationTests
{
    private const string ExpectedSql = "UPDATE qualification SET is_level_binding = true;";

    [Test]
    public void Up_MarksAllExistingQualificationsBinding_WithoutFilter()
    {
        var operations = new PreserveLevelBlockForExistingQualifications().UpOperations;

        operations.Count.ShouldBe(1);
        operations.OfType<SqlOperation>().Single().Sql.ShouldBe(ExpectedSql);
    }

    [Test]
    public void Up_DoesNotChangeTheSchema()
    {
        new PreserveLevelBlockForExistingQualifications().UpOperations
            .ShouldAllBe(o => o is SqlOperation);
    }
}
