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
    [TestCase("set_proactive_governance")]
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

    // Ordinary writers that may run unconfirmed at the default Autonomous level. Since the classifier
    // fails closed, they are Irreversible because IrreversibleSkills lists them - not because they fell
    // through anything.
    [TestCase("accept_scenario")]
    [TestCase("update_client")]
    [TestCase("delete_shift")]
    [TestCase("update_general_settings")]
    [TestCase("create_calendar_selection")]
    [TestCase("reset_container_day")]
    public void Classify_ListedWriters_ReturnsIrreversible(string name)
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
    [TestCase("list_open_findings")]
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

    // The grouping apply skill is deliberately NOT Sensitive (owner decision): it is the second half of
    // a propose/apply pair, so the user already approved the full preview before it is ever called. The
    // extra gate asked a second time for the same action and, because the token does not survive into
    // the next turn's history, kept failing to be redeemed. Irreversible still gates it below the
    // Autonomous default level, so a deliberately lowered autonomy level keeps its confirmation.
    [TestCase("apply_grouping")]
    public void Classify_GroupingApplySkill_IsNotSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Irreversible));
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

    // The read-only dry run that precedes the apply skill must never be gated.
    [TestCase("propose_grouping")]
    public void Classify_GroupingProposalSkills_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Query)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // Etappe 5: create_container_template used to fall through to the Irreversible default (Crud is a
    // write category), which the hardened UnattendedSkillPolicy refuses on every background path unless a
    // scheduled task opts in - so the autonomous remediation of an empty container would have hit a wall.
    // It is Reversible now because a real inverse skill exists and is registered, not because it was
    // asserted into ReversibleExtras.
    [Test]
    public void Classify_CreateContainerTemplate_IsReversible_ThroughItsRegisteredInverse()
    {
        InverseSkillRegistry.TryGet("create_container_template", out var inverse).ShouldBeTrue();
        inverse.SkillName.ShouldBe("delete_container_template");

        _sut.Classify(Descriptor("create_container_template")).ShouldBe(SkillRiskClass.Reversible);
    }

    // The inverse itself is container-scoped: it deletes EVERY weekday template of the container, so it
    // must not run unconfirmed at the default Autonomous level. Being Sensitive does not weaken the
    // reversibility it lends its counterpart - IsReversible only asks whether an inverse is registered.
    [Test]
    public void Classify_DeleteContainerTemplate_IsSensitive_BecauseItWipesEveryTemplateOfTheContainer()
    {
        _sut.Classify(Descriptor("delete_container_template")).ShouldBe(SkillRiskClass.Sensitive);
    }

    // create_donation_checkout opens a Stripe Checkout session and hands back its payment link, so it
    // starts a real payment flow. Owner decision - Sensitive, and the reason is unattended execution
    // rather than irreversibility: its Action category alone would fall through to the classifier's
    // Irreversible default, which a scheduled task carrying the per-task opt-in still passes at the
    // Autonomous level (the dev default already sits at FullyAutonomous). Only Sensitive is
    // unconditionally closed on every background path - proven by
    // UnattendedSkillPolicyTests.Decide_CreateDonationCheckout_StaysDeniedAtTheHighestLevelEvenWithTheOptIn.
    [Test]
    public void Classify_CreateDonationCheckout_IsSensitive_BecauseItStartsAPaymentFlow()
    {
        _sut.Classify(Descriptor("create_donation_checkout", SkillCategory.Action))
            .ShouldBe(SkillRiskClass.Sensitive);
    }

    [TestCase(SkillCategory.Query)]
    [TestCase(SkillCategory.Read)]
    [TestCase(SkillCategory.Validation)]
    [TestCase(SkillCategory.UI)]
    public void Classify_ReadOnlyCategories_ReturnsReadOnly(SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor("some_unknown_skill", category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // The trap: a write-category skill whose name carries a read-only prefix must NOT be read-only. None
    // of these is listed anywhere, so they land on the fail-closed Sensitive default - the point of the
    // test is that the read-only prefix buys them nothing.
    [TestCase("evaluate_revenue")]
    [TestCase("generate_invoice")]
    [TestCase("check_balance")]
    [TestCase("detect_fraud")]
    public void Classify_WriteCategoryWithReadPrefix_IsNeverReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Crud)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // A read-only name prefix still classifies non-write categories (e.g. System) as read-only.
    [Test]
    public void Classify_ReadPrefixOnNonWriteCategory_ReturnsReadOnly()
    {
        Assert.That(
            _sut.Classify(Descriptor("get_system_status", SkillCategory.System)),
            Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // The fail-closed default. A write skill nobody classified used to be Irreversible, which passes the
    // chat gate unconfirmed at the factory-default Autonomous level and passes an opted-in scheduled task
    // - so simply forgetting a new skill handed it live data unasked. Sensitive is held at every level and
    // refused on every unattended path instead. WriteSkillRiskDecisionCoverageTests makes sure a real
    // seeded skill never gets here silently.
    [Test]
    public void Classify_UnlistedWriteSkill_FailsClosedToSensitive()
    {
        Assert.That(_sut.Classify(Descriptor("brand_new_writer")), Is.EqualTo(SkillRiskClass.Sensitive));
        Assert.That(_sut.Classify(Descriptor("brand_new_writer", SkillCategory.Action)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // The messaging plugin seeds category "Communication", which SkillCategory does not know, so
    // ParseCategory falls back to Action - a write category. That made two plain reads Irreversible and
    // therefore gated; they are allow-listed explicitly now, while the plugin's writer stays Sensitive.
    [TestCase("read_messages")]
    [TestCase("list_messaging_providers")]
    public void Classify_MessagingPluginReads_ReturnsReadOnly(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name, SkillCategory.Action)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // send_message reaches a client's phone through Telegram, WhatsApp, Signal or SMS and cannot be
    // recalled - the same outward-facing, irrevocable shape as create_donation_checkout.
    [Test]
    public void Classify_SendMessage_IsSensitive_BecauseItLeavesTheInstallation()
    {
        Assert.That(_sut.Classify(Descriptor("send_message", SkillCategory.Action)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // rollback_my_last_change only looks the inverse up and returns it as a proposal; the inverse call it
    // suggests runs through the gate on its own. Same reason as create_plan.
    [Test]
    public void Classify_RollbackProposal_ReturnsReadOnly()
    {
        Assert.That(_sut.Classify(Descriptor("rollback_my_last_change", SkillCategory.Action)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }

    // seal_shift turns an order into a permanently immutable SealedOrder and no unseal skill exists;
    // set_sealed_order_until_date is the single change that row ever accepts again, and only once.
    [TestCase("seal_shift")]
    [TestCase("set_sealed_order_until_date")]
    public void Classify_SealLifecycleWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // Membership dates are the plannability boundary - the reason delete_membership is Sensitive.
    [TestCase("end_client_membership")]
    [TestCase("update_membership")]
    public void Classify_MembershipBoundaryWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // Same payroll blast radius that put delete_contract, delete_macro, delete_monthly_target_hours and
    // update_calendar_selection into SensitiveSkills: a wrong value silently moves figures that were
    // already computed against it.
    [TestCase("update_contract")]
    [TestCase("create_macro")]
    [TestCase("update_macro")]
    [TestCase("create_monthly_target_hours")]
    [TestCase("update_monthly_target_hours")]
    [TestCase("update_overtime_settings")]
    [TestCase("update_surcharge_mode_settings")]
    [TestCase("update_compensatory_rest_settings")]
    [TestCase("update_owner_locale_settings")]
    [TestCase("import_calendar_rules")]
    public void Classify_PayrollRelevantWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // create_spam_rule and update_spam_rule call the same TriggerReclassification() sweep that put
    // delete_spam_rule into SensitiveSkills.
    [TestCase("create_spam_rule")]
    [TestCase("update_spam_rule")]
    public void Classify_SpamRuleWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // Klacksy never widens its own mandate: its instructions, its personality and the switch that decides
    // whether a working-time protection refuses or merely warns; plus installing or removing a plugin,
    // which is what ADDS skills to it in the first place.
    [TestCase("update_ai_guidelines", SkillCategory.Crud)]
    [TestCase("update_ai_soul", SkillCategory.Crud)]
    [TestCase("update_compliance_enforcement_settings", SkillCategory.Crud)]
    [TestCase("install_feature_plugin", SkillCategory.Crud)]
    [TestCase("uninstall_feature_plugin", SkillCategory.Crud)]
    [TestCase("install_whisper_plugin", SkillCategory.Crud)]
    [TestCase("uninstall_whisper_plugin", SkillCategory.Crud)]
    public void Classify_MandateWideningWriters_ReturnsSensitive(string name, SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor(name, category)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // update_user changes somebody else's login name and email and update_my_account the caller's own -
    // the same fields either way; update_data_retention_settings drives the background job that
    // hard-deletes soft-deleted rows; clear_client_availability wipes every availability row of a client
    // across a whole range and its clear_ name hides it from the delete_/remove_ guard.
    [TestCase("update_user")]
    [TestCase("update_my_account")]
    [TestCase("update_data_retention_settings")]
    [TestCase("clear_client_availability")]
    public void Classify_IdentityAndBulkLossWriters_ReturnsSensitive(string name)
    {
        Assert.That(_sut.Classify(Descriptor(name)), Is.EqualTo(SkillRiskClass.Sensitive));
    }

    // The three non-mutating UiActions must stay un-gated: search_in_list and select_group classify
    // ReadOnly via their Query category, start_guided_tour (Action category, only launches the
    // onboarding tour overlay) is allow-listed explicitly. All mutating UiActions keep the
    // Irreversible default (see update_general_settings above).
    [TestCase("search_in_list", SkillCategory.Query)]
    [TestCase("select_group", SkillCategory.Query)]
    [TestCase("start_guided_tour", SkillCategory.Action)]
    [TestCase("email_schedule_to_client", SkillCategory.UI)]
    public void Classify_NonMutatingUiActions_ReturnsReadOnly(string name, SkillCategory category)
    {
        Assert.That(_sut.Classify(Descriptor(name, category)), Is.EqualTo(SkillRiskClass.ReadOnly));
    }
}
