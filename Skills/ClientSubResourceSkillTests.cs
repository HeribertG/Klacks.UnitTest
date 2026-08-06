// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the client-family skills that append or remove one sub-resource row. They share one contract
/// worth pinning: the write goes to the endpoint that owns that row — communications, annotations,
/// group items — and never to the client endpoint. Routing them through the client would mean reading
/// the whole client and writing it back, which opens a lost-update window and runs the full
/// client-update machinery (including an IMAP re-assignment sweep) for a single row.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClientSubResourceSkillTests
{
    private const string CommunicationsRoute = "api/backend/Communications";
    private const string AnnotationsRoute = "api/backend/Annotations";
    private const string GroupItemsRoute = "api/backend/GroupItems";

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();

    private FakeSelfApi _api = null!;
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IGroupRepository _groupRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new FakeSelfApi();
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();

        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = ClientId, FirstName = "Victor", LastName = "Frey" }],
                TotalCount = 1
            });
        _clientRepository.Get(ClientId).Returns(BuildClient());
        _groupRepository.List().Returns(new List<Group> { new() { Id = GroupId, Name = "Verkauf" } });
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static Client BuildClient() => new()
    {
        Id = ClientId,
        FirstName = "Victor",
        Name = "Frey",
        GroupItems = [new GroupItem { Id = MembershipId, ClientId = ClientId, GroupId = GroupId }]
    };

    private static SkillExecutionContext Ctx(string? token = "caller-jwt") => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = token is null ? null : new BearerToken(token)
    };

    private AddClientPhoneSkill PhoneSkill() =>
        new(_clientRepository, _searchRepository, _api.Client, new SelfApiRouteResolver());

    private AddClientNoteSkill NoteSkill() =>
        new(_clientRepository, _searchRepository, _api.Client, new SelfApiRouteResolver());

    private RemoveClientFromGroupSkill RemoveFromGroupSkill() =>
        new(_clientRepository, _searchRepository, _groupRepository, TestGroupScopeGuard.Unrestricted(),
            _api.Client, new SelfApiRouteResolver());

    [Test]
    public async Task AddPhone_PostsOneCommunication()
    {
        _api.Respond(HttpMethod.Post, CommunicationsRoute, new CommunicationResource
        {
            Id = Guid.NewGuid(), ClientId = ClientId, Type = CommunicationTypeEnum.PrivateCellPhone, Value = "079 111 22 33"
        });

        var result = await PhoneSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["phone"] = "079 111 22 33"
        });

        result.Success.ShouldBeTrue();
        _api.SingleCall.Route.ShouldBe(CommunicationsRoute);
        _api.SingleCall.SkillName.ShouldBe("add_client_phone");

        var sent = _api.BodyOf<CommunicationResource>();
        sent!.ClientId.ShouldBe(ClientId);
        sent.Type.ShouldBe(CommunicationTypeEnum.PrivateCellPhone);
        sent.Value.ShouldBe("079 111 22 33");
    }

    [Test]
    public async Task AddNote_PostsOneAnnotation()
    {
        _api.Respond(HttpMethod.Post, AnnotationsRoute, new AnnotationResource
        {
            Id = Guid.NewGuid(), ClientId = ClientId, Note = "Prefers morning shifts"
        });

        var result = await NoteSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["note"] = "Prefers morning shifts"
        });

        result.Success.ShouldBeTrue();
        _api.SingleCall.Route.ShouldBe(AnnotationsRoute);
        _api.SingleCall.SkillName.ShouldBe("add_client_note");
        _api.BodyOf<AnnotationResource>()!.Note.ShouldBe("Prefers morning shifts");
    }

    [Test]
    public async Task RemoveFromGroup_DeletesTheGroupItemById()
    {
        _api.Respond(HttpMethod.Delete, $"{GroupItemsRoute}/{MembershipId}", new GroupItemResource { Id = MembershipId });

        var result = await RemoveFromGroupSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["groupName"] = "Verkauf"
        });

        result.Success.ShouldBeTrue();
        _api.SingleCall.Method.ShouldBe(HttpMethod.Delete);
        _api.SingleCall.Route.ShouldBe($"{GroupItemsRoute}/{MembershipId}");
        _api.SingleCall.SkillName.ShouldBe("remove_client_from_group");
    }

    [Test]
    public async Task NoneOfThem_EverTouchesTheClientEndpoint()
    {
        _api.Respond(HttpMethod.Post, CommunicationsRoute, new CommunicationResource { Id = Guid.NewGuid() });
        _api.Respond(HttpMethod.Post, AnnotationsRoute, new AnnotationResource { Id = Guid.NewGuid() });
        _api.Respond(HttpMethod.Delete, $"{GroupItemsRoute}/{MembershipId}", new GroupItemResource { Id = MembershipId });

        await PhoneSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["phone"] = "079 111 22 33"
        });
        await NoteSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["note"] = "n"
        });
        await RemoveFromGroupSkill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Victor", ["lastName"] = "Frey", ["groupName"] = "Verkauf"
        });

        _api.Calls.Count.ShouldBe(3);
        _api.Calls.ShouldAllBe(call => !call.Route.Contains("Clients"));
    }

    [TestCase("phone")]
    [TestCase("note")]
    [TestCase("group")]
    public async Task WithoutToken_TheyAreRefusedAndSendNothing(string which)
    {
        var result = which switch
        {
            "phone" => await PhoneSkill().ExecuteAsync(Ctx(token: null), new Dictionary<string, object>
            {
                ["firstName"] = "Victor", ["lastName"] = "Frey", ["phone"] = "079"
            }),
            "note" => await NoteSkill().ExecuteAsync(Ctx(token: null), new Dictionary<string, object>
            {
                ["firstName"] = "Victor", ["lastName"] = "Frey", ["note"] = "n"
            }),
            _ => await RemoveFromGroupSkill().ExecuteAsync(Ctx(token: null), new Dictionary<string, object>
            {
                ["firstName"] = "Victor", ["lastName"] = "Frey", ["groupName"] = "Verkauf"
            })
        };

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("access token");
        _api.Calls.ShouldBeEmpty();
    }
}
