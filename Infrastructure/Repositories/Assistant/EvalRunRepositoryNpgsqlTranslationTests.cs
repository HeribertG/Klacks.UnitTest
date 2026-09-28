// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that GetLatestFullRunAsync's model filter translates to SQL on the real Npgsql provider: the
/// method runs against an unreachable server, so an untranslatable expression would fail with "could not be
/// translated" before any connection attempt, while a translatable one fails only on the connection.
/// Verified 2026-09-27: string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase) does NOT translate
/// on this provider (this test failed with exactly that InvalidOperationException before the repository was
/// switched to r.Model.ToLower() == model.ToLower(), the case-insensitive idiom already used everywhere else
/// in this codebase a filter like this has to run on the server).
/// Pattern: MacroAssignmentRepositoriesNpgsqlTranslationTests / EmailClientAssignmentServiceNpgsqlTranslationTests.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class EvalRunRepositoryNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";
    private const string TranslationFailureMarker = "could not be translated";
    private const string Goldset = "turn-selection-v1";
    private const string Model = "deepseek-v4-pro";
    private const int ScorerVersion = 2;

    private DataBaseContext _context = null!;
    private EvalRunRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new EvalRunRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetLatestFullRunAsync_TranslatesToSql() =>
        AssertConnectionFailureOnly(await Should.ThrowAsync<Exception>(
            () => _repository.GetLatestFullRunAsync(Goldset, ScorerVersion, Model)));

    private static void AssertConnectionFailureOnly(Exception exception)
    {
        var message = exception.ToString();

        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
