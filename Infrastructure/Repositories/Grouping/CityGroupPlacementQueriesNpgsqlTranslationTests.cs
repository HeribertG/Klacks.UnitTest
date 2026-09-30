// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the two queries behind assign_shifts_to_city_groups translate to SQL on the real Npgsql
/// provider: the per-city address centroid (GroupBy on a trimmed, lower-cased city with Average/Count
/// projected into a record) and the shift load (Includes with a split query and a customer-name filter).
/// The repositories run against an unreachable server: translation happens before a connection is
/// opened, so an untranslatable expression fails with "could not be translated", while a translatable
/// one only fails on the connection (an NpgsqlException).
/// </summary>

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Repositories.Grouping;

[TestFixture]
public class CityGroupPlacementQueriesNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";

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
    public async Task GetCityCentroidsAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var repository = new AddressRepository(_context, Substitute.For<ILogger<Address>>());

        var exception = await Should.ThrowAsync<Exception>(() => repository.GetCityCentroidsAsync());

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    [Test]
    public async Task GetShiftsForCityGroupPlacementAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var repository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            null!,
            null!,
            null!,
            null!,
            null!,
            Substitute.For<ICompanyClock>());

        var exception = await Should.ThrowAsync<Exception>(
            () => repository.GetShiftsForCityGroupPlacementAsync("Muster"));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    private static void AssertConnectionFailureNotTranslationFailure(Exception exception)
    {
        var message = exception.ToString();

        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
