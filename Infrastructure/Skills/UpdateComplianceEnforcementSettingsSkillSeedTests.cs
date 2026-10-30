// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded parameters of update_compliance_enforcement_settings: every protection takes a reaction (an Enum
/// of warn and block) and no parameter takes an hour or day number, because the COMPLIANCE_ENFORCEMENT_* keys hold the
/// reaction only.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class UpdateComplianceEnforcementSettingsSkillSeedTests
{
    private const string UpdateSkill = "update_compliance_enforcement_settings";
    private const string EnumType = "Enum";

    private static readonly string[] ReactionParameters =
    [
        "defaultMode", "maxDailyHoursMode", "maxWeeklyHoursMode", "minRestHoursMode", "minRestDaysMode",
        "maxConsecutiveDaysMode", "periodCapMode", "rollingAverageMode", "restDayRotationMode", "counterRuleMode",
        "compensatoryRestMode", "restrictedTimeWindowMode", "holidayWorkMode"
    ];

    [Test]
    public void EveryReactionParameter_IsAnEnumOfWarnAndBlock()
    {
        var parameters = Parameters();

        foreach (var name in ReactionParameters)
        {
            parameters.ShouldContainKey(name);
            parameters[name].GetProperty("type").GetString().ShouldBe(EnumType, name);
            parameters[name].GetProperty("enumValues").EnumerateArray().Select(v => v.GetString())
                .ToList().ShouldBe(["warn", "block"], name);
        }
    }

    [Test]
    public void NoParameterTakesAnHourOrDayNumber()
    {
        var numeric = Parameters()
            .Where(p => p.Value.GetProperty("type").GetString() is "Decimal" or "Integer")
            .Select(p => p.Key)
            .ToList();

        numeric.ShouldBe(["rosterPublicationMinLeadDays"]);
        Parameters().Keys.ShouldNotContain("maxDailyHours");
        Parameters().Keys.ShouldNotContain("minRestHours");
    }

    private static Dictionary<string, JsonElement> Parameters() =>
        Skill().GetProperty("parameters").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.Clone());

    private static JsonElement Skill()
    {
        var path = RepositoryRootLocator.RequireFile(
            ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"]);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == UpdateSkill);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{UpdateSkill}' is missing from the seed file.");
        return skill.Clone();
    }
}
