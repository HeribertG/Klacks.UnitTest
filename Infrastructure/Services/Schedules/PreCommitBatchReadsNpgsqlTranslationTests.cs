// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves on the real Npgsql provider that the reads the pre-commit check makes for several people at once translate,
/// and that each one keeps the filters of the single-person read it replaces: the checker's works and breaks (id list,
/// window, soft delete, top level, exact token), its work changes (work id list, soft delete), the Work rows of the
/// rest-day rotation and of the counter rules (id list, range, soft delete, exact token, container children included),
/// the employment starts and the open compensatory-rest obligations in trigger-date order. ToQueryString needs no
/// server, so the context points at an unreachable one.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitBatchReadsNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TopLevelOnly = "parent_work_id IS NULL";
    private const string ParentWorkColumn = "parent_work_id";
    private const string NotDeleted = "NOT (w.is_deleted)";

    private static readonly DateOnly From = new(2026, 12, 8);
    private static readonly DateOnly Until = new(2026, 12, 22);
    private static readonly Guid[] Ids = [Guid.NewGuid(), Guid.NewGuid()];

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void CheckerWorksOfSeveralPeople_FilterByIdsWindowTopLevelAndExactToken_InTheDatabase()
    {
        // Act
        var sql = Checker().QueryWorks(Ids, From, Until, Guid.NewGuid()).ToQueryString();

        // Assert
        sql.ShouldContain("FROM work AS w");
        sql.ShouldContain("w.client_id = ANY (@");
        sql.ShouldContain("w.workday >= @");
        sql.ShouldContain("w.workday <= @");
        sql.ShouldContain(NotDeleted);
        sql.ShouldContain("w." + TopLevelOnly);
        sql.ShouldContain("w.analyse_token = @");
    }

    [Test]
    public void CheckerWorksOfTheRealPlan_ReadOnlyRowsWithoutAToken()
    {
        // Act
        var sql = Checker().QueryWorks(Ids, From, Until, analyseToken: null).ToQueryString();

        // Assert
        sql.ShouldContain("w.analyse_token IS NULL");
        sql.ShouldNotContain("w.analyse_token = @");
    }

    [Test]
    public void CheckerBreaksOfSeveralPeople_FilterByIdsWindowTopLevelAndExactToken_InTheDatabase()
    {
        // Act
        var scenarioSql = Checker().QueryBreaks(Ids, From, Until, Guid.NewGuid()).ToQueryString();
        var realPlanSql = Checker().QueryBreaks(Ids, From, Until, analyseToken: null).ToQueryString();

        // Assert
        scenarioSql.ShouldContain("FROM break AS b");
        scenarioSql.ShouldContain("b.client_id = ANY (@");
        scenarioSql.ShouldContain("b.workday >= @");
        scenarioSql.ShouldContain("b.workday <= @");
        scenarioSql.ShouldContain("NOT (b.is_deleted)");
        scenarioSql.ShouldContain("b." + TopLevelOnly);
        scenarioSql.ShouldContain("b.analyse_token = @");
        realPlanSql.ShouldContain("b.analyse_token IS NULL");
    }

    [Test]
    public void CheckerWorkChanges_FilterByWorkIdsAndSoftDelete_InTheDatabase()
    {
        // Act
        var sql = Checker().QueryWorkChanges(Ids).ToQueryString();

        // Assert
        sql.ShouldContain("FROM work_change AS w");
        sql.ShouldContain("w.work_id = ANY (@");
        sql.ShouldContain(NotDeleted);
    }

    [Test]
    public void RestDayRotationWorksOfSeveralPeople_KeepTheSinglePersonFilter_InTheDatabase()
    {
        // Arrange
        var evaluator = new RestDayRotationEvaluator(
            Substitute.For<IRestDayRotationRuleRepository>(),
            _context,
            Substitute.For<IComplianceEnforcementResolver>(),
            Substitute.For<IClientMembershipStartResolver>(),
            Substitute.For<IClientContractDataProvider>());

        // Act
        var scenarioSql = evaluator.QueryWorksOfClients(Ids, From, Until, Guid.NewGuid()).ToQueryString();
        var realPlanSql = evaluator.QueryWorksOfClients(Ids, From, Until, analyseToken: null).ToQueryString();

        // Assert
        scenarioSql.ShouldContain("SELECT w.client_id, w.workday, w.start_time, w.end_time");
        scenarioSql.ShouldContain("w.client_id = ANY (@");
        scenarioSql.ShouldContain("w.workday >= @");
        scenarioSql.ShouldContain("w.workday <= @");
        scenarioSql.ShouldContain(NotDeleted);
        scenarioSql.ShouldContain("w.analyse_token = @");
        scenarioSql.ShouldNotContain(ParentWorkColumn);
        realPlanSql.ShouldContain("w.analyse_token IS NULL");
    }

    [Test]
    public void CounterRuleWorksOfSeveralPeople_KeepTheSinglePersonFilter_InTheDatabase()
    {
        // Arrange
        var evaluator = new CounterRuleEvaluator(
            Substitute.For<ICounterRuleRepository>(),
            _context,
            Substitute.For<IComplianceEnforcementResolver>(),
            Substitute.For<IClientContractDataProvider>());

        // Act
        var scenarioSql = evaluator.QueryWorksOfClients(Ids, From, Until, Guid.NewGuid()).ToQueryString();
        var realPlanSql = evaluator.QueryWorksOfClients(Ids, From, Until, analyseToken: null).ToQueryString();

        // Assert
        scenarioSql.ShouldContain("SELECT w.client_id, w.workday, w.start_time, w.end_time");
        scenarioSql.ShouldContain("w.client_id = ANY (@");
        scenarioSql.ShouldContain("w.workday >= @");
        scenarioSql.ShouldContain("w.workday <= @");
        scenarioSql.ShouldContain(NotDeleted);
        scenarioSql.ShouldContain("w.analyse_token = @");
        scenarioSql.ShouldNotContain(ParentWorkColumn);
        realPlanSql.ShouldContain("w.analyse_token IS NULL");
    }

    [Test]
    public void EmploymentStartsOfSeveralPeople_ReadTheClientWithItsMembership_InTheDatabase()
    {
        // Act
        var sql = new ClientMembershipStartResolver(_context).QueryValidFrom(Ids).ToQueryString();

        // Assert
        sql.ShouldContain("FROM client AS c");
        sql.ShouldContain("LEFT JOIN");
        sql.ShouldContain("membership");
        sql.ShouldContain("valid_from");
        sql.ShouldContain("c.id = ANY (@");
    }

    [Test]
    public void OpenObligationsOfSeveralPeople_FilterOpenAndNotDeleted_InTriggerDateOrder_InTheDatabase()
    {
        // Act
        var sql = new CompensatoryRestObligationRepository(_context).QueryOpenByClients(Ids).ToQueryString();

        // Assert
        sql.ShouldContain("FROM compensatory_rest_obligation AS c");
        sql.ShouldContain("c.client_id = ANY (@");
        sql.ShouldContain("c.fulfilled_on IS NULL");
        sql.ShouldContain("NOT (c.is_deleted)");
        sql.ShouldContain("ORDER BY c.trigger_date");
    }

    private PreCommitConflictChecker Checker()
        => new(
            _context,
            Substitute.For<IShiftRequiredQualificationRepository>(),
            Substitute.For<ITimelineCalculationService>(),
            Substitute.For<ISchedulingPolicyResolver>(),
            Substitute.For<IComplianceEscalationService>(),
            Substitute.For<ISettingsReader>(),
            Substitute.For<IPeriodCapEvaluator>(),
            Substitute.For<IRestDayRotationEvaluator>(),
            Substitute.For<ICounterRuleEvaluator>(),
            Substitute.For<IRestrictedTimeWindowEvaluator>(),
            Substitute.For<ICompensatoryRestEvaluator>(),
            Substitute.For<IHolidayWorkEvaluator>(),
            Substitute.For<IPlanningRuleEvaluatorService>(),
            Substitute.For<IDayDirectiveConflictEvaluator>());
}
