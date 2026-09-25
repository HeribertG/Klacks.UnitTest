// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The usage statistics count calls that ran. A UiAction row the stop of its turn cancelled and a call the
/// stop cut short never ran, so they are neither a use nor a failure in the analytics, the success rate, the
/// suggestions and the relation learning that read these methods. A row still Dispatched was dispatched and
/// stays in. The selection is pinned against the Npgsql translation too, because the in-memory provider
/// would stay green if the predicate vanished.
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
public class SkillUsageRepositoryStatisticsTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";
    private const string SkillName = "open_settings";

    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FromUtc = NowUtc.AddDays(-1);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public async Task SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();

        await using var seed = new DataBaseContext(_options, _httpAccessor);
        seed.SkillUsageRecords.AddRange(
            Row(success: true, uiActionStatus: null, failureKind: null),
            Row(success: false, uiActionStatus: null, failureKind: SkillFailureKind.Exception),
            Row(success: true, uiActionStatus: UiActionStatus.Dispatched, failureKind: null),
            Row(success: false, uiActionStatus: UiActionStatus.Cancelled, failureKind: null),
            Row(success: false, uiActionStatus: null, failureKind: SkillFailureKind.Cancelled));
        await seed.SaveChangesAsync();
    }

    [Test]
    public async Task TheRecordReaders_LeaveOutTheRowsTheStopCancelled()
    {
        var repository = CreateRepository();

        (await repository.GetRecordsAsync(FromUtc)).Count.ShouldBe(3);
        (await repository.GetRecordsBySkillAsync(SkillName, FromUtc)).Count.ShouldBe(3);
        (await repository.GetRecordsByUserAsync(User, FromUtc)).Count.ShouldBe(3);
        (await repository.GetTotalExecutionsAsync(FromUtc)).ShouldBe(3);
    }

    [Test]
    public async Task TheSuccessRate_DoesNotCountACancelledRowAsAFailure()
    {
        var rate = await CreateRepository().GetSuccessRateAsync(FromUtc);

        rate.ShouldBe(2m / 3m * 100m);
    }

    [Test]
    public void TheSelection_KeepsTheNullSafeCancelledPredicatesOnTheNpgsqlTranslation()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var sql = Regex.Replace(new SkillUsageRepository(context).RanRows().ToQueryString(), WhitespaceRuns, SingleSpace);

        var uiAction = ((int)UiActionStatus.Cancelled).ToString(CultureInfo.InvariantCulture);
        var failureKind = ((int)SkillFailureKind.Cancelled).ToString(CultureInfo.InvariantCulture);
        Regex.IsMatch(sql, $@"ui_action_status <> {uiAction} OR \w+\.ui_action_status IS NULL").ShouldBeTrue(sql);
        Regex.IsMatch(sql, $@"failure_kind <> {failureKind} OR \w+\.failure_kind IS NULL").ShouldBeTrue(sql);
    }

    private SkillUsageRepository CreateRepository() => new(new DataBaseContext(_options, _httpAccessor));

    private static SkillUsageRecord Row(bool success, UiActionStatus? uiActionStatus, SkillFailureKind? failureKind) => new()
    {
        Id = Guid.NewGuid(),
        SkillName = SkillName,
        Category = SkillCategory.UI,
        UserId = User,
        TenantId = Guid.NewGuid(),
        Success = success,
        UiActionStatus = uiActionStatus,
        FailureKind = failureKind,
        DurationMs = 5,
        Timestamp = NowUtc.AddHours(-1)
    };
}
