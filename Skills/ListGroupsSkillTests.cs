// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the search term of ListGroupsSkill. Recipes (add-employee-to-group, add-shift-to-group,
/// create-shift-order) capture the lone row of Groups and write to it, so a term selects groups with the Strict
/// name policy: "Nord" never captures the only "Nordwest" (that group is listed by name under OtherMatches and the
/// message is the name ask-back), an exact name wins over groups that contain it as a whole word, a whole-word
/// forward match and a label-prefixed name still select, and without a term every group is listed.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListGroupsSkillTests
{
    private const string SkillName = "list_groups";
    private const string GroupSlot = "groupName";

    private static readonly DateTimeOffset Today = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private IGroupRepository _groupRepository = null!;
    private ListGroupsSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _skill = new ListGroupsSkill(
            _groupRepository,
            Substitute.For<ICalendarSelectionRepository>(),
            Substitute.For<ICountryResolver>(),
            new FixedCompanyClock(Today));
    }

    private void GroupsExist(params string[] names)
    {
        _groupRepository.List().Returns(names
            .Select(name => new Group { Id = Guid.NewGuid(), Name = name, ValidFrom = Today.UtcDateTime.AddYears(-1) })
            .ToList());
    }

    private async Task<SkillResult> ListAsync(string? searchTerm)
    {
        var parameters = new Dictionary<string, object>();
        if (searchTerm != null)
        {
            parameters["searchTerm"] = searchTerm;
        }

        return await _skill.ExecuteAsync(Ctx(), parameters);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    private static List<string> GroupNames(SkillResult result) =>
        Data(result).GetProperty("Groups").EnumerateArray()
            .Select(group => group.GetProperty("Name").GetString()!)
            .ToList();

    [Test]
    public async Task PartialWord_DoesNotSelectTheOnlyGroupContainingIt_AndAsksBack()
    {
        GroupsExist("Nordwest", "Süd");

        var result = await ListAsync("Nord");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(GroupNames(result), Is.Empty);
            Assert.That(Data(result).GetProperty("OtherMatches")[0].GetString(), Is.EqualTo("Nordwest"));
            Assert.That(result.Message, Does.Contain("Did the user mean 'Nordwest'?"));
            Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.True);
        });
    }

    [Test]
    public async Task WholeWord_SelectsTheGroup()
    {
        GroupsExist("Region Nord", "Nordwest");

        var result = await ListAsync("Nord");

        Assert.That(GroupNames(result), Is.EqualTo(new[] { "Region Nord" }));
        Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.False);
    }

    [Test]
    public async Task ExactName_WinsOverGroupsContainingItAsAWord()
    {
        GroupsExist("Nord", "Region Nord");

        var result = await ListAsync("Nord");

        Assert.That(GroupNames(result), Is.EqualTo(new[] { "Nord" }));
        Assert.That(Data(result).GetProperty("OtherMatches")[0].GetString(), Is.EqualTo("Region Nord"));
    }

    [Test]
    public async Task LabelPrefixedName_SelectsTheGroup()
    {
        GroupsExist("Leitung", "Verkauf");

        var result = await ListAsync("Gruppe Leitung");

        Assert.That(GroupNames(result), Is.EqualTo(new[] { "Leitung" }));
    }

    [Test]
    public async Task WithoutSearchTerm_ListsEveryGroup()
    {
        GroupsExist("Nordwest", "Süd");

        var result = await ListAsync(null);

        Assert.That(GroupNames(result), Is.EquivalentTo(new[] { "Nordwest", "Süd" }));
    }

    [Test]
    public async Task RecipeCapture_OfAPartialWord_FailsAndRewindsToTheGroupQuestion()
    {
        GroupsExist("Nordwest");
        var plan = new RecipeExecutionPlan("r",
        [
            new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = GroupSlot, Prompt = "Which group?" },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Search, Skill = SkillName,
                Inject = new Dictionary<string, string> { ["searchTerm"] = "$" + GroupSlot },
                Capture = "Groups[].Id as groupId"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate, Skill = "add_client_to_group",
                Inject = new Dictionary<string, string> { ["groupId"] = "$groupId" }
            }
        ]);
        plan.PrefillSlots(new Dictionary<string, string> { [GroupSlot] = "Nord" });
        plan.AdvanceOverSatisfied();

        var result = await ListAsync("Nord");
        plan.Observe([new LLMFunctionCall
        {
            FunctionName = SkillName,
            Success = result.Success,
            Result = $"{result.Message}\nData: {JsonSerializer.Serialize(result.Data)}"
        }]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Slots.ContainsKey("groupId"), Is.False);
            Assert.That(plan.IsActive, Is.True);
            Assert.That(plan.CurrentIsAsk, Is.True);
            Assert.That(plan.CurrentStep!.Slot, Is.EqualTo(GroupSlot));
        });
    }
}
