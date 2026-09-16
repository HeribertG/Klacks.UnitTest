// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The contract between the structured half of InverseSkillRegistry and the skills it points at: the
/// argument names the registry writes are names those skills actually declare, and every argument they
/// require is written. Nothing else pins this - the registry is a hand-maintained table, the seeds are a
/// separate file, and a renamed parameter on either side would only show up as an undo the model calls
/// with an argument the executor drops, which looks like a successful undo that changed nothing. The
/// entries are exercised through TryBuildUndo rather than read field by field, so what is asserted is
/// the call that would really be held as a pending confirmation.
/// </summary>

using System.Text.Json;
using System.Text.Json.Nodes;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InverseSkillArgumentContractTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SkillsProperty = "skills";
    private const string NameProperty = "name";
    private const string ParametersProperty = "parameters";
    private const string RequiredProperty = "required";
    private const string SyntheticValue = "synthetic-value";

    /// <summary>
    /// The five pairs the spec review of 2026-09-16 left in the table. A lower bound rather than an exact
    /// count, so an approved sixth pair does not fail the build - what it guards is that the structural
    /// filter below never silently selects nothing, which would leave every assertion here vacuous.
    /// </summary>
    private const int MinimumStructuredPairCount = 5;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Name, bool Required)>>
        SeededParameters = LoadSeededParameters();

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory} by walking up from the test base directory.");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<(string Name, bool Required)>> LoadSeededParameters()
    {
        var seedPath = Path.Combine(
            RepositoryRoot().FullName, ApiProjectDirectory, "Application", "Skills", "Definitions", "skill-seeds.json");
        using var document = JsonDocument.Parse(File.ReadAllText(seedPath));

        var seeds = new Dictionary<string, IReadOnlyList<(string Name, bool Required)>>(StringComparer.Ordinal);

        foreach (var seed in document.RootElement.GetProperty(SkillsProperty).EnumerateArray())
        {
            var skillName = seed.GetProperty(NameProperty).GetString();
            if (string.IsNullOrWhiteSpace(skillName))
            {
                continue;
            }

            var parameters = new List<(string Name, bool Required)>();
            if (seed.TryGetProperty(ParametersProperty, out var declared)
                && declared.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in declared.EnumerateArray())
                {
                    var parameterName = parameter.GetProperty(NameProperty).GetString();
                    if (string.IsNullOrWhiteSpace(parameterName))
                    {
                        continue;
                    }

                    parameters.Add((
                        parameterName!,
                        parameter.TryGetProperty(RequiredProperty, out var required)
                        && required.ValueKind == JsonValueKind.True));
                }
            }

            seeds[skillName!] = parameters;
        }

        return seeds;
    }

    /// <summary>
    /// Every structured entry, built into the undo it would really produce. The synthetic call satisfies
    /// the entry by construction - one value per copied argument, one for the result id - so a false
    /// TryBuildUndo here means the entry contradicts itself and is reported as such rather than skipped.
    /// </summary>
    private static List<(string SkillName, InverseSkillEntry Entry, SkillUndoInvocation Undo)> StructuredUndos()
    {
        var built = new List<(string SkillName, InverseSkillEntry Entry, SkillUndoInvocation Undo)>();

        foreach (var (skillName, entry) in InverseSkillRegistry.Map)
        {
            if (entry.CopiedArguments.Count == 0 && entry.ResultIdProperty == null)
            {
                continue;
            }

            var arguments = new JsonObject();
            foreach (var argumentName in entry.CopiedArguments)
            {
                arguments[argumentName] = SyntheticValue;
            }

            var result = new JsonObject();
            if (entry.ResultIdProperty != null)
            {
                result[entry.ResultIdProperty] = SyntheticValue;
            }

            InverseSkillRegistry.TryBuildUndo(
                    skillName, arguments.ToJsonString(), result.ToJsonString(), out var undo)
                .ShouldBeTrue(
                    $"{skillName} declares a structured inverse, but building it from a call that satisfies "
                    + "that declaration produced nothing");

            built.Add((skillName, entry, undo!));
        }

        return built;
    }

    /// <summary>
    /// The parameters the inverse skill declares in the seeds. A missing skill is reported here rather
    /// than thrown as a lookup failure, so the two argument guards below stay readable when the pair also
    /// fails EveryStructuredEntry_PointsAtASkillThatIsSeeded.
    /// </summary>
    /// <param name="skillName">The skill being undone, named so the failure points at the entry.</param>
    /// <param name="entry">Its registry entry, whose SkillName is the inverse looked up.</param>
    private static IReadOnlyList<(string Name, bool Required)> DeclaredParameters(
        string skillName, InverseSkillEntry entry)
    {
        SeededParameters.TryGetValue(entry.SkillName, out var declared).ShouldBeTrue(
            $"{skillName} is undone by '{entry.SkillName}', which is not in skill-seeds.json");

        return declared!;
    }

    [Test]
    public void TheStructuredHalfOfTheRegistry_IsNotEmpty_SoTheseGuardsAreNotVacuous()
    {
        StructuredUndos().Count.ShouldBeGreaterThanOrEqualTo(MinimumStructuredPairCount);
    }

    [Test]
    public void EveryStructuredEntry_PointsAtASkillThatIsSeeded()
    {
        foreach (var (skillName, entry, _) in StructuredUndos())
        {
            SeededParameters.ShouldContainKey(
                entry.SkillName,
                $"{skillName} is undone by '{entry.SkillName}', which is not in skill-seeds.json - the undo "
                + "would be held as a pending confirmation for a skill the executor cannot call");
        }
    }

    [Test]
    public void EveryStructuredEntry_ProducesOnlyArgumentsTheInverseSkillDeclares()
    {
        foreach (var (skillName, entry, undo) in StructuredUndos())
        {
            var declared = DeclaredParameters(skillName, entry).Select(parameter => parameter.Name).ToList();

            foreach (var produced in undo.Arguments.Keys)
            {
                declared.ShouldContain(
                    produced,
                    $"{skillName} builds its undo with '{produced}', which '{entry.SkillName}' does not "
                    + $"declare (it declares {string.Join(", ", declared)}) - the value would be dropped and "
                    + "the undo would report success without undoing anything");
            }
        }
    }

    [Test]
    public void EveryStructuredEntry_ProducesEveryRequiredArgumentOfTheInverseSkill()
    {
        foreach (var (skillName, entry, undo) in StructuredUndos())
        {
            var missing = DeclaredParameters(skillName, entry)
                .Where(parameter => parameter.Required && !undo.Arguments.ContainsKey(parameter.Name))
                .Select(parameter => parameter.Name)
                .ToList();

            missing.ShouldBeEmpty(
                $"{skillName} builds an undo for '{entry.SkillName}' without its required argument(s) "
                + $"{string.Join(", ", missing)} - the call would fail after the offer was already made");
        }
    }
}
