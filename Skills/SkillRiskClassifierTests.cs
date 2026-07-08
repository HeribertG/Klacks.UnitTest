// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillRiskClassifierTests
{
    private SkillRiskClassifier _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new SkillRiskClassifier();
    }

    private static SkillDescriptor Descriptor(string name, SkillCategory category = SkillCategory.Crud)
        => new(name, "test", category, [], [], [], null);

    [TestCase("delete_system_user")]
    [TestCase("assign_user_permissions")]
    [TestCase("set_user_group_scope")]
    [TestCase("set_autonomy_level")]
    [TestCase("create_identity_provider")]
    [TestCase("update_identity_provider")]
    [TestCase("delete_identity_provider")]
    [TestCase("delete_group")]
    [TestCase("delete_branch")]
    [TestCase("delete_client")]
    [TestCase("delete_membership")]
    [TestCase("create_personal_access_token")]
    [TestCase("close_period")]
    [TestCase("create_user")]
    public void Classify_SensitiveSkills_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    [TestCase("propose_plan")]
    [TestCase("start_autowizard")]
    [TestCase("start_wizard1")]
    [TestCase("cover_absence")]
    public void Classify_ScenarioGatedSkills_ReturnsScenarioGated(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.ScenarioGated));
    }

    [TestCase("place_work")]
    [TestCase("add_break")]
    [TestCase("confirm_work")]
    [TestCase("approve_day")]
    [TestCase("reopen_period")]
    [TestCase("create_branch")]
    [TestCase("create_contract")]
    [TestCase("delete_work")]
    [TestCase("cancel_wizard_job")]
    public void Classify_InverseMappedOrExtraSkills_ReturnsReversible(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Reversible));
    }

    [TestCase("accept_scenario")]
    [TestCase("update_client")]
    [TestCase("delete_shift")]
    [TestCase("update_general_settings")]
    [TestCase("email_schedule_to_client")]
    public void Classify_UnmappedWriters_ReturnsIrreversible(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Irreversible));
    }

    // Real read skills carry a read-only category (Query/Read), so they classify as ReadOnly via
    // category — not via their name prefix.
    [TestCase("get_client_details")]
    [TestCase("list_groups")]
    [TestCase("search_employees")]
    [TestCase("read_schedule_state")]
    [TestCase("detect_conflicts")]
    [TestCase("interpret_resource_monitor")]
    [TestCase("evaluate_scenario")]
    [TestCase("generate_period_summary")]
    public void Classify_QueryCategoryReadSkills_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Query)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // Read-only skills that happen to carry a write (Crud) category are allow-listed explicitly.
    [TestCase("find_customer_candidates")]
    [TestCase("find_split_shift_candidates")]
    public void Classify_ReadOnlyExtras_WithCrudCategory_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Crud)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    [TestCase(SkillCategory.Query)]
    [TestCase(SkillCategory.Read)]
    [TestCase(SkillCategory.Validation)]
    [TestCase(SkillCategory.UI)]
    public void Classify_ReadOnlyCategories_ReturnsReadOnly(SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor("some_unknown_skill", category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // The trap: a write-category skill whose name carries a read-only prefix must NOT be read-only.
    [TestCase("evaluate_revenue")]
    [TestCase("generate_invoice")]
    [TestCase("check_balance")]
    [TestCase("detect_fraud")]
    public void Classify_WriteCategoryWithReadPrefix_ReturnsIrreversible(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Crud)), Is.EqualTo(SkillRiskClass.Irreversible));
    }

    // A read-only name prefix still classifies non-write categories (e.g. System) as read-only.
    [Test]
    public void Classify_ReadPrefixOnNonWriteCategory_ReturnsReadOnly()
    {
        Assert.That(
            _sut.Classify(Descriptor("get_system_status", SkillCategory.System)),
            Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    [Test]
    public void Classify_UnknownCrudSkill_DefaultsToIrreversible()
    {
        Assert.That(_sut.Classify(Descriptor("brand_new_writer")), Is.EqualTo(SkillRiskClass.Irreversible));
    }
}
