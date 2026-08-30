// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parity gate for the declarative generic handlers (handlerConfig with repositoryInterface):
/// every configured method (list: method; delete: getMethod/deleteMethod) must actually resolve on
/// the declared repository interface INCLUDING methods inherited from base interfaces such as
/// IBaseRepository&lt;T&gt;. This is the regression test for the GenericListExecutor bug where
/// list_branches and list_scheduling_rules always failed with "Method 'List' not found".
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Application.Skills.Generic;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class GenericHandlerConfigParityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly Type[] GuidParameter = [typeof(Guid)];

    [Test]
    public void GenericHandlerMethods_MustResolveOnDeclaredInterfaceIncludingInherited()
    {
        var violations = new List<string>();

        foreach (var (skillName, handlerConfig) in EnumerateGenericHandlerConfigs())
        {
            var repositoryInterface = handlerConfig.GetProperty("repositoryInterface").GetString();
            if (string.IsNullOrWhiteSpace(repositoryInterface))
            {
                violations.Add($"{skillName}: handlerConfig has blank repositoryInterface");
                continue;
            }

            var interfaceType = FindInterface(repositoryInterface);
            if (interfaceType == null)
            {
                violations.Add($"{skillName}: repository interface '{repositoryInterface}' not found in loaded assemblies");
                continue;
            }

            if (handlerConfig.TryGetProperty("method", out var methodElement))
            {
                var methodName = methodElement.GetString() ?? string.Empty;
                if (ReflectionMethodResolver.FindOnInterface(interfaceType, methodName, Type.EmptyTypes) == null)
                {
                    violations.Add(
                        $"{skillName}: method '{methodName}' not found on '{repositoryInterface}' (including inherited interfaces)");
                }

                continue;
            }

            if (handlerConfig.TryGetProperty("getMethod", out var getMethodElement))
            {
                var getMethod = getMethodElement.GetString() ?? string.Empty;
                if (ReflectionMethodResolver.FindOnInterface(interfaceType, getMethod, GuidParameter) == null)
                {
                    violations.Add(
                        $"{skillName}: getMethod '{getMethod}' not found on '{repositoryInterface}' (including inherited interfaces)");
                }

                var deleteMethod = handlerConfig.TryGetProperty("deleteMethod", out var deleteMethodElement)
                    ? deleteMethodElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(deleteMethod))
                {
                    violations.Add($"{skillName}: handlerConfig has getMethod but no deleteMethod");
                }
                else if (ReflectionMethodResolver.FindOnInterface(interfaceType, deleteMethod, GuidParameter) == null)
                {
                    violations.Add(
                        $"{skillName}: deleteMethod '{deleteMethod}' not found on '{repositoryInterface}' (including inherited interfaces)");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains generic handler configs whose methods do not exist on the " +
            "declared repository interface (including inherited interfaces). Offenders: " +
            string.Join(" | ", violations));
    }

    private static IEnumerable<(string SkillName, JsonElement HandlerConfig)> EnumerateGenericHandlerConfigs()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!skill.TryGetProperty("handlerConfig", out var handlerConfig)
                || handlerConfig.ValueKind != JsonValueKind.Object
                || !handlerConfig.TryGetProperty("repositoryInterface", out var repositoryInterface)
                || repositoryInterface.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            yield return (skill.GetProperty("name").GetString() ?? string.Empty, handlerConfig);
        }
    }

    private static Type? FindInterface(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    return Array.Empty<Type>();
                }
            })
            .FirstOrDefault(type => type.IsInterface && string.Equals(type.Name, name, StringComparison.Ordinal));
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}
