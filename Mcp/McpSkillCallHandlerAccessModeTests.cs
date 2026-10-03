// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Enforcement of the personal access token access mode in McpSkillCallHandler: a Read token is refused
/// every non-read-only skill and confirm_pending_action before a token is minted or the skill runs, even
/// when the client never listed the tool; Write tokens and login JWTs behave as before.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpSkillCallHandlerAccessModeTests
{
    private const string ReadOnlySkill = "search_employees";
    private const string WriteSkill = "update_client";

    private IMediator _mediator = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IInternalTokenIssuer _tokenIssuer = null!;
    private McpSkillCallHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SkillExecuteResponse { Success = true, Message = "done", ResultType = SkillResultType.Data });
        _skillRegistry = Substitute.For<ISkillRegistry>();
        var exposurePolicy = Substitute.For<IMcpSkillExposurePolicy>();
        exposurePolicy.IsExposed(Arg.Any<SkillDescriptor>()).Returns(true);
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
        _tokenIssuer = Substitute.For<IInternalTokenIssuer>();
        _tokenIssuer.IssueForOwnerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(InternalTokenResult.Issued(new BearerToken("mcp-jwt"), new[] { Roles.Authorised }));
        _sut = new McpSkillCallHandler(
            _mediator,
            _skillRegistry,
            exposurePolicy,
            new McpReadModeToolPolicy(_riskClassifier),
            _tokenIssuer,
            Substitute.For<ILogger<McpSkillCallHandler>>());
    }

    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task ReadToken_WritingSkill_IsRefusedWithoutMintingOrExecution(SkillRiskClass riskClass)
    {
        var descriptor = RegisterSkill(WriteSkill, SkillCategory.Crud, riskClass);

        var result = await CallAsync(WriteSkill, PersonalAccessTokenAccessMode.Read);

        result.IsError.ShouldBe(true);
        FirstText(result).ShouldBe(string.Format(McpServerConstants.ReadOnlyAccessRejectionMessage, WriteSkill));
        await _tokenIssuer.DidNotReceiveWithAnyArgs().IssueForOwnerAsync(default, default, default);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
        _riskClassifier.Received().Classify(descriptor);
    }

    [Test]
    public async Task ReadToken_ConfirmPendingAction_IsRefusedEvenIfClassifiedReadOnly()
    {
        RegisterSkill(AutonomyDefaults.ConfirmPendingActionSkillName, SkillCategory.Action, SkillRiskClass.ReadOnly);

        var result = await CallAsync(AutonomyDefaults.ConfirmPendingActionSkillName, PersonalAccessTokenAccessMode.Read);

        result.IsError.ShouldBe(true);
        FirstText(result).ShouldContain("read-only");
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
    }

    [TestCase(PlanSkillDefaults.CreatePlanSkillName)]
    [TestCase("start_company_rule")]
    [TestCase("set_company_rule_parameters")]
    [TestCase("cancel_company_rule")]
    [TestCase("start_planning_profile_setup")]
    [TestCase("set_planning_profile_parameters")]
    [TestCase("cancel_planning_profile_setup")]
    public async Task ReadToken_ReadOnlyClassifiedDraftWriter_IsRefused(string skillName)
    {
        RegisterSkill(skillName, SkillCategory.Action, SkillRiskClass.ReadOnly);

        var result = await CallAsync(skillName, PersonalAccessTokenAccessMode.Read);

        result.IsError.ShouldBe(true);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
    }

    [Test]
    public async Task ReadToken_ReadOnlySkill_Executes()
    {
        RegisterSkill(ReadOnlySkill, SkillCategory.Query, SkillRiskClass.ReadOnly);

        var result = await CallAsync(ReadOnlySkill, PersonalAccessTokenAccessMode.Read);

        result.IsError.ShouldBe(false);
        await _mediator.Received(1).Send(
            Arg.Is<ExecuteSkillCommand>(command => command.Request.SkillName == ReadOnlySkill),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PatWithoutModeClaim_WritingSkill_IsRefused()
    {
        RegisterSkill(WriteSkill, SkillCategory.Crud, SkillRiskClass.Irreversible);

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = WriteSkill },
            McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), accessMode: null, Roles.Authorised),
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
    }

    [Test]
    public async Task WriteToken_WritingSkill_Executes()
    {
        RegisterSkill(WriteSkill, SkillCategory.Crud, SkillRiskClass.Irreversible);

        var result = await CallAsync(WriteSkill, PersonalAccessTokenAccessMode.Write);

        result.IsError.ShouldBe(false);
        await _mediator.Received(1).Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoginJwt_ConfirmPendingAction_Executes()
    {
        RegisterSkill(AutonomyDefaults.ConfirmPendingActionSkillName, SkillCategory.Action, SkillRiskClass.Irreversible);

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = AutonomyDefaults.ConfirmPendingActionSkillName },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "alice", Roles.Authorised),
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        await _mediator.Received(1).Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    public async Task PatCall_PassesItsAccessModeToTheSkillExecution(PersonalAccessTokenAccessMode accessMode)
    {
        RegisterSkill(ReadOnlySkill, SkillCategory.Query, SkillRiskClass.ReadOnly);

        await CallAsync(ReadOnlySkill, accessMode);

        await _mediator.Received(1).Send(
            Arg.Is<ExecuteSkillCommand>(command => command.ExternalAgentAccessMode == accessMode),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoginJwtCall_IsMarkedAsExternalAgentWithWriteMode()
    {
        RegisterSkill(ReadOnlySkill, SkillCategory.Query, SkillRiskClass.ReadOnly);

        await _sut.HandleAsync(
            new CallToolRequestParams { Name = ReadOnlySkill },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "alice", Roles.Authorised),
            CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<ExecuteSkillCommand>(command => command.ExternalAgentAccessMode == PersonalAccessTokenAccessMode.Write),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReadToken_NotExposedSkill_KeepsNotAvailableMessage()
    {
        var descriptor = RegisterSkill(WriteSkill, SkillCategory.Crud, SkillRiskClass.Sensitive);
        var exposurePolicy = Substitute.For<IMcpSkillExposurePolicy>();
        exposurePolicy.IsExposed(descriptor).Returns(false);
        var sut = new McpSkillCallHandler(
            _mediator,
            _skillRegistry,
            exposurePolicy,
            new McpReadModeToolPolicy(_riskClassifier),
            _tokenIssuer,
            Substitute.For<ILogger<McpSkillCallHandler>>());

        var result = await sut.HandleAsync(
            new CallToolRequestParams { Name = WriteSkill },
            McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), PersonalAccessTokenAccessMode.Read, Roles.Authorised),
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        FirstText(result).ShouldContain("not available");
    }

    private SkillDescriptor RegisterSkill(string name, SkillCategory category, SkillRiskClass riskClass)
    {
        var descriptor = McpTestData.Descriptor(name, category);
        _skillRegistry.GetSkillByName(name).Returns(descriptor);
        _riskClassifier.Classify(descriptor).Returns(riskClass);
        return descriptor;
    }

    private Task<CallToolResult> CallAsync(string skillName, PersonalAccessTokenAccessMode accessMode)
    {
        return _sut.HandleAsync(
            new CallToolRequestParams { Name = skillName },
            McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), accessMode, Roles.Authorised),
            CancellationToken.None);
    }

    private static string FirstText(CallToolResult result)
    {
        return ((TextContentBlock)result.Content[0]).Text;
    }
}
