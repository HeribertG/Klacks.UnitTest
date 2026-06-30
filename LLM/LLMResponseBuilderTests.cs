using NUnit.Framework;
using Shouldly;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class LLMResponseBuilderTests
{
    private LLMResponseBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = new LLMResponseBuilder();
    }

    private static LLMProviderResponse CreateProviderResponse() => new()
    {
        Content = string.Empty,
        Success = true,
        FunctionCalls = new List<LLMFunctionCall>(),
        Usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage { InputTokens = 10, OutputTokens = 20, Cost = 0.001m }
    };

    [Test]
    public void BuildSuccessResponse_WithSuggestionsBlock_ExtractsSuggestions()
    {
        var content = "Here is your answer.\n[SUGGESTIONS: \"Show all clients\" | \"Create new client\" | \"Go to dashboard\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Suggestions!.Count().ShouldBe(3);
        response.Suggestions![0].ShouldBe("Show all clients");
        response.Suggestions![1].ShouldBe("Create new client");
        response.Suggestions![2].ShouldBe("Go to dashboard");
    }

    [Test]
    public void BuildSuccessResponse_WithSuggestionsBlock_RemovesBlockFromMessage()
    {
        var content = "Here is your answer.\n[SUGGESTIONS: \"Show all clients\" | \"Create new client\" | \"Go to dashboard\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Message.ShouldBe("Here is your answer.");
        response.Message.ShouldNotContain("[SUGGESTIONS:");
    }

    [Test]
    public void BuildSuccessResponse_WithoutSuggestionsBlock_ReturnEmptySuggestions()
    {
        var content = "Here is your answer without any suggestions.";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Suggestions.ShouldBeEmpty();
        response.Message.ShouldBe(content);
    }

    [Test]
    public void BuildSuccessResponse_WithEmptyContent_ReturnEmptySuggestions()
    {
        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", string.Empty);

        response.Suggestions.ShouldBeEmpty();
        response.Message.ShouldBeEmpty();
    }

    [Test]
    public void BuildSuccessResponse_WithMoreThanMaxSuggestions_CapsAtMax()
    {
        var content = "Answer.\n[SUGGESTIONS: \"One\" | \"Two\" | \"Three\" | \"Four\" | \"Five\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Suggestions!.Count().ShouldBe(4);
    }

    [Test]
    public void BuildSuccessResponse_WithMalformedSuggestionsBlock_ReturnEmptySuggestions()
    {
        var content = "Answer.\n[SUGGESTIONS: no quotes here | also no quotes]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Suggestions.ShouldBeEmpty();
    }

    [Test]
    public void BuildSuccessResponse_SetsConversationId()
    {
        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-42", "Hello");

        response.ConversationId.ShouldBe("conv-42");
    }

    [Test]
    public void BuildSuccessResponse_SetsUsage()
    {
        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", "Hello");

        response.Usage.ShouldNotBeNull();
        response.Usage!.InputTokens.ShouldBe(10);
        response.Usage!.OutputTokens.ShouldBe(20);
    }

    [Test]
    public void BuildErrorResponse_ReturnsEmptysuggestions()
    {
        var response = _builder.BuildErrorResponse("Something went wrong.");

        response.Suggestions.ShouldBeEmpty();
        response.Message.ShouldContain("Something went wrong.");
    }

    [Test]
    public void BuildSuccessResponse_SuggestionsBlockOnSameLine_IsStillParsed()
    {
        var content = "Your data is ready. [SUGGESTIONS: \"View details\" | \"Export data\" | \"Back to list\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Suggestions!.Count().ShouldBe(3);
        response.Message.ShouldBe("Your data is ready.");
    }

    [Test]
    public void BuildSuccessResponse_WithDateRepliesBlock_ReturnsDateModeAndLabel()
    {
        var content = "Please enter the birthdate. [REPLIES:date \"Date of birth\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.SuggestedReplies.ShouldNotBeNull();
        response.SuggestedReplies!.SelectionMode.ShouldBe("date");
        response.SuggestedReplies.Prompt.ShouldBe("Date of birth");
        response.SuggestedReplies.Options.ShouldBeEmpty();
    }

    [Test]
    public void BuildSuccessResponse_WithDateRepliesBlock_RemovesBlockFromMessage()
    {
        var content = "Please enter the birthdate. [REPLIES:date \"Date of birth\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.Message.ShouldBe("Please enter the birthdate.");
        response.Message.ShouldNotContain("[REPLIES:");
    }

    [Test]
    public void BuildSuccessResponse_WithSingleRepliesBlock_StillWorksAfterDateSupport()
    {
        var content = "Choose a gender. [REPLIES:single \"Male\" | \"Female\" | \"Other\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.SuggestedReplies.ShouldNotBeNull();
        response.SuggestedReplies!.SelectionMode.ShouldBe("single");
        response.SuggestedReplies.Options.Count.ShouldBe(3);
        response.Message.ShouldNotContain("[REPLIES:");
    }

    [Test]
    public void BuildSuccessResponse_WithMultiRepliesBlock_StillWorksAfterDateSupport()
    {
        var content = "Select items. [REPLIES:multi:Pick one \"Alpha=a\" | \"Beta=b\"]";

        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", content);

        response.SuggestedReplies.ShouldNotBeNull();
        response.SuggestedReplies!.SelectionMode.ShouldBe("multi");
        response.SuggestedReplies.Options.Count.ShouldBe(2);
        response.SuggestedReplies.Options[0].Label.ShouldBe("Alpha");
        response.SuggestedReplies.Options[0].Value.ShouldBe("a");
    }

    [Test]
    public void BuildSuccessResponse_WithNavigationTarget_SetsNavigateToTarget()
    {
        var response = _builder.BuildSuccessResponse(
            CreateProviderResponse(), "conv-1", "Navigating...",
            navigationRoute: "/workplace/settings",
            navigationTarget: "macros");

        response.NavigateTo.ShouldBe("/workplace/settings");
        response.NavigateToTarget.ShouldBe("macros");
    }

    [Test]
    public void BuildSuccessResponse_WithNullNavigationTarget_NavigateToTargetIsNull()
    {
        var response = _builder.BuildSuccessResponse(
            CreateProviderResponse(), "conv-1", "Navigating...",
            navigationRoute: "/workplace/settings",
            navigationTarget: null);

        response.NavigateTo.ShouldBe("/workplace/settings");
        response.NavigateToTarget.ShouldBeNull();
    }

    [Test]
    public void BuildSuccessResponse_WithNoNavigation_BothNavigateFieldsAreNull()
    {
        var response = _builder.BuildSuccessResponse(CreateProviderResponse(), "conv-1", "Hello");

        response.NavigateTo.ShouldBeNull();
        response.NavigateToTarget.ShouldBeNull();
    }
}
