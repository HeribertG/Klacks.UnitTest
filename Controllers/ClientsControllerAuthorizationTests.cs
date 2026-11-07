// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The client endpoints and the two paths PUT api/backend/Clients takes. The note card of edit-address
/// saves its notes through the client aggregate, so Put has to be reachable by a caller without any
/// role (Planer) — which InputBaseController made impossible, because an attribute on an override is
/// AND-combined with the base method's. The controller therefore derives from BaseController and
/// declares its own verbs: Get and Put open to every authenticated caller, Post still Admin/Authorised
/// and Delete Admin-only. A caller with CanEditClients writes the whole client; a caller with only
/// CanEditClientNotes reaches a command that can write nothing but the notes, so the rest of the sent
/// resource is ignored instead of refused.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class ClientsControllerAuthorizationTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private ClientsController ControllerFor(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r)).ToArray();

        return new ClientsController(_mediator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
    }

    private static AuthorizeAttribute? AuthorizeOf(string methodName)
        => typeof(ClientsController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .FirstOrDefault();

    [Test]
    public void ClientsController_DoesNotInheritTheInputBaseRestriction()
    {
        typeof(ClientsController).BaseType.ShouldBe(
            typeof(BaseController),
            "Notes are saved through the client aggregate, so Put must be open to a Planer. An " +
            "override cannot lift the InputBaseController role restriction.");
    }

    [Test]
    public void Put_CarriesNoRoleRestriction()
    {
        typeof(ClientsController)
            .GetMethod(nameof(ClientsController.Put), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void Get_CarriesNoRoleRestriction()
    {
        typeof(ClientsController)
            .GetMethod(nameof(ClientsController.Get), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void Post_StaysRestrictedToAdminAndAuthorised()
    {
        AuthorizeOf(nameof(ClientsController.Post))!.Roles.ShouldBe($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void Delete_IsAdminOnly()
    {
        AuthorizeOf(nameof(ClientsController.Delete))!.Roles.ShouldBe(
            Roles.Admin,
            "Deleting a person is administrative: Authorised holds no CanDelete* right, and the client " +
            "list hides the delete behind CanDeleteClients, so the endpoint was wider than both the rights " +
            "model and the only caller.");
    }

    [Test]
    public async Task Put_Planner_ReachesOnlyTheNoteCommand_NeverTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        resource.Annotations.Add(new AnnotationResource { ClientId = resource.Id, Note = "new note" });
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_Planner_PassesTheClientIdAndTheNotesOfTheSentResource()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        resource.Annotations.Add(new AnnotationResource { ClientId = resource.Id, Note = "new note" });
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        await ControllerFor().Put(resource, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<UpdateClientAnnotationsCommand>(command =>
                command.ClientId == resource.Id
                && command.Annotations.Count == 1
                && command.Annotations[0].Note == "new note"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_PlannerWithANullAnnotationList_SavesNoNotesInsteadOfFailing()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Annotations = null! };
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(resource);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<UpdateClientAnnotationsCommand>(command => command.Annotations.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_PlannerOnAnUnknownClient_IsNotFound()
    {
        var resource = new ClientResource { Id = Guid.NewGuid() };
        _mediator.Send(Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>())
            .Returns((ClientResource?)null);

        var result = await ControllerFor().Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task Put_SupervisorChangingTheName_TakesTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        _mediator.Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>()).Returns(resource);

        var result = await ControllerFor(Roles.Authorised).Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(
            Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_AdminChangingTheName_TakesTheFullUpdate()
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "changed" };
        _mediator.Send(Arg.Any<PutCommand<ClientResource>>(), Arg.Any<CancellationToken>()).Returns(resource);

        var result = await ControllerFor(Roles.Admin).Put(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.DidNotReceive().Send(
            Arg.Any<UpdateClientAnnotationsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateStandIns_Planner_IsForbidden_AndNothingIsSent()
    {
        var result = await ControllerFor().CreateStandIns(new CreateStandInsResource(), CancellationToken.None);

        result.Result.ShouldBeOfType<ForbidResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<CreateStandInsCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(Roles.Authorised)]
    [TestCase(Roles.Admin)]
    public async Task CreateStandIns_SupervisorOrAdmin_SendsTheCommand(string role)
    {
        var resource = new CreateStandInsResource { Count = 2, NamePattern = "Fair {n}" };
        _mediator.Send(Arg.Any<CreateStandInsCommand>(), Arg.Any<CancellationToken>()).Returns(new List<CreatedStandInResource>());

        var result = await ControllerFor(role).CreateStandIns(resource, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<CreateStandInsCommand>(command => command.Resource == resource), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConvertStandIn_Planner_IsForbidden_AndNothingIsSent()
    {
        var result = await ControllerFor().ConvertStandIn(Guid.NewGuid(), new ConvertStandInResource(), CancellationToken.None);

        result.Result.ShouldBeOfType<ForbidResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<ConvertStandInCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(Roles.Authorised)]
    [TestCase(Roles.Admin)]
    public async Task ConvertStandIn_SupervisorOrAdmin_SendsTheCommandForTheRouteId(string role)
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<ConvertStandInCommand>(), Arg.Any<CancellationToken>()).Returns(new ClientResource { Id = id });

        var result = await ControllerFor(role).ConvertStandIn(id, new ConvertStandInResource(), CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<ConvertStandInCommand>(command => command.ClientId == id), Arg.Any<CancellationToken>());
    }

    [TestCase(nameof(ClientsController.CreateStandIns))]
    [TestCase(nameof(ClientsController.ConvertStandIn))]
    public void StandInActions_PinTheJwtScheme_WithoutARoleList_AndHonourAnIdempotencyKey(string action)
    {
        var method = typeof(ClientsController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!;

        var authorize = method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ShouldHaveSingleItem();
        authorize.AuthenticationSchemes.ShouldBe(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        authorize.Roles.ShouldBeNull();
        method.GetCustomAttribute<Klacks.Api.Presentation.Filters.IdempotentRequestAttribute>().ShouldNotBeNull();
    }
}
