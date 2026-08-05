// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Sentinel probe contract: the synthetic turn must produce a persisted tier-1 finding through
/// the real evaluator (probe returns true), a dead evaluator is detected as a missing finding
/// (probe returns false), Off mode skips the probe entirely, and the sentinel agent id never
/// reaches the reflection lesson path even in Active mode with a repeated scope.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class AnswerGroundingSentinelProbeTests
{
    private List<AnswerGroundingFinding> _findings = null!;
    private IAnswerGroundingRepository _repository = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;

    [SetUp]
    public void SetUp()
    {
        _findings = new List<AnswerGroundingFinding>();
        _repository = Substitute.For<IAnswerGroundingRepository>();
        _repository.AddFindingAsync(Arg.Do<AnswerGroundingFinding>(f => _findings.Add(f)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repository.CountFindingsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => _findings.Count(f =>
                f.AgentId == ci.ArgAt<Guid>(0) &&
                f.ScopeKey == ci.ArgAt<string>(1) &&
                f.PrimaryClaimKind == ci.ArgAt<string>(2) &&
                f.Tier == ci.ArgAt<int>(3)));
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _memoryRepository.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
        _backgroundTasks = Substitute.For<ILLMBackgroundTaskService>();
    }

    private AnswerGroundingSentinelProbe Probe(string mode = "Shadow")
    {
        var options = new AnswerGroundingOptions(mode);
        var evaluator = new AnswerGroundingEvaluator(options, _repository, _memoryRepository,
            _backgroundTasks, NullLogger<AnswerGroundingEvaluator>.Instance);
        return new AnswerGroundingSentinelProbe(options, evaluator, _repository,
            NullLogger<AnswerGroundingSentinelProbe>.Instance);
    }

    [Test]
    public async Task Probe_ProducesAPersistedTierOneSentinelFinding()
    {
        var alive = await Probe().RunAsync();

        alive.ShouldBeTrue();
        _findings.Count.ShouldBe(1);
        _findings[0].AgentId.ShouldBe(AnswerGroundingSentinel.AgentId);
        _findings[0].Tier.ShouldBe(AnswerGroundingEvaluator.UuidTier);
        _findings[0].ScopeKey.ShouldBe(AnswerGroundingEvaluator.NoToolScopeKey);
        _findings[0].PrimaryClaimKind.ShouldBe(nameof(AnswerClaimKind.Uuid));
    }

    [Test]
    public async Task DeadEvaluator_IsDetectedAsMissingFinding()
    {
        var deadEvaluator = Substitute.For<IAnswerGroundingEvaluator>();
        var probe = new AnswerGroundingSentinelProbe(new AnswerGroundingOptions("Shadow"), deadEvaluator,
            _repository, NullLogger<AnswerGroundingSentinelProbe>.Instance);

        var alive = await probe.RunAsync();

        alive.ShouldBeFalse();
    }

    [Test]
    public async Task PersistenceFailure_IsDetectedAsMissingFinding()
    {
        _repository = Substitute.For<IAnswerGroundingRepository>();
        _repository.AddFindingAsync(Arg.Any<AnswerGroundingFinding>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var alive = await Probe().RunAsync();

        alive.ShouldBeFalse();
    }

    [Test]
    public async Task OffMode_SkipsTheProbe()
    {
        var evaluator = Substitute.For<IAnswerGroundingEvaluator>();
        var probe = new AnswerGroundingSentinelProbe(new AnswerGroundingOptions("Off"), evaluator,
            _repository, NullLogger<AnswerGroundingSentinelProbe>.Instance);

        var alive = await probe.RunAsync();

        alive.ShouldBeTrue();
        await evaluator.DidNotReceive().EvaluateAsync(
            Arg.Any<Guid>(), Arg.Any<LLMContext>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<LLMFunctionCall>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SentinelAgent_NeverTriggersALesson_EvenInActiveModeWithRepeatedScope()
    {
        _findings.Add(new AnswerGroundingFinding
        {
            AgentId = AnswerGroundingSentinel.AgentId,
            ScopeKey = AnswerGroundingEvaluator.NoToolScopeKey,
            PrimaryClaimKind = nameof(AnswerClaimKind.Uuid),
            Tier = AnswerGroundingEvaluator.UuidTier
        });

        var alive = await Probe("Active").RunAsync();

        alive.ShouldBeTrue();
        _backgroundTasks.DidNotReceive().TriggerReflection(Arg.Any<TurnReflectionRequest>());
    }
}
