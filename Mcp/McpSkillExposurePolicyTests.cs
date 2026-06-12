// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
}
