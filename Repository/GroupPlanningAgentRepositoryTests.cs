// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Planning agents of a selected group must be the clients the schedule shows for it: members of the group
/// and all its descendants, no customers, membership overlapping the period, no scenario memberships.
/// Regression for "No agents resolved for group 'Winterthur'": the skills used a root-keyed query, so any
/// sub-group resolved to zero agents. The subtree expansion itself (a recursive SQL CTE) is covered against
/// Postgres in Klacks.IntegrationTest; here it is modelled from an in-test parent map.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class GroupPlanningAgentRepositoryTests
{
    private static readonly DateOnly PeriodFrom = new(2026, 10, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 10, 31);

    private readonly Guid _root = Guid.NewGuid();
    private readonly Guid _canton = Guid.NewGuid();
    private readonly Guid _zurich = Guid.NewGuid();
    private readonly Guid _winterthur = Guid.NewGuid();
    private readonly Guid _sibling = Guid.NewGuid();
    private readonly Guid _emptyLeaf = Guid.NewGuid();

    private Dictionary<Guid, Guid?> _parentOf = null!;
    private DataBaseContext _context = null!;
    private IGroupVisibilityService _visibility = null!;
    private IUserService _user = null!;
    private GroupPlanningAgentRepository _repository = null!;

    private Guid _winterthurEmployee;
    private Guid _winterthurExtern;
    private Guid _zurichEmployee;
    private Guid _cantonEmployee;
    private Guid _siblingEmployee;

    [SetUp]
    public async Task SetUp()
    {
        _parentOf = new Dictionary<Guid, Guid?>
        {
            [_root] = null,
            [_canton] = _root,
            [_zurich] = _canton,
            [_winterthur] = _canton,
            [_emptyLeaf] = _canton,
            [_sibling] = _root
        };

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        groupClient.GetAllGroupIdsIncludingSubgroups(Arg.Any<Guid>())
            .Returns(call => Task.FromResult(Subtree(call.Arg<Guid>())));

        _visibility = Substitute.For<IGroupVisibilityService>();
        _visibility.GetVisibilityScopeAsync().Returns(Task.FromResult(GroupVisibilityScope.Unrestricted()));
        _user = Substitute.For<IUserService>();
        _user.GetIdString().Returns(string.Empty);

        var filter = new ClientGroupFilterService(
            groupClient, _visibility, _user, Substitute.For<ILogger<ClientGroupFilterService>>());
        _repository = new GroupPlanningAgentRepository(_context, filter);

        await SeedAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task SubGroupLeaf_ResolvesItsOwnMembers()
    {
        var ids = await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil);

        ids.ShouldBe(new[] { _winterthurEmployee, _winterthurExtern }, ignoreOrder: true);
    }

    [Test]
    public async Task InnerGroup_IncludesAllDescendants_ButNotSiblings()
    {
        var ids = await _repository.GetAgentIdsAsync(_canton, PeriodFrom, PeriodUntil);

        ids.ShouldBe(
            new[] { _winterthurEmployee, _winterthurExtern, _zurichEmployee, _cantonEmployee },
            ignoreOrder: true);
    }

    [Test]
    public async Task Root_ResolvesTheWholeTree()
    {
        var ids = await _repository.GetAgentIdsAsync(_root, PeriodFrom, PeriodUntil);

        ids.ShouldBe(
            new[] { _winterthurEmployee, _winterthurExtern, _zurichEmployee, _cantonEmployee, _siblingEmployee },
            ignoreOrder: true);
    }

    [Test]
    public async Task GroupWithoutMembers_ResolvesEmpty()
    {
        var ids = await _repository.GetAgentIdsAsync(_emptyLeaf, PeriodFrom, PeriodUntil);

        ids.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownGroup_ResolvesEmpty()
    {
        var ids = await _repository.GetAgentIdsAsync(Guid.NewGuid(), PeriodFrom, PeriodUntil);

        ids.ShouldBeEmpty();
    }

    [Test]
    public async Task MembershipOutsidePeriod_IsExcluded_ButCountsForAnOverlappingPeriod()
    {
        var earlier = await _repository.GetAgentIdsAsync(
            _winterthur, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

        earlier.ShouldContain(ExpiredMemberId);
        (await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil)).ShouldNotContain(ExpiredMemberId);
    }

    [Test]
    public async Task MembershipEndingExactlyAtPeriodStart_IsIncluded()
    {
        var memberId = await AddMemberAndSaveAsync(
            membershipUntil: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil);

        ids.ShouldContain(memberId);
    }

    [Test]
    public async Task MembershipEndingOneDayBeforePeriodStart_IsExcluded()
    {
        var memberId = await AddMemberAndSaveAsync(
            membershipUntil: new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil);

        ids.ShouldNotContain(memberId);
    }

    [Test]
    public async Task MembershipStartingOnTheLastDayOfThePeriod_IsIncluded()
    {
        var atMidnight = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc));
        var lateInDay = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil);

        ids.ShouldContain(atMidnight);
        ids.ShouldContain(lateInDay);
    }

    [Test]
    public async Task MembershipStartingTheDayAfterThePeriod_IsExcluded()
    {
        var memberId = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_winterthur, PeriodFrom, PeriodUntil);

        ids.ShouldNotContain(memberId);
    }

    [Test]
    public async Task RestrictedCaller_OnlyGetsVisibleDescendants()
    {
        _user.GetIdString().Returns("planner");
        _visibility.GetVisibilityScopeAsync().Returns(Task.FromResult(
            GroupVisibilityScope.Restricted([_root], [_winterthur])));

        var ids = await _repository.GetAgentIdsAsync(_canton, PeriodFrom, PeriodUntil);

        ids.ShouldBe(new[] { _winterthurEmployee, _winterthurExtern }, ignoreOrder: true);
    }

    private Guid ExpiredMemberId { get; set; }

    private HashSet<Guid> Subtree(Guid groupId)
    {
        if (!_parentOf.ContainsKey(groupId))
        {
            return [];
        }

        var result = new HashSet<Guid> { groupId };
        bool added;
        do
        {
            added = false;
            foreach (var (child, parent) in _parentOf)
            {
                if (parent.HasValue && result.Contains(parent.Value) && result.Add(child))
                {
                    added = true;
                }
            }
        }
        while (added);

        return result;
    }

    private async Task SeedAsync()
    {
        _winterthurEmployee = AddClient(_winterthur, EntityTypeEnum.Employee);
        _winterthurExtern = AddClient(_winterthur, EntityTypeEnum.ExternEmp);
        _zurichEmployee = AddClient(_zurich, EntityTypeEnum.Employee);
        _cantonEmployee = AddClient(_canton, EntityTypeEnum.Employee);
        _siblingEmployee = AddClient(_sibling, EntityTypeEnum.Employee);

        AddClient(_winterthur, EntityTypeEnum.Customer);
        AddClient(_winterthur, EntityTypeEnum.Employee, withMembership: false);
        AddClient(_winterthur, EntityTypeEnum.Employee, scenarioToken: Guid.NewGuid());
        ExpiredMemberId = AddClient(
            _winterthur, EntityTypeEnum.Employee, membershipUntil: new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        await _context.SaveChangesAsync();
    }

    private async Task<Guid> AddMemberAndSaveAsync(DateTime? membershipFrom = null, DateTime? membershipUntil = null)
    {
        var clientId = AddClient(
            _winterthur, EntityTypeEnum.Employee, membershipFrom: membershipFrom, membershipUntil: membershipUntil);
        await _context.SaveChangesAsync();

        return clientId;
    }

    private Guid AddClient(
        Guid groupId,
        EntityTypeEnum type,
        bool withMembership = true,
        Guid? scenarioToken = null,
        DateTime? membershipFrom = null,
        DateTime? membershipUntil = null)
    {
        var clientId = Guid.NewGuid();
        var client = new Client { Id = clientId, Name = "Agent " + clientId, Type = type };
        if (withMembership)
        {
            client.Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ValidFrom = membershipFrom ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = membershipUntil
            };
        }

        _context.Client.Add(client);
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            AnalyseToken = scenarioToken
        });

        return clientId;
    }
}
