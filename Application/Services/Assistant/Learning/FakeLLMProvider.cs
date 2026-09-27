// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

/// <summary>
/// A language model that answers from a queue instead of from a network. The learning tests need the
/// answer to be a fact of the test, not of whichever provider an installation happens to have configured -
/// that is the whole point of the loop being provider-agnostic, and a substitute that returns canned text
/// is the only way to assert on prompt content and on what the loop does with a bad answer.
/// </summary>
internal sealed class FakeLLMProvider : ILLMProvider
{
    private readonly Queue<LLMProviderResponse> _answers = new();

    public string ProviderId => "fake";

    public string ProviderName => "Fake";

    public bool IsEnabled => true;

    public List<LLMProviderRequest> Requests { get; } = [];

    public bool? LastCancellationTokenWasCancellable { get; private set; }

    public FakeLLMProvider Answering(params string[] answers)
    {
        foreach (var answer in answers)
        {
            _answers.Enqueue(new LLMProviderResponse { Success = true, Content = answer });
        }

        return this;
    }

    /// <summary>
    /// Enqueues a fully controlled response, for cases plain content strings cannot express - for example a
    /// reasoning model that spent its token budget on reasoning and answered with empty content.
    /// </summary>
    public FakeLLMProvider AnsweringWith(LLMProviderResponse response)
    {
        _answers.Enqueue(response);
        return this;
    }

    /// <summary>
    /// Discards every answer queued so far, so a test that set up its scenario through a helper which already
    /// queued a default answer can replace it with a fully controlled one instead of appending behind it.
    /// </summary>
    public FakeLLMProvider ClearAnswers()
    {
        _answers.Clear();
        return this;
    }

    public void Configure(LLMProvider providerConfig)
    {
    }

    public Task<LLMProviderResponse> ProcessAsync(
        LLMProviderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        LastCancellationTokenWasCancellable = cancellationToken.CanBeCanceled;

        return Task.FromResult(_answers.Count == 0
            ? new LLMProviderResponse { Success = false, Content = string.Empty }
            : _answers.Dequeue());
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(true);
}
