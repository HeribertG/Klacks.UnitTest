// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SendMessageSkill: client-name resolution (including the lone-match/disambiguation
/// fix), self-alias and phone-number passthrough, and the recipientType='user' path (name search,
/// disambiguation, explicit-provider vs. preferred-channel resolution, missing-channel error).
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Contracts.Skills;
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
public class SendMessageSkillTests
{
    private IMessagingService _messagingService = null!;
    private IMessengerContactRepository _messengerContactRepository = null!;
    private IOwnerMessengerReader _ownerMessengerReader = null!;
    private IAppUserDirectoryReader _appUserDirectoryReader = null!;
    private IUserMessengerContactRepository _userMessengerContactRepository = null!;
    private IMessagingProviderRepository _providerRepository = null!;
    private SendMessageSkill _sut = null!;
    private Klacks.Plugin.Contracts.Skills.SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _messengerContactRepository = Substitute.For<IMessengerContactRepository>();
        _ownerMessengerReader = Substitute.For<IOwnerMessengerReader>();
        _appUserDirectoryReader = Substitute.For<IAppUserDirectoryReader>();
        _userMessengerContactRepository = Substitute.For<IUserMessengerContactRepository>();
        _providerRepository = Substitute.For<IMessagingProviderRepository>();

        _sut = new SendMessageSkill(
            _messagingService,
            _messengerContactRepository,
            _ownerMessengerReader,
            _appUserDirectoryReader,
            _userMessengerContactRepository,
            _providerRepository);

        _context = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "tester",
            UserPermissions = Array.Empty<string>(),
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

    private static ClientMessengerMatch Match(Guid clientId, string value, string firstName, string name) =>
        new() { ClientId = clientId, Value = value, FirstName = firstName, Name = name };

    // --- client path ---

    [Test]
    public async Task ExecuteAsync_ClientPath_LoneMatch_SendsSuccessfully()
    {
        _messengerContactRepository
            .SearchByClientNameAsync("Hans Muster", MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new[] { Match(Guid.NewGuid(), "chat-123", "Hans", "Muster") });

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Muster"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Muster, Hans");
        result.Message.ShouldContain("Muster, Maria");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_PhoneNumberRecipient_PassesThroughUnchanged()
    {
        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "mir"), ("content", "Notiz an mich")));

        result.Success.ShouldBeTrue();
        await _messagingService.Received(1).SendMessageAsync(
            "telegram", Arg.Is<SendMessageRequest>(r => r.Recipient == "owner-chat"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ClientPath_NoProviderGiven_NoEnabledProviders_ReturnsNoProviderConfiguredError()
    {
        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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
        var result = await _sut.ExecuteAsync(_context, Params(
            ("provider", "telegram"), ("recipient", "Hans"), ("content", "Hallo"), ("recipientType", "group")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("recipientType");
    }

    // --- user path ---

    [Test]
    public async Task ExecuteAsync_UserPath_NoMatch_ReturnsError()
    {
        _appUserDirectoryReader.SearchByNameAsync("Unbekannt", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AppUserDirectoryInfo>());

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
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

        var result = await _sut.ExecuteAsync(_context, Params(
            ("recipientType", "user"), ("recipient", "Hans"), ("content", "Hallo")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Threema");
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }
}
