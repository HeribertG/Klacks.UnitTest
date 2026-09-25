// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A skill the stop cut short is recorded as what it was: a Cancelled usage row, written with a token that
/// cannot itself be cancelled (the token of the call is already cancelled, so writing with it would drop
/// the very row that says why the call ended), and not as an Exception, which would count the stop as a
/// skill failure in the effectiveness figures.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillExecutorCancellationTests
{
    private const string SkillName = "probe_slow_read";

    private ISkillUsageTracker _usageTracker = null!;
    private SkillExecutorService _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _usageTracker = Substitute.For<ISkillUsageTracker>();

        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName,
            string.Empty,
            SkillCategory.Query,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            typeof(CancellingSkill)));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(CancellingSkill)).Returns(new CancellingSkill());

        _executor = new SkillExecutorService(
            registry,
            _usageTracker,
            serviceProvider,
            Substitute.For<IGenericSkillDispatcher>(),
            Substitute.For<IAutonomyGate>(),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            NullLogger<SkillExecutorService>.Instance);
    }

    [Test]
    public async Task ASkillTheStopCutShort_IsTrackedAsCancelledWithATokenNoCancellationCanReach()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = await _executor.ExecuteAsync(Invocation(), Context(), stop.Token);

        result.Type.ShouldBe(SkillResultType.Cancelled);
        await _usageTracker.Received(1).TrackFailureAsync(
            SkillName,
            SkillFailureKind.Cancelled,
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>?>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<SkillCategory>(),
            Arg.Is<CancellationToken>(token => !token.CanBeCanceled));
        await _usageTracker.DidNotReceive().TrackFailureAsync(
            Arg.Any<string>(),
            SkillFailureKind.Exception,
            Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>?>(),
            Arg.Any<string?>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<SkillCategory>(),
            Arg.Any<CancellationToken>());
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
        UserName = nameof(SkillExecutorCancellationTests),
        UserPermissions = Array.Empty<string>()
    };

    private sealed class CancellingSkill : BaseSkillImplementation
    {
        public override Task<SkillResult> ExecuteAsync(
            SkillExecutionContext context,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SkillResult.SuccessResult(null));
        }
    }
}
