// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the row selection of the stopped-turn cleanup against the Npgsql translation: the turn and the
/// Dispatched predicate must stay in the SQL, because the in-memory provider would stay green if either
/// vanished. The behaviour itself (which rows become Cancelled, that nothing else is written) runs through
/// ExecuteUpdateAsync, which the in-memory provider does not support; it is covered against real Postgres in
/// Klacks.IntegrationTest (StoppedTurnCleanupPostgresTests).
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
public class SkillUsageRepositoryCancelDispatchedTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string UsageTable = "skill_usage_records";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";

    [Test]
    public void TheRowSelection_KeepsTheTurnAndTheDispatchedPredicateOnTheNpgsqlTranslation()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var sql = Regex.Replace(
            new SkillUsageRepository(context).DispatchedRowsOfTurn(Guid.NewGuid()).ToQueryString(),
            WhitespaceRuns,
            SingleSpace);

        sql.ShouldContain($"FROM {UsageTable}");
        sql.ShouldContain("turn_id =");
        sql.ShouldContain($"ui_action_status = {((int)UiActionStatus.Dispatched).ToString(CultureInfo.InvariantCulture)}");
    }
}
