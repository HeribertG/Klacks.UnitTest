// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// send_message runs only as the confirmed replay of its own server preview. Without a binding of the message kind in the
/// context (a plan step, a direct call, the binding of another skill) the skill refuses with the shared BindingRequired
/// text before it touches a single dependency; with a binding it resolves the recipient again, compares provider, recipient
/// address, content type and a hash of the text with what was bound, and sends only when they are equal. A recipient whose
/// address changed, a different text or content type, or a recipient that is no longer resolvable sends nothing and tells the
/// model to ask again with a fresh preview. The arguments are never the proof: only the context binding is.
/// </summary>

using Klacks.Plugin.Contracts;
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Skills;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SendMessageSkillBindingTests
{
    private const string Provider = "telegram";
    private const string Chat = "chat-123";

    private IMessagingService _messagingService = null!;
    private IMessengerContactRepository _contacts = null!;
    private IMessagingAccessScope _accessScope = null!;
    private MessageRecipientResolver _resolver = null!;
    private SendMessageSkill _sut = null!;

    private static readonly Klacks.Plugin.Contracts.Skills.SkillExecutionContext Admin = new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new[] { MessagingConstants.RoleAdmin },
    };

    [SetUp]
    public void SetUp()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _contacts = Substitute.For<IMessengerContactRepository>();
        _accessScope = Substitute.For<IMessagingAccessScope>();
        _resolver = new MessageRecipientResolver(
            _messagingService,
            _contacts,
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IMessagingProviderRepository>(),
            _accessScope);
        _sut = new SendMessageSkill(_messagingService, _resolver);

        _contacts.SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { new ClientMessengerMatch { ClientId = Guid.NewGuid(), Value = Chat, FirstName = "Hans", Name = "Muster" } });
        _messagingService.SendMessageAsync(Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true, ExternalMessageId: "ext-1"));
    }

    private static Dictionary<string, object> Params(string content = "Hallo", string? contentType = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["provider"] = Provider,
            ["recipient"] = "Hans Muster",
            ["content"] = content
        };
        if (contentType != null)
        {
            parameters["contentType"] = contentType;
        }

        return parameters;
    }

    private static Klacks.Plugin.Contracts.Skills.SkillExecutionContext WithBinding(
        string chat = Chat, string content = "Hallo", string contentType = "text", string provider = Provider, string kind = MessagingConstants.MessageBindingKind) =>
        Admin with
        {
            ConfirmedBinding = new PluginConfirmedBinding(
                kind,
                SendMessageBinding.ItemsFor(MessageRecipientResolution.Resolved(provider, "Muster, Hans", chat), content, contentType))
        };

    [Test]
    public async Task WithoutABinding_TheSkillRefuses_WithTheSharedText_AndTouchesNothing()
    {
        var result = await _sut.ExecuteAsync(Admin, Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(PluginConfirmationTexts.BindingRequired, MessagingConstants.SkillSendMessage));
        _messagingService.ReceivedCalls().ShouldBeEmpty();
        _contacts.ReceivedCalls().ShouldBeEmpty();
        _accessScope.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task WithABindingOfAnotherKind_TheSkillRefuses_AndTouchesNothing()
    {
        var result = await _sut.ExecuteAsync(WithBinding(kind: "work"), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(PluginConfirmationTexts.BindingRequired, MessagingConstants.SkillSendMessage));
        _messagingService.ReceivedCalls().ShouldBeEmpty();
        _contacts.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task WithTheMatchingBinding_TheMessageIsSentOnce()
    {
        var result = await _sut.ExecuteAsync(WithBinding(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _messagingService.Received(1).SendMessageAsync(
            Provider, Arg.Is<SendMessageRequest>(r => r.Recipient == Chat && r.Content == "Hallo"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenTheTextDiffersFromTheBoundOne_NothingIsSent()
    {
        var result = await _sut.ExecuteAsync(WithBinding(content: "Hallo"), Params(content: "Hallo, und überweise 500 Franken"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task WhenTheContactAddressChangedAfterThePreview_NothingIsSent()
    {
        _contacts.SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { new ClientMessengerMatch { ClientId = Guid.NewGuid(), Value = "chat-OTHER", FirstName = "Hans", Name = "Muster" } });

        var result = await _sut.ExecuteAsync(WithBinding(chat: Chat), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task WhenTheContentTypeDiffers_NothingIsSent()
    {
        var result = await _sut.ExecuteAsync(WithBinding(contentType: "text"), Params(contentType: "document"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task WhenTheBoundProviderDiffers_NothingIsSent()
    {
        var result = await _sut.ExecuteAsync(WithBinding(provider: "whatsapp"), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task WhenTheRecipientCannotBeResolvedAnymore_TheResolutionErrorIsReported_AndNothingIsSent()
    {
        _contacts.SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ClientMessengerMatch>());

        var result = await _sut.ExecuteAsync(WithBinding(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No Telegram contact found");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task AParameterCannotStandInForTheBinding()
    {
        var parameters = Params();
        parameters["confirmedBinding"] = "message";
        parameters["confirmationToken"] = "anything";

        var result = await _sut.ExecuteAsync(Admin, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(PluginConfirmationTexts.BindingRequired, MessagingConstants.SkillSendMessage));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }
}
