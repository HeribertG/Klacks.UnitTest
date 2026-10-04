// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-04 every signed-in user, the role-less planner included, could list all stored messages with
/// their chat ids, phone numbers and content, send to any recipient and broadcast to any group. That read around
/// the group visibility of the messenger contacts. Message routes are now limited to admins and supervisors, and
/// a supervisor only reaches clients of its visible groups, answered like missing ones otherwise. Admins keep the
/// unrestricted path.
/// </summary>

using System.Reflection;
using System.Security.Claims;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.DTOs;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using HostRoles = Klacks.Api.Domain.Constants.Roles;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingControllerAccessTests
{
    private const string Provider = "Telegram";
    private const string HiddenChatId = "884411223";
    private const string VisibleChatId = "112233445";
    private const string Content = "Dienst morgen 06:00";

    private static readonly string[] ProviderReadsOpenToEverySignedInUser =
    {
        nameof(MessagingController.GetProviders),
        nameof(MessagingController.GetProvider),
    };

    private static readonly string[] MessageRoutes =
    {
        nameof(MessagingController.GetMessages),
        nameof(MessagingController.GetMessage),
        nameof(MessagingController.SendMessage),
        nameof(MessagingController.PreviewBroadcast),
        nameof(MessagingController.SendBroadcast),
        nameof(MessagingController.PreviewBroadcastToIdNumbers),
        nameof(MessagingController.SendBroadcastToIdNumbers),
    };

    private IMessagingService _messagingService = null!;
    private IMessagingAccessScope _accessScope = null!;
    private MessagingController _sut = null!;
    private Guid _hiddenClientId;
    private Guid _visibleClientId;

    [SetUp]
    public void Setup()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _hiddenClientId = Guid.NewGuid();
        _visibleClientId = Guid.NewGuid();

        _accessScope.IsClientVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Guid?>() == _visibleClientId);
        _accessScope.IsMessageVisibleAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Message>().ClientId == _visibleClientId);
        _messagingService.ResolveRecipientClientIdAsync(Provider, HiddenChatId, Arg.Any<CancellationToken>())
            .Returns(_hiddenClientId);
        _messagingService.ResolveRecipientClientIdAsync(Provider, VisibleChatId, Arg.Any<CancellationToken>())
            .Returns(_visibleClientId);
        _messagingService.SendMessageAsync(Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true));

        _sut = new MessagingController(
            _messagingService,
            Substitute.For<IMessagingProviderRepository>(),
            Substitute.For<IPluginUnitOfWork>(),
            Substitute.For<ITelegramRolloutTrigger>(),
            _accessScope);
        SignInAs(MessagingConstants.RoleSupervisor);
    }

    [Test]
    public void RoleConstants_MatchTheHostRoleClaims()
    {
        MessagingConstants.RoleAdmin.ShouldBe(HostRoles.Admin);
        MessagingConstants.RoleSupervisor.ShouldBe(HostRoles.Authorised);
    }

    [Test]
    public void ClassAuthorize_PinsTheJwtScheme()
    {
        var classAuthorize = typeof(MessagingController).GetCustomAttribute<AuthorizeAttribute>(inherit: false);

        classAuthorize.ShouldNotBeNull();
        classAuthorize.AuthenticationSchemes.ShouldBe(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
    }

    [Test]
    public void EveryRoute_ExceptProviderReads_RequiresAdminOrSupervisor()
    {
        var unguarded = typeof(MessagingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Where(m => !ProviderReadsOpenToEverySignedInUser.Contains(m.Name))
            .Where(m => !IsAdminOnly(RolesOf(m.Name)) && !IsAdminOrSupervisor(RolesOf(m.Name)))
            .Select(m => m.Name)
            .ToList();

        unguarded.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(MessageRoutes))]
    public void MessageRoute_IsOpenToAdminAndSupervisorOnly(string actionName)
    {
        IsAdminOrSupervisor(RolesOf(actionName)).ShouldBeTrue();
    }

    [Test]
    public void OwnerMessengerRead_IsAdminOnly()
    {
        RolesOf(typeof(OwnerMessengerController), nameof(OwnerMessengerController.Get))
            .ShouldBe(new[] { MessagingConstants.RoleAdmin });
    }

    [Test]
    public async Task GetMessages_Admin_ReadsTheUnrestrictedList()
    {
        SignInAs(MessagingConstants.RoleAdmin);
        var internalMessage = new Message { Id = Guid.NewGuid(), ClientId = null };
        _messagingService.GetMessagesAsync(null, null, null, MessageScope.Internal, 50, 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { internalMessage });

        var result = await _sut.GetMessages(scope: MessageScope.Internal);

        var dtos = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IEnumerable<MessageDto>>()!;
        dtos.Single().Id.ShouldBe(internalMessage.Id);
        await _accessScope.DidNotReceiveWithAnyArgs().GetVisibleMessagesAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task GetMessages_Supervisor_ReadsOnlyTheVisibilityScopedList()
    {
        var visible = new Message { Id = Guid.NewGuid(), ClientId = _visibleClientId };
        _accessScope.GetVisibleMessagesAsync(null, null, null, null, 50, 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { visible });

        var result = await _sut.GetMessages();

        var dtos = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeAssignableTo<IEnumerable<MessageDto>>()!;
        dtos.Single().Id.ShouldBe(visible.Id);
        await _messagingService.DidNotReceiveWithAnyArgs().GetMessagesAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task GetMessage_Supervisor_MessageOfHiddenClient_NotFound()
    {
        var id = GiveStoredMessage(_hiddenClientId);

        var result = await _sut.GetMessage(id);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task GetMessage_Supervisor_MessageWithoutClient_NotFound()
    {
        var id = GiveStoredMessage(null);

        var result = await _sut.GetMessage(id);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task GetMessage_Supervisor_MessageOfVisibleClient_Ok()
    {
        var id = GiveStoredMessage(_visibleClientId);

        var result = await _sut.GetMessage(id);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Test]
    public async Task GetMessage_Admin_MessageWithoutClient_Ok()
    {
        SignInAs(MessagingConstants.RoleAdmin);
        var id = GiveStoredMessage(null);

        var result = await _sut.GetMessage(id);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _accessScope.DidNotReceiveWithAnyArgs().IsMessageVisibleAsync(default!, default);
    }

    [Test]
    public async Task SendMessage_Supervisor_RecipientOfHiddenClient_NotFound_NothingSent()
    {
        var result = await _sut.SendMessage(new SendMessageDto { Provider = Provider, Recipient = HiddenChatId, Content = Content });

        result.Result.ShouldBeOfType<NotFoundResult>();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_Supervisor_RecipientWithoutContact_NotFound_NothingSent()
    {
        var result = await _sut.SendMessage(new SendMessageDto { Provider = Provider, Recipient = "unknown", Content = Content });

        result.Result.ShouldBeOfType<NotFoundResult>();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_Supervisor_RecipientOfVisibleClient_IsSent()
    {
        var result = await _sut.SendMessage(new SendMessageDto { Provider = Provider, Recipient = VisibleChatId, Content = Content });

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _messagingService.Received(1).SendMessageAsync(
            Provider, Arg.Is<SendMessageRequest>(r => r.Recipient == VisibleChatId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessage_Admin_AnyRecipient_IsSentWithoutVisibilityCheck()
    {
        SignInAs(MessagingConstants.RoleAdmin);

        var result = await _sut.SendMessage(new SendMessageDto { Provider = Provider, Recipient = HiddenChatId, Content = Content });

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _messagingService.DidNotReceiveWithAnyArgs().ResolveRecipientClientIdAsync(default!, default!, default);
    }

    [Test]
    public async Task SendBroadcast_Supervisor_SendsOnlyToVisibleGroupMembers()
    {
        var groupId = Guid.NewGuid();
        var visibleMembers = new List<Guid> { _visibleClientId };
        _accessScope.GetVisibleGroupClientIdsAsync(groupId, Arg.Any<CancellationToken>()).Returns(visibleMembers);

        await _sut.SendBroadcast(new SendBroadcastDto { Provider = Provider, GroupId = groupId, Content = Content });

        await _messagingService.Received(1).SendBroadcastToClientsAsync(
            Provider, visibleMembers, Content, MessagingConstants.DefaultContentType,
            MessagingConstants.BroadcastGroupEmptyError, Arg.Any<CancellationToken>());
        await _messagingService.DidNotReceiveWithAnyArgs().SendBroadcastAsync(default!, default, default!, default!, default);
    }

    [Test]
    public async Task SendBroadcast_Supervisor_HiddenGroup_AnsweredLikeAnEmptyGroup()
    {
        var groupId = Guid.NewGuid();
        _accessScope.GetVisibleGroupClientIdsAsync(groupId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _messagingService
            .SendBroadcastToClientsAsync(Provider, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), Content,
                Arg.Any<string>(), MessagingConstants.BroadcastGroupEmptyError, Arg.Any<CancellationToken>())
            .Returns<BroadcastSendResult>(_ => throw new InvalidOperationException(MessagingConstants.BroadcastGroupEmptyError));

        var result = await _sut.SendBroadcast(new SendBroadcastDto { Provider = Provider, GroupId = groupId, Content = Content });

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value).ShouldBe(MessagingConstants.BroadcastGroupEmptyError);
    }

    [Test]
    public async Task SendBroadcast_Admin_UsesTheUnrestrictedGroupBroadcast()
    {
        SignInAs(MessagingConstants.RoleAdmin);
        var groupId = Guid.NewGuid();

        await _sut.SendBroadcast(new SendBroadcastDto { Provider = Provider, GroupId = groupId, Content = Content });

        await _messagingService.Received(1).SendBroadcastAsync(
            Provider, groupId, Content, MessagingConstants.DefaultContentType, Arg.Any<CancellationToken>());
        await _accessScope.DidNotReceiveWithAnyArgs().GetVisibleGroupClientIdsAsync(default, default);
    }

    [Test]
    public async Task PreviewBroadcast_Supervisor_PreviewsOnlyVisibleGroupMembers()
    {
        var groupId = Guid.NewGuid();
        var visibleMembers = new List<Guid> { _visibleClientId };
        _accessScope.GetVisibleGroupClientIdsAsync(groupId, Arg.Any<CancellationToken>()).Returns(visibleMembers);

        await _sut.PreviewBroadcast(Provider, groupId);

        await _messagingService.Received(1).PreviewBroadcastToClientsAsync(Provider, visibleMembers, Arg.Any<CancellationToken>());
        await _messagingService.DidNotReceiveWithAnyArgs().PreviewBroadcastAsync(default!, default, default);
    }

    [Test]
    public async Task PreviewBroadcastToIdNumbers_Supervisor_OnlyHiddenIdNumbers_AnsweredLikeUnknownIdNumbers()
    {
        var idNumbers = new[] { 4711 };
        _accessScope.GetVisibleIdNumberClientIdsAsync(idNumbers, Arg.Any<CancellationToken>()).Returns(new List<Guid>());

        var result = await _sut.PreviewBroadcastToIdNumbers(Provider, idNumbers);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value)
            .ShouldBe(MessagingConstants.BroadcastNoClientsForIdNumbersError);
        await _messagingService.DidNotReceiveWithAnyArgs().PreviewBroadcastToClientsAsync(default!, default!, default);
    }

    [Test]
    public async Task SendBroadcastToIdNumbers_Supervisor_SendsOnlyToVisibleClients()
    {
        var idNumbers = new[] { 4711, 4712 };
        var visible = new List<Guid> { _visibleClientId };
        _accessScope.GetVisibleIdNumberClientIdsAsync(idNumbers, Arg.Any<CancellationToken>()).Returns(visible);

        await _sut.SendBroadcastToIdNumbers(new SendBroadcastToIdNumbersDto { Provider = Provider, IdNumbers = idNumbers, Content = Content });

        await _messagingService.Received(1).SendBroadcastToClientsAsync(
            Provider, visible, Content, MessagingConstants.DefaultContentType,
            MessagingConstants.BroadcastNoClientsForIdNumbersError, Arg.Any<CancellationToken>());
        await _messagingService.DidNotReceiveWithAnyArgs().SendBroadcastToIdNumbersAsync(default!, default!, default!, default!, default);
    }

    [Test]
    public void IncomingMessageNotification_CarriesNoSenderOrContent()
    {
        var properties = typeof(IncomingMessageDto).GetProperties().Select(p => p.Name).ToList();

        properties.ShouldBe(
            new[]
            {
                nameof(IncomingMessageDto.MessageId),
                nameof(IncomingMessageDto.ProviderName),
                nameof(IncomingMessageDto.ProviderDisplayName),
                nameof(IncomingMessageDto.Timestamp),
            },
            ignoreOrder: true);
    }

    private Guid GiveStoredMessage(Guid? clientId)
    {
        var message = new Message { Id = Guid.NewGuid(), ClientId = clientId, Sender = HiddenChatId };
        _messagingService.GetMessageAsync(message.Id, Arg.Any<CancellationToken>()).Returns(message);
        return message.Id;
    }

    private void SignInAs(string role)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role) },
            "TestAuth",
            ClaimTypes.Name,
            ClaimTypes.Role);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private static string[] RolesOf(string actionName) => RolesOf(typeof(MessagingController), actionName);

    private static string[] RolesOf(Type controller, string actionName)
    {
        return controller
            .GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Where(a => a.Roles != null)
            .SelectMany(a => a.Roles!.Split(','))
            .ToArray();
    }

    private static bool IsAdminOnly(string[] roles)
    {
        return roles.Length == 1 && roles[0] == MessagingConstants.RoleAdmin;
    }

    private static bool IsAdminOrSupervisor(string[] roles)
    {
        return roles.Length == 2
            && roles.Contains(MessagingConstants.RoleAdmin)
            && roles.Contains(MessagingConstants.RoleSupervisor);
    }
}
