// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the read-only SearchEmployeesSkill — happy path with matches, empty result,
/// entity-type mapping and the truncation hint when more rows exist than the limit. Recipes capture the lone row of
/// Results, so only a person the term really names may be in Results: a row found inside a word or only by the fuzzy
/// search is listed under OtherMatches without an id, the message is the name ask-back, and a recipe capturing
/// "Results[].Id" finds no row and rewinds to the question that asked for the name.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SearchEmployeesSkillTests
{
    private const string SkillName = "search_employees";
    private const string ClientSlot = "clientName";

    private IClientSearchRepository _searchRepository = null!;
    private SearchEmployeesSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _skill = new SearchEmployeesSkill(_searchRepository, TestAddressLocationResolver.SwissAndGerman());
        WireFuzzySearch();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private void WireSearch(int totalCount, params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = totalCount });
    }

    private void WireJudgedSearch(int totalCount, params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), 100, Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = totalCount });
    }

    private void WireFuzzySearch(params ClientSearchItem[] items)
    {
        _searchRepository.SearchByFuzzyNameAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private static ClientSearchItem Item(string firstName, string lastName, int idNumber = 1) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        LastName = lastName,
        IdNumber = idNumber
    };

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    [Test]
    public async Task ReturnsMatches_WithCountInMessage()
    {
        WireSearch(2, Item("Anna", "Müller"), Item("Max", "Müller"));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Müller" });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Found 2"));
        Assert.That(result.Message, Does.Contain("'Müller'"));
        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task ReturnsEmptyResultMessage_WhenNothingMatches()
    {
        WireSearch(0);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Nobody" });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No employees found"));
    }

    [Test]
    public async Task PassesCantonAndEntityTypeToRepository()
    {
        WireSearch(1, Item("Anna", "Müller"));
        var parameters = new Dictionary<string, object>
        {
            ["searchTerm"] = "Anna",
            ["canton"] = "BE",
            ["entityType"] = "Customer"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _searchRepository.Received(1).SearchAsync(
            "Anna", "BE", EntityTypeEnum.Customer, null, 10, Arg.Any<CancellationToken>());
    }

    [TestCase("Bern")]
    [TestCase("Berne")]
    [TestCase("be")]
    public async Task CantonByNameInAnyLanguage_IsResolvedToTheStoredCode(string canton)
    {
        WireSearch(1, Item("Anna", "Müller"));

        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Anna", ["canton"] = canton });

        await _searchRepository.Received(1).SearchAsync(
            "Anna", "BE", Arg.Any<EntityTypeEnum?>(), null, 10, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CantonOutsideTheOldSwissList_IsAccepted()
    {
        WireSearch(1, Item("Anna", "Müller"));

        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Anna", ["canton"] = "Tessin" });

        await _searchRepository.Received(1).SearchAsync(
            "Anna", "TI", Arg.Any<EntityTypeEnum?>(), null, 10, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IgnoresUnknownEntityType()
    {
        WireSearch(1, Item("Anna", "Müller"));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["entityType"] = "Alien" });

        Assert.That(result.Success, Is.True);
        await _searchRepository.Received(1).SearchAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), null, null, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsTruncation_WhenMoreResultsThanLimit()
    {
        WireSearch(12, Item("Anna", "Müller"), Item("Max", "Müller"));
        WireJudgedSearch(12, Enumerable.Range(1, 12).Select(i => Item("Person" + i, "Müller", i)).ToArray());

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Müller", ["limit"] = 2 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Found 12"));
        Assert.That(result.Message, Does.Contain("Showing first 2"));
        Assert.That(Data(result).GetProperty("HasMore").GetBoolean(), Is.True);
    }

    [Test]
    public async Task LoneNamedRowOnAnIncompletePage_IsNotCapturable_WhenTheFullResultNamesMore()
    {
        var onPage = Item("Hans", "Meier", 60);
        var page = new[] { onPage }
            .Concat(Enumerable.Range(1, 9).Select(i => Item("Peter", "Meierhans" + i, 60 + i)))
            .ToArray();
        WireSearch(11, page);
        WireJudgedSearch(11, page.Append(Item("Hans", "Meier", 99)).ToArray());
        var plan = PlanAtSearchStep("Hans Meier");

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Hans Meier" });
        plan.Observe([ExecutedCall(result)]);

        Assert.Multiple(() =>
        {
            Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(2));
            Assert.That(Data(result).GetProperty("TotalCount").GetInt32(), Is.EqualTo(2));
            Assert.That(plan.Slots.ContainsKey("clientId"), Is.False);
            Assert.That(plan.CurrentIsAsk, Is.True);
        });
    }

    [Test]
    public async Task ResultStillIncompleteAfterTheJudgedSearch_PicksNobody_AndAsksBack()
    {
        var rows = Enumerable.Range(1, 10).Select(i => Item("Hans", "Meier", i)).ToArray();
        WireSearch(500, rows);
        WireJudgedSearch(500, rows);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Hans Meier" });

        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(0));
        Assert.That(result.Message, Does.Contain("incomplete"));
        Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.True);
    }

    [Test]
    public async Task SeveralMatches_AreNeverCutDownToASingleCapturableRow()
    {
        WireSearch(3, Item("Anna", "Müller", 1), Item("Max", "Müller", 2), Item("Eva", "Müller", 3));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Müller", ["limit"] = 1 });

        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(2));
        Assert.That(Data(result).GetProperty("HasMore").GetBoolean(), Is.True);
    }

    [Test]
    public async Task WithoutSearchTerm_ListsEveryRowAndNeverRunsTheFuzzySearch()
    {
        WireSearch(2, Item("Anna", "Müller"), Item("Max", "Meier"));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["canton"] = "BE" });

        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(2));
        await _searchRepository.DidNotReceiveWithAnyArgs().SearchByFuzzyNameAsync(default!);
    }

    [Test]
    public async Task FuzzyOnlyHit_IsAnIdLessOtherMatch_WithTheAskBack()
    {
        WireSearch(0);
        WireFuzzySearch(Item("Hans", "Mayer", 12));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Hans Meier" });

        var data = Data(result);
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(data.GetProperty("Results").GetArrayLength(), Is.EqualTo(0));
            Assert.That(data.GetProperty("OtherMatches").GetArrayLength(), Is.EqualTo(1));
            Assert.That(data.GetProperty("OtherMatches")[0].TryGetProperty("Id", out _), Is.False);
            Assert.That(result.Message, Does.Contain("Did the user mean 'Hans Mayer (#12)'?"));
            Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.True);
        });
    }

    [Test]
    public async Task TextHitInsideAWord_IsAnOtherMatch_WithTheAskBack()
    {
        WireSearch(1, Item("Peter", "Meierhans", 21));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Meier" });

        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(0));
        Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.True);
    }

    [Test]
    public async Task NamedPersonStaysInResults_WhileALooserRowMovesToOtherMatches()
    {
        var named = Item("Hans", "Meier", 30);
        WireSearch(2, named, Item("Peter", "Meierhans", 31));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Meier Hans" });

        var data = Data(result);
        Assert.That(data.GetProperty("Results").GetArrayLength(), Is.EqualTo(1));
        Assert.That(data.GetProperty("Results")[0].GetProperty("Id").GetGuid(), Is.EqualTo(named.Id));
        Assert.That(data.GetProperty("OtherMatches").GetArrayLength(), Is.EqualTo(1));
        Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.False);
    }

    [Test]
    public async Task CustomerNamedByItsCompany_StaysInResults()
    {
        var customer = new ClientSearchItem { Id = Guid.NewGuid(), Company = "Migros Aare", IdNumber = 50 };
        WireSearch(1, customer);

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Migros", ["entityType"] = "Customer" });

        Assert.That(Data(result).GetProperty("Results").GetArrayLength(), Is.EqualTo(1));
        Assert.That(NameResolutionAskBack.IsAskBack(result.Message), Is.False);
    }

    [Test]
    public async Task RecipeCapture_TakesTheNamedPerson()
    {
        var named = Item("Hans", "Meier", 40);
        WireSearch(1, named);
        var plan = PlanAtSearchStep("Hans Meier");

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Hans Meier" });
        plan.Observe([ExecutedCall(result)]);

        Assert.That(plan.Slots["clientId"], Is.EqualTo(named.Id.ToString()));
    }

    [Test]
    public async Task RecipeCapture_OfAFuzzyOnlyHit_FailsAndRewindsToTheNameQuestion()
    {
        WireSearch(0);
        WireFuzzySearch(Item("Hans", "Mayer", 12));
        var plan = PlanAtSearchStep("Hans Meier");

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["searchTerm"] = "Hans Meier" });
        plan.Observe([ExecutedCall(result)]);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Slots.ContainsKey("clientId"), Is.False);
            Assert.That(plan.Slots.ContainsKey(ClientSlot), Is.False);
            Assert.That(plan.IsActive, Is.True);
            Assert.That(plan.CurrentIsAsk, Is.True);
            Assert.That(plan.CurrentStep!.Slot, Is.EqualTo(ClientSlot));
        });
    }

    private static RecipeExecutionPlan PlanAtSearchStep(string clientName)
    {
        var plan = new RecipeExecutionPlan("r",
        [
            new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = ClientSlot, Prompt = "Which employee?" },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Search, Skill = SkillName,
                Inject = new Dictionary<string, string> { ["searchTerm"] = "$" + ClientSlot },
                Capture = "Results[].Id as clientId"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate, Skill = "add_client_to_group",
                Inject = new Dictionary<string, string> { ["clientId"] = "$clientId" }
            }
        ]);
        plan.PrefillSlots(new Dictionary<string, string> { [ClientSlot] = clientName });
        plan.AdvanceOverSatisfied();
        return plan;
    }

    private static LLMFunctionCall ExecutedCall(SkillResult result) => new()
    {
        FunctionName = SkillName,
        Success = result.Success,
        Result = $"{result.Message}\nData: {JsonSerializer.Serialize(result.Data)}"
    };
}
