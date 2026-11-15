// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the two reads of ShiftGroupIneligibilityQuery (shift links, group trees). The behavioural
/// tests run on the in-memory provider, which would stay green if the relational translation failed or dropped a leg of
/// the predicate. ToQueryString generates the SQL offline, no connection is opened.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class ShiftGroupIneligibilityQueryNpgsqlTranslationTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";

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
    public void LinksQuery_TranslatesEveryLeg()
    {
        var sql = Normalise(ShiftGroupIneligibilityQuery.LinksQuery(_context, [Guid.NewGuid()]).ToQueryString());

        sql.ShouldContain("FROM group_item");
        Regex.IsMatch(sql, @"\w+\.shift_id = ANY").ShouldBeTrue($"Only the shifts of the plan. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.analyse_token IS NULL").ShouldBeTrue($"Only real-plan links. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.scenario_source_group_item_id IS NULL").ShouldBeTrue($"No scenario clones. Generated SQL: {sql}");
        sql.ShouldContain("is_deleted");
    }

    [Test]
    public void TreeQuery_TranslatesTheRootFallback()
    {
        var sql = Normalise(ShiftGroupIneligibilityQuery.TreeQuery(_context, [Guid.NewGuid()]).ToQueryString());

        sql.ShouldContain("COALESCE(");
        Regex.IsMatch(sql, @"COALESCE\(\w+\.root, \w+\.id\) = ANY").ShouldBeTrue($"A root group without root counts by its id. Generated SQL: {sql}");
    }

    private static string Normalise(string sql) => Regex.Replace(sql, WhitespaceRuns, SingleSpace);
}
