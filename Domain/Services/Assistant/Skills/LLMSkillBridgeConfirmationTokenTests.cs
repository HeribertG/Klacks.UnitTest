// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The bridge carries the one-time token of a confirmation result to the chat loop, so the loop knows every
/// token the turn issued no matter which skill minted it.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Adapters;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class LLMSkillBridgeConfirmationTokenTests
{
    private const string Token = "one-time-token";
    private const string SkillName = "update_membership";

    private ISkillExecutor _skillExecutor = null!;
    private LLMSkillBridge _bridge = null!;

    [SetUp]
    public void SetUp()
    {
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _bridge = new LLMSkillBridge(
            Substitute.For<ISkillRegistry>(),
            _skillExecutor,
            Substitute.For<ISkillAdapterFactory>(),
            Substitute.For<ILogger<LLMSkillBridge>>());
    }

    [Test]
    public async Task AConfirmationResult_CarriesItsToken()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Confirmation("Please confirm.", Token));

        var result = await _bridge.ExecuteSkillFromLLMCallAsync(
            new LLMFunctionCall { FunctionName = SkillName }, Context());

        result.ConfirmationToken.ShouldBe(Token);
    }

    [Test]
    public async Task AnOrdinaryResult_CarriesNoToken()
    {
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "Done."));

        var result = await _bridge.ExecuteSkillFromLLMCallAsync(
            new LLMFunctionCall { FunctionName = SkillName }, Context());

        result.ConfirmationToken.ShouldBeNull();
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = nameof(LLMSkillBridgeConfirmationTokenTests),
        UserPermissions = Array.Empty<string>()
    };
}
