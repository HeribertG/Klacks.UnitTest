// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Presentation.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpPromptCatalogTests
{
    private McpPromptCatalog _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new McpPromptCatalog();
    }

    [Test]
    public void ListPrompts_ReturnsThreeGuidedWorkflowPrompts()
    {
        var prompts = _sut.ListPrompts();

        Assert.That(prompts, Has.Count.EqualTo(3));
        Assert.That(prompts.Select(prompt => prompt.Name), Is.EquivalentTo(new[]
        {
            McpPromptCatalog.OnboardEmployeePromptName,
            McpPromptCatalog.PlanScheduleWeekPromptName,
            McpPromptCatalog.CoverAbsenceGuidedPromptName
        }));
    }

    [Test]
    public void ListPrompts_EveryPromptHasDescriptionAndArguments()
    {
        var prompts = _sut.ListPrompts();

        foreach (var prompt in prompts)
        {
            Assert.That(prompt.Description, Is.Not.Null.And.Not.Empty);
            Assert.That(prompt.Arguments, Is.Not.Null.And.Not.Empty);
            Assert.That(prompt.Arguments!.Any(argument => argument.Required == true), Is.True);
        }
    }

    [Test]
    public void GetPrompt_OnboardEmployee_InterpolatesArgumentsIntoUserMessage()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.FirstNameArgument] = "Anna",
            [McpPromptCatalog.LastNameArgument] = "Muster",
            [McpPromptCatalog.EmailArgument] = "anna.muster@example.com"
        });

        var result = _sut.GetPrompt(McpPromptCatalog.OnboardEmployeePromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Anna"));
        Assert.That(text, Does.Contain("Muster"));
        Assert.That(text, Does.Contain("anna.muster@example.com"));
        Assert.That(text, Does.Contain("create_employee"));
        Assert.That(text, Does.Contain("add_client_email"));
        Assert.That(text, Does.Contain("assign_contract_by_name"));
        Assert.That(text, Does.Contain("confirm_pending_action"));
    }

    [Test]
    public void GetPrompt_OnboardEmployee_WithoutOptionalEmail_StillRenders()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.FirstNameArgument] = "Anna",
            [McpPromptCatalog.LastNameArgument] = "Muster"
        });

        var result = _sut.GetPrompt(McpPromptCatalog.OnboardEmployeePromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Anna"));
        Assert.That(text, Does.Contain("add_client_email"));
    }

    [Test]
    public void GetPrompt_PlanScheduleWeek_InterpolatesArgumentsIntoUserMessage()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.GroupNameArgument] = "Night Watch",
            [McpPromptCatalog.WeekStartArgument] = "2026-06-15"
        });

        var result = _sut.GetPrompt(McpPromptCatalog.PlanScheduleWeekPromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Night Watch"));
        Assert.That(text, Does.Contain("2026-06-15"));
        Assert.That(text, Does.Contain("read_schedule_state"));
        Assert.That(text, Does.Contain("detect_conflicts"));
        Assert.That(text, Does.Contain("place_work"));
    }

    [Test]
    public void GetPrompt_CoverAbsenceGuided_InterpolatesArgumentsIntoUserMessage()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.EmployeeNameArgument] = "Yasmine Keller",
            [McpPromptCatalog.FromDateArgument] = "2026-06-20",
            [McpPromptCatalog.UntilDateArgument] = "2026-06-22"
        });

        var result = _sut.GetPrompt(McpPromptCatalog.CoverAbsenceGuidedPromptName, arguments);

        var text = ExtractSingleUserMessageText(result);
        Assert.That(text, Does.Contain("Yasmine Keller"));
        Assert.That(text, Does.Contain("2026-06-20"));
        Assert.That(text, Does.Contain("2026-06-22"));
        Assert.That(text, Does.Contain("search_employees"));
        Assert.That(text, Does.Contain("cover_absence"));
        Assert.That(text, Does.Contain("find_replacement"));
    }

    [Test]
    public void GetPrompt_UnknownName_ThrowsInvalidParams()
    {
        var exception = Assert.Throws<McpProtocolException>(() =>
            _sut.GetPrompt("does_not_exist", arguments: null));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
    }

    [Test]
    public void GetPrompt_MissingRequiredArgument_ThrowsInvalidParams()
    {
        var arguments = BuildArguments(new Dictionary<string, string>
        {
            [McpPromptCatalog.FirstNameArgument] = "Anna"
        });

        var exception = Assert.Throws<McpProtocolException>(() =>
            _sut.GetPrompt(McpPromptCatalog.OnboardEmployeePromptName, arguments));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
        Assert.That(exception.Message, Does.Contain(McpPromptCatalog.LastNameArgument));
    }

    [Test]
    public void GetPrompt_NullArgumentsForPromptWithRequiredArguments_ThrowsInvalidParams()
    {
        var exception = Assert.Throws<McpProtocolException>(() =>
            _sut.GetPrompt(McpPromptCatalog.PlanScheduleWeekPromptName, arguments: null));

        Assert.That(exception!.ErrorCode, Is.EqualTo(McpErrorCode.InvalidParams));
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
}
