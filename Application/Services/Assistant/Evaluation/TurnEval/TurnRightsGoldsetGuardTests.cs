// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the role dimension of the goldsets against the rights model itself: an item that claims a request
/// exceeds its role must name a skill whose required permissions the role really lacks, an item that expects a
/// tool for a role must only expect skills that role may use, and the rights goldset must cover the tiers and
/// the four languages the dimension is meant to measure. Without this a mislabelled item would score a correct
/// model as wrong, or a wrong one as right, with nothing in the numbers pointing at the label.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnRightsGoldsetGuardTests
{
    private const string RightsGoldsetFileName = "turn-rights-v1.json";
    private const int MinRightsItems = 10;
    private const int MaxRightsItems = 20;

    private static readonly string[] RequiredLocales = ["de", "en", "fr", "it"];

    private static readonly string[] GoldsetsRelativePath = ["Klacks.Api", "Application", "Skills", "Goldsets"];

    private static readonly string[] SkillSeedsRelativePath =
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"];

    private static readonly string[] PluginFeaturesPath = ["Klacks.Api", "Plugins", "Features"];

    private static readonly HashSet<string> TurnGoldsetKinds =
        new(["turn-selection", "turn-honesty", "turn-correction"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void RightsGoldset_HasTheExpectedSizeAndEveryItemDeclaresARole()
    {
        var items = LoadRightsItems();

        items.Count.ShouldBeInRange(MinRightsItems, MaxRightsItems);
        items.Where(i => i.Role == null).Select(i => i.Id).ShouldBeEmpty();
    }

    [Test]
    public void RightsGoldset_CoversEveryTierAndBothOutcomesOfThePlanner()
    {
        var items = LoadRightsItems();

        items.Select(i => i.Role).Distinct().Order().ShouldBe(
            [TurnGoldsetRole.Admin, TurnGoldsetRole.Supervisor, TurnGoldsetRole.Planner]);
        items.Count(i => i.Role == TurnGoldsetRole.Planner && i.ExpectsRightsRefusal).ShouldBeGreaterThan(0);
        items.Count(i => i.Role == TurnGoldsetRole.Planner && i.ExpectedTool != null).ShouldBeGreaterThan(0);
        RequiredLocales.Except(items.Select(i => i.Locale!)).ShouldBeEmpty();
    }

    [Test]
    public void RightsGoldset_ItemsAreHandwritten()
    {
        LoadRightsItems()
            .Where(i => TurnGoldsetOriginResolver.Resolve(i.Source) != TurnGoldsetOrigin.Handwritten)
            .Select(i => i.Id)
            .ShouldBeEmpty();
    }

    [Test]
    public void RightsRefusalItems_NameADeniedSkillTheirRoleReallyLacks()
    {
        var skills = LoadSkillPermissions();
        var violations = new List<string>();

        foreach (var (fileName, item) in LoadRoleBearingItems().Where(x => x.Item.ExpectsRightsRefusal))
        {
            if (item.ExpectedTool != null || item.ExpectedSlots.Count > 0 || item.ExpectedRecipe != null)
            {
                violations.Add($"{fileName}/{item.Id}: a rights-refusal item must not expect a tool, slots or a recipe");
            }

            if (item.Role == TurnGoldsetRole.Admin)
            {
                violations.Add($"{fileName}/{item.Id}: Admin holds every right, nothing can exceed it");
            }

            if (string.IsNullOrWhiteSpace(item.DeniedSkill) || !skills.TryGetValue(item.DeniedSkill, out var required))
            {
                violations.Add($"{fileName}/{item.Id}: deniedSkill '{item.DeniedSkill}' is not a skill seed");
                continue;
            }

            var rights = TurnGoldsetRightsResolver.Resolve(item, []);
            if (required.Count == 0 || Permissions.HasAllPermissions(rights, required.ToArray()))
            {
                violations.Add(
                    $"{fileName}/{item.Id}: role {item.Role} holds the rights of '{item.DeniedSkill}' " +
                    $"({string.Join(", ", required)}), so no refusal is expected");
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void RoleItemsExpectingATool_OnlyExpectSkillsTheirRoleMayUse()
    {
        var skills = LoadSkillPermissions();
        var violations = new List<string>();

        foreach (var (fileName, item) in LoadRoleBearingItems().Where(x => !x.Item.ExpectsRightsRefusal))
        {
            var rights = TurnGoldsetRightsResolver.Resolve(item, []);
            foreach (var tool in new[] { item.ExpectedTool }.Concat(item.AlternativeTools).OfType<string>())
            {
                if (!skills.TryGetValue(tool, out var required))
                {
                    violations.Add($"{fileName}/{item.Id}: '{tool}' is not a skill seed");
                }
                else if (!Permissions.HasAllPermissions(rights, required.ToArray()))
                {
                    violations.Add($"{fileName}/{item.Id}: role {item.Role} lacks {string.Join(", ", required)} for '{tool}'");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void DeniedSkillWithoutARightsRefusalExpectation_IsRejected()
    {
        LoadRoleBearingItems()
            .Where(x => x.Item.DeniedSkill != null && !x.Item.ExpectsRightsRefusal)
            .Select(x => $"{x.FileName}/{x.Item.Id}")
            .ShouldBeEmpty();
    }

    private static List<TurnGoldsetItem> LoadRightsItems() =>
        Deserialize(RepositoryRootLocator.RequireFile([.. GoldsetsRelativePath, RightsGoldsetFileName])).Items;

    private static List<(string FileName, TurnGoldsetItem Item)> LoadRoleBearingItems()
    {
        var result = new List<(string, TurnGoldsetItem)>();
        var directory = RepositoryRootLocator.RequireDirectory(GoldsetsRelativePath);

        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!TryDeserializeTurnGoldset(path, out var document))
            {
                continue;
            }

            result.AddRange(document.Items
                .Where(i => i.Role != null || i.ExpectsRightsRefusal || i.DeniedSkill != null)
                .Select(i => (Path.GetFileName(path), i)));
        }

        return result;
    }

    private static bool TryDeserializeTurnGoldset(string path, out TurnGoldsetDocument document)
    {
        using var root = JsonDocument.Parse(File.ReadAllText(path));
        document = new TurnGoldsetDocument();
        if (root.RootElement.ValueKind != JsonValueKind.Object
            || !root.RootElement.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !TurnGoldsetKinds.Contains(kind.GetString()!))
        {
            return false;
        }

        document = Deserialize(path);
        return true;
    }

    private static TurnGoldsetDocument Deserialize(string path) =>
        JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions)
        ?? throw new JsonException($"{path} is empty");

    private static Dictionary<string, List<string>> LoadSkillPermissions()
    {
        var permissions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seedFiles = new[] { RepositoryRootLocator.RequireFile(SkillSeedsRelativePath) }
            .Concat(Directory.GetFiles(
                RepositoryRootLocator.RequireDirectory(PluginFeaturesPath), "skill-seeds.json", SearchOption.AllDirectories));

        foreach (var file in seedFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var skills = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.GetProperty("skills");

            foreach (var skill in skills.EnumerateArray())
            {
                permissions[skill.GetProperty("name").GetString()!] =
                    skill.TryGetProperty("requiredPermissions", out var required)
                        ? required.EnumerateArray().Select(p => p.GetString()!).ToList()
                        : [];
            }
        }

        return permissions;
    }
}
