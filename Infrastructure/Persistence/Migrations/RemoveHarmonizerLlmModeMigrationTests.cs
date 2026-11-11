// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The RemoveHarmonizerLlmMode migration is a pure data cleanup after the language-model mode of wizard stage 3 was
/// removed: it deletes the two Klacksy skills get_wizard_settings and update_wizard_settings with their name-keyed
/// rows (phrases, relations, proposed changes) before the skill rows, and the three WIZARD3_* settings. No schema
/// change; Down is deliberately empty.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class RemoveHarmonizerLlmModeMigrationTests
{
    private const string SkillNames = "('get_wizard_settings', 'update_wizard_settings')";

    private static List<string> UpStatements()
        => new RemoveHarmonizerLlmMode().UpOperations
            .Select(o => o.ShouldBeOfType<SqlOperation>().Sql)
            .Select(sql => Regex.Replace(sql, @"\s+", " ").Trim())
            .ToList();

    [Test]
    public void Up_DeletesTheNameKeyedRowsBeforeTheSkillRows_AndThenTheSettings()
    {
        UpStatements().ShouldBe(new[]
        {
            "DELETE FROM skill_phrase WHERE owner_kind = 'Skill' AND owner_name IN " + SkillNames + ";",
            "DELETE FROM skill_relations WHERE skill_a_name IN " + SkillNames + " OR skill_b_name IN " + SkillNames + ";",
            "DELETE FROM proposed_skill_changes WHERE skill_name IN " + SkillNames + ";",
            "DELETE FROM agent_skills WHERE name IN " + SkillNames + ";",
            "DELETE FROM settings WHERE type IN ('WIZARD3_MODE', 'WIZARD3_LLM_MODEL', 'WIZARD3_CRITIC_MODEL');",
        });
    }

    [Test]
    public void Down_ChangesNothing()
    {
        new RemoveHarmonizerLlmMode().DownOperations.ShouldBeEmpty();
    }
}
