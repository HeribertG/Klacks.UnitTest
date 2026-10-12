// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The REST execute-function handler normalizes function names with the same table as the chat loop
/// (SkillNameAliases): an alias reaches the bridge as its real skill, and a real skill name is never remapped. The
/// handler used to keep its own table, which sent the real skill get_user_context to get_user_permissions and
/// get_current_user to get_user_permissions, while the chat loop ran get_user_context for both.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class ExecuteLLMFunctionCommandHandlerAliasTests
{
    private ILLMSkillBridge _bridge = null!;
    private string? _calledSkill;

    [SetUp]
    public void Setup()
    {
        _calledSkill = null;
        _bridge = Substitute.For<ILLMSkillBridge>();
        _bridge.ExecuteSkillFromLLMCallAsync(
                Arg.Do<LLMFunctionCall>(call => _calledSkill = call.FunctionName),
                Arg.Any<SkillExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new SkillBridgeResult { Success = true, Message = "ok" });
    }

    [TestCase("get_current_user", "get_user_context")]
    [TestCase("getUserPermissions", "get_user_permissions")]
    [TestCase("create_client", "create_employee")]
    [TestCase("search_clients", "search_employees")]
    [TestCase("navigate_to_page", "navigate_to")]
    [TestCase("get_user_context", "get_user_context")]
    [TestCase("get_user_permissions", "get_user_permissions")]
    [TestCase("delete_shift", "delete_shift")]
    public async Task TheBridgeReceivesTheSkillTheChatLoopWouldRun(string requested, string expectedSkill)
    {
        var handler = new ExecuteLLMFunctionCommandHandler(
            Substitute.For<ILogger<ExecuteLLMFunctionCommandHandler>>(), _bridge);

        await handler.Handle(
            new ExecuteLLMFunctionCommand
            {
                FunctionName = requested,
                Parameters = new Dictionary<string, object>(),
                UserId = Guid.NewGuid().ToString(),
                UserRights = new List<string>()
            },
            CancellationToken.None);

        _calledSkill.ShouldBe(expectedSkill);
    }
}
