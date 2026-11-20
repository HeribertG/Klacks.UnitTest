// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Pseudonymization;
using Microsoft.Extensions.Logging.Abstractions;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;
using ProviderMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class PseudonymizingLLMProviderTests
{
    private const string CloudUrl = "https://api.openai.com/v1/";

    private static readonly string[] ForbiddenOnTheLine =
        ["Anna", "Meier", "Müller", "M\\u00FCller", "anna.meier@example.ch", "079 123 45 67", "4711", "1980-05-03"];

    private RecordingProvider _inner = null!;
    private IPseudonymMapSource _source = null!;
    private ICompanyClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = new RecordingProvider();
        var dictionary = Dictionary(
        [
            Employee(17, "Anna", "Meier", idNumber: 4711),
            Employee(18, "Jürg", "Müller", GenderEnum.Male),
            Employee(30, "Beat", "Meier", GenderEnum.Male)
        ]);
        _source = Substitute.For<IPseudonymMapSource>();
        _source.GetDictionaryAsync(Arg.Any<CancellationToken>()).Returns(dictionary);
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
    }

    private PseudonymizingLLMProvider Sut(string? baseUrl = CloudUrl) =>
        new(_inner, baseUrl, _source, _clock, NullLogger.Instance);

    private static LLMProviderRequest Request() => new()
    {
        ModelId = "gpt-test",
        SystemPrompt = "You help the planner Anna Meier.",
        Message = "Plane Anna statt Meier ein, Mail anna.meier@example.ch, Tel 079 123 45 67",
        ConversationHistory =
        [
            new ProviderMessage { Role = "user", Content = "Wer ist Müller?" },
            new ProviderMessage
            {
                Role = "user",
                Content = "Data: {\"Name\":\"M\\u00FCller\",\"IdNumber\":4711,\"Birthdate\":\"1980-05-03T00:00:00\"}"
            }
        ]
    };

    private static IEnumerable<string> LineTexts(LLMProviderRequest request) =>
        new[] { request.SystemPrompt, request.VolatileSystemPrompt ?? string.Empty, request.Message }
            .Concat(request.ConversationHistory.Select(message => message.Content));

    [Test]
    public async Task ProcessAsync_SendsNoKnownPlainValue_AndLeavesTheCallersRequestUnchanged()
    {
        var request = Request();
        var originalMessage = request.Message;
        var originalHistory = request.ConversationHistory[1];

        await Sut().ProcessAsync(request);

        var sent = _inner.Requests.Single();
        sent.ShouldNotBeSameAs(request);
        foreach (var text in LineTexts(sent))
        {
            foreach (var forbidden in ForbiddenOnTheLine)
            {
                text.ShouldNotContain(forbidden, Case.Insensitive);
            }
        }

        sent.Message.ShouldContain("MA-0017-V");
        sent.Message.ShouldContain("NAME-0042");
        sent.VolatileSystemPrompt.ShouldNotBeNull();
        sent.VolatileSystemPrompt.ShouldContain("MA-0018: male");
        request.Message.ShouldBe(originalMessage);
        request.ConversationHistory[1].ShouldBeSameAs(originalHistory);
        originalHistory.Content.ShouldContain("M\\u00FCller");
        request.VolatileSystemPrompt.ShouldBeNull();
    }

    [Test]
    public async Task ProcessAsync_RestoresAnswerAndToolArguments()
    {
        _inner.Response = new LLMProviderResponse
        {
            Content = "MA-0017-V ist eingeplant, MA-0999 nicht.",
            FunctionCalls =
            [
                new LLMFunctionCall
                {
                    FunctionName = "update_client",
                    Parameters = new Dictionary<string, object>
                    {
                        ["lastName"] = "MA-0018-N",
                        ["idNumber"] = JsonSerializer.SerializeToElement("MA-0017-P"),
                        ["note"] = "MA-0999"
                    }
                }
            ]
        };

        var response = await Sut().ProcessAsync(Request());

        response.Content.ShouldBe("Anna ist eingeplant, MA-0999 nicht.");
        var parameters = response.FunctionCalls.Single().Parameters;
        parameters["lastName"].ShouldBe("Müller");
        ((JsonElement)parameters["idNumber"]).GetInt32().ShouldBe(4711);
        parameters["note"].ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "MA-0999");
    }

    [Test]
    public async Task ProcessStreamAsync_SendsPseudonyms_AndStreamsRealValues()
    {
        _inner.StreamTokens = ["MA-00", "17-V kommt"];

        var tokens = new List<string>();
        await foreach (var token in Sut().ProcessStreamAsync(Request()))
        {
            tokens.Add(token);
        }

        string.Concat(tokens).ShouldBe("Anna kommt");
        _inner.Requests.Single().Message.ShouldNotContain("Anna");
    }

    [TestCase("http://localhost:11434/v1", "llama3")]
    [TestCase("http://192.168.1.20:1234/v1", "qwen")]
    [TestCase("http://ollama:11434/v1", "llama3")]
    public async Task LocalModel_PassesThroughUnchanged(string baseUrl, string model)
    {
        var request = Request();
        request.ModelId = model;

        await Sut(baseUrl).ProcessAsync(request);

        _inner.Requests.Single().ShouldBeSameAs(request);
        await _source.DidNotReceive().GetDictionaryAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LocalOllamaCloudModel_IsPseudonymised()
    {
        var request = Request();
        request.ModelId = "gpt-oss:120b-cloud";

        await Sut("http://localhost:11434/v1").ProcessAsync(request);

        _inner.Requests.Single().Message.ShouldNotContain("Anna");
    }

    [Test]
    public async Task RequestWithImage_IsRefused_AndNothingIsSent()
    {
        var request = Request();
        request.ImagePng = [1, 2, 3];

        var response = await Sut().ProcessAsync(request);

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe(PseudonymizationConstants.ImageRefusedError);
        _inner.Requests.ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in Sut().ProcessStreamAsync(request))
            {
            }
        });
        _inner.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task DictionaryFailure_FailsClosed()
    {
        _source.GetDictionaryAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<PseudonymDictionary>(new TimeoutException("db down")));

        var response = await Sut().ProcessAsync(Request());

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe(PseudonymizationConstants.DictionaryUnavailableError);
        _inner.Requests.ShouldBeEmpty();
    }

    [Test]
    public void Decorator_ImplementsEveryInterfaceMemberItself()
    {
        var map = typeof(PseudonymizingLLMProvider).GetInterfaceMap(typeof(ILLMProvider));

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            map.TargetMethods[i].DeclaringType.ShouldBe(
                typeof(PseudonymizingLLMProvider),
                $"{map.InterfaceMethods[i].Name} would fall back to the interface default");
        }
    }

    [Test]
    public void RequestCopier_CarriesEverySettableProperty()
    {
        var source = new LLMProviderRequest();
        foreach (var property in typeof(LLMProviderRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanWrite))
        {
            property.SetValue(source, SampleValue(property.PropertyType));
        }

        var copy = PseudonymRequestCopier.Copy(source, text => text);

        foreach (var property in typeof(LLMProviderRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.CanWrite && property.PropertyType != typeof(List<ProviderMessage>)))
        {
            property.GetValue(copy).ShouldBe(property.GetValue(source), $"{property.Name} is not copied");
        }

        var message = new ProviderMessage();
        foreach (var property in typeof(ProviderMessage).GetProperties().Where(property => property.CanWrite))
        {
            property.SetValue(message, SampleValue(property.PropertyType));
        }

        source.ConversationHistory = [message];
        var copiedMessage = PseudonymRequestCopier.Copy(source, text => text).ConversationHistory.Single();
        copiedMessage.ShouldNotBeSameAs(message);
        foreach (var property in typeof(ProviderMessage).GetProperties().Where(property => property.CanWrite))
        {
            property.GetValue(copiedMessage).ShouldBe(property.GetValue(message), $"LLMMessage.{property.Name} is not copied");
        }
    }

    private static object? SampleValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying switch
        {
            _ when underlying == typeof(string) => "sample",
            _ when underlying == typeof(int) => 7,
            _ when underlying == typeof(double) => 0.5,
            _ when underlying == typeof(decimal) => 1.5m,
            _ when underlying == typeof(bool) => true,
            _ when underlying == typeof(DateTime) => new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            _ when underlying == typeof(byte[]) => new byte[] { 1 },
            _ when underlying == typeof(Action<ProviderUsage>) => new Action<ProviderUsage>(_ => { }),
            _ => Activator.CreateInstance(underlying)
        };
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public List<LLMProviderRequest> Requests { get; } = new();

        public LLMProviderResponse Response { get; set; } = new() { Content = "ok" };

        public string[] StreamTokens { get; set; } = [];

        public string ProviderId => "openai";

        public string ProviderName => "Recording";

        public bool IsEnabled => true;

        public bool SupportsStreaming => true;

        public void Configure(Klacks.Api.Domain.Models.Assistant.LLMProvider providerConfig)
        {
        }

        public Task<LLMProviderResponse> ProcessAsync(LLMProviderRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<string> ProcessStreamAsync(
            LLMProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var token in StreamTokens)
            {
                await Task.Yield();
                yield return token;
            }
        }

        public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(true);
    }
}
