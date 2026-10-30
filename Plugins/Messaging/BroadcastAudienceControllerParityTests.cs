// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The audience rule exists twice on purpose: MessagingController branches itself, and its admin routes are pinned to the
/// unrestricted service calls, while the broadcast skills ask BroadcastAudienceResolver. This pins that both give a Supervisor
/// the same clients and the same empty-audience refusal, for a group and for id numbers, so the skill can never reach more
/// people than the HTTP route.
/// </summary>

using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.DTOs;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using System.Security.Claims;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class BroadcastAudienceControllerParityTests
{
    private const string Provider = "Telegram";
    private const string Content = "Hallo";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly int[] IdNumbers = [5002, 5043];
    private static readonly Guid[] Visible = [Guid.NewGuid(), Guid.NewGuid()];

    private IMessagingService _messagingService = null!;
    private IMessagingAccessScope _accessScope = null!;
    private MessagingController _controller = null!;
    private BroadcastAudienceResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _controller = new MessagingController(
            _messagingService,
            Substitute.For<IMessagingProviderRepository>(),
            Substitute.For<IPluginUnitOfWork>(),
            Substitute.For<ITelegramRolloutTrigger>(),
            _accessScope)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Role, MessagingConstants.RoleSupervisor)], "test"))
                }
            }
        };
        _resolver = new BroadcastAudienceResolver(
            Substitute.For<IClientGroupReader>(), Substitute.For<IClientIdNumberReader>(), _accessScope);
    }

    private async Task ShouldSendTo(IReadOnlyList<Guid> clients, string emptyError) =>
        await _messagingService.Received(1).SendBroadcastToClientsAsync(
            Provider,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(clients)),
            Content,
            MessagingConstants.DefaultContentType,
            emptyError,
            Arg.Any<CancellationToken>());

    [Test]
    public async Task AGroup_GivesTheSameClientsAndTheSameRefusal()
    {
        _accessScope.GetVisibleGroupClientIdsAsync(GroupId, Arg.Any<CancellationToken>()).Returns(Visible);

        await _controller.SendBroadcast(new SendBroadcastDto { Provider = Provider, GroupId = GroupId, Content = Content });
        var audience = await _resolver.ResolveAsync(new BroadcastAudienceParameters(GroupId, []), isAdmin: false);

        await ShouldSendTo(audience.ClientIds, audience.EmptyError);
    }

    [Test]
    public async Task IdNumbers_GiveTheSameClientsAndTheSameRefusal()
    {
        _accessScope.GetVisibleIdNumberClientIdsAsync(IdNumbers, Arg.Any<CancellationToken>()).Returns(Visible);

        await _controller.SendBroadcastToIdNumbers(new SendBroadcastToIdNumbersDto { Provider = Provider, IdNumbers = IdNumbers, Content = Content });
        var audience = await _resolver.ResolveAsync(new BroadcastAudienceParameters(null, IdNumbers), isAdmin: false);

        await ShouldSendTo(audience.ClientIds, audience.EmptyError);
    }
}
