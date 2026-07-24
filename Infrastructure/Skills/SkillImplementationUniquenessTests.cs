// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard against duplicate skill implementations. Every skill name may be carried by exactly one
/// [SkillImplementation] class compiled into Klacks.Api (hand-written or generated from
/// settings-reader-skills.json). SkillRegistryInitializer refuses to boot on duplicates; this test
/// surfaces the collision at build time, because with a silent first-wins scan the winning class
/// would depend on assembly metadata order.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillImplementationUniquenessTests
{
    [Test]
    public void EverySkillName_MustHaveExactlyOneImplementationClass()
    {
        var implementations = new List<(string SkillName, Type Type)>();

        foreach (var type in GetLoadableTypes(typeof(SkillRegistryInitializer).Assembly).Where(t => !t.IsAbstract))
        {
            var coreAttribute = type.GetCustomAttribute<Klacks.Api.Domain.Attributes.SkillImplementationAttribute>();
            if (coreAttribute != null)
            {
                implementations.Add((coreAttribute.SkillName, type));
                continue;
            }

            var contractAttribute = type.GetCustomAttribute<Klacks.Plugin.Contracts.Skills.SkillImplementationAttribute>();
            if (contractAttribute != null)
            {
                implementations.Add((contractAttribute.SkillName, type));
            }
        }

        var duplicates = implementations
            .GroupBy(i => i.SkillName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"{group.Key}: {string.Join(", ", group.Select(i => i.Type.FullName).OrderBy(n => n, StringComparer.Ordinal))}")
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToList();

        duplicates.ShouldBeEmpty(
            "Each skill name must be implemented by exactly one [SkillImplementation] class. With " +
            "duplicates the registry match depends on assembly scan order and SkillRegistryInitializer " +
            "fails fast at boot. Duplicates: " + string.Join("; ", duplicates));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!);
        }
    }
}
