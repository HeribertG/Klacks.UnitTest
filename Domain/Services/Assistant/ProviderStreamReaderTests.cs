// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// First direct coverage of the streaming read loop, which lived inside LLMService.ProcessStreamAsync and
/// was only ever exercised against a live provider. Three rules matter and none of them was pinned:
/// tool-call deltas and the end marker are accumulated but never yielded (yielding them would put NUL
/// sentinels on the user's screen); a transient failure before the first content token is retried; and a
/// transient failure AFTER content already reached the caller is NOT retried, because a retry would
/// duplicate the visible answer. Content that is nothing but an echo of the loop's tool-call stand-in text
/// is held back and dropped, because the chat client keeps whatever was streamed as the final message.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ProviderStreamReaderTests
{
    private const string TransientError = "429 rate limit reached";
    private const string PermanentError = "invalid api key";
    private const string ModelId = "test-model";

    private sealed class ScriptedProvider : ILLMProvider
    {
        private readonly Queue<Func<CancellationToken, IAsyncEnumerable<string>>> _attempts;

        internal ScriptedProvider(params Func<IAsyncEnumerable<string>>[] attempts)
        {
            _attempts = new Queue<Func<CancellationToken, IAsyncEnumerable<string>>>(
                attempts.Select(attempt => new Func<CancellationToken, IAsyncEnumerable<string>>(_ => attempt())));
        }

        internal ScriptedProvider(Func<CancellationToken, IAsyncEnumerable<string>> attempt)
        {
            _attempts = new Queue<Func<CancellationToken, IAsyncEnumerable<string>>>(new[] { attempt });
        }

        internal int Attempts { get; private set; }

        public string ProviderId => "scripted";

        public string ProviderName => "Scripted";

        public bool IsEnabled => true;

        public bool SupportsStreaming => true;

        public void Configure(LLMProvider providerConfig)
        {
        }

        public Task<LLMProviderResponse> ProcessAsync(
            LLMProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LLMProviderResponse());

        public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(true);

        public IAsyncEnumerable<string> ProcessStreamAsync(
            LLMProviderRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return _attempts.Dequeue()(cancellationToken);
        }
    }

    private static async IAsyncEnumerable<string> Tokens(
        IEnumerable<string> tokens,
        string? throwAfterwards = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var token in tokens)
        {
            await Task.Yield();
            yield return token;
        }

        if (throwAfterwards != null)
        {
            throw new InvalidOperationException(throwAfterwards);
        }
    }

    private static async Task<List<string>> Read(ProviderStreamReader reader, ILLMProvider provider)
    {
        var yielded = new List<string>();
        await foreach (var token in reader.ReadAsync(
                           provider, new LLMProviderRequest(), ModelId, CancellationToken.None))
        {
            yielded.Add(token);
        }

        return yielded;
    }

    private static ProviderStreamReader NewReader() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static async IAsyncEnumerable<string> HonouringTheToken(
        IEnumerable<string> tokens,
        Action onDisposed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            onDisposed();
        }
    }

    private static async IAsyncEnumerable<string> IgnoringTheToken(
        IEnumerable<string> tokens,
        Action onDisposed)
    {
        try
        {
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }
        }
        finally
        {
            onDisposed();
        }
    }

    private static async Task<List<string>> ReadUntilCancelled(
        ProviderStreamReader reader, ILLMProvider provider, CancellationTokenSource source)
    {
        var yielded = new List<string>();
        await foreach (var token in reader.ReadAsync(provider, new LLMProviderRequest(), ModelId, source.Token))
        {
            yielded.Add(token);
            source.Cancel();
        }

        return yielded;
    }

    [Test]
    public async Task ToolCallDeltasAndEndMarker_AreAccumulatedButNeverYielded()
    {
        var provider = new ScriptedProvider(() => Tokens(new[]
        {
            "Hello ",
            LLMStreamingTokens.ToolCallPrefix + """{"index":0,"name":"get_shifts","arguments":"{}"}""",
            LLMStreamingTokens.ToolCallEnd,
            "world"
        }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBe(new[] { "Hello ", "world" });
        reader.HasToolEnd.ShouldBeTrue();
        reader.Failed.ShouldBeFalse();
        reader.Accumulator.AccumulatedContent.ShouldBe("Hello world");
    }

    [Test]
    public async Task TransientFailureBeforeAnyContent_IsRetried()
    {
        var provider = new ScriptedProvider(
            () => Tokens(Array.Empty<string>(), TransientError),
            () => Tokens(new[] { "second attempt" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        provider.Attempts.ShouldBe(2);
        yielded.ShouldBe(new[] { "second attempt" });
        reader.Failed.ShouldBeFalse();
    }

    [Test]
    public async Task TransientFailureAfterContentReachedTheCaller_IsNotRetried()
    {
        var provider = new ScriptedProvider(
            () => Tokens(new[] { "already on screen" }, TransientError),
            () => Tokens(new[] { "must never run" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        provider.Attempts.ShouldBe(1);
        yielded.ShouldBe(new[] { "already on screen" });
        reader.Failed.ShouldBeTrue();
    }

    [Test]
    public async Task PermanentFailure_IsNotRetried()
    {
        var provider = new ScriptedProvider(
            () => Tokens(Array.Empty<string>(), PermanentError),
            () => Tokens(new[] { "must never run" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        provider.Attempts.ShouldBe(1);
        yielded.ShouldBeEmpty();
        reader.Failed.ShouldBeTrue();
    }

    [Test]
    public async Task MalformedToolCallDelta_IsSkippedWithoutEndingTheStream()
    {
        var provider = new ScriptedProvider(() => Tokens(new[]
        {
            LLMStreamingTokens.ToolCallPrefix + "not json",
            "still streaming"
        }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBe(new[] { "still streaming" });
        reader.Failed.ShouldBeFalse();
    }

    [Test]
    public async Task EchoedLegacyPlaceholder_IsDroppedAndNeverYielded()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "[Exec", "uting function", " calls]", "\n" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBeEmpty();
        reader.Accumulator.AccumulatedContent.ShouldBeEmpty();
    }

    [Test]
    public async Task EchoedToolCallHistoryNote_IsDroppedAndNeverYielded()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "(Called to", "ols: get_employee.", " Their results follow.)" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBeEmpty();
        reader.Accumulator.AccumulatedContent.ShouldBeEmpty();
    }

    [Test]
    public async Task EchoFollowedByARealAnswer_YieldsOnlyTheAnswer()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "[Executing function calls]", "\n", "Anna works ", "today." }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        string.Concat(yielded).ShouldBe("Anna works today.");
        reader.Accumulator.AccumulatedContent.ShouldBe("Anna works today.");
    }

    [Test]
    public async Task AnswerThatOnlyStartsLikeAPlaceholder_IsReleasedUnchangedOnceItDiverges()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "[", "REPLIES:", "yes|no]" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBe(new[] { "[REPLIES:", "yes|no]" });
        reader.Accumulator.AccumulatedContent.ShouldBe("[REPLIES:yes|no]");
    }

    [Test]
    public async Task ShortAnswerThatIsAPlaceholderPrefix_IsReleasedWhenTheCallEnds()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "(" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        yielded.ShouldBe(new[] { "(" });
    }

    [Test]
    public async Task TransientFailureWhileOnlyAnEchoWasHeldBack_IsRetried()
    {
        var provider = new ScriptedProvider(
            () => Tokens(new[] { "[Executing" }, TransientError),
            () => Tokens(new[] { "second attempt" }));
        var reader = NewReader();

        var yielded = await Read(reader, provider);

        provider.Attempts.ShouldBe(2);
        yielded.ShouldBe(new[] { "second attempt" });
    }

    [Test]
    public async Task CancellationWhileStreaming_EndsQuietlyWithoutErrorLogFailureOrRetry()
    {
        var disposed = false;
        using var source = new CancellationTokenSource();
        var provider = new ScriptedProvider(token =>
            HonouringTheToken(new[] { "Hel", "lo " }, () => disposed = true, token));
        var logger = new RecordingLogger<ProviderStreamReaderTests>();
        var reader = new ProviderStreamReader(logger);

        var yielded = await ReadUntilCancelled(reader, provider, source);

        yielded.ShouldBe(new[] { "Hel" });
        reader.Cancelled.ShouldBeTrue();
        reader.Failed.ShouldBeFalse();
        provider.Attempts.ShouldBe(1);
        disposed.ShouldBeTrue();
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Test]
    public async Task CancellationWithAProviderThatIgnoresTheToken_StopsAtTheNextToken()
    {
        var disposed = false;
        using var source = new CancellationTokenSource();
        var provider = new ScriptedProvider(_ => IgnoringTheToken(new[] { "one ", "two ", "three " }, () => disposed = true));
        var reader = NewReader();

        var yielded = await ReadUntilCancelled(reader, provider, source);

        yielded.ShouldBe(new[] { "one " });
        reader.Cancelled.ShouldBeTrue();
        reader.Failed.ShouldBeFalse();
        disposed.ShouldBeTrue();
    }

    [Test]
    public async Task CancellationMidToolCall_KeepsTheTextAndNeverMarksTheToolCallsComplete()
    {
        using var source = new CancellationTokenSource();
        var provider = new ScriptedProvider(token => HonouringTheToken(new[]
        {
            "Creating it. ",
            LLMStreamingTokens.ToolCallPrefix + """{"index":0,"name":"create_employee","arguments":"{\"na"}"""
        }, () => { }, token));
        var reader = NewReader();

        var yielded = await ReadUntilCancelled(reader, provider, source);

        yielded.ShouldBe(new[] { "Creating it. " });
        reader.Accumulator.AccumulatedContent.ShouldBe("Creating it. ");
        reader.HasToolEnd.ShouldBeFalse();
        reader.Cancelled.ShouldBeTrue();
    }

    [Test]
    public async Task ACompletedRead_IsNotReportedAsCancelled()
    {
        var provider = new ScriptedProvider(() => Tokens(new[] { "all of it" }));
        var reader = NewReader();

        await Read(reader, provider);

        reader.Cancelled.ShouldBeFalse();
    }

    [Test]
    public async Task ACallerThatStopsEnumeratingEarly_StillDisposesTheProviderStream()
    {
        var disposed = false;
        var provider = new ScriptedProvider(_ => IgnoringTheToken(new[] { "one ", "two " }, () => disposed = true));
        var reader = NewReader();

        await foreach (var _ in reader.ReadAsync(provider, new LLMProviderRequest(), ModelId, CancellationToken.None))
        {
            break;
        }

        disposed.ShouldBeTrue();
    }

    [Test]
    public async Task AnErrorWithoutACancellation_IsStillLoggedAsAnError()
    {
        var provider = new ScriptedProvider(() => Tokens(Array.Empty<string>(), PermanentError));
        var logger = new RecordingLogger<ProviderStreamReaderTests>();
        var reader = new ProviderStreamReader(logger);

        await Read(reader, provider);

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error);
        reader.Cancelled.ShouldBeFalse();
    }
}
