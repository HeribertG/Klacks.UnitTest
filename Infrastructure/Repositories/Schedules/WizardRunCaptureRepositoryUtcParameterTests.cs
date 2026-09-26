// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression guard for the group-scoped seal sweep of WizardRunCaptureRepository: every DateTime bound to a
/// "timestamp with time zone" parameter must carry Kind=Utc, because Npgsql rejects any other Kind at execution
/// time ("Cannot write DateTime with Kind=Unspecified ..."). Live 2026-09-26 this failed after every group seal,
/// since the group-membership window was built with DateOnly.ToDateTime(TimeOnly) (Kind=Unspecified). The
/// in-memory provider cannot see this, so the queries run against the real Npgsql provider with an interceptor
/// that suppresses opening the connection and executing the command: the parameters EF binds are inspected
/// exactly as Npgsql would receive them, without a database.
/// </summary>

using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NpgsqlTypes;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WizardRunCaptureRepositoryUtcParameterTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2";

    private ParameterCapturingInterceptor _interceptor = null!;
    private DataBaseContext _context = null!;
    private WizardRunCaptureRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _interceptor = new ParameterCapturingInterceptor();
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(_interceptor)
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new WizardRunCaptureRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetUnmeasuredForSealAsync_GroupScoped_BindsEveryTimestampTzParameterAsUtc()
    {
        await _sut.GetUnmeasuredForSealAsync(new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 20), Guid.NewGuid());

        var timestampTz = _interceptor.TimestampTzParameters;
        timestampTz.ShouldNotBeEmpty(
            "The group-membership window must be bound as timestamptz parameters; an empty capture would make " +
            "this guard vacuous.");
        timestampTz.ShouldAllBe(parameter => ((DateTime)parameter.Value!).Kind == DateTimeKind.Utc);
    }

    [Test]
    public async Task GetUnmeasuredForSealAsync_GroupScoped_WindowCoversTheWholeSealPeriod()
    {
        await _sut.GetUnmeasuredForSealAsync(new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 20), Guid.NewGuid());

        var values = _interceptor.TimestampTzParameters.Select(parameter => (DateTime)parameter.Value!).ToList();
        values.ShouldContain(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));
        values.ShouldContain(value => value.Date == new DateTime(2026, 9, 20) && value.TimeOfDay > TimeSpan.FromHours(23));
    }

    /// <summary>
    /// Suppresses the connection open and every reader execution (answered with an empty reader) and records
    /// the parameters EF bound to each suppressed command.
    /// </summary>
    private sealed class ParameterCapturingInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        private readonly List<NpgsqlParameter> _parameters = new();

        public IReadOnlyList<NpgsqlParameter> TimestampTzParameters =>
            _parameters.Where(parameter => parameter.NpgsqlDbType == NpgsqlDbType.TimestampTz).ToList();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _parameters.AddRange(command.Parameters.OfType<NpgsqlParameter>());
            return ValueTask.FromResult(
                InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));
        }
    }
}
