// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Loads the real grouping-intent.json of every language pack and checks that a request to group clients by
/// qualification, written in a pack language, guarantees partition_clients_by_qualification, and that every
/// pack ships at least one qualification token.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class GroupingIntentPluginQualificationTests
{
    private const string QualificationTokensProperty = "qualificationTokens";
    private const string QualificationSkill = "partition_clients_by_qualification";

    [OneTimeSetUp]
    public void LoadRealPackFiles() => GroupingIntentPluginLoader.Load(ApiDirectory());

    [Test]
    public void EveryPack_ShipsQualificationTokens()
    {
        var missing = Directory.GetDirectories(Path.Combine(ApiDirectory(), LanguagePluginConstants.PluginDirectory))
            .Select(dir => Path.Combine(dir, LanguagePluginConstants.GroupingIntentFileName))
            .Where(File.Exists)
            .Where(file =>
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                return !doc.RootElement.TryGetProperty(QualificationTokensProperty, out var tokens)
                    || tokens.GetArrayLength() == 0;
            })
            .ToList();

        missing.ShouldBeEmpty();
    }

    [TestCase("Agrupa a los empleados por cualificación")]
    [TestCase("従業員を資格ごとにグループ分けして")]
    [TestCase("Pogrupuj pracowników według kwalifikacji")]
    [TestCase("按技能给员工分组")]
    [TestCase("Groepeer de medewerkers per kwalificatie")]
    [TestCase("Gruppera medarbetarna efter kvalifikation")]
    public void PackLanguageQualificationGroupingRequest_GuaranteesTheQualificationSkill(string message)
    {
        GroupingIntentResolver.GuaranteedSkillNames(message).ShouldContain(QualificationSkill);
    }

    private static string ApiDirectory()
    {
        return RepositoryRootLocator.ApiProject;
    }
}
