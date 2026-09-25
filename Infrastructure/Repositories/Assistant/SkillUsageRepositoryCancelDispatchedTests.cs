// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Closing the UiAction rows of a stopped turn: only the rows of that turn still in Dispatched state become
/// Cancelled and unsuccessful; rows of other turns, rows that already carry an outcome and rows that are no
/// UiAction at all stay as they are. The query that picks the rows is also pinned against the Npgsql
/// translation, because the in-memory provider would stay green if the turn or status predicate vanished.
/// </summary>

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
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

    private static readonly DateTime SeededUtc = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    [Test]
    public async Task TheDispatchedRowsOfTheTurn_BecomeCancelledAndUnsuccessful()
    {
        var turnId = Guid.NewGuid();
        var first = await SeedAsync(turnId, UiActionStatus.Dispatched, success: true);
        var second = await SeedAsync(turnId, UiActionStatus.Dispatched, success: true);

        var closed = await CreateRepository().CancelDispatchedForTurnAsync(turnId);

        closed.ShouldBe(2);
        foreach (var id in new[] { first, second })
        {
            var row = await ReadAsync(id);
            row.UiActionStatus.ShouldBe(UiActionStatus.Cancelled);
            row.Success.ShouldBeFalse();
        }
    }

    [Test]
    public async Task ARowOfAnotherTurn_IsNotTouched()
    {
        var stopped = Guid.NewGuid();
        var other = await SeedAsync(Guid.NewGuid(), UiActionStatus.Dispatched, success: true);
        await SeedAsync(stopped, UiActionStatus.Dispatched, success: true);

        await CreateRepository().CancelDispatchedForTurnAsync(stopped);

        var row = await ReadAsync(other);
        row.UiActionStatus.ShouldBe(UiActionStatus.Dispatched);
        row.Success.ShouldBeTrue();
    }

    [TestCase(UiActionStatus.Completed, true)]
    [TestCase(UiActionStatus.Failed, false)]
    public async Task ARowThatAlreadyCarriesAnOutcome_KeepsIt(UiActionStatus outcome, bool success)
    {
        var turnId = Guid.NewGuid();
        var id = await SeedAsync(turnId, outcome, success);

        var closed = await CreateRepository().CancelDispatchedForTurnAsync(turnId);

        closed.ShouldBe(0);
        var row = await ReadAsync(id);
        row.UiActionStatus.ShouldBe(outcome);
        row.Success.ShouldBe(success);
    }

    [Test]
    public async Task ARowThatIsNoUiAction_IsNotTouched()
    {
        var turnId = Guid.NewGuid();
        var id = await SeedAsync(turnId, uiActionStatus: null, success: true);

        var closed = await CreateRepository().CancelDispatchedForTurnAsync(turnId);

        closed.ShouldBe(0);
        (await ReadAsync(id)).Success.ShouldBeTrue();
    }

    [Test]
    public async Task ATurnWithoutRows_ClosesNothing()
    {
        (await CreateRepository().CancelDispatchedForTurnAsync(Guid.NewGuid())).ShouldBe(0);
    }

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

    private SkillUsageRepository CreateRepository() => new(new DataBaseContext(_options, _httpAccessor));

    private async Task<Guid> SeedAsync(Guid turnId, UiActionStatus? uiActionStatus, bool success)
    {
        await using var seed = new DataBaseContext(_options, _httpAccessor);
        var record = new SkillUsageRecord
        {
            Id = Guid.NewGuid(),
            SkillName = "open_settings",
            Category = SkillCategory.UI,
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            TurnId = turnId,
            Success = success,
            UiActionStatus = uiActionStatus,
            DurationMs = 5,
            Timestamp = SeededUtc
        };
        seed.SkillUsageRecords.Add(record);
        await seed.SaveChangesAsync();
        return record.Id;
    }

    private async Task<SkillUsageRecord> ReadAsync(Guid id)
    {
        await using var read = new DataBaseContext(_options, _httpAccessor);
        return await read.SkillUsageRecords.AsNoTracking().SingleAsync(r => r.Id == id);
    }
}
