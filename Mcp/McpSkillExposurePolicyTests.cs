// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpSkillExposurePolicyTests
{
    private ISkillRiskClassifier _riskClassifier = null!;
    private McpSkillExposurePolicy _sut = null!;

    [SetUp]
    public void Setup()
    {
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
        _sut = new McpSkillExposurePolicy(_riskClassifier);
    }

    [Test]
    public void BackendSkill_IsExposed()
    {
        var descriptor = McpTestData.Descriptor("search_employees");

        Assert.That(_sut.IsExposed(descriptor), Is.True);
    }

    [Test]
    public void UiActionExecutionType_IsNotExposed()
    {
        var descriptor = McpTestData.Descriptor("open_dialog", executionType: LlmExecutionTypes.UiAction);

        Assert.That(_sut.IsExposed(descriptor), Is.False);
    }

    [Test]
    public void UiCategory_IsNotExposed()
    {
        var descriptor = McpTestData.Descriptor("navigate_to", category: SkillCategory.UI);

        Assert.That(_sut.IsExposed(descriptor), Is.False);
    }

    [Test]
    public void SensitiveSkill_IsNotExposed()
    {
        var descriptor = McpTestData.Descriptor("delete_system_user", category: SkillCategory.Crud);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Sensitive);

        Assert.That(_sut.IsExposed(descriptor), Is.False);
    }

    [Test]
    public void IrreversibleSkill_IsExposed()
    {
        var descriptor = McpTestData.Descriptor("update_client", category: SkillCategory.Crud);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.Irreversible);

        Assert.That(_sut.IsExposed(descriptor), Is.True);
    }

    [Test]
    public void ExcludedSkill_IsNotExposed_EvenWhenClassifiedReadOnly()
    {
        var descriptor = McpTestData.Descriptor("list_personal_access_tokens", category: SkillCategory.Query);
        _riskClassifier.Classify(descriptor).Returns(SkillRiskClass.ReadOnly);

        Assert.That(_sut.IsExposed(descriptor), Is.False);
    }

    // End-to-end guard with the REAL classifier, no mock: all three personal-access-token skills stay
    // invisible to external agents, for two different reasons. create/revoke are Sensitive; the pure
    // read is a plain ReadOnly Query and is withheld by the policy's own exclusion list instead, which
    // is what keeps it free of a chat confirmation. The /mcp endpoint accepts PAT authentication, so a
    // stolen token must never enumerate or revoke the remaining tokens of its owner.
    [TestCase("list_personal_access_tokens", SkillCategory.Query)]
    [TestCase("create_personal_access_token", SkillCategory.Crud)]
    [TestCase("revoke_personal_access_token", SkillCategory.Crud)]
    public void PersonalAccessTokenSkills_RealClassifier_AreNotExposed(string name, SkillCategory category)
    {
        var sut = new McpSkillExposurePolicy(new SkillRiskClassifier());

        Assert.That(sut.IsExposed(McpTestData.Descriptor(name, category)), Is.False);
    }

    // Control for the case above: the exclusion must bite on the NAME, not on "read-only skill in the
    // security area". Without it the test above would also pass with an empty exclusion list.
    [Test]
    public void OtherReadOnlySecuritySkill_RealClassifier_StaysExposed()
    {
        var sut = new McpSkillExposurePolicy(new SkillRiskClassifier());

        var descriptor = McpTestData.Descriptor("explain_personal_access_tokens", category: SkillCategory.Query);

        Assert.That(sut.IsExposed(descriptor), Is.True);
    }
}
