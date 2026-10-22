// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the group-item reads of analyze_group_semantics (member count per group) and remove_shift_from_group
/// (the link row that is deleted) over the real GroupItemRepository (EF InMemory) instead of a stubbed IQueryable:
/// the member count counts real client rows of the group (scenario rows, shift rows, other groups and deleted rows
/// do not count; validity is not checked), and the removal deletes the link of exactly the asked shift and group.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupItemSkillReadsOverRepositoryTests
{
    private DataBaseContext _context = null!;
    private GroupItemRepository _groupItemRepository = null!;

    private readonly Guid _bernId = Guid.NewGuid();
    private readonly Guid _zurichId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _groupItemRepository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task AnalyzeGroupSemantics_CountsRealClientRowsPerGroup_IncludingEndedOnes()
    {
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernId, ValidUntil = past },
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernId, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _bernId, IsDeleted = true },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), GroupId = _bernId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = _zurichId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), GroupId = Guid.NewGuid() });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group>
        {
            new() { Id = _bernId, Name = "Bern" },
            new() { Id = _zurichId, Name = "Zürich" },
            new() { Id = Guid.NewGuid(), Name = "Empty" },
        });
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>()).Returns(new List<Client>());
        var qualificationRepository = Substitute.For<IQualificationRepository>();
        qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification>());
        var contractRepository = Substitute.For<IContractRepository>();
        contractRepository.List().Returns(new List<Contract>());

        var skill = new AnalyzeGroupSemanticsSkill(
            groupRepository,
            TestGroupScopeGuard.Unrestricted(),
            _groupItemRepository,
            clientRepository,
            contractRepository,
            qualificationRepository,
            Mediator());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var counts = ((AnalyzeGroupSemanticsResult)result.Data!).GroupMemberCounts;
        counts.Select(c => (c.GroupName, c.ActiveMemberCount)).ShouldBe([("Bern", 2), ("Zürich", 1), ("Empty", 0)]);
    }

    [Test]
    public async Task RemoveShiftFromGroup_DeletesTheLinkOfTheAskedShiftAndGroup()
    {
        var shiftId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _zurichId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), GroupId = _bernId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _bernId, IsDeleted = true },
            new GroupItem { Id = linkId, ShiftId = shiftId, GroupId = _bernId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        using var api = new FakeSelfApi();
        api.Respond(HttpMethod.Delete, $"api/backend/GroupItems/{linkId}", new GroupItemResource { Id = linkId });
        var skill = RemoveSkill(api, shiftId);

        var result = await skill.ExecuteAsync(Ctx(), RemoveParams(shiftId));

        result.Success.ShouldBeTrue(result.Message);
        api.SingleCall.Method.ShouldBe(HttpMethod.Delete);
    }

    [Test]
    public async Task RemoveShiftFromGroup_WithOnlyADeletedLinkInTheGroup_IsRejectedAndNamesTheOtherGroups()
    {
        var shiftId = Guid.NewGuid();
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _zurichId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _bernId, IsDeleted = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        using var api = new FakeSelfApi();
        var skill = RemoveSkill(api, shiftId);

        var result = await skill.ExecuteAsync(Ctx(), RemoveParams(shiftId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not linked");
        result.Message.ShouldContain("1 other group(s)");
        api.Calls.ShouldBeEmpty();
    }

    private static IMediator Mediator()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StateResource>().AsEnumerable());
        return mediator;
    }

    private RemoveShiftFromGroupSkill RemoveSkill(FakeSelfApi api, Guid shiftId)
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Exists(shiftId).Returns(true);
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.Get(_bernId).Returns(new Group { Id = _bernId, Name = "Bern" });

        return new RemoveShiftFromGroupSkill(
            shiftRepository,
            groupRepository,
            TestGroupScopeGuard.Unrestricted(),
            _groupItemRepository,
            api.Client,
            new SelfApiRouteResolver());
    }

    private Dictionary<string, object> RemoveParams(Guid shiftId) => new()
    {
        ["shiftId"] = shiftId.ToString(),
        ["groupId"] = _bernId.ToString(),
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt"),
    };
}
