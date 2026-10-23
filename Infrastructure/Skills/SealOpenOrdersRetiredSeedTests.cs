// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-10-23 ("Massen-Versiegeln: nein"): Klacksy may no longer call seal_open_orders at all - not in the
/// chat, not in a plan, not over MCP or a background path. The skill is switched off in its seed, with a raised version so
/// the loader switches the stored row off on existing installations too, and the registry only ever loads enabled rows.
/// Mass sealing comes back only as part of the guided order review (Phase B). The single-order seal_shift stays enabled.
/// </summary>

using System.Text.Json.Nodes;
using Klacks.Api.Application.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SealOpenOrdersRetiredSeedTests
{
    private const int LastEnabledVersion = 4;

    private static readonly string[] SeedRelativePath =
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"];

    private static JsonObject Seed(string skillName)
    {
        var root = JsonNode.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(SeedRelativePath)))!;
        return root["skills"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(skill => skill["name"]!.GetValue<string>() == skillName);
    }

    [Test]
    public void SealOpenOrders_IsSwitchedOff_WithAVersionThatReachesExistingInstallations()
    {
        var seed = Seed(SealOpenOrdersSkill.SkillName);

        seed["isEnabled"]!.GetValue<bool>().ShouldBeFalse();
        seed["version"]!.GetValue<int>().ShouldBeGreaterThan(LastEnabledVersion);
    }

    [Test]
    public void SealShift_StaysEnabled()
    {
        Seed(SealShiftSkill.SkillName)["isEnabled"]!.GetValue<bool>().ShouldBeTrue();
    }
}
