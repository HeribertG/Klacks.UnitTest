// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the Npgsql translation of the turn-id lookup the correction endpoint uses. The behavioural tests run
/// on the in-memory provider, which would stay green if the owner predicate stopped reaching the SQL; the
/// owner in the WHERE clause is what keeps a stranger's turn from being found by guessing an id. The model
/// is built against Npgsql without opening a connection, because ToQueryString generates the SQL offline.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SkillSelectionTrajectoryTurnLookupSqlTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";

    private static readonly Regex OwnerAndTurnPredicate = new(
        @"(?<alias>\w+)\.user_id = @\w+ AND \k<alias>\.turn_id = @\w+");

    private DataBaseContext _context = null!;
    private SkillSelectionTrajectoryRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new SkillSelectionTrajectoryRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void TheTurnLookup_PutsTheOwnerAndTheTurnIntoTheSqlPredicate()
    {
        var sql = GenerateSql();

        OwnerAndTurnPredicate.IsMatch(sql).ShouldBeTrue(
            $"The owner and the turn id are no longer both in the WHERE clause. Generated SQL: {sql}");
    }

    [Test]
    public void TheTurnLookup_StillCarriesTheSoftDeleteFilter() =>
        GenerateSql().ShouldContain("is_deleted");

    private string GenerateSql() => Regex.Replace(
        _repository.ByUserAndTurnIdQuery("user-1", Guid.NewGuid()).ToQueryString(), WhitespaceRuns, SingleSpace);
}
