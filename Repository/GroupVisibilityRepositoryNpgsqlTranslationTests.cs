// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that GroupVisibilityRepository.CountNonAdminUsersSeeingGroupAsync translates to SQL on the real Npgsql
/// provider (the in-memory provider used elsewhere evaluates any expression). The query runs against an unreachable
/// server: translation happens before a connection is opened, so an untranslatable expression would fail with
/// "could not be translated", while a translatable one only fails on the connection.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class GroupVisibilityRepositoryNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";

    private DataBaseContext _context = null!;
    private GroupVisibilityRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new GroupVisibilityRepository(
            _context,
            Substitute.For<IGroupVisibilityService>(),
            Substitute.For<ILogger<GroupVisibility>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CountNonAdminUsersSeeingGroupAsync_TranslatesToSql_FailsOnlyOnConnection()
    {
        var exception = await Should.ThrowAsync<Exception>(
            () => _repository.CountNonAdminUsersSeeingGroupAsync(Guid.NewGuid()));

        var message = exception.ToString();
        message.ShouldNotContain(TranslationFailureMarker);
        (exception is NpgsqlException || exception.InnerException is NpgsqlException || message.Contains(nameof(NpgsqlException)))
            .ShouldBeTrue(message);
    }
}