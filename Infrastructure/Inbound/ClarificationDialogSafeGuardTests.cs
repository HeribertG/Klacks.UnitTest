// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationDialogSafeGuard, the shared wrapper both channel adapters use around the
/// clarification dialog: a coordinator that is missing from DI or throws degrades to None (before the
/// analysis) or Continue (after the analysis) and logs a warning, the coordinator's results and the
/// in-memory analysis instance are passed through unchanged, and cancellation escapes only when the
/// caller's token is cancelled.
/// </summary>

using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationDialogSafeGuardTests
{
    private IClarificationCoordinator _coordinator = null!;
    private ILogger _logger = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _coordinator = Substitute.For<IClarificationCoordinator>();
        _logger = Substitute.For<ILogger>();
        var services = new ServiceCollection();
        services.AddSingleton(_coordinator);
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    private static ClarificationRequest Request() => new(
        ClientId: Guid.NewGuid(),
        ClientType: EntityTypeEnum.Employee,
        Source: new InboundSource(
            SourceId: Guid.NewGuid(),
            SourceKind: InboundSourceKind.Email,
            Channel: "Email",
            SenderDisplay: "Anna Muster",
            Subject: "Heute",
            Body: "Ich bin krank",
            ReceivedAt: new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc)),
        ReplyChannel: "Email",
        SenderAddress: "anna@example.com",
        EmailThread: null);

    [Test]
    public async Task Before_ReturnsTheCoordinatorResult()
    {
        var request = Request();
        var answer = ClarificationPreAnalysis.Answer(new InboundAnalysis(), "answer");
        _coordinator.BeforeAnalysisAsync(request, Arg.Any<CancellationToken>()).Returns(answer);

        var result = await ClarificationDialogSafeGuard.BeforeAnalysisSafelyAsync(_provider, request, _logger, CancellationToken.None);

        result.ShouldBeSameAs(answer);
    }

    [Test]
    public async Task After_PassesTheSameAnalysisInstance_AndReturnsTheCoordinatorResult()
    {
        var request = Request();
        var analysis = new InboundAnalysis { NeedsClarification = true };
        _coordinator.AfterAnalysisAsync(request, Arg.Is<InboundAnalysis>(a => ReferenceEquals(a, analysis)), Arg.Any<CancellationToken>())
            .Returns(ClarificationPostAnalysis.Sent);

        var result = await ClarificationDialogSafeGuard.AfterAnalysisSafelyAsync(
            _provider, request, analysis, _logger, CancellationToken.None);

        result.ShouldBeSameAs(ClarificationPostAnalysis.Sent);
    }

    [Test]
    public async Task CoordinatorNotRegistered_DegradesToNoneAndContinue()
    {
        using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var request = Request();

        var before = await ClarificationDialogSafeGuard.BeforeAnalysisSafelyAsync(emptyProvider, request, _logger, CancellationToken.None);
        var after = await ClarificationDialogSafeGuard.AfterAnalysisSafelyAsync(
            emptyProvider, request, new InboundAnalysis(), _logger, CancellationToken.None);

        before.ShouldBeSameAs(ClarificationPreAnalysis.None);
        after.ShouldBeSameAs(ClarificationPostAnalysis.Continue);
        _logger.Received(2).Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task CoordinatorThrows_DegradesToNoneAndContinue_AndLogsAWarning()
    {
        var request = Request();
        _coordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPreAnalysis>(_ => throw new InvalidOperationException("boom"));
        _coordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPostAnalysis>(_ => throw new InvalidOperationException("boom"));

        var before = await ClarificationDialogSafeGuard.BeforeAnalysisSafelyAsync(_provider, request, _logger, CancellationToken.None);
        var after = await ClarificationDialogSafeGuard.AfterAnalysisSafelyAsync(
            _provider, request, new InboundAnalysis(), _logger, CancellationToken.None);

        before.ShouldBeSameAs(ClarificationPreAnalysis.None);
        after.ShouldBeSameAs(ClarificationPostAnalysis.Continue);
        _logger.Received(2).Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task CancellationWithACancelledToken_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _coordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPreAnalysis>(_ => throw new OperationCanceledException(cts.Token));
        _coordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPostAnalysis>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ClarificationDialogSafeGuard.BeforeAnalysisSafelyAsync(_provider, Request(), _logger, cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            ClarificationDialogSafeGuard.AfterAnalysisSafelyAsync(_provider, Request(), new InboundAnalysis(), _logger, cts.Token));
    }

    [Test]
    public async Task CancellationWhileTheTokenIsNotCancelled_DegradesToNoneAndContinue()
    {
        _coordinator.BeforeAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPreAnalysis>(_ => throw new OperationCanceledException());
        _coordinator.AfterAnalysisAsync(Arg.Any<ClarificationRequest>(), Arg.Any<InboundAnalysis>(), Arg.Any<CancellationToken>())
            .Returns<ClarificationPostAnalysis>(_ => throw new OperationCanceledException());

        var before = await ClarificationDialogSafeGuard.BeforeAnalysisSafelyAsync(_provider, Request(), _logger, CancellationToken.None);
        var after = await ClarificationDialogSafeGuard.AfterAnalysisSafelyAsync(
            _provider, Request(), new InboundAnalysis(), _logger, CancellationToken.None);

        before.ShouldBeSameAs(ClarificationPreAnalysis.None);
        after.ShouldBeSameAs(ClarificationPostAnalysis.Continue);
    }
}
