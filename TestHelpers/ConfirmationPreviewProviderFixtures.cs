// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shared lookups of the architecture guards over the confirmation preview providers: the provider types of Klacks.Api,
/// the ones the real composition root registers (the real AddDomainServices on an empty configuration, not a text search),
/// each provider built with substitutes for its dependencies, and the skills they support with the category the skill is
/// seeded with (the messaging plugin seeds a category SkillCategory does not know, which the registry maps to Action, so
/// does this helper).
/// </summary>

using System.Reflection;
using System.Text.Json.Nodes;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Attributes;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.TestHelpers;

public static class ConfirmationPreviewProviderFixtures
{
    private const string SkillsProperty = "skills";
    private const string NameProperty = "name";
    private const string CategoryProperty = "category";

    private static readonly string[][] SeedRelativePaths =
    [
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"],
        ["Klacks.Api", "Plugins", "Features", "messaging", "skill-seeds.json"]
    ];

    private static readonly Assembly ApiAssembly = typeof(BulkWorkDeletionConfirmationPreviewProvider).Assembly;

    public static IReadOnlyList<Type> ProviderTypes() =>
        ApiAssembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                           && typeof(ISkillConfirmationPreviewProvider).IsAssignableFrom(type))
            .ToList();

    public static IReadOnlyList<Type> RegisteredProviderTypes()
    {
        var services = new ServiceCollection();
        services.AddDomainServices(new ConfigurationBuilder().Build());
        return services
            .Where(descriptor => descriptor.ServiceType == typeof(ISkillConfirmationPreviewProvider))
            .Select(descriptor => descriptor.ImplementationType!)
            .ToList();
    }

    public static ISkillConfirmationPreviewProvider Build(Type type)
    {
        var constructor = type.GetConstructors().Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => Substitute.For([parameter.ParameterType], []))
            .ToArray();
        return (ISkillConfirmationPreviewProvider)constructor.Invoke(arguments);
    }

    public static IReadOnlyList<ISkillConfirmationPreviewProvider> Providers() => ProviderTypes().Select(Build).ToList();

    public static IReadOnlyDictionary<string, SkillCategory?> CandidateSkills()
    {
        var skills = new Dictionary<string, SkillCategory?>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in SeedRelativePaths)
        {
            var root = JsonNode.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(relativePath)))!;
            var seeded = root is JsonArray pluginSeeds ? pluginSeeds : root[SkillsProperty]!.AsArray();
            foreach (var skill in seeded)
            {
                var rawCategory = skill![CategoryProperty]?.GetValue<string>();
                skills[skill[NameProperty]!.GetValue<string>()] =
                    Enum.TryParse<SkillCategory>(rawCategory, ignoreCase: true, out var parsed) ? parsed : SkillCategory.Action;
            }
        }

        foreach (var attribute in ApiAssembly.GetTypes().SelectMany(type => type.GetCustomAttributes<SkillImplementationAttribute>()))
        {
            skills.TryAdd(attribute.SkillName, null);
        }

        return skills;
    }

    public static IReadOnlyList<(ISkillConfirmationPreviewProvider Provider, string SkillName, SkillCategory? Category)> SupportedSkills()
    {
        var candidates = CandidateSkills();
        return Providers()
            .SelectMany(provider => candidates.Where(candidate => provider.Supports(candidate.Key))
                .Select(candidate => (provider, candidate.Key, candidate.Value)))
            .ToList();
    }

    public static Type ImplementationOf(string skillName) =>
        ApiAssembly.GetTypes().Single(type => type.GetCustomAttributes<SkillImplementationAttribute>()
            .Any(attribute => string.Equals(attribute.SkillName, skillName, StringComparison.OrdinalIgnoreCase)));
}
