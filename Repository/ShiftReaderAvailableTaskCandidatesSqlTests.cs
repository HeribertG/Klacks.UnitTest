// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Renders ShiftReader's candidate query of a container template slot with the real Npgsql provider (ToQueryString, no
/// connection is opened): it translates, keeps only real-plan rows (analyse_token IS NULL) and, when the criteria carry
/// the container's validity period, compares from_date and until_date against it; without a period no validity
/// comparison is emitted. Execution against PostgreSQL is pinned by the integration test
/// ContainerAvailableTasksValidityPostgresTests.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ShiftReaderAvailableTaskCandidatesSqlTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private DataBaseContext _context = null!;
    private ShiftReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _reader = new ShiftReader(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void WithAValidityPeriod_TheSqlFiltersRealPlanRowsAndBothValidityBounds()
    {
        var criteria = new AvailableTaskCandidateCriteria(1, new TimeOnly(8, 0), new TimeOnly(18, 0))
        {
            ValidFrom = new DateOnly(2026, 11, 1),
            ValidUntil = new DateOnly(2026, 11, 30),
        };

        var sql = _reader.BuildAvailableTaskCandidates(criteria).ToQueryString();

        sql.ShouldContain("analyse_token IS NULL");
        sql.ShouldContain("from_date <=");
        sql.ShouldContain("until_date IS NULL");
        sql.ShouldContain("until_date >=");
    }

    [Test]
    public void WithoutAValidityPeriod_TheSqlHasNoValidityComparison_ButStillFiltersRealPlanRows()
    {
        var criteria = new AvailableTaskCandidateCriteria(1, new TimeOnly(8, 0), new TimeOnly(18, 0));

        var sql = _reader.BuildAvailableTaskCandidates(criteria).ToQueryString();

        sql.ShouldContain("analyse_token IS NULL");
        sql.ShouldNotContain("from_date <=");
        sql.ShouldNotContain("until_date >=");
    }
}
