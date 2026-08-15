// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the two skills that used to mint a credential and hand its plaintext back in the skill
/// result. LLMFunctionExecutor composes a tool result as $"{result.Message}\nData: {dataJson}" and
/// LLMService.FormatFunctionResults folds that string into the next request to the external
/// language-model provider, so anything either field carries leaves the system. Both skills must
/// therefore return guidance only: no secret in Data, no secret in Message, and no create command
/// sent at all — a token minted here could never be handed to the user, because the plaintext is
/// never persisted and no endpoint reveals it later.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Authentification;
using Klacks.Api.Application.Commands.ErpImportTokens;
using Klacks.Api.Application.DTOs.Authentification;
using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class TokenCreationSkillSecretLeakTests
{
    private const string PatSentinel = PatConstants.TokenPrefix + "MUTATIONPROBESECRETVALUE";

    private const string ErpSentinel = ErpImportTokenConstants.TokenPrefix + "MUTATIONPROBESECRETVALUE";

    private static readonly JsonSerializerOptions ExecutorSerializerOptions = new() { WriteIndented = false };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin, Permissions.CanEditSettings, Permissions.CanPlan }
    };

    // Mirrors LLMFunctionExecutor: the tool result handed onwards is message plus serialized data.
    private static string ComposeToolResult(SkillResult result)
    {
        var dataJson = result.Data == null
            ? string.Empty
            : JsonSerializer.Serialize(result.Data, ExecutorSerializerOptions);

        return $"{result.Message}\nData: {dataJson}";
    }

    private static IMediator MediatorThatWouldMintPat()
    {
        var mediator = Substitute.For<IMediator>();
        mediator
            .Send(Arg.Any<CreatePersonalAccessTokenCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalAccessTokenCreatedDto(
                Guid.NewGuid(),
                "Claude Desktop",
                PatConstants.TokenPrefix + "MUTATION",
                DateTime.UtcNow.AddDays(365),
                PatSentinel));

        return mediator;
    }

    private static IMediator MediatorThatWouldMintErpToken()
    {
        var mediator = Substitute.For<IMediator>();
        var dropPointId = Guid.NewGuid();

        mediator
            .Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { Id = dropPointId, Name = "Default" });

        mediator
            .Send(Arg.Any<CreateErpImportTokenCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ErpImportTokenCreatedDto(
                Guid.NewGuid(),
                dropPointId,
                "ERP vendor",
                ErpImportTokenConstants.TokenPrefix + "MUTATION",
                DateTime.UtcNow.AddDays(365),
                ErpSentinel));

        return mediator;
    }

    [Test]
    public async Task CreatePersonalAccessToken_LeaksNoSecret_AndMintsNothing()
    {
        var mediator = MediatorThatWouldMintPat();
        var skill = new CreatePersonalAccessTokenSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Claude Desktop",
            ["expiresInDays"] = 30
        });

        result.Success.ShouldBeTrue();

        result.Data.ShouldBeNull(
            "Data is serialized into the tool result and travels to the language-model provider.");

        result.Message.ShouldNotBeNull();
        result.Message!.ShouldNotContain(PatSentinel);
        result.Message.ShouldNotContain(PatConstants.TokenPrefix);

        var toolResult = ComposeToolResult(result);
        toolResult.ShouldNotContain(PatSentinel);
        toolResult.ShouldNotContain(PatConstants.TokenPrefix);

        await mediator.DidNotReceive().Send(
            Arg.Any<CreatePersonalAccessTokenCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreatePersonalAccessToken_PointsAtTheSettingsCard()
    {
        var skill = new CreatePersonalAccessTokenSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("No token was created");
        result.Message.ShouldContain("settings");
    }

    [Test]
    public async Task CreateErpImportToken_LeaksNoSecret_AndMintsNothing()
    {
        var mediator = MediatorThatWouldMintErpToken();
        var skill = new CreateErpImportTokenSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "ERP vendor",
            ["expiresInDays"] = 30
        });

        result.Success.ShouldBeTrue();

        result.Data.ShouldBeNull(
            "Data is serialized into the tool result and travels to the language-model provider.");

        result.Message.ShouldNotBeNull();
        result.Message!.ShouldNotContain(ErpSentinel);
        result.Message.ShouldNotContain(ErpImportTokenConstants.TokenPrefix);

        var toolResult = ComposeToolResult(result);
        toolResult.ShouldNotContain(ErpSentinel);
        toolResult.ShouldNotContain(ErpImportTokenConstants.TokenPrefix);

        await mediator.DidNotReceive().Send(
            Arg.Any<CreateErpImportTokenCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateErpImportToken_PointsAtTheSettingsCard()
    {
        var skill = new CreateErpImportTokenSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("No key was issued");
        result.Message.ShouldContain("settings");
    }

    private static SkillDescriptor CrudDescriptor(string name)
        => new(name, "test", SkillCategory.Crud, [], [], [], null) { ExecutionType = LlmExecutionTypes.Skill };

    // The ERP import token pair authenticates the order-upload endpoint and was only Irreversible,
    // which passes unattended at the Autonomous default level and is exposed over /mcp — where a
    // stolen personal access token authenticates. Its PAT twin has always been Sensitive.
    [TestCase("create_erp_import_token")]
    [TestCase("revoke_erp_import_token")]
    [TestCase("create_personal_access_token")]
    [TestCase("revoke_personal_access_token")]
    public void TokenManagementSkills_AreSensitive(string name)
    {
        new SkillRiskClassifier().Classify(CrudDescriptor(name)).ShouldBe(SkillRiskClass.Sensitive);
    }

    [TestCase("create_erp_import_token")]
    [TestCase("revoke_erp_import_token")]
    [TestCase("create_personal_access_token")]
    [TestCase("revoke_personal_access_token")]
    public void TokenManagementSkills_AreNotExposedOverMcp(string name)
    {
        var policy = new McpSkillExposurePolicy(new SkillRiskClassifier());

        policy.IsExposed(CrudDescriptor(name)).ShouldBeFalse();
    }
}
