// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that every query of the macro dry-run translates to SQL on the real Npgsql provider (the in-memory provider
/// used elsewhere evaluates any C# expression and cannot reveal an untranslatable one), including the filter over a list
/// of holder ids that a cut group needs, passed as the same runtime type the service builds. ToQueryString generates the
/// SQL without opening a connection, so the unreachable server is never contacted.
/// </summary>

using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class MacroDryRunQueriesNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";
    private const string SelectKeyword = "SELECT";

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
    public void EveryDryRunQuery_TranslatesToSql()
    {
        IReadOnlyCollection<Guid> holderIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        var statements = new[]
        {
            MacroDryRunQueries.WorksOfShifts(_context, holderIds).ToQueryString(),
            MacroDryRunQueries.SealedWorksOfShifts(_context, holderIds).ToQueryString(),
            MacroDryRunQueries.OpenWorkSamples(_context, holderIds, MacroDryRunService.SampleSize).ToQueryString(),
            MacroDryRunQueries.BreaksOfAbsenceTypes(_context, holderIds).ToQueryString(),
            MacroDryRunQueries.SealedBreaksOfAbsenceTypes(_context, holderIds).ToQueryString(),
            MacroDryRunQueries.OpenBreakSamples(_context, holderIds, MacroDryRunService.SampleSize).ToQueryString(),
            MacroDryRunQueries.MacroContent(_context, Guid.NewGuid()).ToQueryString()
        };

        statements.ShouldAllBe(sql => sql.Contains(SelectKeyword));
    }
}
