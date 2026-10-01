// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the duplicate lookups of the employee import. The evaluator tests
/// substitute the repository, and the in-memory provider would accept LINQ that Npgsql cannot translate
/// (ToLowerInvariant broke inbound e-mail once). The model is built against Npgsql without opening a
/// connection; ToQueryString generates the SQL offline.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportLookupRepositorySqlTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string CollectionMatch = @"= ANY \(| IN \(";

    private DataBaseContext _context = null!;
    private ClientImportLookupRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ClientImportLookupRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void MailLookup_TranslatesToLowerAndAnArrayMatch()
    {
        var sql = Normalize(_repository.ClientIdsByMailQuery(["anna@example.com"]).ToQueryString());

        sql.ShouldContain("FROM communication");
        sql.ShouldContain("lower(");
        Regex.IsMatch(sql, CollectionMatch).ShouldBeTrue(sql);
        sql.ShouldContain("is_deleted");
    }

    [Test]
    public void CandidateLookup_TranslatesNamesIdsAndTheMailProjection()
    {
        var sql = Normalize(_repository.CandidatesQuery(["muster"], [Guid.NewGuid()]).ToQueryString());

        sql.ShouldContain("FROM client");
        sql.ShouldContain("lower(");
        Regex.IsMatch(sql, CollectionMatch).ShouldBeTrue(sql);
        sql.ShouldContain("LEFT JOIN");
        sql.ShouldContain("communication");
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ");
}
