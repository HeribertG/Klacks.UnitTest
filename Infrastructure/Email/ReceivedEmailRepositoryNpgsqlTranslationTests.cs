// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the grouped sender queries of ReceivedEmailRepository (counts per sender address of one folder, counts per
/// sender domain over several folders) translate to SQL on the real Npgsql provider. The in-memory provider evaluates any
/// C# expression and cannot reveal an untranslatable call. The repository is run against an unreachable server: query
/// translation happens before a connection is opened, so an untranslatable expression fails with an
/// InvalidOperationException ("could not be translated"), while a translatable one only fails on the connection.
/// </summary>

using Klacks.Api.Infrastructure.Repositories.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class ReceivedEmailRepositoryNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";
    private const string JunkFolder = "INBOX.Junk";
    private const string SecondFolder = "INBOX";
    private const string Domain = "spam.example";
    private const string HiddenAddress = "hidden@kunde.example";
    private const int MaxSenders = 50;

    private DataBaseContext _context = null!;
    private ReceivedEmailRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ReceivedEmailRepository(_context, Substitute.For<ILogger<ReceivedEmailRepository>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetSenderMailCountsAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => _repository.GetSenderMailCountsAsync(JunkFolder, [HiddenAddress], MaxSenders));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    [Test]
    public async Task GetSenderMailCountsAsync_WithoutExcludedSenders_TranslatesToSql()
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => _repository.GetSenderMailCountsAsync(JunkFolder, null, MaxSenders));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    [Test]
    public async Task GetMailCountsBySenderDomainAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => _repository.GetMailCountsBySenderDomainAsync([SecondFolder, "client-assigned"], [Domain, "Other.Example"]));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    [Test]
    public async Task GetMailCountsBySenderDomainAsync_WithoutFoldersOrDomains_ReadsNothing()
    {
        (await _repository.GetMailCountsBySenderDomainAsync([], [Domain])).ShouldBeEmpty();
        (await _repository.GetMailCountsBySenderDomainAsync([SecondFolder], [])).ShouldBeEmpty();
    }

    private static void AssertConnectionFailureNotTranslationFailure(Exception exception)
    {
        var message = exception.ToString();

        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
