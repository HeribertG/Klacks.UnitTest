// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for SendMessageSkill: client-name resolution (including the lone-match/disambiguation
/// fix), self-alias and phone-number passthrough, and the recipientType='user' path (name search,
/// disambiguation, explicit-provider vs. preferred-channel resolution, missing-channel error), all as an
/// administrator, who keeps every path. The supervisor block pins the REST semantics of
/// MessagingController.SendMessage for a non-admin caller: only contacts of clients the group visibility lets
/// the caller see, answered like a missing contact otherwise; no owner, no application user, no raw address
/// without a visible contact, and an ambiguity list that never names a hidden client. Every call here runs as the confirmed
/// replay of its own preview (ExecuteConfirmedAsync binds exactly what the resolver resolves); the refusal without a
/// binding and the comparison with the bound facts are pinned in SendMessageSkillBindingTests.
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
public class SendMessageSkillTests
{
    private IMessagingService _messagingService = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
    private IOwnerMessengerReader _ownerMessengerReader = null!;
    private IAppUserDirectoryReader _appUserDirectoryReader = null!;
    private IUserMessengerContactRepository _userMessengerContactRepository = null!;
    private IMessagingProviderRepository _providerRepository = null!;
    private IMessagingAccessScope _accessScope = null!;
    private MessageRecipientResolver _resolver = null!;
    private SendMessageSkill _sut = null!;
    private Klacks.Plugin.Contracts.Skills.SkillExecutionContext _context = null!;
    private Klacks.Plugin.Contracts.Skills.SkillExecutionContext _supervisorContext = null!;

    [SetUp]
    public void SetUp()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _messengerContactRepository = Substitute.For<IMessengerContactRepository>();
        _ownerMessengerReader = Substitute.For<IOwnerMessengerReader>();
        _appUserDirectoryReader = Substitute.For<IAppUserDirectoryReader>();
        _userMessengerContactRepository = Substitute.For<IUserMessengerContactRepository>();
        _providerRepository = Substitute.For<IMessagingProviderRepository>();
        _accessScope = Substitute.For<IMessagingAccessScope>();

        _resolver = new MessageRecipientResolver(
            _messagingService,
            _messengerContactRepository,
            _ownerMessengerReader,
            _appUserDirectoryReader,
            _userMessengerContactRepository,
            _providerRepository,
            _accessScope);
        _sut = new SendMessageSkill(_messagingService, _resolver);

        _context = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "tester",
            UserPermissions = new[] { MessagingConstants.RoleAdmin },
        };

        _supervisorContext = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "supervisor",
            UserPermissions = new[] { MessagingConstants.RoleSupervisor },
        };

        _messagingService.SendMessageAsync(Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true, ExternalMessageId: "ext-1"));
    }

    private static Dictionary<string, object> Params(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    private async Task<Klacks.Plugin.Contracts.Skills.SkillResult> ExecuteConfirmedAsync(
        Klacks.Plugin.Contracts.Skills.SkillExecutionContext context,
        Dictionary<string, object> parameters)
    {
        var request = SendMessageParameters.Parse(parameters);
        var resolution = await _resolver.ResolveAsync(
            request, context.UserPermissions.Contains(MessagingConstants.RoleAdmin), CancellationToken.None);
        var items = resolution.Success
            ? SendMessageBinding.ItemsFor(resolution, request.Content, request.ContentType)
            : Array.Empty<string>();
        var confirmed = context with { ConfirmedBinding = new PluginConfirmedBinding(MessagingConstants.MessageBindingKind, items) };
        return await _sut.ExecuteAsync(confirmed, parameters);
    }

    private static ClientMessengerMatch Match(Guid clientId, string value, string firstName, string name) =>
        new() { ClientId = clientId, Value = value, FirstName = firstName, Name = name };

    [Test]
    public async Task ExecuteAsync_ClientPath_LoneMatch_SendsSuccessfully()
    {
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(Guid.NewGuid(), "chat-123", "Hans", "Muster") });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram",
            Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-123" && r.Content == "Hallo"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_NoMatch_ReturnsNotFoundError()
    {
        _messengerContactRepository
            .SearchByClientNameAsync("Unbekannt", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ClientMessengerMatch>());

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Unbekannt"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No Telegram contact found");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_MultipleMatches_ReturnsAmbiguousErrorWithCandidateNames_InsteadOfPickingFirst()
    {
        _messengerContactRepository
            .SearchByClientNameAsync("Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Match(Guid.NewGuid(), "chat-1", "Hans", "Muster"),
                Match(Guid.NewGuid(), "chat-2", "Maria", "Muster"),
            });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Muster, Hans");
        result.Message.ShouldContain("Muster, Maria");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_PhoneNumberRecipient_PassesThroughUnchanged()
    {
        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "sms"), ("recipient", "+41791234567"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "sms",
            Arg.Is<SendMessageRequest>(r => r.Recipient == "+41791234567"),
            Arg.Any<CancellationToken>());
        await _messengerContactRepository.DidNotReceiveWithAnyArgs()
            .SearchByClientNameAsync(default!, default, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_SelfAlias_ResolvesOwnerMessengerEntry()
    {
        _ownerMessengerReader.GetByTypeAsync(MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new OwnerMessengerEntry { Type = MessengerType.Telegram, Value = "owner-chat" });
        _ownerMessengerReader.GetOwnerDisplayNameAsync(Arg.Any<CancellationToken>()).Returns("Heribert");

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "mir"), ("content", "Notiz an mich")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram", Arg.Is<SendMessageRequest>(r => r.Recipient == "owner-chat"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_NoProviderGiven_NoEnabledProviders_ReturnsNoProviderConfiguredError()
    {
        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("No messaging provider is configured and enabled. An admin must set one up under Settings -> Messaging.");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_NoProviderGiven_MultipleEnabledProviders_ReturnsMustSpecifyError()
    {
        _providerRepository.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Id = Guid.NewGuid(), Name = "telegram-main", ProviderType = "telegram", IsEnabled = true },
            new MessagingProvider { Id = Guid.NewGuid(), Name = "whatsapp-main", ProviderType = "whatsapp", IsEnabled = true },
        });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("telegram-main");
        result.Message.ShouldContain("whatsapp-main");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_NoProviderGiven_ExactlyOneEnabledProvider_AutoResolvesAndSends()
    {
        _providerRepository.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Id = Guid.NewGuid(), Name = "telegram-main", ProviderType = "telegram", IsEnabled = true },
        });
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(Guid.NewGuid(), "chat-123", "Hans", "Muster") });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram",
            Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-123" && r.Content == "Hallo"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_UnknownRecipientType_ReturnsError()
    {
        var result = await ExecuteConfirmedAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Hans"), ("content", "Hallo"), ("recipientType", "group")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("recipientType");
    }

    [Test]
    public async Task ExecuteAsync_UserPath_NoMatch_ReturnsError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Unbekannt", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AppUserDirectoryInfo>());

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Unbekannt"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No active user found");
    }

    [Test]
    public async Task ExecuteAsync_UserPath_MultipleMatches_ReturnsAmbiguousError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Muster", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com"),
                new AppUserDirectoryInfo("u2", "Maria", "Muster", "maria@example.com"),
            });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Hans Muster");
        result.Message.ShouldContain("Maria Muster");
    }

    [Test]
    public async Task ExecuteAsync_UserPath_ExplicitProvider_SendsToThatChannel()
    {
        _appUserDirectoryReader.SearchByNameAsync("Hans", Arg.Any<CancellationToken>())
            .Returns(new[] { new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com") });
        _userMessengerContactRepository.GetByUserAndTypeAsync("u1", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = "u1", Type = MessengerType.Telegram, Value = "user-chat-1" });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("provider", "telegram"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram", Arg.Is<SendMessageRequest>(r => r.Recipient == "user-chat-1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_UserPath_ExplicitProviderButNoPairedChannel_ListsPairedChannelsInError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Hans", Arg.Any<CancellationToken>())
            .Returns(new[] { new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com") });
        _userMessengerContactRepository.GetByUserAndTypeAsync("u1", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns((UserMessengerContact?)null);
        _userMessengerContactRepository.GetByUserIdAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new[] { new UserMessengerContact { UserId = "u1", Type = MessengerType.Slack, Value = "slack-id" } });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("provider", "telegram"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Telegram");
        result.Message.ShouldContain("Slack");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_UserPath_NoProviderGiven_UsesPreferredChannelAndMatchingEnabledProvider()
    {
        _appUserDirectoryReader.SearchByNameAsync("Hans", Arg.Any<CancellationToken>())
            .Returns(new[] { new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com") });
        _userMessengerContactRepository.GetPreferredAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = "u1", Type = MessengerType.Telegram, Value = "preferred-chat" });
        _providerRepository.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Id = Guid.NewGuid(), Name = "telegram-main", ProviderType = "telegram", IsEnabled = true },
        });

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram-main", Arg.Is<SendMessageRequest>(r => r.Recipient == "preferred-chat"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_UserPath_NoProviderGiven_NoPreferredChannel_ReturnsNoChannelError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Hans", Arg.Any<CancellationToken>())
            .Returns(new[] { new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com") });
        _userMessengerContactRepository.GetPreferredAsync("u1", Arg.Any<CancellationToken>())
            .Returns((UserMessengerContact?)null);
        _userMessengerContactRepository.GetByUserIdAsync("u1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserMessengerContact>());

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("none");
        result.Message.ShouldContain("admin");
    }

    [Test]
    public async Task ExecuteAsync_UserPath_NoProviderGiven_PreferredChannelTypeHasNoEnabledProvider_ReturnsError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Hans", Arg.Any<CancellationToken>())
            .Returns(new[] { new AppUserDirectoryInfo("u1", "Hans", "Muster", "hans@example.com") });
        _userMessengerContactRepository.GetPreferredAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = "u1", Type = MessengerType.Threema, Value = "threema-id" });
        _providerRepository.GetEnabledAsync().Returns(Array.Empty<MessagingProvider>());

        var result = await ExecuteConfirmedAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Threema");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    private const string SupervisorProvider = "telegram";
    private const string HiddenOrMissingText = "No Telegram contact found";

    private Guid GivenClientVisibility(bool visible, string chatId = "chat-123")
    {
        var clientId = Guid.NewGuid();
        _accessScope.IsClientVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(visible);
        _messagingService.ResolveRecipientClientIdAsync(SupervisorProvider, chatId, Arg.Any<CancellationToken>())
            .Returns(clientId);
        return clientId;
    }

    [Test]
    public async Task Supervisor_NameOfAVisibleClient_SendsAndChecksTheAttributedClient()
    {
        var clientId = GivenClientVisibility(visible: true);
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(clientId, "chat-123", "Hans", "Muster") });

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            SupervisorProvider, Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-123"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_NameOfAHiddenClient_IsAnsweredLikeAnUnknownContact_AndNothingIsSent()
    {
        var hiddenId = GivenClientVisibility(visible: false);
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(hiddenId, "chat-123", "Hans", "Muster") });
        _messengerContactRepository
            .SearchByClientNameAsync("Niemand Unbekannt", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ClientMessengerMatch>());

        var hidden = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Hans Muster"), ("content", "Hallo")));
        var missing = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Niemand Unbekannt"), ("content", "Hallo")));

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldContain(HiddenOrMissingText);
        hidden.Message.Replace("Hans Muster", "X").ShouldBe(missing.Message.Replace("Niemand Unbekannt", "X"));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_RawAddressWithoutAContact_IsRefused_AndNothingIsSent()
    {
        _messagingService.ResolveRecipientClientIdAsync(SupervisorProvider, "+41791234567", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "+41791234567"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(HiddenOrMissingText);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_RawAddressOfAHiddenClientsContact_IsRefused_AndNothingIsSent()
    {
        GivenClientVisibility(visible: false, chatId: "884411223");

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "884411223"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(HiddenOrMissingText);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_RawAddressOfAVisibleClientsContact_IsSent()
    {
        GivenClientVisibility(visible: true, chatId: "112233445");

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "112233445"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            SupervisorProvider, Arg.Is<SendMessageRequest>(r => r.Recipient == "112233445"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_RawAddressOnACustomNamedProvider_IsGuardedToo()
    {
        _messagingService.ResolveRecipientClientIdAsync("telegram-main", "+41791234567", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", "telegram-main"), ("recipient", "+41791234567"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Unknown messaging provider 'telegram-main'.");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [TestCase("mir")]
    [TestCase("Ich")]
    [TestCase("me")]
    public async Task Supervisor_SelfAlias_IsRefusedBeforeAnyLookup(string alias)
    {
        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", alias), ("content", "Notiz")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MessagingConstants.SendRequiresAdministratorError);
        await _ownerMessengerReader.DidNotReceiveWithAnyArgs().GetByTypeAsync(default, default);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_RecipientTypeUser_IsRefusedBeforeAnyLookup()
    {
        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("recipientType", "user"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MessagingConstants.SendRequiresAdministratorError);
        await _appUserDirectoryReader.DidNotReceiveWithAnyArgs().SearchByNameAsync(default!, default);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_AmbiguousName_WithOneHiddenCandidate_UsesTheSingleVisibleClient()
    {
        var visibleId = GivenClientVisibility(visible: true, chatId: "chat-1");
        var hiddenId = Guid.NewGuid();
        _accessScope.IsClientVisibleAsync(hiddenId, Arg.Any<CancellationToken>()).Returns(false);
        _messengerContactRepository
            .SearchByClientNameAsync("Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Match(visibleId, "chat-1", "Hans", "Muster"),
                Match(hiddenId, "chat-2", "Maria", "Muster"),
            });

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            SupervisorProvider, Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_AmbiguousName_NeverNamesAHiddenCandidate()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();
        _accessScope.IsClientVisibleAsync(firstId, Arg.Any<CancellationToken>()).Returns(true);
        _accessScope.IsClientVisibleAsync(secondId, Arg.Any<CancellationToken>()).Returns(true);
        _accessScope.IsClientVisibleAsync(hiddenId, Arg.Any<CancellationToken>()).Returns(false);
        _messengerContactRepository
            .SearchByClientNameAsync("Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Match(firstId, "chat-1", "Hans", "Muster"),
                Match(secondId, "chat-2", "Maria", "Muster"),
                Match(hiddenId, "chat-3", "Geheim", "Muster"),
            });

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Muster, Hans");
        result.Message.ShouldContain("Muster, Maria");
        result.Message.ShouldNotContain("Geheim");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_AmbiguousName_ListsAtMostTheCandidateLimit_CountedAfterTheVisibilityFilter()
    {
        var hidden = Enumerable.Range(0, 6).Select(i => (Id: Guid.NewGuid(), Name: $"Geheim{i}")).ToList();
        var visible = Enumerable.Range(0, MessagingConstants.ClientNameCandidateLimit + 2)
            .Select(i => (Id: Guid.NewGuid(), Name: $"Sichtbar{i}")).ToList();
        foreach (var (id, _) in hidden)
            _accessScope.IsClientVisibleAsync(id, Arg.Any<CancellationToken>()).Returns(false);
        foreach (var (id, _) in visible)
            _accessScope.IsClientVisibleAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        _messengerContactRepository
            .SearchByClientNameAsync("Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(hidden.Concat(visible).Select((c, i) => Match(c.Id, $"chat-{i}", c.Name, "Muster")).ToList());

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotContain("Geheim");
        visible.Count(c => result.Message!.Contains(c.Name)).ShouldBe(MessagingConstants.ClientNameCandidateLimit);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_NameMatchVisible_ButAddressAttributedToAHiddenClient_IsRefused()
    {
        var matchedId = Guid.NewGuid();
        var attributedHiddenId = Guid.NewGuid();
        _accessScope.IsClientVisibleAsync(matchedId, Arg.Any<CancellationToken>()).Returns(true);
        _accessScope.IsClientVisibleAsync(attributedHiddenId, Arg.Any<CancellationToken>()).Returns(false);
        _messagingService.ResolveRecipientClientIdAsync(SupervisorProvider, "shared-chat", Arg.Any<CancellationToken>())
            .Returns(attributedHiddenId);
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(matchedId, "shared-chat", "Hans", "Muster") });

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("provider", SupervisorProvider), ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Supervisor_WithoutProvider_UsesTheSoleEnabledProvider_AndStillGuardsTheContact()
    {
        _providerRepository.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Id = Guid.NewGuid(), Name = "main", ProviderType = "Telegram", IsEnabled = true },
        });
        var visibleId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();
        _accessScope.IsClientVisibleAsync(visibleId, Arg.Any<CancellationToken>()).Returns(true);
        _accessScope.IsClientVisibleAsync(hiddenId, Arg.Any<CancellationToken>()).Returns(false);
        _messagingService.ResolveRecipientClientIdAsync("Telegram", "chat-123", Arg.Any<CancellationToken>()).Returns(visibleId);
        _messagingService.ResolveRecipientClientIdAsync("Telegram", "chat-9", Arg.Any<CancellationToken>()).Returns(hiddenId);
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(visibleId, "chat-123", "Hans", "Muster") });
        _messengerContactRepository
            .SearchByClientNameAsync("Geheim Person", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(hiddenId, "chat-9", "Geheim", "Person") });

        var visible = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("recipient", "Hans Muster"), ("content", "Hallo")));
        var hidden = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("recipient", "Geheim Person"), ("content", "Hallo")));

        visible.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "Telegram", Arg.Is<SendMessageRequest>(r => r.Recipient == "chat-123"), Arg.Any<CancellationToken>());
        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldContain(HiddenOrMissingText);
        _messagingService.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IMessagingService.SendMessageAsync))
            .ShouldBe(1);
    }

    [Test]
    public async Task Supervisor_WithoutProvider_AndSeveralEnabled_AsksForTheProvider_AndSendsNothing()
    {
        _providerRepository.GetEnabledAsync().Returns(new[]
        {
            new MessagingProvider { Id = Guid.NewGuid(), Name = "tg", ProviderType = "Telegram", IsEnabled = true },
            new MessagingProvider { Id = Guid.NewGuid(), Name = "wa", ProviderType = "WhatsApp", IsEnabled = true },
        });

        var result = await ExecuteConfirmedAsync(_supervisorContext, Params(
            ("recipient", "Hans Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("'provider' parameter is required");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task Admin_NeverAsksTheAccessScope_ForAnyRecipientPath()
    {
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(Guid.NewGuid(), "chat-123", "Hans", "Muster") });

        await ExecuteConfirmedAsync(_context, Params(("provider", SupervisorProvider), ("recipient", "Hans Muster"), ("content", "Hallo")));
        await ExecuteConfirmedAsync(_context, Params(("provider", "sms"), ("recipient", "+41791234567"), ("content", "Hallo")));

        await _accessScope.DidNotReceiveWithAnyArgs().IsClientVisibleAsync(default, default);
        await _messagingService.DidNotReceiveWithAnyArgs().ResolveRecipientClientIdAsync(default!, default!, default);
    }
}
