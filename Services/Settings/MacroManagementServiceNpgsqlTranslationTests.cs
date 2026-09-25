// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that the origin lookup of MacroManagementService.UpdateMacroAsync (IgnoreQueryFilters plus a
/// projection onto a nullable enum) translates to SQL on the real Npgsql provider; the InMemory provider used by
/// MacroManagementServiceTests would evaluate any expression. The service runs against an unreachable server:
/// translation happens before a connection is opened, so an untranslatable query fails with "could not be
/// translated", a translatable one only with an NpgsqlException.
/// </summary>

using Klacks.Api.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class MacroManagementServiceNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";

    private DataBaseContext _context = null!;
    private MacroManagementService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new MacroManagementService(
            _context, Substitute.For<IMacroCache>(), Substitute.For<ILogger<MacroManagementService>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task UpdateMacroAsync_OriginLookup_TranslatesToSql_FailsOnlyOnConnection()
    {
        var macro = new Macro { Id = Guid.NewGuid(), Name = "Probe", Description = new MultiLanguage() };

        var exception = await Should.ThrowAsync<Exception>(() => _service.UpdateMacroAsync(macro, byAssistant: false));

        var message = exception.ToString();
        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
