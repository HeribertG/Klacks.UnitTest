// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves on the real Npgsql provider that the read behind ListByClientAsync translates and that it filters in the database by
/// the client and by the soft-delete flag. ToQueryString needs no server, so the context points at an unreachable one.
/// </summary>

using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories;

[TestFixture]
public class MembershipRepositoryNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    [Test]
    public void QueryByClient_FiltersByClientAndSoftDeleteInTheDatabase()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        var repository = new MembershipRepository(context, Substitute.For<ILogger<Membership>>());

        var sql = repository.QueryByClient(Guid.NewGuid()).ToQueryString();

        sql.ShouldContain("client_id");
        sql.ShouldContain("is_deleted");
    }
}
