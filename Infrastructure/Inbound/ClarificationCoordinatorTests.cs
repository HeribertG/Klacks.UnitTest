// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationCoordinator: asking (persist Open with deadline, send privately, inform
/// planners), suggesting at global level Propose or with the kill switch, the missing-contact hint
/// (also when resolving the contact throws), composer/insert/send failures, rate limit, auto-generated
/// mail, column bounds (shift context, email message id), answer correlation (Answered, Unresolved
/// without a second question and never with high confidence, race with the expiry sweep, retry of the
/// original message), the expired-predecessor note (thread headers and 24-hour window), TakenOver, and
/// that no exception other than cancellation escapes.
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationCoordinatorTests
{
    private const string Question = "Heißt das, du kannst deinen Spätdienst heute (14:00-22:00) nicht antreten?";
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc);

    private ISettingsReader _settingsReader = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private IInboundClarificationRepository _repository = null!;
    private IClarificationQuestionComposer _composer = null!;
    private IInboundReplySender _emailSender = null!;
    private IInboundReplySender _messengerSender = null!;
    private IInboundIntentAnalysisService _analysisService = null!;
    private IInboundAnalysisNotifier _notifier = null!;
    private ClarificationCoordinator _coordinator = null!;

    private static TimeZoneInfo FixedOffsetZone(int offsetHours)
    {
        var id = $"Test{offsetHours:+0;-0;0}";
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(offsetHours), id, id);
    }

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        SwitchIs("true");
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _governanceResolver.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>()).Returns(AutonomyLevel.Assisted);

        _repository = Substitute.For<IInboundClarificationRepository>();
        _repository.GetOpenByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((InboundClarification?)null);
        _repository.CountAskedSinceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
        _repository.TryAddOpenAsync(Arg.Any<InboundClarification>(), Arg.Any<CancellationToken>()).Returns(true);
        _repository.TryResolveAsync(Arg.Any<Guid>(), Arg.Any<InboundClarificationStatus>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _composer = Substitute.For<IClarificationQuestionComposer>();
        _composer.ComposeAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns(new ComposedClarificationQuestion(Question, "Spätdienst 2026-09-23 14:00-22:00", new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc)));

        _emailSender = Substitute.For<IInboundReplySender>();
        _emailSender.SourceKind.Returns(InboundSourceKind.Email);
        _emailSender.ResolveTargetAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InboundReplyTarget(
                Recipient: "anna@example.com", Subject: "Re: Krank", InReplyTo: "<original-1@example.com>", References: "<original-1@example.com>"));
        _emailSender.SendAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundReplyTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(InboundReplyResult.Sent);

        _messengerSender = Substitute.For<IInboundReplySender>();
        _messengerSender.SourceKind.Returns(InboundSourceKind.Messenger);
        _messengerSender.ResolveTargetAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InboundReplyTarget(Recipient: "123456789", Subject: null, InReplyTo: null, References: null));
        _messengerSender.SendAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundReplyTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(InboundReplyResult.Sent);

        _analysisService = Substitute.For<IInboundIntentAnalysisService>();
        _notifier = Substitute.For<IInboundAnalysisNotifier>();

        _coordinator = new ClarificationCoordinator(
            _settingsReader,
            _governanceResolver,
            _repository,
            _composer,
            new[] { _emailSender, _messengerSender },
            _analysisService,
            _notifier,
            new FixedCompanyClock(new DateTimeOffset(NowUtc), FixedOffsetZone(2)),
            Substitute.For<ILogger<ClarificationCoordinator>>());
    }

    private void SwitchIs(string value) =>
        _settingsReader.GetSetting(AppSettings.INBOUND_CLARIFICATION_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = value });

    private static ClarificationRequest MessengerRequest(Guid? sourceId = null, EntityTypeEnum clientType = EntityTypeEnum.Employee) => new(
        ClientId: ClientId,
        ClientType: clientType,
        Source: new InboundSource(sourceId ?? Guid.NewGuid(), InboundSourceKind.Messenger, "Messenger:Telegram", "Anna Muster", null,
            "Ich fühle mich nicht gut.", NowUtc),
        ReplyChannel: "Telegram",
        SenderAddress: "123456789",
        EmailThread: null);

    private static ClarificationRequest EmailRequest(ClarificationEmailThread thread) => new(
        ClientId: ClientId,
        ClientType: EntityTypeEnum.Employee,
        Source: new InboundSource(Guid.NewGuid(), InboundSourceKind.Email, "Email", "Anna Muster (anna@example.com)", "Krank",
            "Ich fühle mich nicht gut.", NowUtc),
        ReplyChannel: "Email",
        SenderAddress: "anna@example.com",
        EmailThread: thread);

    private static InboundAnalysis UnclearAnalysis() => new()
    {
        Id = Guid.NewGuid(),
        Intent = EmailIntent.Other,
        Confidence = EmailConfidence.Low,
        Summary = "Fühlt sich nicht gut.",
        NeedsClarification = true,
        ClarificationQuestion = "Kannst du heute arbeiten?"
    };

    private static InboundClarification OpenClarification(Guid? originalSourceId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        Status = InboundClarificationStatus.Open,
        Question = Question,
        OriginalText = "Ich fühle mich nicht gut.",
        OriginalReceivedAt = NowUtc.AddMinutes(-20),
        AskedAt = NowUtc.AddMinutes(-19),
        DeadlineAt = NowUtc.AddMinutes(40),
        OriginalSourceId = originalSourceId ?? Guid.NewGuid(),
        SenderDisplay = "Anna Muster"
    };

    [Test]
    public async Task After_AnalysisWithoutNeed_ContinuesWithoutTouchingAnything()
    {
        var analysis = UnclearAnalysis();
        analysis.NeedsClarification = false;

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), analysis);

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _settingsReader.DidNotReceiveWithAnyArgs().GetSetting(default!);
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
    }

    [Test]
    public async Task After_SwitchOff_Continues()
    {
        SwitchIs("false");

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
    }

    [Test]
    public async Task After_Ask_PersistsOpenClarification_SendsPrivately_AndInformsPlanners()
    {
        var analysis = UnclearAnalysis();
        var request = MessengerRequest();

        var result = await _coordinator.AfterAnalysisAsync(request, analysis);

        result.ShouldBe(ClarificationPostAnalysis.Sent);
        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c =>
                c.ClientId == ClientId &&
                c.Channel == "Telegram" &&
                c.Recipient == "123456789" &&
                c.OriginalAnalysisId == analysis.Id &&
                c.OriginalSourceId == request.Source.SourceId &&
                c.OriginalText == "Ich fühle mich nicht gut." &&
                c.Question == Question &&
                c.ShiftContext == "Spätdienst 2026-09-23 14:00-22:00" &&
                c.AskedAt == NowUtc &&
                c.DeadlineAt == NowUtc.AddMinutes(60)),
            Arg.Any<CancellationToken>());
        await _messengerSender.Received(1).SendAsync(request, Arg.Any<InboundReplyTarget>(), Question, Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
        await _notifier.Received(1).NotifyMessageAsync(
            Arg.Is<string>(m => m.Contains("Clarification requested") && m.Contains(Question) && m.Contains("2026-09-23 09:00")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_Ask_SendsOnlyToTheResolvedTarget_NeverToTheSenderAddress()
    {
        _messengerSender.ResolveTargetAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InboundReplyTarget(Recipient: "987654321", Subject: null, InReplyTo: null, References: null));

        await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        await _messengerSender.Received(1).SendAsync(
            Arg.Any<ClarificationRequest>(), Arg.Is<InboundReplyTarget>(t => t.Recipient == "987654321"), Question, Arg.Any<CancellationToken>());
        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.Recipient == "987654321"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_Ask_PassesTheInMemoryAnalysisToTheComposer()
    {
        var analysis = UnclearAnalysis();
        analysis.DateAssumed = true;

        await _coordinator.AfterAnalysisAsync(MessengerRequest(), analysis);

        await _composer.Received(1).ComposeAsync(
            Arg.Any<ClarificationRequest>(), Arg.Is<InboundAnalysis>(a => ReferenceEquals(a, analysis) && a.DateAssumed), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_ShiftStartsSoon_DeadlineIsThirtyMinutesBeforeShiftStart()
    {
        _composer.ComposeAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns(new ComposedClarificationQuestion(Question, "Frühdienst 2026-09-23 08:50-16:00", NowUtc.AddMinutes(50)));

        await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.DeadlineAt == NowUtc.AddMinutes(20)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_LongShiftContext_IsTruncatedToTheColumnLength()
    {
        var longContext = new string('x', InboundClarificationConstants.MaxShiftContextLength + 50);
        _composer.ComposeAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns(new ComposedClarificationQuestion(Question, longContext, null));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Sent);
        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.ShiftContext != null && c.ShiftContext.Length == InboundClarificationConstants.MaxShiftContextLength),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_GlobalLevelPropose_SuggestsWithoutSending()
    {
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>()).Returns(AutonomyLevel.Propose);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.QuestionSent.ShouldBeFalse();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("would ask back");
        result.NotifierContext.ShouldContain(Question);
        await _repository.Received(1).AddAsync(
            Arg.Is<InboundClarification>(c => c.Status == InboundClarificationStatus.Suggested && c.Recipient == string.Empty),
            Arg.Any<CancellationToken>());
        await _messengerSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
        await _messengerSender.DidNotReceiveWithAnyArgs().ResolveTargetAsync(default!, default);
        await _repository.DidNotReceiveWithAnyArgs().TryAddOpenAsync(default!, default);
    }

    [Test]
    public async Task After_KillSwitchActive_Suggests()
    {
        _governanceResolver.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>()).Returns(AutonomyLevel.FullyAutonomous);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("would ask back");
        await _messengerSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task After_NoPersonalContact_ContinuesWithHint_AndComposesNothing()
    {
        _messengerSender.ResolveTargetAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns((InboundReplyTarget?)null);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.QuestionSent.ShouldBeFalse();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("cannot ask back");
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
    }

    [Test]
    public async Task After_ResolvingTheContactThrows_IsTreatedAsNoPersonalContact()
    {
        _messengerSender.ResolveTargetAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<InboundReplyTarget?>(_ => throw new InvalidOperationException("db down"));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.QuestionSent.ShouldBeFalse();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("cannot ask back");
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
        await _messengerSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task After_ComposerYieldsNothing_ContinuesOnTheRegularPath()
    {
        _composer.ComposeAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns((ComposedClarificationQuestion?)null);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _repository.DidNotReceiveWithAnyArgs().TryAddOpenAsync(default!, default);
    }

    [Test]
    public async Task After_OpenInsertLosesTheRace_ContinuesWithoutSending()
    {
        _repository.TryAddOpenAsync(Arg.Any<InboundClarification>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _messengerSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task After_ClientAlreadyHasAnOpenClarification_Continues()
    {
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(OpenClarification());

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
    }

    [Test]
    public async Task After_SendFails_MarksUnresolved_AndTellsThePlanners()
    {
        _messengerSender.SendAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundReplyTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(InboundReplyResult.Failed("chat not found"));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.QuestionSent.ShouldBeFalse();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("could not be sent");
        result.NotifierContext.ShouldContain("chat not found");
        await _repository.Received(1).TryResolveAsync(
            Arg.Any<Guid>(),
            InboundClarificationStatus.Unresolved,
            Arg.Is<Guid?>(id => id == null),
            Arg.Is<Guid?>(id => id == null),
            NowUtc,
            Arg.Any<CancellationToken>());
        await _notifier.DidNotReceiveWithAnyArgs().NotifyMessageAsync(default!, default);
    }

    [Test]
    public async Task After_SendFailsAndMarkingUnresolvedThrows_StillTellsThePlanners()
    {
        _messengerSender.SendAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundReplyTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(InboundReplyResult.Failed("chat not found"));
        _repository.TryResolveAsync(Arg.Any<Guid>(), Arg.Any<InboundClarificationStatus>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("db down"));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("could not be sent");
    }

    [Test]
    public async Task After_SenderThrows_IsTreatedAsASendFailure()
    {
        _messengerSender.SendAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundReplyTarget>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<InboundReplyResult>(_ => throw new InvalidOperationException("boom"));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("could not be sent");
    }

    [Test]
    public async Task After_RateLimitReached_Continues()
    {
        _repository.CountAskedSinceAsync(ClientId, NowUtc.AddMinutes(-60), Arg.Any<CancellationToken>()).Returns(1);

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
    }

    [Test]
    public async Task After_AutoGeneratedMail_Continues()
    {
        var request = EmailRequest(new ClarificationEmailThread("original-1@example.com", null, null, IsAutoGenerated: true));

        var result = await _coordinator.AfterAnalysisAsync(request, UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
        await _emailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task After_Email_StoresTheOriginalMessageIdForThreading()
    {
        var request = EmailRequest(new ClarificationEmailThread("original-1@example.com", null, null, IsAutoGenerated: false));

        var result = await _coordinator.AfterAnalysisAsync(request, UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Sent);
        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.EmailMessageId == "original-1@example.com" && c.Recipient == "anna@example.com"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_Email_StoresTheMessageIdWithoutAngleBrackets()
    {
        var request = EmailRequest(new ClarificationEmailThread("<original-1@example.com>", null, null, IsAutoGenerated: false));

        await _coordinator.AfterAnalysisAsync(request, UnclearAnalysis());

        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.EmailMessageId == "original-1@example.com"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_Email_OversizedMessageIdIsNotStored()
    {
        var oversized = new string('a', InboundClarificationConstants.MaxStoredEmailMessageIdLength) + "@example.com";
        var request = EmailRequest(new ClarificationEmailThread(oversized, null, null, IsAutoGenerated: false));

        var result = await _coordinator.AfterAnalysisAsync(request, UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Sent);
        await _repository.Received(1).TryAddOpenAsync(
            Arg.Is<InboundClarification>(c => c.EmailMessageId == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task After_ComposerThrows_ContinuesWithoutException()
    {
        _composer.ComposeAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns<ComposedClarificationQuestion?>(_ => throw new InvalidOperationException("boom"));

        var result = await _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis());

        result.ShouldBe(ClarificationPostAnalysis.Continue);
    }

    [Test]
    public async Task After_Cancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _repository.GetOpenByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<InboundClarification?>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _coordinator.AfterAnalysisAsync(MessengerRequest(), UnclearAnalysis(), cts.Token));
    }

    [Test]
    public async Task Before_Customer_ReturnsNoneWithoutRepositoryAccess()
    {
        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest(clientType: EntityTypeEnum.Customer));

        result.ShouldBe(ClarificationPreAnalysis.None);
        await _repository.DidNotReceiveWithAnyArgs().GetOpenByClientAsync(default, default);
    }

    [Test]
    public async Task Before_NothingOpenNothingExpired_ReturnsNone()
    {
        (await _coordinator.BeforeAnalysisAsync(MessengerRequest())).ShouldBe(ClarificationPreAnalysis.None);
    }

    [Test]
    public async Task Before_OpenClarification_ReanalysesWithHistory_AndMarksItAnswered()
    {
        var open = OpenClarification();
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        var request = MessengerRequest();
        var answerAnalysis = new InboundAnalysis { Intent = EmailIntent.WorkCancellation, Confidence = EmailConfidence.High, NeedsClarification = false };
        _analysisService.AnalyzeAnswerAsync(
                ClientId, EntityTypeEnum.Employee, request.Source,
                Arg.Is<ClarificationHistory>(h => h.Question == Question && h.OriginalText == "Ich fühle mich nicht gut."),
                Arg.Any<CancellationToken>())
            .Returns(answerAnalysis);

        var result = await _coordinator.BeforeAnalysisAsync(request);

        result.AnswerAnalysis.ShouldBeSameAs(answerAnalysis);
        answerAnalysis.Id.ShouldNotBe(Guid.Empty);
        answerAnalysis.Confidence.ShouldBe(EmailConfidence.High);
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain(Question);
        await _repository.Received(1).TryResolveAsync(
            open.Id, InboundClarificationStatus.Answered, request.Source.SourceId, answerAnalysis.Id, NowUtc, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Before_AnswerStillUnclear_MarksUnresolved_AndNeverAsksAgain()
    {
        var open = OpenClarification();
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        _analysisService.AnalyzeAnswerAsync(Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<ClarificationHistory>(), Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { Intent = EmailIntent.Other, NeedsClarification = true });

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest());

        result.AnswerAnalysis.ShouldNotBeNull();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("does not ask a second time");
        await _repository.Received(1).TryResolveAsync(
            open.Id, InboundClarificationStatus.Unresolved, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _composer.DidNotReceiveWithAnyArgs().ComposeAsync(default!, default!, default);
        await _messengerSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task Before_AnswerStillUnclear_NeverReachesTheOrchestratorWithHighConfidence()
    {
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(OpenClarification());
        _analysisService.AnalyzeAnswerAsync(Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<ClarificationHistory>(), Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { Intent = EmailIntent.WorkCancellation, Confidence = EmailConfidence.High, NeedsClarification = true });

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest());

        result.AnswerAnalysis.ShouldNotBeNull();
        result.AnswerAnalysis.Confidence.ShouldBe(EmailConfidence.Low);
    }

    [Test]
    public async Task Before_AnswerAnalysisFailed_MarksUnresolved()
    {
        var open = OpenClarification();
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(open);
        _analysisService.AnalyzeAnswerAsync(Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<ClarificationHistory>(), Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { Intent = EmailIntent.Other, FailureReason = "LLM call failed: down" });

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest());

        result.AnswerAnalysis.ShouldNotBeNull();
        result.AnswerAnalysis.Confidence.ShouldBe(EmailConfidence.Low);
        await _repository.Received(1).TryResolveAsync(
            open.Id, InboundClarificationStatus.Unresolved, Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Before_SweepWonTheRace_ReturnsExpiryNote_AndTheMessageIsAnalyzedNormally()
    {
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(OpenClarification());
        _analysisService.AnalyzeAnswerAsync(Arg.Any<Guid>(), Arg.Any<EntityTypeEnum>(), Arg.Any<InboundSource>(), Arg.Any<ClarificationHistory>(), Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { Intent = EmailIntent.WorkCancellation });
        _repository.TryResolveAsync(Arg.Any<Guid>(), Arg.Any<InboundClarificationStatus>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest());

        result.AnswerAnalysis.ShouldBeNull();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("expired unanswered");
    }

    [Test]
    public async Task Before_RetryOfTheOriginalMessage_ReturnsNone()
    {
        var originalSourceId = Guid.NewGuid();
        _repository.GetOpenByClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(OpenClarification(originalSourceId));

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest(originalSourceId));

        result.ShouldBe(ClarificationPreAnalysis.None);
        await _analysisService.DidNotReceiveWithAnyArgs().AnalyzeAnswerAsync(default, default, default!, default!, default);
    }

    [Test]
    public async Task Before_RecentlyExpiredQuestion_IsNoted()
    {
        var expired = OpenClarification();
        expired.Status = InboundClarificationStatus.Expired;
        _repository.GetLatestExpiredByClientSinceAsync(ClientId, NowUtc.AddHours(-24), Arg.Any<CancellationToken>()).Returns(expired);

        var result = await _coordinator.BeforeAnalysisAsync(MessengerRequest());

        result.AnswerAnalysis.ShouldBeNull();
        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("expired unanswered");
    }

    [Test]
    public async Task Before_EmailReplyToAnExpiredQuestion_IsNotedThroughTheThreadHeaders()
    {
        var expired = OpenClarification();
        expired.Status = InboundClarificationStatus.Expired;
        _repository.GetLatestByEmailMessageIdsAsync(
                ClientId, Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("original-1@example.com")), Arg.Any<CancellationToken>())
            .Returns(expired);
        var request = EmailRequest(new ClarificationEmailThread(
            "answer-1@example.com", "<question-1@klacks.example>", "<original-1@example.com> <question-1@klacks.example>", false));

        var result = await _coordinator.BeforeAnalysisAsync(request);

        result.NotifierContext.ShouldNotBeNull();
        result.NotifierContext.ShouldContain("expired unanswered");
        await _repository.DidNotReceiveWithAnyArgs().GetLatestExpiredByClientSinceAsync(default, default, default);
    }

    [Test]
    public async Task Before_TakenOverClarification_LeavesTheMessageToTheRegularAnalysis()
    {
        var takenOver = OpenClarification();
        takenOver.Status = InboundClarificationStatus.TakenOver;
        _repository.GetLatestByEmailMessageIdsAsync(ClientId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(takenOver);
        var request = EmailRequest(new ClarificationEmailThread("answer-2@example.com", "<original-1@example.com>", null, false));

        var result = await _coordinator.BeforeAnalysisAsync(request);

        result.ShouldBe(ClarificationPreAnalysis.None);
    }

    [Test]
    public async Task Before_RepositoryThrows_ReturnsNone()
    {
        _repository.GetOpenByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<InboundClarification?>(_ => throw new InvalidOperationException("db down"));

        (await _coordinator.BeforeAnalysisAsync(MessengerRequest())).ShouldBe(ClarificationPreAnalysis.None);
    }

    [Test]
    public async Task Before_Cancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _repository.GetOpenByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<InboundClarification?>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _coordinator.BeforeAnalysisAsync(MessengerRequest(), cts.Token));
    }

    [Test]
    public void ThreadMessageIds_CollectsInReplyToAndReferencesWithoutBrackets()
    {
        var ids = ClarificationCoordinator.ThreadMessageIds(
            new ClarificationEmailThread("answer@example.com", "<b@example.com>", "a@example.com <b@example.com>", false));

        ids.ShouldBe(new[] { "b@example.com", "a@example.com" }, ignoreOrder: true);
    }

    [Test]
    public void ThreadMessageIds_KeepsOnlyTheLastReferences_AndDropsOversizedIds()
    {
        var references = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"<ref-{i}@example.com>"));
        var oversized = new string('a', InboundClarificationConstants.MaxStoredEmailMessageIdLength) + "@example.com";

        var ids = ClarificationCoordinator.ThreadMessageIds(
            new ClarificationEmailThread("answer@example.com", $"<{oversized}>", references, false));

        ids.Count.ShouldBe(InboundClarificationConstants.MaxReferencesCount);
        ids.ShouldContain("ref-30@example.com");
        ids.ShouldNotContain("ref-1@example.com");
        ids.ShouldNotContain(oversized);
    }
}
