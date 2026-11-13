// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves on the real Npgsql provider that the queries of the temporary membership per day translate (the in-memory
/// provider used elsewhere evaluates any expression): the three named predicates of RegularMembership as filters, the
/// three filtered Includes built from an expression tree, the period predicate inside a correlated subquery, and the
/// two reads that ask whether a person holds a cover on one day. ToQueryString needs no server, so the context points
/// at an unreachable one and no connection is opened. The queries that only exist behind an awaited call (the day rows
/// of the scenario stager, the accept step, the subtree read of GroupClientService) run against PostgreSQL in
/// TemporaryMembershipPerDayTests.
/// </summary>

using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Associations;

[TestFixture]
public class TemporaryMembershipQueriesNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string NoToken = "analyse_token IS NULL";
    private const string NotTemporary = "NOT (g.is_temporary)";
    private const string TemporaryColumn = "is_temporary";

    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateOnly Friday = new(2026, 6, 5);
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

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
    public void IsRegular_FiltersOnTheTokenAndOnTheTemporaryFlag()
    {
        // Act
        var sql = _context.GroupItem.Where(RegularMembership.IsRegular).ToQueryString();

        // Assert
        sql.ShouldContain(NoToken);
        sql.ShouldContain(NotTemporary);
    }

    [Test]
    public void OrTemporary_FiltersOnTheTokenOnly()
    {
        // Act
        var sql = _context.GroupItem.Where(RegularMembership.OrTemporary).ToQueryString();

        // Assert
        sql.ShouldContain(NoToken);
        sql.ShouldNotContain(NotTemporary);
    }

    [Test]
    public void OrTemporaryWithin_BoundsOnlyTheTemporaryRowsByThePeriod()
    {
        // Act
        var sql = _context.GroupItem.Where(RegularMembership.OrTemporaryWithin(Monday, Friday)).ToQueryString();

        // Assert
        sql.ShouldContain(NoToken);
        sql.ShouldContain(NotTemporary);
        sql.ShouldContain("g.valid_from <= @");
        sql.ShouldContain("g.valid_until >= @");
    }

    [Test]
    public void OrTemporaryWithin_TranslatesInsideACorrelatedSubquery()
    {
        // Arrange: the shape of the period-closing note reader.
        var memberOfThePeriod = RegularMembership.OrTemporaryWithin(Monday, Friday);

        // Act
        var sql = _context.Client
            .Where(client => _context.GroupItem
                .Where(memberOfThePeriod)
                .Any(gi => !gi.IsDeleted && gi.ClientId == client.Id && gi.GroupId == GroupId))
            .ToQueryString();

        // Assert
        sql.ShouldContain("EXISTS");
        sql.ShouldContain(NoToken);
        sql.ShouldContain("valid_from <= @");
    }

    [Test]
    public void ClientHasOne_FiltersClientsByARegularMembershipInTheGroups()
    {
        // Arrange: the shape of the planning-agent read.
        var groupIds = new HashSet<Guid> { GroupId };

        // Act
        var sql = _context.Client
            .Where(RegularMembership.ClientHasOne(item => groupIds.Contains(item.GroupId)))
            .ToQueryString();

        // Assert
        sql.ShouldContain("EXISTS");
        sql.ShouldContain(NoToken);
        sql.ShouldContain(TemporaryColumn);
        sql.ShouldContain("group_id");
    }

    [Test]
    public void OfClient_IncludesTheRegularMembershipsWithTheirGroup()
    {
        // Act
        var sql = _context.Client
            .Include(RegularMembership.OfClient)
            .ThenInclude(gi => gi.Group)
            .Where(client => client.Id == ClientId)
            .ToQueryString();

        // Assert
        sql.ShouldContain("group_item");
        sql.ShouldContain(NoToken);
        sql.ShouldContain(TemporaryColumn);
    }

    [Test]
    public void ItemsOfGroup_IncludesEveryItemThatIsNeitherScenarioNorTemporary()
    {
        // Act
        var sql = _context.Group
            .Include(RegularMembership.ItemsOfGroup)
            .ThenInclude(gi => gi.Client)
            .Where(group => group.Id == GroupId)
            .ToQueryString();

        // Assert
        sql.ShouldContain("group_item");
        sql.ShouldContain(NoToken);
        sql.ShouldContain(TemporaryColumn);
        sql.ShouldNotContain("shift_id IS NULL");
    }

    [Test]
    public void MembersOfGroup_IncludesTheRegularClientRows_WithoutTheShiftLinks()
    {
        // Act
        var sql = _context.Group
            .Include(RegularMembership.MembersOfGroup)
            .ThenInclude(gi => gi.Client)
            .Where(group => group.Id == GroupId)
            .ToQueryString();

        // Assert
        sql.ShouldContain(NoToken);
        sql.ShouldContain(TemporaryColumn);
        sql.ShouldContain("client_id IS NOT NULL");
        sql.ShouldContain("shift_id IS NULL");
    }

    [Test]
    public void StandInsOnADay_FilterByTokenStandInAndWorkday()
    {
        // Arrange
        var repository = new WorkChangeRepository(
            _context, Substitute.For<ILogger<WorkChange>>(), Substitute.For<IWorkMacroService>());

        // Act
        var sql = repository.QueryStandInsOnDay(Token, ClientId, Monday).ToQueryString();

        // Assert
        sql.ShouldContain("analyse_token = @");
        sql.ShouldContain("replace_client_id = @");
        sql.ShouldContain("workday = @");
    }

    [Test]
    public void MovedWorksHeldOnADay_FilterByTokenOwnerOriginAndWorkday()
    {
        // Arrange
        var repository = new WorkRepository(
            _context, Substitute.For<ILogger<Work>>(), Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(), Substitute.For<IClientContractDataProvider>());

        // Act
        var sql = repository.QueryMovedWorksHeldOnDay(Token, ClientId, Monday).ToQueryString();

        // Assert
        sql.ShouldContain("analyse_token = @");
        sql.ShouldContain("client_id = @");
        sql.ShouldContain("moved_from_client_id IS NOT NULL");
        sql.ShouldContain("workday = @");
    }
}
