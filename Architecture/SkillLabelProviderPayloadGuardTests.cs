// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// LLMFunction.Labels carries the authored, user-facing name of a skill per language. Its doc comment
/// claims the labels are "never part of the provider payload", and [JsonIgnore] is what makes that true
/// today - but only for a provider that serializes an LLMFunction directly. Every provider instead maps
/// LLMFunction onto its OWN tool schema by hand, and a hand-written mapping is exactly where a well-meant
/// "let's give the model the nicer label too" lands: it would ship 25 translations of every tool
/// description into every request, in a payload that is billed per token and cached per byte.
///
/// The guard is behavioural rather than a source scan: it discovers every mapping method by shape -
/// one parameter of List&lt;LLMFunction&gt; - on every concrete provider type, runs it against a function
/// whose labels are unmistakable strings, serializes what came back and asserts none of the label text
/// is in it. Discovery by shape rather than by name means a seventh provider is covered the day it is
/// added; the reviewer's list named five sites, the assembly answers with six (MistralProvider.BuildTools
/// is the one the list missed) plus whatever inherits BaseOpenAICompatibleProvider.MapFunctions.
///
/// Instances are created with GetUninitializedObject, deliberately: all six mapping methods are pure
/// projections of their argument, none of them reads a field, and constructing every provider for real
/// would mean six different HttpClient/IConfiguration fixtures for no gain. If a future mapping does read
/// instance state it will throw here, and the test reports that method by name instead of passing
/// silently - a loud failure is the right answer, because a mapping that depends on provider
/// configuration is a mapping this guard can no longer vouch for.
///
/// NOT covered: a provider that leaks labels through some other path than a List&lt;LLMFunction&gt;
/// mapping method (a system-prompt builder, say), providers outside the Klacks.Api assembly, and the
/// serializer options each provider actually sends with - the check serializes with its own options, so
/// a provider-specific converter that re-added the labels would not be seen.
///
/// Verified to FAIL on dirty code, not only to pass on clean code: appending the labels to
/// BaseOpenAICompatibleProvider's Description named that site and those labels. That run is also why the
/// payload is serialized with the relaxed encoder - with the default one the zh-TW label came back as
/// \uXXXX escapes and was the only leaked label the check did not see.
/// </summary>

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SkillLabelProviderPayloadGuardTests
{
    private const string ProviderNamespace = "Klacks.Api.Infrastructure.Services.Assistant.Providers";
    private const string SkillName = "add_client_to_group";
    private const string SkillDescription = "Adds an employee to a group.";
    private const int MinimumMappingMethods = 6;

    /// <summary>
    /// Non-ASCII is left as literal text rather than \uXXXX escapes. With the default encoder a leaked
    /// zh-TW label would be escaped in the payload and the substring search would miss it - the guard
    /// would then pass for exactly the languages it exists to protect.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "ZZ-LABEL-DE-MITARBEITENDE-EINER-GRUPPE-ZUWEISEN",
            ["en"] = "ZZ-LABEL-EN-ADD-AN-EMPLOYEE-TO-A-GROUP",
            ["fr"] = "ZZ-LABEL-FR-AFFECTER-UN-COLLABORATEUR",
            ["zh-TW"] = "ZZ-LABEL-ZHTW-指派員工至群組"
        };

    private static readonly string[] KnownMappingSites =
    [
        "AnthropicProvider.MapTools",
        "BaseOpenAICompatibleProvider.MapFunctions",
        "DeepSeekProvider.BuildTools",
        "GeminiProvider.BuildTools",
        "GenericOpenAICompatibleProvider.BuildTools",
        "MistralProvider.BuildTools"
    ];

    [Test]
    public void NoProviderToolMapping_PutsAnAuthoredLabelIntoItsPayload()
    {
        // Arrange
        var offenders = new List<string>();
        var unreachable = new List<string>();
        var exercised = new List<string>();

        // Act
        foreach (var (site, method, instance) in MappingMethods())
        {
            object? mapped;
            try
            {
                mapped = method.Invoke(instance, [new List<LLMFunction> { LabelledFunction() }]);
            }
            catch (Exception ex)
            {
                unreachable.Add($"{site}: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}");
                continue;
            }

            exercised.Add(site);
            var payload = JsonSerializer.Serialize(mapped, PayloadOptions);

            offenders.AddRange(
                Labels.Values
                    .Where(label => payload.Contains(label, StringComparison.Ordinal))
                    .Select(label => $"{site} put the {nameof(LLMFunction.Labels)} entry '{label}' into its payload"));
        }

        // Assert
        unreachable.ShouldBeEmpty(
            "A tool mapping could not be exercised, so this guard no longer vouches for it: "
            + string.Join("; ", unreachable));
        offenders.ShouldBeEmpty(string.Join(Environment.NewLine, offenders));
        exercised.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumMappingMethods,
            $"Only {exercised.Count} tool mappings were discovered - the guard would pass by finding nothing.");
    }

    // The mapping has to actually produce a payload, otherwise "no label in it" is true of an empty
    // string and the test above proves nothing.
    [Test]
    public void EveryDiscoveredToolMapping_StillCarriesTheSkillNameAndDescription()
    {
        // Arrange
        var silent = new List<string>();

        // Act
        foreach (var (site, method, instance) in MappingMethods())
        {
            var payload = JsonSerializer.Serialize(
                method.Invoke(instance, [new List<LLMFunction> { LabelledFunction() }]), PayloadOptions);

            if (!payload.Contains(SkillName, StringComparison.Ordinal)
                || !payload.Contains(SkillDescription, StringComparison.Ordinal))
            {
                silent.Add(site);
            }
        }

        // Assert
        silent.ShouldBeEmpty(
            "These mappings produced no recognisable tool payload: " + string.Join(", ", silent));
    }

    /// <summary>
    /// Pins the encoder choice rather than the serializer: a payload reading that escapes non-ASCII would
    /// let a leaked zh-TW, ar or he label through while still reporting the ASCII ones, which is the
    /// quietest way this guard could stop working.
    /// </summary>
    [Test]
    public void ThePayloadReading_SeesANonAsciiLabel()
    {
        // Arrange
        var leaked = JsonSerializer.Serialize(new { description = Labels["zh-TW"] }, PayloadOptions);

        // Act & Assert
        leaked.Contains(Labels["zh-TW"], StringComparison.Ordinal).ShouldBeTrue();
    }

    [Test]
    public void EveryKnownMappingSite_IsStillDiscovered()
    {
        // Arrange
        var discovered = MappingMethods().Select(m => m.Site).Distinct(StringComparer.Ordinal).ToList();

        // Act & Assert
        foreach (var site in KnownMappingSites)
        {
            discovered.ShouldContain(site);
        }
    }

    private static LLMFunction LabelledFunction() => new()
    {
        Name = SkillName,
        Description = SkillDescription,
        Parameters = new Dictionary<string, object> { ["groupId"] = new { type = "string" } },
        RequiredParameters = ["groupId"],
        Labels = Labels
    };

    /// <summary>
    /// Every tool-mapping method reachable from a concrete provider, keyed by the type that DECLARES it
    /// so an inherited mapping is reported once rather than once per subclass. Inherited members are
    /// included on purpose: BaseOpenAICompatibleProvider.MapFunctions is declared on an abstract type
    /// that cannot be instantiated, and a subclass overriding it has to be exercised through the
    /// subclass to reach the override at all.
    /// </summary>
    private static List<(string Site, MethodInfo Method, object? Instance)> MappingMethods()
    {
        var found = new Dictionary<string, (string, MethodInfo, object?)>(StringComparer.Ordinal);

        foreach (var type in ProviderTypes())
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Instance | BindingFlags.Static
                         | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!MapsAToolList(method))
                {
                    continue;
                }

                var site = $"{method.DeclaringType!.Name}.{method.Name}";
                if (found.ContainsKey(site))
                {
                    continue;
                }

                found[site] = (site, method, method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(type));
            }
        }

        return found.Values.Select(entry => (entry.Item1, entry.Item2, entry.Item3)).ToList();
    }

    private static bool MapsAToolList(MethodInfo method)
    {
        if (method.IsGenericMethod || method.ReturnType == typeof(void))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(List<LLMFunction>);
    }

    private static IEnumerable<Type> ProviderTypes() =>
        typeof(LLMFunction).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && t.Namespace != null
                        && t.Namespace.StartsWith(ProviderNamespace, StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
}
