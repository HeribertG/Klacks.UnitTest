// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that the case-insensitive sender lookups of EmailClientAssignmentService translate to SQL
/// on the real Npgsql provider. The in-memory provider used elsewhere evaluates any C# expression and
/// therefore cannot reveal an untranslatable call such as string.ToLowerInvariant(). The service is run
/// against an unreachable server: query translation happens before a connection is opened, so an
/// untranslatable expression fails with an InvalidOperationException ("could not be translated"),
/// while a translatable one only fails when the connection cannot be established (an NpgsqlException,
/// possibly wrapped by EF's transient-failure InvalidOperationException).
/// </summary>

using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailClientAssignmentServiceNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string MixedCaseAddress = "Max.Muster@Example.COM";
    private const string TranslationFailureMarker = "could not be translated";

    private DataBaseContext _context = null!;
    private EmailClientAssignmentService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new EmailClientAssignmentService(
            _context,
            Substitute.For<IEmailFolderRepository>(),
            Substitute.For<ILogger<EmailClientAssignmentService>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ResolveClientAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var email = new ReceivedEmail { FromAddress = MixedCaseAddress };

        var exception = await Should.ThrowAsync<Exception>(() => _service.ResolveClientAsync(email));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    [Test]
    public async Task GetStoredAddressAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => _service.GetStoredAddressAsync(Guid.NewGuid(), MixedCaseAddress));

        AssertConnectionFailureNotTranslationFailure(exception);
    }

    private static void AssertConnectionFailureNotTranslationFailure(Exception exception)
    {
        var message = exception.ToString();

        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
