// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SlackOwnerBridgeService: no-op when the owner has no Slack self-alias configured; the
/// first-ever cycle initializes the watermark to "now" and processes nothing instead of replaying
/// history; a new inbound message dispatches ProcessLLMMessageCommand with Roles.Authorised rights
/// and sends the reply back to the same sender; a failure on one message never blocks a later message
/// in the same batch; and the watermark advances past every attempted message, not only the
/// successful ones.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SlackOwnerBridgeServiceTests
{
    private const string OwnerSlackId = "U-OWNER-123";
    private const string AdminId = "11111111-1111-1111-1111-111111111111";

    private IOwnerMessengerReader _ownerMessengerReader = null!;
    private IMessagingService _messagingService = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private IMediator _mediator = null!;
    private SlackOwnerBridgeService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _ownerMessengerReader = Substitute.For<IOwnerMessengerReader>();
        _messagingService = Substitute.For<IMessagingService>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _mediator = Substitute.For<IMediator>();

        _planningAudienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string> { AdminId });

        _sut = new SlackOwnerBridgeService(
            _ownerMessengerReader,
            _messagingService,
            _settingsRepository,
            _unitOfWork,
            _planningAudienceResolver,
            _mediator,
            NullLogger<SlackOwnerBridgeService>.Instance);
    }

    [Test]
    public async Task RunCycleAsync_NoOwnerAliasConfigured_ReturnsZeroAndNeverTouchesMessagesOrLlm()
    {
        _ownerMessengerReader.GetByTypeAsync(MessengerType.Slack, Arg.Any<CancellationToken>())
            .Returns((OwnerMessengerEntry?)null);

        var processed = await _sut.RunCycleAsync();

        processed.ShouldBe(0);
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>());
        await _messagingService.DidNotReceive().GetMessagesAsync(
            Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_FirstEverCycle_InitializesWatermarkToNowAndProcessesNothing()
    {
        SetUpOwnerAlias();
        _settingsRepository.GetSetting(SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        var processed = await _sut.RunCycleAsync();

        processed.ShouldBe(0);
        await _settingsRepository.Received(1).UpsertSettingAsync(SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK, Arg.Any<string>());
        await _messagingService.DidNotReceive().GetMessagesAsync(
            Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_FirstEverCycle_CommitsTheWatermarkInsteadOfOnlyStagingIt()
    {
        SetUpOwnerAlias();
        _settingsRepository.GetSetting(SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        await _sut.RunCycleAsync();

        // UpsertSettingAsync alone only stages the row on the DbContext. Without the commit the
        // watermark never lands, every later cycle sees null again, takes this same first-ever
        // branch and returns 0 - the bridge would never process a single message.
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RunCycleAsync_AfterProcessing_CommitsTheAdvancedWatermark()
    {
        SetUpOwnerAlias();
        SetUpWatermark(DateTime.UtcNow.AddMinutes(-5));
        _messagingService.GetMessagesAsync(
                null, MessageDirection.Inbound, OwnerSlackId, Arg.Any<int>(), 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { BuildMessage(DateTime.UtcNow, content: "Test") });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Message = "Antwort" });

        await _sut.RunCycleAsync();

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RunCycleAsync_NewMessage_PassesTheConfiguredDefaultLanguageToTheLlm()
    {
        SetUpOwnerAlias();
        SetUpWatermark(DateTime.UtcNow.AddMinutes(-5));
        _settingsRepository.GetSetting(SettingKeys.DefaultLanguage).Returns(
            new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.DefaultLanguage, Value = "de" });
        _messagingService.GetMessagesAsync(
                null, MessageDirection.Inbound, OwnerSlackId, Arg.Any<int>(), 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { BuildMessage(DateTime.UtcNow, content: "Wie weit bist du") });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Message = "Antwort" });

        await _sut.RunCycleAsync();

        // No browser session exists behind an inbound message, so without this the model answers
        // in English no matter which language was written to it.
        await _mediator.Received(1).Send(
            Arg.Is<ProcessLLMMessageCommand>(c => c.Language == "de"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunCycleAsync_NewMessage_DispatchesWithAuthorisedRightsAndSendsReplyToSameSender()
    {
        SetUpOwnerAlias();
        SetUpWatermark(DateTime.UtcNow.AddMinutes(-5));
        var message = BuildMessage(DateTime.UtcNow, content: "Wie viele Ferien hat Anna noch?");
        _messagingService.GetMessagesAsync(
                null, MessageDirection.Inbound, OwnerSlackId, Arg.Any<int>(), 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { message });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Message = "Anna hat noch 12 Tage." });

        var processed = await _sut.RunCycleAsync();

        processed.ShouldBe(1);
        await _mediator.Received(1).Send(
            Arg.Is<ProcessLLMMessageCommand>(c =>
                c.Message == message.Content &&
                c.UserId == AdminId &&
                c.ConversationId == "slack-owner:" + AdminId &&
                c.UserRights.SequenceEqual(Permissions.GetPermissionsForRole(Roles.Authorised))),
            Arg.Any<CancellationToken>());
        await _messagingService.Received(1).SendMessageAsync(
            MessagingConstants.ProviderSlack,
            Arg.Is<SendMessageRequest>(r => r.Recipient == OwnerSlackId && r.Content == "Anna hat noch 12 Tage."),
            Arg.Any<CancellationToken>());
        await _settingsRepository.Received(1).UpsertSettingAsync(
            SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK, Arg.Any<string>());
    }

    [Test]
    public async Task RunCycleAsync_FirstOfTwoMessagesFails_SecondMessageIsStillProcessed()
    {
        SetUpOwnerAlias();
        SetUpWatermark(DateTime.UtcNow.AddMinutes(-5));
        var first = BuildMessage(DateTime.UtcNow.AddSeconds(-2), content: "erste Nachricht");
        var second = BuildMessage(DateTime.UtcNow, content: "zweite Nachricht");
        _messagingService.GetMessagesAsync(
                null, MessageDirection.Inbound, OwnerSlackId, Arg.Any<int>(), 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { first, second });
        _mediator.Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new InvalidOperationException("LLM call failed"),
                _ => new LLMResponse { Message = "Antwort auf die zweite Nachricht" });

        var processed = await _sut.RunCycleAsync();

        processed.ShouldBe(1);
        await _mediator.Received(2).Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>());
        await _messagingService.Received(1).SendMessageAsync(
            MessagingConstants.ProviderSlack,
            Arg.Is<SendMessageRequest>(r => r.Content == "Antwort auf die zweite Nachricht"),
            Arg.Any<CancellationToken>());
        // The watermark still advances past the failed message, so it is not retried forever and
        // does not re-trigger a duplicate reply for the already-successful second message.
        await _settingsRepository.Received(1).UpsertSettingAsync(
            SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK,
            second.Timestamp.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task RunCycleAsync_NoResolvableAdmin_LeavesMessagesUnprocessedAndDoesNotAdvanceWatermark()
    {
        SetUpOwnerAlias();
        SetUpWatermark(DateTime.UtcNow.AddMinutes(-5));
        _planningAudienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>());
        _messagingService.GetMessagesAsync(
                null, MessageDirection.Inbound, OwnerSlackId, Arg.Any<int>(), 0, Arg.Any<CancellationToken>())
            .Returns(new List<Message> { BuildMessage(DateTime.UtcNow, "hallo") });

        var processed = await _sut.RunCycleAsync();

        processed.ShouldBe(0);
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessLLMMessageCommand>(), Arg.Any<CancellationToken>());
        await _settingsRepository.DidNotReceive().UpsertSettingAsync(
            SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK, Arg.Any<string>());
    }

    private void SetUpOwnerAlias()
    {
        _ownerMessengerReader.GetByTypeAsync(MessengerType.Slack, Arg.Any<CancellationToken>())
            .Returns(new OwnerMessengerEntry { Type = MessengerType.Slack, Value = OwnerSlackId });
    }

    private void SetUpWatermark(DateTime watermark)
    {
        _settingsRepository.GetSetting(SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Type = SettingsConstants.SLACK_OWNER_BRIDGE_WATERMARK,
                Value = watermark.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    private static Message BuildMessage(DateTime timestamp, string content)
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            Sender = OwnerSlackId,
            Content = content,
            Direction = MessageDirection.Inbound,
            Timestamp = timestamp,
        };
    }
}
