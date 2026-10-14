// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for ReadMessagesSkill: an administrator reads the unfiltered message store, every other caller (a supervisor
/// reaching the skill through CanEditClients) reads only through IMessagingAccessScope, exactly like
/// MessagingController.GetMessages, so messages of clients hidden by the group visibility and messages without a client
/// (owner bridge, staff, unknown sender) never reach a non-admin. The repository must never be touched on that path.
/// </summary>
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Skills;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class ReadMessagesSkillTests
{
    private const string ProviderName = "telegram-main";
    private const string VisibleContent = "Ich kann morgen frueher kommen";
    private const string UnfilteredContent = "Owner bridge note";

    private IMessageRepository _messageRepository = null!;
    private IMessagingProviderRepository _providerRepository = null!;
    private IMessagingAccessScope _accessScope = null!;
    private ReadMessagesSkill _sut = null!;
    private Guid _providerId;

    [SetUp]
    public void SetUp()
    {
        _messageRepository = Substitute.For<IMessageRepository>();
        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _providerId = Guid.NewGuid();

        _providerRepository.GetByNameAsync(ProviderName)
            .Returns(new MessagingProvider { Id = _providerId, Name = ProviderName, ProviderType = "telegram", IsEnabled = true });

        _sut = new ReadMessagesSkill(_messageRepository, _providerRepository, _accessScope);
    }

    private static Klacks.Plugin.Contracts.Skills.SkillExecutionContext Context(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = permissions
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }

        return dict;
    }

    private static Message InboundMessage(string content) => new()
    {
        Id = Guid.NewGuid(),
        Sender = "chat-1",
        Recipient = "bot",
        Content = content,
        Direction = MessageDirection.Inbound,
        Status = MessageStatus.Delivered,
        Timestamp = DateTime.UtcNow
    };

    [Test]
    public async Task Supervisor_ReadsThroughTheAccessScope_AndNeverTouchesTheRepository()
    {
        _accessScope.GetVisibleMessagesAsync(
                _providerId, MessageDirection.Inbound, "chat-1", null, 7, 0, Arg.Any<CancellationToken>())
            .Returns(new[] { InboundMessage(VisibleContent) });

        var result = await _sut.ExecuteAsync(
            Context(MessagingConstants.RoleSupervisor),
            Params(("provider", ProviderName), ("direction", "inbound"), ("count", 7), ("sender", "chat-1")));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Found 1 Inbound message(s)");
        await _accessScope.Received(1).GetVisibleMessagesAsync(
            _providerId, MessageDirection.Inbound, "chat-1", null, 7, 0, Arg.Any<CancellationToken>());
        await _messageRepository.DidNotReceiveWithAnyArgs()
            .GetMessagesAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task CallerWithoutAnyRole_IsTreatedAsNonAdmin_AndReadsThroughTheAccessScope()
    {
        _accessScope.GetVisibleMessagesAsync(
                null, null, null, null, 10, 0, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        var result = await _sut.ExecuteAsync(Context(), Params());

        result.Success.ShouldBeTrue();
        await _messageRepository.DidNotReceiveWithAnyArgs()
            .GetMessagesAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task Supervisor_NeverSeesWhatTheAccessScopeWithholds()
    {
        _accessScope.GetVisibleMessagesAsync(
                Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<MessageScope?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { InboundMessage(VisibleContent) });
        _messageRepository.GetMessagesAsync(
                Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<MessageScope?>(),
                Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[] { InboundMessage(VisibleContent), InboundMessage(UnfilteredContent) });

        var result = await _sut.ExecuteAsync(Context(MessagingConstants.RoleSupervisor), Params());

        ContentsOf(result.Data).ShouldBe(new[] { VisibleContent });
    }

    [Test]
    public async Task Admin_ReadsTheUnfilteredRepository_AndSkipsTheAccessScope()
    {
        _messageRepository.GetMessagesAsync(null, null, null, null, 10, 0)
            .Returns(new[] { InboundMessage(VisibleContent), InboundMessage(UnfilteredContent) });

        var result = await _sut.ExecuteAsync(Context(MessagingConstants.RoleAdmin), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Found 2 all message(s)");
        await _accessScope.DidNotReceiveWithAnyArgs().GetVisibleMessagesAsync(
            default, default, default, default, default, default, default);
    }

    [TestCase(MessagingConstants.RoleAdmin)]
    [TestCase(MessagingConstants.RoleSupervisor)]
    public async Task UnknownProvider_IsRefused_ForEveryCaller(string role)
    {
        var result = await _sut.ExecuteAsync(Context(role), Params(("provider", "nope")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Messaging provider 'nope' not found.");
        await _accessScope.DidNotReceiveWithAnyArgs().GetVisibleMessagesAsync(
            default, default, default, default, default, default, default);
        await _messageRepository.DidNotReceiveWithAnyArgs()
            .GetMessagesAsync(default, default, default, default, default, default);
    }

    [TestCase(500, 50)]
    [TestCase(0, 1)]
    public async Task Supervisor_CountIsClampedBeforeTheScopeIsAsked(int requested, int expected)
    {
        _accessScope.GetVisibleMessagesAsync(
                null, null, null, null, expected, 0, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());

        await _sut.ExecuteAsync(Context(MessagingConstants.RoleSupervisor), Params(("count", requested)));

        await _accessScope.Received(1).GetVisibleMessagesAsync(
            null, null, null, null, expected, 0, Arg.Any<CancellationToken>());
    }

    private static IReadOnlyList<string> ContentsOf(object? data) =>
        ((System.Collections.IEnumerable)data!).Cast<object>()
            .Select(row => (string)row.GetType().GetProperty(nameof(Message.Content))!.GetValue(row)!)
            .ToList();
}
