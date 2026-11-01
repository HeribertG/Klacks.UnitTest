// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the two name lookups of the uncovered-absence finding; the behavioural tests run on
/// the in-memory provider. The model is built against Npgsql without opening a connection, because ToQueryString
/// generates the SQL offline. Both lookups have to read retired rows, so neither may carry the soft-delete filter.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class UncoveredAbsenceLookupLoaderSqlTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string SoftDeleteColumn = "is_deleted";

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void ShiftLabels_TranslatesAndReadsRetiredShifts()
    {
        var sql = UncoveredAbsenceLookupLoader.ShiftLabels(_context, [Guid.NewGuid()]).ToQueryString();

        sql.ShouldContain("abbreviation");
        sql.ShouldNotContain(SoftDeleteColumn + " =");
        sql.ShouldNotContain("NOT (s." + SoftDeleteColumn);
    }

    [Test]
    public void AbsenceNames_TranslatesAndReadsRetiredTypes()
    {
        var sql = UncoveredAbsenceLookupLoader.AbsenceNames(_context, [Guid.NewGuid()]).ToQueryString();

        sql.ShouldContain("FROM");
        sql.ShouldNotContain("WHERE NOT");
        sql.ShouldContain("= ANY");
    }
}
