// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NavigateToSkill — verifies that navigation to client editor pages
/// is refused without an entityId, permitted with one, and unrestricted for other pages.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NavigateToSkillTests
{
    private IKlacksyPageKeyCatalog _catalog = null!;
    private NavigateToSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _catalog = Substitute.For<IKlacksyPageKeyCatalog>();
        _skill = new NavigateToSkill(_catalog);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static KlacksyPageKeyEntry MakeEntry(string pageKey, string route = "/workplace/test", bool hasEntityParam = true) =>
        new(pageKey, route, null, hasEntityParam);

    [Test]
    public async Task ReturnsError_WhenEditEmployee_WithoutEntityId()
    {
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.EditEmployee };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
        Assert.That(result.Message, Does.Contain("entityId"));
    }

    [Test]
    public async Task ReturnsError_WhenEditAddress_WithoutEntityId()
    {
        var parameters = new Dictionary<string, object> { ["page"] = UiPageKeys.EditAddress };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
        Assert.That(result.Message, Does.Contain("entityId"));
    }

    [Test]
    public async Task Navigates_WhenEditEmployee_WithEntityId()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditEmployee)
            .Returns(MakeEntry(UiPageKeys.EditEmployee, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditEmployee,
            ["entityId"] = entityId
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task Navigates_WhenEditAddress_WithEntityId()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditAddress)
            .Returns(MakeEntry(UiPageKeys.EditAddress, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditAddress,
            ["entityId"] = entityId
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task Navigates_WhenOtherPage_WithoutEntityId()
    {
        _catalog.GetByPageKey("client")
            .Returns(MakeEntry("client", "/workplace/client", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "client" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task ReturnsError_WhenEditEmployee_PageKeyIsCaseInsensitive()
    {
        var parameters = new Dictionary<string, object> { ["page"] = "Edit-Employee" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("create_employee"));
    }

    [Test]
    public async Task NavigationToExplainablePage_KeepsPlainMessage_KnowledgeInjectionLivesInExecutor()
    {
        _catalog.GetByPageKey("new-shift").Returns(MakeEntry("new-shift", "/workplace/new-shift", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "new-shift" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Not.Contain("explain_page"));
    }

    [Test]
    public async Task WithTargetParam_IncludesTargetInNavigationData()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = "settings",
            ["target"] = "macros"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var data = result.Data as dynamic;
        Assert.That(data, Is.Not.Null);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"macros\""));
    }

    [Test]
    public async Task WithoutTargetParam_DataHasNullTarget()
    {
        _catalog.GetByPageKey("settings").Returns(MakeEntry("settings", "/workplace/settings", hasEntityParam: false));
        var parameters = new Dictionary<string, object> { ["page"] = "settings" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":null"));
    }

    [Test]
    public async Task WithTargetAndEntityId_BothIncludedInNavigationData()
    {
        var entityId = Guid.NewGuid().ToString();
        _catalog.GetByPageKey(UiPageKeys.EditEmployee)
            .Returns(MakeEntry(UiPageKeys.EditEmployee, "/workplace/edit-address"));
        var parameters = new Dictionary<string, object>
        {
            ["page"] = UiPageKeys.EditEmployee,
            ["entityId"] = entityId,
            ["target"] = "address-contracts"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.That(dataJson, Does.Contain("\"Target\":\"address-contracts\""));
        Assert.That(dataJson, Does.Contain(entityId));
    }
}
