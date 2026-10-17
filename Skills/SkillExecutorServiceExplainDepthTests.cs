// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The explanation depth on the SkillExecutionContext reaches the generic dispatcher unchanged, so a knowledge happen
/// picks its stages for the calling user; a context without depth hands null on (treated like a beginner downstream).
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceExplainDepthTests
{
    private const string SkillName = "explain_group_model";
    private const string KnowledgeHandlerType = "knowledge-happen";
    private const string HandlerConfig = "{\"memoryKey\":\"explain_group_model\"}";

    private IGenericSkillDispatcher _genericDispatcher = null!;
    private SkillExecutorService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var registry = Substitute.For<ISkillRegistry>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        var autonomyGate = Substitute.For<IAutonomyGate>();
        autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        _genericDispatcher.CanHandle(KnowledgeHandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                KnowledgeHandlerType, HandlerConfig,
                Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ran"));
        registry.GetSkillByName(SkillName).Returns(
            new SkillDescriptor(SkillName, "test", SkillCategory.Query, [], [], [], null)
            {
                HandlerType = KnowledgeHandlerType,
                HandlerConfig = HandlerConfig
            });

        _sut = new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            Substitute.For<ISkillScenarioScopeGuard>(),
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillExecutionContext Context(ExplainDepthProfile? depth) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "planner",
        UserPermissions = new List<string>(),
        ExplainDepth = depth
    };

    [Test]
    public async Task ContextDepth_IsHandedToTheDispatcher()
    {
        var depth = new ExplainDepthProfile(ExplainExperienceLevel.Advanced, true);

        await _sut.ExecuteAsync(new SkillInvocation { SkillName = SkillName, Parameters = new() }, Context(depth));

        await _genericDispatcher.Received(1).ExecuteAsync(
            KnowledgeHandlerType, HandlerConfig,
            Arg.Any<Dictionary<string, object>>(), depth, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ContextWithoutDepth_HandsNullToTheDispatcher()
    {
        await _sut.ExecuteAsync(new SkillInvocation { SkillName = SkillName, Parameters = new() }, Context(null));

        await _genericDispatcher.Received(1).ExecuteAsync(
            KnowledgeHandlerType, HandlerConfig,
            Arg.Any<Dictionary<string, object>>(), (ExplainDepthProfile?)null, Arg.Any<CancellationToken>());
    }
}
