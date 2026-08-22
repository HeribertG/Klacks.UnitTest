// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The read-only prefix list is the single source shared by the LLM loop's repeat guard
/// (LLMService.RejectRepeatedWriteCalls) and SkillRiskClassifier's name fallback. Before the merge the
/// two carried different lists and drifted apart. These tests pin the merged contract: every declared
/// prefix constant is part of All, matching is case-insensitive, and a name without a read-only prefix
/// is not accepted — so a future write skill cannot slip through by name alone.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class ReadOnlySkillPrefixesTests
{
    [TestCase("get_client")]
    [TestCase("list_groups")]
    [TestCase("search_employees")]
    [TestCase("find_replacement")]
    [TestCase("read_email")]
    [TestCase("lookup_location")]
    [TestCase("verify_my_last_action")]
    [TestCase("check_absence_conflicts")]
    [TestCase("detect_conflicts")]
    [TestCase("interpret_resource_monitor")]
    [TestCase("validate_address")]
    [TestCase("test_imap_connection")]
    [TestCase("evaluate_scenario")]
    [TestCase("generate_period_summary")]
    public void SeededReadOnlySkills_AreRecognised(string skillName)
    {
        ReadOnlySkillPrefixes.HasReadOnlyPrefix(skillName).ShouldBeTrue();
    }

    [TestCase("create_employee")]
    [TestCase("delete_client")]
    [TestCase("update_contract")]
    [TestCase("send_message")]
    [TestCase("navigate_to")]
    [TestCase("fetch_new_emails")]
    public void MutatingOrUnprefixedSkills_AreNotRecognised(string skillName)
    {
        ReadOnlySkillPrefixes.HasReadOnlyPrefix(skillName).ShouldBeFalse();
    }

    [Test]
    public void Matching_IsCaseInsensitive()
    {
        ReadOnlySkillPrefixes.HasReadOnlyPrefix("List_Groups").ShouldBeTrue();
    }

    [TestCase(null)]
    [TestCase("")]
    public void EmptyName_IsNotReadOnly(string? skillName)
    {
        ReadOnlySkillPrefixes.HasReadOnlyPrefix(skillName).ShouldBeFalse();
    }

    [Test]
    public void All_ContainsEveryDeclaredPrefixExactlyOnce()
    {
        var declared = typeof(ReadOnlySkillPrefixes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        ReadOnlySkillPrefixes.All.Count.ShouldBe(declared.Count);
        ReadOnlySkillPrefixes.All.Distinct().Count().ShouldBe(ReadOnlySkillPrefixes.All.Count);
        foreach (var prefix in declared)
        {
            ReadOnlySkillPrefixes.All.ShouldContain(prefix);
        }
    }
}
