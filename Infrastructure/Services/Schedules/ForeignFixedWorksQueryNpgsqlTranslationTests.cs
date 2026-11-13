// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the read behind WizardHardConstraintBuilder.LoadForeignFixedSeatsAsync. The behavioural
/// tests run on the in-memory provider, which would stay green if the relational translation failed or dropped a leg of the
/// predicate. The model is built against Npgsql without opening a connection; ToQueryString generates the SQL offline.
/// The fixed-work decision (locked or protected by the apply) runs in memory and is pinned by the behavioural tests.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ForeignFixedWorksQueryNpgsqlTranslationTests
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
    public void ForeignWorksQuery_RealPlan_TranslatesEveryLeg_AndReadsOnlyRowsWithoutToken()
    {
        var sql = Translate(analyseToken: null);

        AssertCommonLegs(sql);
        Regex.IsMatch(sql, @"\w+\.analyse_token IS NULL").ShouldBeTrue($"Real plan only. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.analyse_token = @").ShouldBeFalse($"No token comparison for the real plan. Generated SQL: {sql}");
    }

    [Test]
    public void ForeignWorksQuery_Scenario_TranslatesEveryLeg_AndReadsOnlyRowsOfTheToken()
    {
        var sql = Translate(Guid.NewGuid());

        AssertCommonLegs(sql);
        Regex.IsMatch(sql, @"\w+\.analyse_token = @").ShouldBeTrue($"Exactly the scenario token. Generated SQL: {sql}");
    }

    private string Translate(Guid? analyseToken)
        => Normalize(WizardHardConstraintBuilder.ForeignWorksQuery(
                _context,
                [Guid.NewGuid()],
                [Guid.NewGuid()],
                new DateOnly(2026, 3, 9),
                new DateOnly(2026, 3, 15),
                analyseToken)
            .Select(w => new { w.Id, w.ShiftId, w.CurrentDate, w.LockLevel })
            .ToQueryString());

    private static void AssertCommonLegs(string sql)
    {
        sql.ShouldContain("FROM work");
        Regex.IsMatch(sql, @"NOT \(\w+\.client_id = ANY").ShouldBeTrue($"The agents must be left out. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.shift_id = ANY").ShouldBeTrue($"Only the run's shifts. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.workday >= @").ShouldBeTrue($"Start day inclusive. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.workday <= @").ShouldBeTrue($"End day inclusive. Generated SQL: {sql}");
        sql.ShouldContain("is_deleted");
    }

    private static string Normalize(string sql) => Regex.Replace(sql, WhitespaceRuns, SingleSpace);
}
