// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Contract guard for ILLMProvider.ResolveForcedToolChoiceSupport. How a provider behaves when a turn
/// forces a tool call is the one property that cannot be guessed: guessing "it works" loses the write
/// silently (the model answers in prose and nothing happens), guessing "it does not" throws away the
/// deterministic forcing the recipe engine relies on. So every provider has to say, and the operator's
/// choice of model or provider must never require a change in this code.
///
/// The interface carries a NotSupported default for test doubles only; a real provider that inherits it
/// fails here. BaseHttpProvider additionally declares the member abstract, so for every HTTP provider a
/// missing declaration is already a compile error - this test covers the rest: a provider that implements
/// ILLMProvider directly (AnthropicProvider does), and a newly added provider whose declared value nobody
/// looked at, which fails until it is entered below together with the evidence for it.
///
/// The declared value is read off an uninitialized instance: every implementation is a constant
/// expression that touches no field, which is what makes that safe. An implementation that starts reading
/// state will fail here rather than pass quietly, and that is the right moment to give this test a real
/// instance instead.
/// </summary>

using System.Reflection;
using System.Runtime.CompilerServices;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ForcedToolChoiceSupportGuardTests
{
    private const string DeclarationMethodName = nameof(ILLMProvider.ResolveForcedToolChoiceSupport);

    // Provider type name -> the value it declares, with the evidence in this file's history:
    // Anthropic maps "required" to {"type":"any"} and never sends a thinking block, so the forcing stands.
    // DeepSeek's thinking models refuse it and the provider switches thinking off per request.
    // Generic passes request.ToolChoice straight through to an OpenAI-style endpoint.
    // Gemini builds no toolConfig at all; Mistral and the legacy function_call base hardcode "auto".
    private static readonly IReadOnlyDictionary<string, ForcedToolChoiceSupport> Expected =
        new Dictionary<string, ForcedToolChoiceSupport>(StringComparer.Ordinal)
        {
            ["AnthropicProvider"] = ForcedToolChoiceSupport.Native,
            ["AzureOpenAIProvider"] = ForcedToolChoiceSupport.NotSupported,
            ["DeepSeekProvider"] = ForcedToolChoiceSupport.RequiresThinkingDisabled,
            ["GeminiProvider"] = ForcedToolChoiceSupport.NotSupported,
            ["GenericOpenAICompatibleProvider"] = ForcedToolChoiceSupport.Native,
            ["MistralProvider"] = ForcedToolChoiceSupport.NotSupported,
            ["OpenAIProvider"] = ForcedToolChoiceSupport.NotSupported,
        };

    private static IReadOnlyList<Type> Providers() =>
        typeof(DeepSeekProvider).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(ILLMProvider).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    [Test]
    public void EveryProvider_DeclaresItsForcedToolChoiceBehaviour()
    {
        var undeclared = Providers()
            .Where(type => type.GetMethod(
                DeclarationMethodName,
                BindingFlags.Public | BindingFlags.Instance,
                [typeof(LLMProviderRequest)]) == null)
            .Select(type => type.Name)
            .ToList();

        undeclared.ShouldBeEmpty(
            $"These providers inherit the NotSupported interface default instead of declaring how they " +
            $"handle a forced tool call: {string.Join(", ", undeclared)}. Add an override and record the " +
            "evidence for the value in ForcedToolChoiceSupportGuardTests.Expected.");
    }

    [Test]
    public void TheDeclaredValues_AreTheOnesThisGuardWasWrittenAgainst()
    {
        var actual = Providers().ToDictionary(
            type => type.Name,
            DeclaredValueOf,
            StringComparer.Ordinal);

        actual.Keys.OrderBy(name => name, StringComparer.Ordinal).ShouldBe(
            Expected.Keys.OrderBy(name => name, StringComparer.Ordinal),
            "A provider was added or removed. Enter it in Expected together with the code evidence for " +
            "the value it declares - never with a value taken from a vendor's marketing page.");

        foreach (var (name, value) in actual)
        {
            value.ShouldBe(Expected[name], $"{name} changed its declared forced-tool-choice behaviour.");
        }
    }

    private static ForcedToolChoiceSupport DeclaredValueOf(Type providerType)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(providerType);
        var method = providerType.GetMethod(
            DeclarationMethodName,
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(LLMProviderRequest)])!;

        return (ForcedToolChoiceSupport)method.Invoke(instance, [new LLMProviderRequest()])!;
    }
}
