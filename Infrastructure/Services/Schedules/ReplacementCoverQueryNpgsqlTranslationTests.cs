// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the cover read over real works outside a scenario (ReplacementCoverQuery joined with
/// OutsideScenarioWorkQuery.RealWorks), which the wizards run in a scenario. The behavioural tests run on the in-memory
/// provider. EF translates the query before it opens a connection, so against a closed port a translatable query fails only
/// on the connection (an NpgsqlException, which EF may wrap as a transient failure), while a translation failure carries none.
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ReplacementCoverQueryNpgsqlTranslationTests
{
    private const string ClosedPortConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=2";

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ClosedPortConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CoversOnRealWorksOutsideTheScenario_Translate_AndFailOnlyOnTheConnection()
    {
        var token = Guid.NewGuid();

        var exception = await Should.ThrowAsync<Exception>(() => ReplacementCoverQuery.LoadAsync(
            _context,
            OutsideScenarioWorkQuery.RealWorks(_context, token),
            changeToken: null,
            new DateOnly(2026, 3, 9),
            new DateOnly(2026, 3, 15),
            [Guid.NewGuid()],
            CancellationToken.None));

        ExceptionChain(exception).ShouldContain(e => e is NpgsqlException,
            $"only the closed port may stop it, a translation failure carries no NpgsqlException: {exception}");
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
