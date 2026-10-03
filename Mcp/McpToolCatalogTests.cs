// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpToolCatalogTests
{
    private ISkillRegistry _skillRegistry = null!;
    private IMcpSkillExposurePolicy _exposurePolicy = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private McpToolCatalog _sut = null!;

    [SetUp]
    public void Setup()
    {
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _exposurePolicy = Substitute.For<IMcpSkillExposurePolicy>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _exposurePolicy.IsExposed(Arg.Any<SkillDescriptor>()).Returns(true);
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
        _sut = new McpToolCatalog(_skillRegistry, _exposurePolicy, _riskClassifier, new McpReadModeToolPolicy(_riskClassifier));
    }

    [Test]
    public void GetToolsForUser_FiltersByExposurePolicy()
    {
        var exposed = McpTestData.Descriptor("search_employees");
        var hidden = McpTestData.Descriptor("navigate_to");
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { exposed, hidden });
        _exposurePolicy.IsExposed(hidden).Returns(false);

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Write);

        Assert.That(tools, Has.Count.EqualTo(1));
        Assert.That(tools[0].Name, Is.EqualTo("search_employees"));
    }

    [Test]
    public void GetToolsForUser_BuildsObjectInputSchemaWithRequiredParameters()
    {
        var descriptor = McpTestData.Descriptor(
            "create_employee",
            SkillCategory.Crud,
            parameters: new List<SkillParameter>
            {
                new("firstName", "First name", SkillParameterType.String, true),
                new("gender", "Gender", SkillParameterType.Enum, false,
                    EnumValues: new List<string> { "male", "female" })
            });
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { descriptor });

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Write);

        var schema = tools[0].InputSchema;
        Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"));
        Assert.That(schema.GetProperty("properties").TryGetProperty("firstName", out _), Is.True);
        Assert.That(schema.GetProperty("properties").GetProperty("gender").GetProperty("enum")[0].GetString(),
            Is.EqualTo("male"));
        Assert.That(schema.GetProperty("required")[0].GetString(), Is.EqualTo("firstName"));
    }

    [Test]
    public void GetToolsForUser_MapsRiskClassToAnnotations()
    {
        var readOnly = McpTestData.Descriptor("list_groups");
        var destructive = McpTestData.Descriptor("update_client", SkillCategory.Crud);
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { readOnly, destructive });
        _riskClassifier.Classify(readOnly).Returns(SkillRiskClass.ReadOnly);
        _riskClassifier.Classify(destructive).Returns(SkillRiskClass.Irreversible);

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Write);

        var readOnlyTool = tools.Single(tool => tool.Name == "list_groups");
        var destructiveTool = tools.Single(tool => tool.Name == "update_client");
        Assert.That(readOnlyTool.Annotations!.ReadOnlyHint, Is.True);
        Assert.That(destructiveTool.Annotations!.DestructiveHint, Is.True);
    }

    [Test]
    public void GetToolsForUser_PassesUserPermissionsToRegistry()
    {
        var permissions = new List<string> { "CanViewClients" };
        _skillRegistry.GetSkillsForUser(permissions).Returns(new List<SkillDescriptor>());

        _sut.GetToolsForUser(permissions, PersonalAccessTokenAccessMode.Write);

        _skillRegistry.Received(1).GetSkillsForUser(permissions);
    }

    [Test]
    public void GetToolsForUser_SetsTheOpenWorldHintOnlyForUntrustedOutputSkills()
    {
        var untrusted = McpTestData.Descriptor("web_search");
        var trusted = McpTestData.Descriptor("list_groups");
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { untrusted, trusted });

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Write);

        Assert.That(tools.Single(tool => tool.Name == "web_search").Annotations!.OpenWorldHint, Is.True);
        Assert.That(tools.Single(tool => tool.Name == "list_groups").Annotations!.OpenWorldHint, Is.Null);
    }

    [Test]
    public void GetToolsForUser_ReadMode_ListsOnlyReadOnlySkills()
    {
        var readOnly = McpTestData.Descriptor("list_groups");
        var reversible = McpTestData.Descriptor("add_client_note", SkillCategory.Crud);
        var irreversible = McpTestData.Descriptor("update_client", SkillCategory.Crud);
        var scenarioGated = McpTestData.Descriptor("start_autowizard", SkillCategory.Action);
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { readOnly, reversible, irreversible, scenarioGated });
        _riskClassifier.Classify(reversible).Returns(SkillRiskClass.Reversible);
        _riskClassifier.Classify(irreversible).Returns(SkillRiskClass.Irreversible);
        _riskClassifier.Classify(scenarioGated).Returns(SkillRiskClass.ScenarioGated);

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Read);

        Assert.That(tools.Select(tool => tool.Name), Is.EquivalentTo(new[] { "list_groups" }));
    }

    [Test]
    public void GetToolsForUser_ReadMode_HidesConfirmPendingActionAndCreatePlan()
    {
        var confirm = McpTestData.Descriptor(AutonomyDefaults.ConfirmPendingActionSkillName, SkillCategory.Action);
        var createPlan = McpTestData.Descriptor(PlanSkillDefaults.CreatePlanSkillName, SkillCategory.Action);
        var readOnly = McpTestData.Descriptor("list_groups");
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { confirm, createPlan, readOnly });

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Read);

        Assert.That(tools.Select(tool => tool.Name), Is.EquivalentTo(new[] { "list_groups" }));
    }

    [Test]
    public void GetToolsForUser_WriteMode_ListsWritingSkillsAsBefore()
    {
        var readOnly = McpTestData.Descriptor("list_groups");
        var irreversible = McpTestData.Descriptor("update_client", SkillCategory.Crud);
        var confirm = McpTestData.Descriptor(AutonomyDefaults.ConfirmPendingActionSkillName, SkillCategory.Action);
        _skillRegistry.GetSkillsForUser(Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<SkillDescriptor> { readOnly, irreversible, confirm });
        _riskClassifier.Classify(irreversible).Returns(SkillRiskClass.Irreversible);
        _riskClassifier.Classify(confirm).Returns(SkillRiskClass.Irreversible);

        var tools = _sut.GetToolsForUser(new List<string>(), PersonalAccessTokenAccessMode.Write);

        Assert.That(tools, Has.Count.EqualTo(3));
    }
}
