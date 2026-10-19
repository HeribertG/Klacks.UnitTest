// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for how SkillExecutorService answers exceptions thrown by a skill or the handler behind it: a
/// user-facing rejection (IUserFacingRejection, e.g. the sealed-day InvalidRequestException) becomes a plain error
/// result carrying exactly the exception message, logged as a warning, while every other exception keeps the
/// "Execution error" result and the error log.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillExecutorServiceRejectionTests
{
    private const string TestSkillName = "test_rejecting_skill";
    private const string HandlerType = "generic";
    private const string SealedMessage = "Day 2026-10-12 is sealed and cannot be modified.";
    private const string ExecutionErrorPrefix = "Execution error:";

    private IGenericSkillDispatcher _genericDispatcher = null!;
    private ISkillUsageTracker _usageTracker = null!;
    private ILogger<SkillExecutorService> _logger = null!;
    private SkillExecutorService _sut = null!;

    [SetUp]
    public void Setup()
    {
        var registry = Substitute.For<ISkillRegistry>();
        var autonomyGate = Substitute.For<IAutonomyGate>();
        _usageTracker = Substitute.For<ISkillUsageTracker>();
        _genericDispatcher = Substitute.For<IGenericSkillDispatcher>();
        _logger = Substitute.For<ILogger<SkillExecutorService>>();
        _sut = new SkillExecutorService(
            registry,
            _usageTracker,
            Substitute.For<IServiceProvider>(),
            _genericDispatcher,
            autonomyGate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Assistant.ISkillScenarioScopeGuard>(),
            _logger);

        var descriptor = new SkillDescriptor(TestSkillName, "test", SkillCategory.Crud, [], [], [], null)
        {
            HandlerType = HandlerType,
            HandlerConfig = "{}"
        };

        registry.GetSkillByName(TestSkillName).Returns(descriptor);
        _genericDispatcher.CanHandle(HandlerType).Returns(true);
        autonomyGate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
    }

    private void DispatcherThrows(Exception exception)
    {
        _genericDispatcher.ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<ExplainDepthProfile?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillResult>>(_ => throw exception);
    }

    private Task<SkillResult> ExecuteAsync() =>
        _sut.ExecuteAsync(
            new SkillInvocation { SkillName = TestSkillName, Parameters = new Dictionary<string, object>() },
            new SkillExecutionContext
            {
                UserId = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                UserName = "admin",
                UserPermissions = new List<string> { Roles.Admin }
            });

    private List<LogLevel> LoggedLevels() =>
        _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => (LogLevel)call.GetArguments()[0]!)
            .ToList();

    private static IEnumerable<TestCaseData> UserFacingRejections()
    {
        yield return new TestCaseData(new InvalidRequestException(SealedMessage)).SetName("InvalidRequestException");
        yield return new TestCaseData(new ClientImportRejectedException("code", SealedMessage)).SetName("InvalidRequestExceptionSubclass");
        yield return new TestCaseData(new BadRequestException(SealedMessage)).SetName("BadRequestException");
        yield return new TestCaseData(new NotFoundException(SealedMessage)).SetName("NotFoundException");
        yield return new TestCaseData(new ConflictException(SealedMessage)).SetName("ConflictException");
        yield return new TestCaseData(new ForbiddenException(SealedMessage)).SetName("ForbiddenException");
        yield return new TestCaseData(new ContainerLockedException(SealedMessage)).SetName("ContainerLockedException");
    }

    [TestCaseSource(nameof(UserFacingRejections))]
    public async Task UserFacingRejection_ReturnsPlainErrorWithExceptionMessage_LoggedAsWarning(Exception rejection)
    {
        DispatcherThrows(rejection);

        var result = await ExecuteAsync();

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldBe(SealedMessage);
        LoggedLevels().ShouldContain(LogLevel.Warning);
        LoggedLevels().ShouldNotContain(LogLevel.Error);
        await _usageTracker.Received(1).TrackFailureAsync(
            TestSkillName, SkillFailureKind.Exception, Arg.Any<SkillExecutionContext>(),
            Arg.Any<Dictionary<string, object>?>(), SealedMessage, Arg.Any<TimeSpan>(),
            Arg.Any<SkillCategory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UserFacingRejection_CarriesRejectedErrorCode_WithoutExceptionType()
    {
        DispatcherThrows(new InvalidRequestException(SealedMessage));

        var result = await ExecuteAsync();

        var data = result.Metadata.ShouldNotBeNull();
        data[SkillErrorKeys.ErrorCode].ShouldBe(SkillErrorKeys.Rejected);
        data[SkillErrorKeys.SkillName].ShouldBe(TestSkillName);
        data.ShouldNotContainKey(SkillErrorKeys.ExceptionType);
    }

    private static IEnumerable<TestCaseData> UnexpectedExceptions()
    {
        yield return new TestCaseData(new InvalidOperationException(SealedMessage)).SetName("InvalidOperationException");
        yield return new TestCaseData(new KeyNotFoundException(SealedMessage)).SetName("KeyNotFoundException");
        yield return new TestCaseData(new UnauthorizedException(SealedMessage)).SetName("UnauthorizedException");
        yield return new TestCaseData(new DatabaseUpdateException(SealedMessage, new Exception())).SetName("DatabaseUpdateException");
    }

    [TestCaseSource(nameof(UnexpectedExceptions))]
    public async Task UnexpectedException_KeepsExecutionErrorAndErrorLog(Exception exception)
    {
        DispatcherThrows(exception);

        var result = await ExecuteAsync();

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith(ExecutionErrorPrefix);
        var data = result.Metadata.ShouldNotBeNull();
        data[SkillErrorKeys.ErrorCode].ShouldBe(SkillErrorKeys.ExecutionError);
        data[SkillErrorKeys.ExceptionType].ShouldBe(exception.GetType().Name);
        LoggedLevels().ShouldContain(LogLevel.Error);
    }
}
