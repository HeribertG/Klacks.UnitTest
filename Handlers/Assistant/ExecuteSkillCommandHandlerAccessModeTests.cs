// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The MCP access mode carried by ExecuteSkillCommand must reach the SkillExecutionContext, because
/// run_analysis' research sub-loop caps its toolset by it. A missing copy would silently turn every MCP
/// research call into an uncapped chat-style one.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class ExecuteSkillCommandHandlerAccessModeTests
{
    private ISkillExecutor _executor = null!;
    private ExecuteSkillCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _executor = Substitute.For<ISkillExecutor>();
        _executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ok"));
        _sut = new ExecuteSkillCommandHandler(
            _executor, new SkillMapper(), Substitute.For<ILogger<ExecuteSkillCommandHandler>>());
    }

    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    [TestCase(null)]
    public async Task Handle_CopiesTheExternalAgentAccessModeIntoTheContext(PersonalAccessTokenAccessMode? accessMode)
    {
        var command = new ExecuteSkillCommand(
            new SkillExecuteRequest { SkillName = "run_analysis", Parameters = new Dictionary<string, object>() },
            Guid.NewGuid(),
            Guid.NewGuid(),
            "caller",
            new List<string>(),
            AccessToken: null,
            ExternalAgentAccessMode: accessMode);

        await _sut.Handle(command, CancellationToken.None);

        await _executor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(context => context.ExternalAgentAccessMode == accessMode),
            Arg.Any<CancellationToken>());
    }
}
