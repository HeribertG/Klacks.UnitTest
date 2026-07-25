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
    [TestCase("update_calendar_selection")]
    [TestCase("delete_calendar_selection")]
    [TestCase("delete_macro")]
    [TestCase("delete_contract")]
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
    [TestCase("add_expense")]
    [TestCase("delete_work")]
    [TestCase("cancel_wizard_job")]
    [TestCase("delete_email")]
    public void Classify_InverseMappedOrExtraSkills_ReturnsReversible(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Reversible));
    }

    [TestCase("accept_scenario")]
    [TestCase("update_client")]
    [TestCase("delete_shift")]
    [TestCase("update_general_settings")]
    [TestCase("email_schedule_to_client")]
    [TestCase("create_calendar_selection")]
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
    // create_plan only drafts a plan + returns its own confirmation; execution is deferred behind
    // confirm_pending_action, so its proposal call must stay un-gated at every autonomy level.
    [TestCase("find_customer_candidates")]
    [TestCase("find_split_shift_candidates")]
    [TestCase("create_plan")]
    public void Classify_ReadOnlyExtras_WithCrudCategory_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Crud)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // The company-rule apply/revert skills persist high-impact, only partially reversible changes and
    // must always require human confirmation (Sensitive).
    [TestCase("apply_company_rule")]
    [TestCase("revert_company_rule")]
    public void Classify_CompanyRuleWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // The company-rule intake steps that only touch the ephemeral in-memory draft must never be gated.
    // start/set/cancel carry a Crud category and are allow-listed; preview carries a Query category.
    [TestCase("start_company_rule", SkillCategory.Crud)]
    [TestCase("set_company_rule_parameters", SkillCategory.Crud)]
    [TestCase("cancel_company_rule", SkillCategory.Crud)]
    [TestCase("preview_company_rule", SkillCategory.Query)]
    [TestCase("list_company_rules", SkillCategory.Query)]
    public void Classify_CompanyRuleDraftAndReadSkills_ReturnsReadOnly(string name, SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor(name, category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // apply_planning_profile creates real SchedulingRule rows and flips ACTIVE_INDUSTRIES, so it must
    // always require human confirmation (Sensitive) even at the Autonomous default level.
    [Test]
    public void Classify_ApplyPlanningProfile_ReturnsSensitive()
    {
        Assert.That(_sut.Classify(Descriptor("apply_planning_profile")), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // The planning-profile intake steps that only touch the ephemeral draft must never be gated.
    // start/set/cancel carry a Crud category and are allow-listed; preview carries a Query category.
    [TestCase("start_planning_profile_setup", SkillCategory.Crud)]
    [TestCase("set_planning_profile_parameters", SkillCategory.Crud)]
    [TestCase("cancel_planning_profile_setup", SkillCategory.Crud)]
    [TestCase("preview_planning_profile", SkillCategory.Query)]
    public void Classify_PlanningProfileDraftAndReadSkills_ReturnsReadOnly(string name, SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor(name, category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // The two grouping apply skills rewrite the membership of the whole customer/employee base in one
    // transaction, so they must always require human confirmation — exactly like the single
    // delete_membership that is already Sensitive.
    [TestCase("apply_customer_grouping")]
    [TestCase("apply_employee_grouping")]
    public void Classify_GroupingApplySkills_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // Counterpart to the test above: the bulk group writers that default to apply=false must NOT be
    // Sensitive. Classify() only sees the skill name, never the parameters, so listing them would gate
    // their read-only preview call too — breaking the dry-run-then-apply idiom (and, for the four that
    // are recipe mutate steps, stalling the recipe on its own preview). Their preview stays un-gated
    // and the apply call keeps the Irreversible default.
    [TestCase("fill_group_by_criteria")]
    [TestCase("group_ungrouped_by_city_name")]
    [TestCase("bulk_add_shifts_to_group")]
    [TestCase("bulk_add_absence_for_group")]
    [TestCase("add_selected_clients_to_group")]
    public void Classify_PreviewDefaultBulkGroupWriters_StayIrreversible(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Irreversible));
    }

    // The read-only dry runs that precede the apply skills must never be gated.
    [TestCase("propose_customer_grouping")]
    [TestCase("propose_employee_grouping")]
    public void Classify_GroupingProposalSkills_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Query)), Is.EqualTo(SkillRiskClass.ReadOnly));
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

    // The three non-mutating UiActions must stay un-gated: search_in_list and select_group classify
    // ReadOnly via their Query category, start_guided_tour (Action category, only launches the
    // onboarding tour overlay) is allow-listed explicitly. All mutating UiActions keep the
    // Irreversible default (see update_general_settings above).
    [TestCase("search_in_list", SkillCategory.Query)]
    [TestCase("select_group", SkillCategory.Query)]
    [TestCase("start_guided_tour", SkillCategory.Action)]
    public void Classify_NonMutatingUiActions_ReturnsReadOnly(string name, SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor(name, category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }
}
