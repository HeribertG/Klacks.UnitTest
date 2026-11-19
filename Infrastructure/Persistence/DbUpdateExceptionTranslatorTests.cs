// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for DbUpdateExceptionTranslator: the duplicate and foreign-key flags of the DatabaseUpdateException come
/// from the PostgreSQL SQLSTATE. 23001 (restrict_violation) counts as a foreign-key violation, because PostgreSQL 18+
/// reports a delete or update blocked by an ON DELETE/UPDATE RESTRICT constraint with it instead of 23503.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence;

[TestFixture]
public class DbUpdateExceptionTranslatorTests
{
    private const string ConstraintName = "fk_child_parent";

    [TestCase("23503", false, true, TestName = "ForeignKeyViolation_IsForeignKeyViolation")]
    [TestCase("23001", false, true, TestName = "RestrictViolation_IsForeignKeyViolation")]
    [TestCase("23505", true, false, TestName = "UniqueViolation_IsDuplicate")]
    [TestCase("23502", false, false, TestName = "NotNullViolation_IsNeither")]
    public void Translate_SetsFlagsFromSqlState(string sqlState, bool expectedDuplicate, bool expectedForeignKey)
    {
        var postgres = new PostgresException("violation", "ERROR", "ERROR", sqlState, constraintName: ConstraintName);

        var translated = DbUpdateExceptionTranslator.Translate(new DbUpdateException("save failed", postgres));

        var databaseUpdate = translated.ShouldBeOfType<DatabaseUpdateException>();
        databaseUpdate.IsDuplicate.ShouldBe(expectedDuplicate);
        databaseUpdate.IsForeignKeyViolation.ShouldBe(expectedForeignKey);
        databaseUpdate.ConstraintName.ShouldBe(ConstraintName);
    }

    [Test]
    public void Translate_WithoutPostgresInnerException_SetsNoFlags()
    {
        var translated = DbUpdateExceptionTranslator.Translate(
            new DbUpdateException("save failed", new InvalidOperationException("not postgres")));

        var databaseUpdate = translated.ShouldBeOfType<DatabaseUpdateException>();
        databaseUpdate.IsDuplicate.ShouldBeFalse();
        databaseUpdate.IsForeignKeyViolation.ShouldBeFalse();
    }
}
