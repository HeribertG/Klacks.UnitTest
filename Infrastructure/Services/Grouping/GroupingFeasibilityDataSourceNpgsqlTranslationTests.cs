// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that every query of GroupingFeasibilityDataSource translates to SQL on the real Npgsql
/// provider. ToQueryString translates without opening a connection, so each query is checked on its
/// own; the end-to-end calls run against an unreachable server and must fail on the connection, never
/// with "could not be translated". Pattern: EmailClientAssignmentServiceNpgsqlTranslationTests.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Grouping;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Grouping;

[TestFixture]
public class GroupingFeasibilityDataSourceNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";

    private static readonly DateOnly From = new(2026, 9, 28);
    private static readonly DateOnly Until = new(2026, 11, 23);
    private static readonly DateOnly Today = new(2026, 9, 27);
    private static readonly List<Guid> Ids = [Guid.NewGuid(), Guid.NewGuid()];

    private DataBaseContext _context = null!;
    private GroupingFeasibilityDataSource _dataSource = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dataSource = new GroupingFeasibilityDataSource(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    [Test]
    public void EveryQuery_TranslatesToSql()
    {
        _dataSource.GroupsQuery().ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.MembershipsQuery().ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.ClientsQuery(Utc(From), Utc(Until)).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.AddressesQuery(Ids, Utc(Today)).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.ShiftsQuery(From, Until).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.RequirementsQuery(Ids).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.QualificationsQuery(Ids).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.BlacklistQuery(Ids).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.AvailabilityQuery(Ids, From, Until).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.FutureWorksQuery(Ids, Today).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.ActiveClientQuery(Utc(Today)).ToQueryString().ShouldNotBeNullOrWhiteSpace();
        _dataSource.GroupedPlannableShiftQuery(Today).ToQueryString().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void ShiftsQuery_ProjectsTheOriginalAndTheCustomerOfTheShift()
    {
        var sql = _dataSource.ShiftsQuery(From, Until).ToQueryString();

        sql.ShouldContain("original_id");
        sql.ShouldContain("client_id");
    }

    [Test]
    public async Task LoadAsync_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(() => _dataSource.LoadAsync(From, Until, Today, CancellationToken.None));

        AssertConnectionFailure(exception);
    }

    [Test]
    public async Task HasAnalysableDataAsync_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(() => _dataSource.HasAnalysableDataAsync(Today, CancellationToken.None));

        AssertConnectionFailure(exception);
    }

    private static void AssertConnectionFailure(Exception exception)
    {
        var message = exception.ToString();
        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
