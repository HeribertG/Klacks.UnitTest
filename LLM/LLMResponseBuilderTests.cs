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
}
