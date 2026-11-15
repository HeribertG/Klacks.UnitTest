// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of ScenarioRuleRows, which every rule check of a scenario reads through. The behavioural
/// tests run on the in-memory provider, which stays green when the relational translation fails or drops a leg of the
/// predicate. ToQueryString generates the SQL offline, no connection is opened.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScenarioRuleRowsNpgsqlTranslationTests
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
    public void Works_Scenario_TranslatesEveryLeg()
    {
        var sql = Normalized(ScenarioRuleRows.Works(_context, Guid.NewGuid()).ToQueryString());

        Regex.IsMatch(sql, @"\w+\.analyse_token = @").ShouldBeTrue($"The scenario's own rows. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.analyse_token IS NULL").ShouldBeTrue($"The real rows. Generated SQL: {sql}");
        sql.ShouldContain("FROM shift");
        Regex.IsMatch(sql, @"\w+\.scenario_source_shift_id").ShouldBeTrue($"The carried shifts. Generated SQL: {sql}");
        sql.ShouldContain("FROM analyse_scenarios");
        Regex.IsMatch(sql, @"\w+\.from_date").ShouldBeTrue($"The cloned span starts at the scenario's from date. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.until_date").ShouldBeTrue($"The cloned span ends at the scenario's until date. Generated SQL: {sql}");
    }

    [Test]
    public void Works_RealPlan_IsTheRealRowsOnly()
    {
        var sql = Normalized(ScenarioRuleRows.Works(_context, analyseToken: null).ToQueryString());

        Regex.IsMatch(sql, @"\w+\.analyse_token IS NULL").ShouldBeTrue($"Only real works. Generated SQL: {sql}");
        sql.ShouldNotContain("FROM shift");
        sql.ShouldNotContain("analyse_scenarios");
    }

    [Test]
    public void Breaks_Scenario_TranslatesTheTwinCheckAndThePauseLeg()
    {
        var sql = Normalized(ScenarioRuleRows.Breaks(_context, Guid.NewGuid()).ToQueryString());

        sql.ShouldContain("FROM break AS");
        Regex.IsMatch(sql, @"\w+\.parent_work_id IS NULL").ShouldBeTrue($"Absences are matched by content. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.absence_id = \w+\.absence_id").ShouldBeTrue($"The twin has the same absence type. Generated SQL: {sql}");
        sql.ShouldContain("FROM work");
    }

    [Test]
    public void WorkChanges_Scenario_FollowTheVisibleWorks()
    {
        var sql = Normalized(ScenarioRuleRows.WorkChanges(_context, Guid.NewGuid()).ToQueryString());

        sql.ShouldContain("FROM work_change");
        sql.ShouldContain("FROM work");
        Regex.IsMatch(sql, @"\w+\.id = \w+\.work_id").ShouldBeTrue($"A change belongs to a visible work. Generated SQL: {sql}");
    }

    private static string Normalized(string sql) => Regex.Replace(sql, WhitespaceRuns, SingleSpace);
}
