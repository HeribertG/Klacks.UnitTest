// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpSkillCallHandlerTests
{
    private IMediator _mediator = null!;
    private ISkillRegistry _skillRegistry = null!;
    private IMcpSkillExposurePolicy _exposurePolicy = null!;
    private McpSkillCallHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _exposurePolicy = Substitute.For<IMcpSkillExposurePolicy>();
        _exposurePolicy.IsExposed(Arg.Any<SkillDescriptor>()).Returns(true);
        _sut = new McpSkillCallHandler(
            _mediator,
            _skillRegistry,
            _exposurePolicy,
            Substitute.For<ILogger<McpSkillCallHandler>>());
    }

    private static string FirstText(CallToolResult result)
    {
        return ((TextContentBlock)result.Content[0]).Text;
    }

    [Test]
    public async Task UnknownTool_ReturnsError()
    {
        _skillRegistry.GetSkillByName("missing").Returns((SkillDescriptor?)null);

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "missing" },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(FirstText(result), Does.Contain("not available"));
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
    }

    [Test]
    public async Task NotExposedTool_ReturnsErrorWithoutExecution()
    {
        var descriptor = McpTestData.Descriptor("delete_system_user");
        _skillRegistry.GetSkillByName("delete_system_user").Returns(descriptor);
        _exposurePolicy.IsExposed(descriptor).Returns(false);

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "delete_system_user" },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ExecuteSkillCommand)!, default);
    }

    [Test]
    public async Task MissingUserIdentity_ReturnsAuthenticationError()
    {
        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "search_employees" },
            null,
            CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(FirstText(result), Does.Contain("Authentication required"));
    }

    [Test]
    public async Task SuccessfulExecution_MapsUserContextAndResult()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var descriptor = McpTestData.Descriptor("search_employees");
        _skillRegistry.GetSkillByName("search_employees").Returns(descriptor);
        _mediator.Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SkillExecuteResponse
            {
                Success = true,
                Message = "Found 3 employees",
                ResultType = SkillResultType.Data
            });

        var arguments = new Dictionary<string, JsonElement>
        {
            ["searchTerm"] = JsonSerializer.SerializeToElement("Muster")
        };

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "search_employees", Arguments = arguments },
            McpTestData.Principal(userId, tenantId, "alice", Roles.Admin),
            CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(FirstText(result), Is.EqualTo("Found 3 employees"));
        Assert.That(result.StructuredContent!.Value.GetProperty("success").GetBoolean(), Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<ExecuteSkillCommand>(command =>
                command.UserId == userId
                && command.TenantId == tenantId
                && command.UserName == "alice"
                && !command.UserPermissions.Contains(Roles.Admin)
                && command.UserPermissions.Contains(Permissions.CanUseAssistant)
                && command.Request.SkillName == "search_employees"
                && command.Request.Parameters.ContainsKey("searchTerm")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NullArguments_ExecutesWithEmptyParameters()
    {
        var descriptor = McpTestData.Descriptor("get_dashboard_summary");
        _skillRegistry.GetSkillByName("get_dashboard_summary").Returns(descriptor);
        _mediator.Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SkillExecuteResponse
            {
                Success = true,
                ResultType = SkillResultType.Data
            });

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "get_dashboard_summary" },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        await _mediator.Received(1).Send(
            Arg.Is<ExecuteSkillCommand>(command => command.Request.Parameters.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfirmationResult_IsNotErrorAndContainsToken()
    {
        var descriptor = McpTestData.Descriptor("delete_client", SkillCategory.Crud);
        _skillRegistry.GetSkillByName("delete_client").Returns(descriptor);
        _mediator.Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SkillExecuteResponse
            {
                Success = false,
                Message = "Deleting client requires confirmation.",
                ResultType = SkillResultType.Confirmation,
                Metadata = new Dictionary<string, object>
                {
                    ["confirmationToken"] = "token-123"
                }
            });

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "delete_client" },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsError, Is.False);
        Assert.That(FirstText(result), Does.Contain(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(FirstText(result), Does.Contain("token-123"));
    }

    [Test]
    public async Task ExecutionException_ReturnsSanitizedError()
    {
        var descriptor = McpTestData.Descriptor("search_employees");
        _skillRegistry.GetSkillByName("search_employees").Returns(descriptor);
        _mediator.Send(Arg.Any<ExecuteSkillCommand>(), Arg.Any<CancellationToken>())
            .Returns<SkillExecuteResponse>(_ => throw new InvalidOperationException("connection string secret"));

        var result = await _sut.HandleAsync(
            new CallToolRequestParams { Name = "search_employees" },
            McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(FirstText(result), Does.Not.Contain("connection string secret"));
    }
}
