// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillExecutorService parameter validation wiring: an argument that is not parsable
/// as its declared type is rejected before the autonomy gate and before dispatch, a parsable string
/// representation ("5" for an Integer) passes through to execution, and missing required parameters
/// are still reported.
/// </summary>

using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceValidationTests
{
    private const string TestSkillName = "test_typed_skill";
    private const string HandlerType = "generic";

    private ISkillRegistry _registry = null!;
    private ISkillUsageTracker _usageTracker = null!;
    private IServiceProvider _serviceProvider = null!;
    private IGenericSkillDispatcher _genericDispatcher = null!;
    private IAutonomyGate _autonomyGate = null!;
    private IEntityChangeNotifier _entityChangeNotifier = null!;
    private SkillExecutorService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _usageTracker = Substitute.For<ISkillUsageTracker>();
        _serviceProvider = Substitute.For<IServiceProvider>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _autonomyGate = Substitute.For<IAutonomyGate>();
        _entityChangeNotifier = Substitute.For<IEntityChangeNotifier>();
        _sut = new SkillExecutorService(
            _registry,
            _usageTracker,
            _serviceProvider,
            _genericDispatcher,
            _autonomyGate,
            _entityChangeNotifier,
            Substitute.For<ILogger<SkillExecutorService>>());

        var descriptor = new SkillDescriptor(
            TestSkillName,
            "test",
            SkillCategory.Crud,
            new[]
            {
                new SkillParameter("count", "test", SkillParameterType.Integer, Required: true),
                new SkillParameter("date", "test", SkillParameterType.Date, Required: false)
            },
            [],
            [],
            null)
        {
            HandlerType = HandlerType,
            HandlerConfig = "{}"
        };

        _registry.GetSkillByName(TestSkillName).Returns(descriptor);
        _genericDispatcher.CanHandle(HandlerType).Returns(true);
        _genericDispatcher.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ok"));
        _autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Klacks.Api.Domain.Constants.Roles.Admin }
    };

    private Task<SkillResult> ExecuteAsync(Dictionary<string, object> parameters) =>
        _sut.ExecuteAsync(
            new SkillInvocation { SkillName = TestSkillName, Parameters = parameters },
            Ctx());

    [Test]
    public async Task UnparsableTypedParameter_IsRejected_BeforeGateAndDispatch()
    {
        var result = await ExecuteAsync(new Dictionary<string, object> { ["count"] = "abc" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("'count'").And.Contain("abc"));
        await _autonomyGate.DidNotReceive().CheckAsync(
            Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
        await _genericDispatcher.DidNotReceive().ExecuteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ParsableStringRepresentation_PassesThroughToDispatch()
    {
        var result = await ExecuteAsync(new Dictionary<string, object>
        {
            ["count"] = "5",
            ["date"] = "2026-08-01"
        });

        Assert.That(result.Success, Is.True);
        await _genericDispatcher.Received(1).ExecuteAsync(
            HandlerType, "{}", Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingRequiredParameter_IsStillReported()
    {
        var result = await ExecuteAsync(new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Missing required parameters").And.Contain("count"));
    }

    [Test]
    public async Task InvalidOptionalTypedParameter_IsRejected()
    {
        var result = await ExecuteAsync(new Dictionary<string, object>
        {
            ["count"] = 3,
            ["date"] = "not-a-date"
        });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("'date'").And.Contain("not-a-date"));
    }
}
