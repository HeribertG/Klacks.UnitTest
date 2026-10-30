// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that the skill usage log keeps no secrets. Whatever a user types into the chat reaches the
/// tracker as tool-call arguments, so an API key or password would otherwise sit in plain text in
/// ParametersJson forever. Nested objects and arrays are covered too, because tool-call arguments
/// arrive as JsonElement and are not flat. The text and the id numbers of a send_broadcast never reach the log either, on the
/// success and the failure path, while the same parameter names stay readable in every other skill.
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

    private const string BroadcastText = "Vertraulicher Rundbrief";
    private const int IdNumber = 40711;

    private static Dictionary<string, object> BroadcastArguments() =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(
            $$"""{ "groupId": "g-1", "content": "{{BroadcastText}}", "idNumbers": [{{IdNumber}}] }""")!;

    private static SkillUsageTrackerService Tracker(ISkillUsageRepository repository) => new(
        repository, Substitute.For<ISkillSequenceProactiveNotifier>(), NullLogger<SkillUsageTrackerService>.Instance);

    private static void ShouldHideTheBroadcast(string? parametersJson)
    {
        parametersJson.ShouldNotBeNull();
        parametersJson.ShouldNotContain(BroadcastText);
        parametersJson.ShouldNotContain(IdNumber.ToString());
        parametersJson.ShouldContain("g-1");
    }

    [Test]
    public void Redact_HidesTheTextOfASendMessage()
    {
        SensitiveSkillParameters.IsPersonalInSkill("send_message", "content").ShouldBeTrue();
        SensitiveSkillParameters.IsPersonalInSkill("send_message", "contentType").ShouldBeFalse();
    }

    [Test]
    public async Task TrackAsync_HidesTheTextAndTheIdNumbersOfABroadcast()
    {
        var repository = Substitute.For<ISkillUsageRepository>();
        SkillUsageRecord? saved = null;
        repository.When(r => r.AddAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>()))
            .Do(ci => saved = ci.Arg<SkillUsageRecord>());
        var descriptor = new SkillDescriptor(
            "send_broadcast", "desc", SkillCategory.Action, Array.Empty<SkillParameter>(),
            Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

        await Tracker(repository).TrackAsync(descriptor, Context(), BroadcastArguments(), SkillResult.SuccessResult(null), TimeSpan.FromMilliseconds(3));

        ShouldHideTheBroadcast(saved!.ParametersJson);
    }

    [Test]
    public async Task TrackFailureAsync_HidesTheTextAndTheIdNumbersOfABroadcast()
    {
        var repository = Substitute.For<ISkillUsageRepository>();
        SkillUsageRecord? saved = null;
        repository.When(r => r.AddAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>()))
            .Do(ci => saved = ci.Arg<SkillUsageRecord>());

        await Tracker(repository).TrackFailureAsync(
            "send_broadcast", SkillFailureKind.PermissionDenied, Context(), BroadcastArguments(), "refused", TimeSpan.FromMilliseconds(3));

        ShouldHideTheBroadcast(saved!.ParametersJson);
    }

    [Test]
    public void AnotherSkill_KeepsItsContentReadable()
    {
        var redacted = Klacks.Api.Domain.Services.Assistant.Skills.SkillParameterRedactor.Redact(
            new Dictionary<string, object> { ["content"] = "Notiz" }, "add_client_note");

        redacted["content"].ShouldBe("Notiz");
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
