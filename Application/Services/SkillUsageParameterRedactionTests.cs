// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the skill usage log keeps no secrets. Whatever a user types into the chat reaches the
/// tracker as tool-call arguments, so an API key or password would otherwise sit in plain text in
/// ParametersJson forever. Nested objects and arrays are covered too, because tool-call arguments
/// arrive as JsonElement and are not flat.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services;

[TestFixture]
public class SkillUsageParameterRedactionTests
{
    private const string TopLevelSecret = "top-level-api-key-9931";
    private const string NestedSecret = "nested-mail-password-4417";
    private const string ArraySecret = "array-client-secret-2286";

    private static SkillDescriptor Descriptor() => new(
        "update_openroute_settings", "desc", SkillCategory.Crud, Array.Empty<SkillParameter>(),
        Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        SessionId = "conv-1",
    };

    private static Dictionary<string, object> ToolCallArguments()
    {
        var json = $$"""
        {
          "provider": "serper",
          "apiKey": "{{TopLevelSecret}}",
          "maxTokens": 4096,
          "connection": { "host": "mail.example.com", "password": "{{NestedSecret}}" },
          "providers": [ { "name": "oidc", "clientSecret": "{{ArraySecret}}" } ]
        }
        """;

        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
    }

    private static async Task<string> TrackAndReadParametersJson(Dictionary<string, object> parameters)
    {
        var repository = Substitute.For<ISkillUsageRepository>();
        SkillUsageRecord? saved = null;
        repository.When(r => r.AddAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>()))
            .Do(ci => saved = ci.Arg<SkillUsageRecord>());

        var tracker = new SkillUsageTrackerService(
            repository,
            Substitute.For<ISkillSequenceProactiveNotifier>(),
            NullLogger<SkillUsageTrackerService>.Instance);

        await tracker.TrackAsync(Descriptor(), Context(), parameters,
            SkillResult.SuccessResult(null), TimeSpan.FromMilliseconds(3));

        saved.ShouldNotBeNull();
        return saved!.ParametersJson!;
    }

    [Test]
    public async Task TrackAsync_RedactsSecretsAtEveryNestingLevel()
    {
        var parametersJson = await TrackAndReadParametersJson(ToolCallArguments());

        parametersJson.ShouldNotContain(TopLevelSecret);
        parametersJson.ShouldNotContain(NestedSecret);
        parametersJson.ShouldNotContain(ArraySecret);
        parametersJson.ShouldContain(SensitiveSkillParameters.RedactedValue);
    }

    [Test]
    public async Task TrackAsync_KeepsNonSecretParametersReadable()
    {
        var parametersJson = await TrackAndReadParametersJson(ToolCallArguments());

        parametersJson.ShouldContain("serper");
        parametersJson.ShouldContain("mail.example.com");
        parametersJson.ShouldContain("4096");
    }

    [Test]
    public async Task TrackAsync_DoesNotModifyTheCallersDictionary()
    {
        var parameters = ToolCallArguments();

        await TrackAndReadParametersJson(parameters);

        parameters["apiKey"].ToString().ShouldBe(TopLevelSecret,
            "redaction must work on a copy, the skill still needs the real value");
    }
}
