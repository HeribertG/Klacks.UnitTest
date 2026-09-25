// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A skill the stop did not interrupt in time (it ignores the token and runs on, or it fails after the stop
/// was already requested) still leaves its usage row: the row is written with a token that cannot be
/// cancelled, so a stop neither loses it nor makes the tracker log an ERROR for a write that only failed
/// because the stop token was already cancelled. Runs against the real tracker with a repository that, like
/// the database, throws when it is handed a cancelled token.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillExecutorTrackingAfterStopTests
{
    private const string SkillName = "probe_read_that_outlives_the_stop";

    private ISkillUsageRepository _repository = null!;
    private RecordingLogger<SkillUsageTrackerService> _trackerLogger = null!;
    private IServiceProvider _serviceProvider = null!;
    private SkillExecutorService _executor = null!;
    private List<SkillUsageRecord> _written = null!;

    [SetUp]
    public void SetUp()
    {
        _written = [];
        _repository = Substitute.For<ISkillUsageRepository>();
        _repository.AddAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            _written.Add(call.Arg<SkillUsageRecord>());
            return Task.CompletedTask;
        });
        _trackerLogger = new RecordingLogger<SkillUsageTrackerService>();
        var tracker = new SkillUsageTrackerService(
            _repository, Substitute.For<ISkillSequenceProactiveNotifier>(), _trackerLogger);

        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName,
            string.Empty,
            SkillCategory.Query,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            typeof(StopIgnoringSkill)));

        _serviceProvider = Substitute.For<IServiceProvider>();
        _executor = new SkillExecutorService(
            registry,
            tracker,
            _serviceProvider,
            Substitute.For<IGenericSkillDispatcher>(),
            Substitute.For<IAutonomyGate>(),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            NullLogger<SkillExecutorService>.Instance);
    }

    [Test]
    public async Task AReadThatFinishesAfterTheStop_StillWritesItsUsageRow()
    {
        using var stop = new CancellationTokenSource();
        _serviceProvider.GetService(typeof(StopIgnoringSkill)).Returns(new StopIgnoringSkill(stop, ThrowsNothing));

        var result = await _executor.ExecuteAsync(Invocation(), Context(), stop.Token);

        result.Success.ShouldBeTrue();
        _written.Count.ShouldBe(1);
        _written[0].SkillName.ShouldBe(SkillName);
        _written[0].Success.ShouldBeTrue();
        _trackerLogger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }

    [Test]
    public async Task ASkillErrorAfterTheStop_StillWritesItsUsageRow()
    {
        using var stop = new CancellationTokenSource();
        _serviceProvider.GetService(typeof(StopIgnoringSkill)).Returns(
            new StopIgnoringSkill(stop, () => throw new SkillException(SkillName, "A domain error.")));

        var result = await _executor.ExecuteAsync(Invocation(), Context(), stop.Token);

        result.Success.ShouldBeFalse();
        _written.Count.ShouldBe(1);
        _written[0].Success.ShouldBeFalse();
        _trackerLogger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }

    [Test]
    public async Task AnUnexpectedErrorAfterTheStop_StillWritesItsUsageRow()
    {
        using var stop = new CancellationTokenSource();
        _serviceProvider.GetService(typeof(StopIgnoringSkill)).Returns(
            new StopIgnoringSkill(stop, () => throw new InvalidOperationException("Boom.")));

        var result = await _executor.ExecuteAsync(Invocation(), Context(), stop.Token);

        result.Success.ShouldBeFalse();
        _written.Count.ShouldBe(1);
        _written[0].Success.ShouldBeFalse();
        _trackerLogger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }

    private static void ThrowsNothing()
    {
    }

    private static SkillInvocation Invocation() => new()
    {
        SkillName = SkillName,
        Parameters = new Dictionary<string, object>()
    };

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = nameof(SkillExecutorTrackingAfterStopTests),
        UserPermissions = Array.Empty<string>()
    };

    private sealed class StopIgnoringSkill(CancellationTokenSource stop, Action afterTheStop) : BaseSkillImplementation
    {
        public override Task<SkillResult> ExecuteAsync(
            SkillExecutionContext context,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            stop.Cancel();
            afterTheStop();
            return Task.FromResult(SkillResult.SuccessResult(null));
        }
    }
}
