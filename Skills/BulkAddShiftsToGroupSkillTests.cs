// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BulkAddShiftsToGroupSkill: a preview (apply=false) never persists, an apply writes the
/// new links inside a transaction and re-reads them to confirm (reporting added/verified), a failed
/// re-read rolls the whole batch back, and an unknown group name is rejected with the real group list.
/// </summary>

using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BulkAddShiftsToGroupSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private FakeSelfApi _api = null!;
    private IMediator _mediator = null!;
    private BulkAddShiftsToGroupSkill _skill = null!;

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid Shift1 = Guid.NewGuid();
    private static readonly Guid Shift2 = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, "api/backend/GroupItems/bulk", new BulkGroupItemResponse());
        _mediator = Substitute.For<IMediator>();
        _skill = new BulkAddShiftsToGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _mediator);

        _groupRepository.List().Returns(new List<Group> { new() { Id = BernGroupId, Name = "Bern" } });

        _mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource
            {
                Shifts = new List<ShiftResource>
                {
                    new() { Id = Shift1, Name = "Nachtwache A" },
                    new() { Id = Shift2, Name = "Nachtwache B" }
                }
            });

        _groupItemRepository.GetShiftIdsByGroupIds(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params(bool apply) => new()
    {
        ["groupName"] = "Bern",
        ["searchTerm"] = "Nachtwache",
        ["apply"] = apply
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task Preview_DoesNotPersist_AndListsMatches()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: false));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Apply_SendsEveryMatchInOneBulkRequest()
    {
        _api.Respond(HttpMethod.Post, "api/backend/GroupItems/bulk", new BulkGroupItemResponse { AddedCount = 2 });

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.True, result.Message);
        // One request, not one per shift: that is what keeps the batch atomic on the server.
        _api.Calls.Count.ShouldBe(1);
        _api.SingleCall.Route.ShouldBe("api/backend/GroupItems/bulk");
        _api.BodyOf<BulkGroupItemRequest>()!.Items.Count.ShouldBe(2);
    }

    [Test]
    public async Task Apply_RelaysTheRollback_WhenTheEndpointCannotConfirmTheBatch()
    {
        // The rollback now happens server-side; the skill's job is to relay why, not to redo it.
        _api.RespondWithProblem(
            HttpMethod.Post, "api/backend/GroupItems/bulk", System.Net.HttpStatusCode.BadRequest,
            "Expected 2 new group links but only 1 were confirmed; the batch was rolled back.");

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
    }

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenGroupNameIsUnknown()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Zürich", ["searchTerm"] = "Nachtwache" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        Assert.That(result.Message, Does.Contain("Bern"));
    }
}
