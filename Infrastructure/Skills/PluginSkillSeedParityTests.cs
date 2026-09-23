// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parity gate between feature-plugin skill seeds and the plugin assemblies. SkillSeedParityTests only
/// resolves core seeds against Klacks.Api, so a plugin seed without an implementation class was invisible:
/// the skill is offered to the model and fails when called. Forward direction: every enabled skill in a
/// plugin's skill-seeds.json must be resolvable by a [SkillImplementation] class in the skill assemblies of
/// the registrar named in that plugin's manifest (backend.registrar), found the way SkillRegistryInitializer
/// finds them at runtime. Reverse direction: every [SkillImplementation] class in those assemblies must have
/// a seed entry in that plugin's skill-seeds.json.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Plugin.Contracts;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class PluginSkillSeedParityTests
{
    private const string ManifestFileName = "manifest.json";
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string PluginAssemblyFilePattern = "Klacks.Plugin.*.dll";
    private const string BackendProperty = "backend";
    private const string RegistrarProperty = "registrar";
    private const string NameProperty = "name";
    private const string IsEnabledProperty = "isEnabled";

    private static readonly string[] FeaturesRelativePath = ["Klacks.Api", "Plugins", "Features"];

    private sealed record PluginSeeds(string Plugin, string? Registrar, List<(string Name, bool IsEnabled)> Skills);

    public static IEnumerable<string> PluginsWithSeeds() =>
        LoadPluginSeeds().Select(p => p.Plugin);

    [Test]
    public void AtLeastOnePluginShipsSkillSeeds()
    {
        LoadPluginSeeds().ShouldNotBeEmpty("no feature plugin ships skill-seeds.json, so this gate proves nothing");
    }

    [TestCaseSource(nameof(PluginsWithSeeds))]
    public void EveryEnabledPluginSeedSkill_MustHaveAnImplementationInThePluginAssembly(string plugin)
    {
        var seeds = LoadPluginSeeds().Single(p => p.Plugin == plugin);
        var implementations = ScanImplementations(seeds);

        var missing = seeds.Skills
            .Where(skill => skill.IsEnabled && !implementations.Contains(skill.Name))
            .Select(skill => skill.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            $"plugin '{plugin}' seeds enabled skills without a [SkillImplementation] class in the skill assemblies " +
            $"of registrar '{seeds.Registrar}'; they are offered to the model but fail at execution time: " +
            string.Join(", ", missing));
    }

    [TestCaseSource(nameof(PluginsWithSeeds))]
    public void EveryPluginImplementationClass_MustHaveASeedEntry(string plugin)
    {
        var seeds = LoadPluginSeeds().Single(p => p.Plugin == plugin);
        var seeded = seeds.Skills.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);

        var orphans = ScanImplementations(seeds)
            .Where(name => !seeded.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty(
            $"plugin '{plugin}' has [SkillImplementation] classes without an entry in its {SkillSeedsFileName}; " +
            "they never reach the skill registry: " + string.Join(", ", orphans));
    }

    private static HashSet<string> ScanImplementations(PluginSeeds seeds)
    {
        seeds.Registrar.ShouldNotBeNullOrWhiteSpace(
            $"plugin '{seeds.Plugin}' ships {SkillSeedsFileName} but its manifest names no backend.registrar, " +
            "so no assembly can provide the skill implementations");

        var registrarType = PluginAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(type => type is { IsClass: true, IsAbstract: false }
                && type.Name == seeds.Registrar
                && typeof(IPluginRegistrar).IsAssignableFrom(type));
        registrarType.ShouldNotBeNull(
            $"registrar '{seeds.Registrar}' of plugin '{seeds.Plugin}' was not found in any {PluginAssemblyFilePattern} " +
            "next to the test assembly");

        var registrar = (IPluginRegistrar)Activator.CreateInstance(registrarType!)!;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assembly in registrar.GetSkillAssemblies().Distinct())
        {
            foreach (var type in SafeGetTypes(assembly).Where(t => !t.IsAbstract))
            {
                var contractAttribute = type.GetCustomAttribute<Klacks.Plugin.Contracts.Skills.SkillImplementationAttribute>();
                if (contractAttribute != null)
                {
                    names.Add(contractAttribute.SkillName);
                    continue;
                }

                var coreAttribute = type.GetCustomAttribute<Klacks.Api.Domain.Attributes.SkillImplementationAttribute>();
                if (coreAttribute != null)
                {
                    names.Add(coreAttribute.SkillName);
                }
            }
        }

        return names;
    }

    private static IEnumerable<Assembly> PluginAssemblies()
    {
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, PluginAssemblyFilePattern))
        {
            yield return Assembly.Load(AssemblyName.GetAssemblyName(file));
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static List<PluginSeeds> LoadPluginSeeds()
    {
        var result = new List<PluginSeeds>();

        foreach (var pluginDir in Directory.GetDirectories(LocateFeaturesDir()).OrderBy(d => d, StringComparer.Ordinal))
        {
            var seedFile = Path.Combine(pluginDir, SkillSeedsFileName);
            if (!File.Exists(seedFile))
            {
                continue;
            }

            string? registrar = null;
            var manifestFile = Path.Combine(pluginDir, ManifestFileName);
            if (File.Exists(manifestFile))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
                if (manifest.RootElement.TryGetProperty(BackendProperty, out var backend)
                    && backend.TryGetProperty(RegistrarProperty, out var registrarElement))
                {
                    registrar = registrarElement.GetString();
                }
            }

            using var seeds = JsonDocument.Parse(File.ReadAllText(seedFile));
            var skills = seeds.RootElement.EnumerateArray()
                .Select(skill => (
                    Name: skill.GetProperty(NameProperty).GetString() ?? string.Empty,
                    IsEnabled: !skill.TryGetProperty(IsEnabledProperty, out var enabled) || enabled.GetBoolean()))
                .ToList();

            result.Add(new PluginSeeds(Path.GetFileName(pluginDir), registrar, skills));
        }

        return result;
    }

    private static string LocateFeaturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. FeaturesRelativePath]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', FeaturesRelativePath)} by walking up from the test base directory.");
    }
}
