// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins which skills a stop may cut short. The rule is the risk class ReadOnly AND not an allow-list
/// exception AND not a UI action; neither the read-only name prefix nor the risk class alone is safe. The
/// two counter-examples are named skills of the catalogue: check_erp_drop_point_folder_health has a
/// read-only prefix and creates the folder when it is missing, and create_plan is ReadOnly by exception
/// although it stores a plan and a confirmation token.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.UnitTest.Infrastructure.Skills;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class CancellableSkillPolicyTests
{
    private const string QuerySkill = "search_employees";
    private const string ReadPrefixedQuerySkill = "list_groups";
    private const string FolderHealthSkill = "check_erp_drop_point_folder_health";
    private const string CreatePlanSkill = "create_plan";
    private const string WriteSkill = "create_employee";
    private const string SensitiveSkill = "delete_client";
    private const string UiActionSkill = "search_in_list";
    private const string UnknownSkill = "no_such_skill";
    private const int MinimumCancellableSeededSkills = 50;

    private ISkillRegistry _registry = null!;
    private CancellableSkillPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _policy = new CancellableSkillPolicy(_registry, new SkillRiskClassifier());
    }

    [Test]
    public void APlainQuerySkill_ReceivesTheStopToken()
    {
        Register(QuerySkill, SkillCategory.Query);

        _policy.ReceivesStopToken(QuerySkill).ShouldBeTrue();
    }

    [Test]
    public void AReadPrefixedSkillOfAReadCategory_ReceivesTheStopToken()
    {
        Register(ReadPrefixedQuerySkill, SkillCategory.Read);

        _policy.ReceivesStopToken(ReadPrefixedQuerySkill).ShouldBeTrue();
    }

    [Test]
    public void TheFolderHealthCheck_NeverReceivesTheStopTokenBecauseItCreatesTheFolder()
    {
        RegisterFromSeeds(FolderHealthSkill);

        ReadOnlySkillPrefixes.HasReadOnlyPrefix(FolderHealthSkill)
            .ShouldBeTrue("the name prefix alone would wrongly qualify it, which is why the prefix is never the rule");
        new SkillRiskClassifier().Classify(_registry.GetSkillByName(FolderHealthSkill)!)
            .ShouldNotBe(SkillRiskClass.ReadOnly);
        _policy.ReceivesStopToken(FolderHealthSkill).ShouldBeFalse();
    }

    [Test]
    public void CreatePlan_NeverReceivesTheStopTokenBecauseItStoresThePlanAndTheToken()
    {
        RegisterFromSeeds(CreatePlanSkill);

        new SkillRiskClassifier().Classify(_registry.GetSkillByName(CreatePlanSkill)!)
            .ShouldBe(SkillRiskClass.ReadOnly, "the counter-example is ReadOnly by exception, which is why the class alone is unsafe");
        _policy.ReceivesStopToken(CreatePlanSkill).ShouldBeFalse();
    }

    [TestCase(WriteSkill, SkillCategory.Crud)]
    [TestCase(SensitiveSkill, SkillCategory.Crud)]
    public void AWriteSkill_NeverReceivesTheStopToken(string skill, SkillCategory category)
    {
        Register(skill, category);

        _policy.ReceivesStopToken(skill).ShouldBeFalse();
    }

    [Test]
    public void AReadOnlyPrefixOnAWriteCategory_IsNotEnough()
    {
        Register("get_and_reset_counter", SkillCategory.Action);

        _policy.ReceivesStopToken("get_and_reset_counter").ShouldBeFalse();
    }

    [Test]
    public void AUiAction_NeverReceivesTheStopTokenEvenWhenItIsAQuery()
    {
        Register(UiActionSkill, SkillCategory.Query, LlmExecutionTypes.UiAction);

        _policy.ReceivesStopToken(UiActionSkill).ShouldBeFalse();
    }

    [Test]
    public void AnUnknownSkill_NeverReceivesTheStopToken()
    {
        _registry.GetSkillByName(UnknownSkill).Returns((SkillDescriptor?)null);

        _policy.ReceivesStopToken(UnknownSkill).ShouldBeFalse();
    }

    [Test]
    public void EverySkillOfTheReadOnlyExceptionList_IsRefusedTheStopToken()
    {
        foreach (var name in SkillRiskClassifier.ReadOnlyExtras)
        {
            Register(name, SkillCategory.Query);

            _policy.ReceivesStopToken(name).ShouldBeFalse(name);
        }
    }

    [Test]
    public void EverySkillOfTheSeedCatalogueThatReceivesTheStopToken_IsARead()
    {
        var seeded = SkillSeedCatalog.EnabledSkills().ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        _registry.GetSkillByName(Arg.Any<string>()).Returns(call =>
            seeded.TryGetValue(call.Arg<string>(), out var skill) ? SkillSeedCatalog.ToDescriptor(skill) : null);

        var cancellable = seeded.Values.Where(skill => _policy.ReceivesStopToken(skill.Name)).ToList();

        cancellable.Count.ShouldBeGreaterThan(MinimumCancellableSeededSkills);
        var notReads = cancellable
            .Where(skill => SkillSeedCatalog.IsWriteCategory(skill.Category)
                || SkillRiskClassifier.IrreversibleSkills.Contains(skill.Name)
                || SkillRiskClassifier.SensitiveSkills.Contains(skill.Name)
                || SkillRiskClassifier.ScenarioGatedSkills.Contains(skill.Name)
                || SkillRiskClassifier.ReversibleExtras.Contains(skill.Name)
                || SkillRiskClassifier.ReadOnlyExtras.Contains(skill.Name))
            .Select(skill => $"{skill.Name} ({skill.Category})")
            .ToList();
        notReads.ShouldBeEmpty("A skill that a stop may cut short must have no write category and sit on no risk list: " + string.Join(", ", notReads));
    }

    [Test]
    public void NoSkillOfTheSeedCatalogueThatTheClassifierListsAsARiskyWrite_IsClassifiedReadOnly()
    {
        // ReadOnly is decided before the Irreversible list, so the seeded category is the only thing that keeps a
        // listed skill from being classified ReadOnly and receiving the stop token.
        var classifier = new SkillRiskClassifier();
        var listedButReadOnly = SkillSeedCatalog.EnabledSkills()
            .Where(skill => SkillRiskClassifier.IrreversibleSkills.Contains(skill.Name)
                || SkillRiskClassifier.SensitiveSkills.Contains(skill.Name)
                || SkillRiskClassifier.ScenarioGatedSkills.Contains(skill.Name))
            .Where(skill => classifier.Classify(SkillSeedCatalog.ToDescriptor(skill)) == SkillRiskClass.ReadOnly)
            .Select(skill => $"{skill.Name} ({skill.Category})")
            .ToList();

        listedButReadOnly.ShouldBeEmpty("A listed write that classifies ReadOnly would receive the stop token: " + string.Join(", ", listedButReadOnly));
    }

    private void RegisterFromSeeds(string name)
    {
        var seeded = SkillSeedCatalog.EnabledSkills().SingleOrDefault(skill =>
            string.Equals(skill.Name, name, StringComparison.OrdinalIgnoreCase));
        seeded.ShouldNotBeNull($"{name} must be seeded, otherwise this pin says nothing about the catalogue");
        _registry.GetSkillByName(name).Returns(SkillSeedCatalog.ToDescriptor(seeded));
    }

    private void Register(string name, SkillCategory category, string executionType = LlmExecutionTypes.Skill)
    {
        _registry.GetSkillByName(name).Returns(new SkillDescriptor(
            name,
            string.Empty,
            category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null)
        {
            ExecutionType = executionType
        });
    }
}
