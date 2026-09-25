// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the Npgsql translation of the two effectiveness counters that leave out a skill the user's stop cut
/// short. The behavioural test of the same counters runs on the in-memory provider, which would stay green
/// if the relational translation dropped the predicate - and a predicate that quietly disappears counts
/// every stop as a failure of the skill that happened to be running. The model is built against Npgsql
/// without ever opening a connection, because ToQueryString generates the SQL offline.
/// </summary>

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SkillEffectivenessCancelledSqlTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string UsageTable = "skill_usage_records";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";

    private static readonly DateTime WindowStartUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string CancelledValue =
        ((int)SkillFailureKind.Cancelled).ToString(CultureInfo.InvariantCulture);

    private DataBaseContext _context = null!;
    private SkillEffectivenessRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new SkillEffectivenessRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void FailureRecords_LeaveOutTheCancelledKind()
    {
        var sql = Normalize(_repository.FailureRecordsQuery(WindowStartUtc).ToQueryString());

        sql.ShouldContain($"FROM {UsageTable}");
        sql.ShouldContain("failure_kind IS NOT NULL");
        sql.ShouldContain($"failure_kind <> {CancelledValue}");
    }

    [Test]
    public void CallRecords_LeaveOutTheCancelledKindButKeepRowsWithoutAFailureKind()
    {
        var sql = Normalize(_repository.CallRecordsQuery(WindowStartUtc).ToQueryString());

        sql.ShouldContain($"FROM {UsageTable}");
        Regex.IsMatch(sql, $@"failure_kind <> {CancelledValue} OR \w+\.failure_kind IS NULL").ShouldBeTrue(
            $"The predicate must keep rows without a failure kind. Generated SQL: {sql}");
    }

    private static string Normalize(string sql) => Regex.Replace(sql, WhitespaceRuns, SingleSpace);
}
