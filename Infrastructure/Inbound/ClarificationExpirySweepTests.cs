// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationExpirySweep.RunCycleAsync: an overdue open clarification is expired and
/// the planners get the "question unanswered" notice with company-local times; a clarification answered
/// meanwhile (conditional transition lost) is not reported; deadlines missed during downtime are all
/// caught up in the first cycle; a failing notification does not stop the remaining rows; a failing
/// repository is logged, not thrown; a cancelled cycle rethrows the cancellation instead of swallowing it.
/// The retention step clears the original text of rounds closed before now minus the configured days
/// (default 30, clamped to 1..3650), also when nothing is due, and is independent of the expire step in
/// both directions: a failure of one never stops or changes the outcome of the other.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationExpirySweepTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);

    private IInboundClarificationRepository _repository = null!;
    private IInboundAnalysisNotifier _notifier = null!;
    private IInstallationLanguageResolver _languageResolver = null!;
    private ServiceProvider _serviceProvider = null!;
    private ClarificationExpirySweep _sweep = null!;

    private static TimeZoneInfo FixedOffsetZone(int offsetHours)
    {
        var id = $"Test{offsetHours:+0;-0;0}";
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(offsetHours), id, id);
    }

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IInboundClarificationRepository>();
        _repository.TryResolveAsync(Arg.Any<Guid>(), Arg.Any<InboundClarificationStatus>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _notifier = Substitute.For<IInboundAnalysisNotifier>();
        _languageResolver = Substitute.For<IInstallationLanguageResolver>();
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(LanguageConfig.DefaultLanguageFallback);

        var services = new ServiceCollection();
        services.AddSingleton(_repository);
        services.AddSingleton(_notifier);
        services.AddSingleton<IClarificationTextService>(
            new ClarificationTextService(_languageResolver, NullLogger<ClarificationTextService>.Instance));
        services.AddSingleton<ICompanyClock>(new FixedCompanyClock(new DateTimeOffset(NowUtc), FixedOffsetZone(2)));
        _serviceProvider = services.BuildServiceProvider();

        _sweep = new ClarificationExpirySweep(
            _serviceProvider,
            new SettableTimeProvider(NowUtc),
            Options.Create(new BackgroundServiceOptions { InboundClarificationSweep = true }),
            NullLogger<ClarificationExpirySweep>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sweep.Dispose();
        _serviceProvider.Dispose();
    }

    private static InboundClarification Due(DateTime deadlineUtc, string sender = "Anna Muster") => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Status = InboundClarificationStatus.Open,
        SenderDisplay = sender,
        Question = "Heißt das, du kannst deinen Spätdienst heute nicht antreten?",
        OriginalText = "Ich fühle mich nicht gut.",
        ShiftContext = "Spätdienst 2026-09-23 14:00-22:00",
        AskedAt = deadlineUtc.AddMinutes(-60),
        DeadlineAt = deadlineUtc
    };

    [Test]
    public async Task OverdueOpenClarification_IsExpired_AndThePlannersAreTold()
    {
        var clarification = Due(NowUtc.AddMinutes(-1));
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(new[] { clarification });

        var expired = await _sweep.RunCycleAsync(CancellationToken.None);

        expired.ShouldBe(1);
        await _repository.Received(1).TryResolveAsync(
            clarification.Id, InboundClarificationStatus.Expired, null, null, NowUtc, Arg.Any<CancellationToken>());
        await _notifier.Received(1).NotifyMessageAsync(
            Arg.Is<string>(m => m.Contains("Question unanswered") && m.Contains("Anna Muster") && m.Contains(clarification.Question)
                                && m.Contains("2026-09-23 09:59") && m.Contains("Spätdienst 2026-09-23 14:00-22:00")),
            Arg.Any<CancellationToken>());
    }

    [TestCase("de", "Frage unbeantwortet")]
    [TestCase("fr", "Question sans réponse")]
    [TestCase("it", "Domanda senza risposta")]
    public async Task OverdueOpenClarification_TellsThePlannersInTheInstallationLanguage(string language, string heading)
    {
        _languageResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(language);
        var clarification = Due(NowUtc.AddMinutes(-1));
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(new[] { clarification });

        await _sweep.RunCycleAsync(CancellationToken.None);

        await _notifier.Received(1).NotifyMessageAsync(
            Arg.Is<string>(m => m.Contains(heading) && m.Contains("Anna Muster") && m.Contains("2026-09-23 09:59")
                                && !m.Contains("Question unanswered") && !m.Contains("{")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnsweredMeanwhile_IsNotReported()
    {
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(new[] { Due(NowUtc.AddMinutes(-1)) });
        _repository.TryResolveAsync(Arg.Any<Guid>(), Arg.Any<InboundClarificationStatus>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var expired = await _sweep.RunCycleAsync(CancellationToken.None);

        expired.ShouldBe(0);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyMessageAsync(default!, default);
    }

    [Test]
    public async Task NothingDue_DoesNothing()
    {
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(Array.Empty<InboundClarification>());

        (await _sweep.RunCycleAsync(CancellationToken.None)).ShouldBe(0);
        await _notifier.DidNotReceiveWithAnyArgs().NotifyMessageAsync(default!, default);
    }

    [Test]
    public async Task DeadlinesMissedDuringDowntime_AreAllCaughtUpInTheFirstCycle()
    {
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>())
            .Returns(new[] { Due(NowUtc.AddHours(-5), "Anna Muster"), Due(NowUtc.AddHours(-3), "Ben Beispiel") });

        var expired = await _sweep.RunCycleAsync(CancellationToken.None);

        expired.ShouldBe(2);
        await _notifier.Received(2).NotifyMessageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingNotification_DoesNotStopTheRemainingRows()
    {
        var first = Due(NowUtc.AddMinutes(-2), "Anna Muster");
        var second = Due(NowUtc.AddMinutes(-1), "Ben Beispiel");
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(new[] { first, second });
        _notifier.NotifyMessageAsync(Arg.Is<string>(m => m.Contains("Anna Muster")), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("notifier down"));

        var expired = await _sweep.RunCycleAsync(CancellationToken.None);

        expired.ShouldBe(2);
        await _notifier.Received(1).NotifyMessageAsync(Arg.Is<string>(m => m.Contains("Ben Beispiel")), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingRepository_IsLoggedNotThrown()
    {
        _repository.GetOpenDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<InboundClarification>>(_ => throw new InvalidOperationException("db down"));

        (await _sweep.RunCycleAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Test]
    public async Task CancelledCycle_RethrowsTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _repository.GetOpenDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<InboundClarification>>(_ => throw new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _sweep.RunCycleAsync(cancellation.Token));
    }

    private ClarificationExpirySweep SweepWithRetentionDays(int retentionDays) => new(
        _serviceProvider,
        new SettableTimeProvider(NowUtc),
        Options.Create(new BackgroundServiceOptions
        {
            InboundClarificationSweep = true,
            InboundClarificationOriginalTextRetentionDays = retentionDays
        }),
        NullLogger<ClarificationExpirySweep>.Instance);

    [Test]
    public async Task Retention_ClearsTheTextOfRoundsClosedBeforeTheDefaultCutoff()
    {
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(Array.Empty<InboundClarification>());
        _repository.ClearOriginalTextAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(3);

        await _sweep.RunCycleAsync(CancellationToken.None);

        await _repository.Received(1).ClearOriginalTextAsync(
            NowUtc.AddDays(-InboundClarificationConstants.DefaultOriginalTextRetentionDays), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Retention_UsesTheConfiguredDays()
    {
        using var sweep = SweepWithRetentionDays(7);
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(Array.Empty<InboundClarification>());

        await sweep.RunCycleAsync(CancellationToken.None);

        await _repository.Received(1).ClearOriginalTextAsync(NowUtc.AddDays(-7), Arg.Any<CancellationToken>());
    }

    [TestCase(0, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(-30, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(int.MinValue, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(int.MaxValue, InboundClarificationConstants.MaxOriginalTextRetentionDays)]
    public async Task Retention_UnusableConfiguration_IsClampedInTheCutoff(int configuredDays, int expectedDays)
    {
        using var sweep = SweepWithRetentionDays(configuredDays);
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(Array.Empty<InboundClarification>());

        await sweep.RunCycleAsync(CancellationToken.None);

        await _repository.Received(1).ClearOriginalTextAsync(NowUtc.AddDays(-expectedDays), Arg.Any<CancellationToken>());
    }

    [TestCase(0, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(-1, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(int.MaxValue, InboundClarificationConstants.MaxOriginalTextRetentionDays)]
    [TestCase(30, 30)]
    [TestCase(InboundClarificationConstants.MinOriginalTextRetentionDays, InboundClarificationConstants.MinOriginalTextRetentionDays)]
    [TestCase(InboundClarificationConstants.MaxOriginalTextRetentionDays, InboundClarificationConstants.MaxOriginalTextRetentionDays)]
    public void ClampedRetentionDays_StaysInsideTheRange(int configuredDays, int expectedDays)
    {
        ClarificationExpirySweep.ClampedRetentionDays(configuredDays).ShouldBe(expectedDays);
    }

    [Test]
    public async Task Retention_FailingExpireStep_DoesNotStopIt()
    {
        _repository.GetOpenDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<InboundClarification>>(_ => throw new InvalidOperationException("db down"));

        (await _sweep.RunCycleAsync(CancellationToken.None)).ShouldBe(0);

        await _repository.Received(1).ClearOriginalTextAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Expire_FailingRetentionStep_DoesNotChangeTheExpireOutcome()
    {
        var clarification = Due(NowUtc.AddMinutes(-1));
        _repository.GetOpenDueAsync(NowUtc, Arg.Any<CancellationToken>()).Returns(new[] { clarification });
        _repository.ClearOriginalTextAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("db down"));

        var expired = await _sweep.RunCycleAsync(CancellationToken.None);

        expired.ShouldBe(1);
        await _notifier.Received(1).NotifyMessageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Retention_CancelledStep_RethrowsTheCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _repository.GetOpenDueAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<InboundClarification>());
        _repository.ClearOriginalTextAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _sweep.RunCycleAsync(cancellation.Token));
    }

    [TestCase(0)]
    [TestCase(-5)]
    [TestCase(int.MinValue)]
    public void ClampedSeconds_NonPositiveConfiguration_BecomesTheMinimum(int configuredSeconds)
    {
        ClarificationExpirySweep.ClampedSeconds(configuredSeconds)
            .ShouldBe(TimeSpan.FromSeconds(InboundClarificationConstants.MinSweepSeconds));
    }

    [TestCase(InboundClarificationConstants.MaxSweepSeconds + 1)]
    [TestCase(4_294_968)]
    [TestCase(int.MaxValue)]
    public void ClampedSeconds_OversizedConfiguration_BecomesTheMaximum(int configuredSeconds)
    {
        ClarificationExpirySweep.ClampedSeconds(configuredSeconds)
            .ShouldBe(TimeSpan.FromSeconds(InboundClarificationConstants.MaxSweepSeconds));
    }

    [TestCase(InboundClarificationConstants.MinSweepSeconds)]
    [TestCase(60)]
    [TestCase(InboundClarificationConstants.MaxSweepSeconds)]
    public void ClampedSeconds_ConfigurationInsideTheRange_IsKept(int configuredSeconds)
    {
        ClarificationExpirySweep.ClampedSeconds(configuredSeconds).ShouldBe(TimeSpan.FromSeconds(configuredSeconds));
    }
}
