// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Presentation.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpPromptCatalogTests
{
    private IAgentRecipeRepository _recipeRepository = null!;
    private McpPromptCatalog _sut = null!;

    [SetUp]
    public void Setup()
    {
        _recipeRepository = Substitute.For<IAgentRecipeRepository>();
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([]);
        _sut = new McpPromptCatalog(_recipeRepository, Substitute.For<ILogger<McpPromptCatalog>>());
    }

    private static AgentRecipe OnboardEmployeeRecipe()
    {
        var steps = new object[]
        {
            new { kind = "ask", slot = "employeeName", prompt = "Ask the user for the employee's full name.", description = "the full name of the new employee" },
            new { kind = "mutate", skill = "create_employee", note = "Create the employee now using the known name." },
            new { kind = "ask", slot = "groupName", prompt = "Ask the user which group to add the employee to.", description = "the name of the group" },
            new { kind = "mutate", skill = "add_client_to_group_by_name", note = "Add the employee to the group now." }
        };

        return new AgentRecipe
        {
            Id = Guid.NewGuid(),
            Name = "onboard-employee",
            Goal = "Onboard a brand-new employee end to end.",
            IsEnabled = true,
            StepsJson = JsonSerializer.Serialize(steps)
        };
    }

    private static Dictionary<string, JsonElement> BuildArguments(Dictionary<string, string> values)
    {
        return values.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value));
    }

    private static string ExtractSingleUserMessageText(GetPromptResult result)
    {
        Assert.That(result.Messages, Has.Count.EqualTo(1));
        var message = result.Messages[0];
        Assert.That(message.Role, Is.EqualTo(Role.User));
        var content = message.Content as TextContentBlock;
        Assert.That(content, Is.Not.Null);

        return content!.Text;
    }

    [Test]
    public async Task ListPromptsAsync_WithNoRecipes_ReturnsOnlyStaticWorkflowPrompts()
    {
        var prompts = await _sut.ListPromptsAsync();

        Assert.That(prompts.Select(prompt => prompt.Name), Is.EquivalentTo(new[]
        {
            McpPromptCatalog.PlanScheduleWeekPromptName,
            McpPromptCatalog.CoverAbsenceGuidedPromptName
        }));
    }

    [Test]
    public async Task ListPromptsAsync_IncludesOneEntryPerEnabledRecipe()
    {
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([OnboardEmployeeRecipe()]);

        var prompts = await _sut.ListPromptsAsync();

        Assert.That(prompts.Select(prompt => prompt.Name), Does.Contain("onboard-employee"));
        var recipePrompt = prompts.Single(prompt => prompt.Name == "onboard-employee");
        Assert.That(recipePrompt.Description, Is.EqualTo("Onboard a brand-new employee end to end."));
        Assert.That(recipePrompt.Arguments!.Select(argument => argument.Name),
            Is.EquivalentTo(new[] { "employeeName", "groupName" }));
        Assert.That(recipePrompt.Arguments!.All(argument => argument.Required != true), Is.True);
    }

    [Test]
    public async Task ListPromptsAsync_RecipeWithoutSteps_IsExcluded()
    {
        var recipe = new AgentRecipe { Id = Guid.NewGuid(), Name = "broken-recipe", Goal = "broken", IsEnabled = true, StepsJson = "[]" };
        _recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([recipe]);

        var prompts = await _sut.ListPromptsAsync();

        Assert.That(prompts.Select(prompt => prompt.Name), Does.Not.Contain("broken-recipe"));
    }

    [Test]
    public async Task GetPromptAsync_Recipe_RendersStepsAndSubstitutesSuppliedSlots()
    {
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>())
            .Returns(OnboardEmployeeRecipe());

        var arguments = BuildArguments(new Dictionary<string, string> { ["employeeName"] = "Anna Muster" });

        var result = await _sut.GetPromptAsync("onboard-employee", arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Anna Muster"));
        Assert.That(text, Does.Contain("create_employee"));
        Assert.That(text, Does.Contain("add_client_to_group_by_name"));
        Assert.That(text, Does.Contain("Ask the user which group to add the employee to."));
        Assert.That(text, Does.Contain("confirm_pending_action"));
    }

    [Test]
    public async Task GetPromptAsync_Recipe_WithoutSuppliedSlots_UsesStepPromptVerbatim()
    {
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>())
            .Returns(OnboardEmployeeRecipe());

        var result = await _sut.GetPromptAsync("onboard-employee", arguments: null);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Ask the user for the employee's full name."));
    }

    [Test]
    public void GetPromptAsync_DisabledRecipe_ThrowsInvalidParams()
    {
        var recipe = OnboardEmployeeRecipe();
        recipe.IsEnabled = false;
        _recipeRepository.GetByNameAsync("onboard-employee", Arg.Any<CancellationToken>()).Returns(recipe);

        var exception = Assert.ThrowsAsync<McpProtocolException>(() =>
            _sut.GetPromptAsync("onboard-employee", arguments: null));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
    }

    [Test]
    public async Task GetPromptAsync_PlanScheduleWeek_InterpolatesArgumentsIntoUserMessage()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.GroupNameArgument] = "Night Watch",
            [McpPromptCatalog.WeekStartArgument] = "2026-06-15"
        });

        var result = await _sut.GetPromptAsync(McpPromptCatalog.PlanScheduleWeekPromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Night Watch"));
        Assert.That(text, Does.Contain("2026-06-15"));
        Assert.That(text, Does.Contain("read_schedule_state"));
        Assert.That(text, Does.Contain("detect_conflicts"));
        Assert.That(text, Does.Contain("place_work"));
    }

    [Test]
    public async Task GetPromptAsync_CoverAbsenceGuided_InterpolatesArgumentsIntoUserMessage()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.EmployeeNameArgument] = "Yasmine Keller",
            [McpPromptCatalog.FromDateArgument] = "2026-06-20",
            [McpPromptCatalog.UntilDateArgument] = "2026-06-22"
        });

        var result = await _sut.GetPromptAsync(McpPromptCatalog.CoverAbsenceGuidedPromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Yasmine Keller"));
        Assert.That(text, Does.Contain("2026-06-20"));
        Assert.That(text, Does.Contain("2026-06-22"));
        Assert.That(text, Does.Contain("search_employees"));
        Assert.That(text, Does.Contain("cover_absence"));
        Assert.That(text, Does.Contain("find_replacement"));
    }

    [Test]
    public void GetPromptAsync_UnknownName_ThrowsInvalidParams()
    {
        _recipeRepository.GetByNameAsync("does_not_exist", Arg.Any<CancellationToken>())
            .Returns((AgentRecipe?)null);

        var exception = Assert.ThrowsAsync<McpProtocolException>(() =>
            _sut.GetPromptAsync("does_not_exist", arguments: null));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
    }

    [Test]
    public void GetPromptAsync_MissingRequiredStaticArgument_ThrowsInvalidParams()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.GroupNameArgument] = "Night Watch"
        });

        var exception = Assert.ThrowsAsync<McpProtocolException>(() =>
            _sut.GetPromptAsync(McpPromptCatalog.PlanScheduleWeekPromptName, arguments));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
        Assert.That(exception.Message, Does.Contain(McpPromptCatalog.WeekStartArgument));
    }

    [Test]
    public void GetPromptAsync_NullArgumentsForStaticPromptWithRequiredArguments_ThrowsInvalidParams()
    {
        var exception = Assert.ThrowsAsync<McpProtocolException>(() =>
            _sut.GetPromptAsync(McpPromptCatalog.PlanScheduleWeekPromptName, arguments: null));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
    }
}
