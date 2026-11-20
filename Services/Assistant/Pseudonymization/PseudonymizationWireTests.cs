// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;
using LLMMessage = Klacks.Api.Domain.Services.Assistant.Providers.LLMMessage;
using LLMProvider = Klacks.Api.Domain.Models.Assistant.LLMProvider;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// End-to-end proof on the wire: a request goes through LLMProviderFactory (switch not set, so on by default), the
/// pseudonymisation decorator and the real OpenAI-compatible provider into a recording HTTP handler. The bytes that
/// would leave the installation must not contain any known plain value; the answer and the tool arguments come back
/// with the real values, and the caller's request (what is stored as history) stays in plain text.
/// </summary>
[TestFixture]
public class PseudonymizationWireTests
{
    private const string ProviderId = "cloud-provider";
    private const string CloudUrl = "https://llm.example.com/v1/";

    private const string AnswerBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"MA-0017-V ist eingeplant, MAIL-0001 informiert.\"}}]}";

    private const string ToolCallBody =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null," +
        "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"assign_shift\"," +
        "\"arguments\":\"{\\\"employee\\\":\\\"MA-0017\\\",\\\"replaces\\\":\\\"MA-0018\\\"}\"}}]}}]}";

    private static readonly string[] Forbidden =
        ["Anna", "Meier", "Jürg", "Müller", "Mueller", "anna.meier@example.ch", "079 123 45 67", "4711", "1985-03-14"];

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

    [Test]
    public async Task ChatRequest_LeavesWithoutPlainValues_AndComesBackInPlainText()
    {
        var handler = new RecordingHandler(AnswerBody, ToolCallBody);
        var factory = CreateFactory(handler);
        var request = new LLMProviderRequest
        {
            ModelId = "gpt-test",
            SystemPrompt = "You help the planner.",
            Message = "Plane Anna Meier statt Jürg Müller ein und schreib ihr an anna.meier@example.ch oder 079 123 45 67.",
            ConversationHistory =
            [
                new LLMMessage { Role = "user", Content = "Wer ist Mitarbeiterin #4711?" },
                new LLMMessage
                {
                    Role = "assistant",
                    Content = "{\"Name\":\"Meier\",\"FirstName\":\"Anna\",\"IdNumber\":4711,\"Birthdate\":\"1985-03-14\"}"
                }
            ]
        };

        var provider = await factory.GetProviderAsync(ProviderId);
        var answer = await provider!.ProcessAsync(request);
        var toolTurn = await provider.ProcessAsync(request);

        handler.Bodies.Count.ShouldBe(2);
        foreach (var wire in handler.Bodies)
        {
            foreach (var value in Forbidden)
            {
                wire.ShouldNotContain(value, Case.Insensitive);
            }

            wire.ShouldContain("MA-0017");
        }

        answer.Content.ShouldBe("Anna ist eingeplant, anna.meier@example.ch informiert.");
        var call = toolTurn.FunctionCalls.ShouldHaveSingleItem();
        call.Parameters["employee"].ToString().ShouldBe("Anna Meier");
        call.Parameters["replaces"].ToString().ShouldBe("Jürg Müller");
        request.Message.ShouldContain("Anna Meier");
        request.ConversationHistory[1].Content.ShouldContain("1985-03-14");
    }

    private static LLMProviderFactory CreateFactory(RecordingHandler handler)
    {
        var concrete = new GenericOpenAICompatibleProvider(
            new HttpClient(handler), NullLogger<GenericOpenAICompatibleProvider>.Instance, new ConfigurationBuilder().Build());
        var repository = Substitute.For<ILLMRepository>();
        repository.GetProviderByIdAsync(ProviderId).Returns(new LLMProvider
        {
            ProviderId = ProviderId, ProviderName = "Cloud", IsEnabled = true, Priority = 1, BaseUrl = CloudUrl, RequiresApiKey = false
        });
        var settings = Substitute.For<ISettingsReader>();
        settings.GetSetting(SettingKeys.LlmPseudonymizationEnabled).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        var dictionary = Dictionary(
        [
            Employee(17, "Anna", "Meier", idNumber: 4711),
            Employee(18, "Jürg", "Müller", GenderEnum.Male)
        ]);
        var mapSource = Substitute.For<IPseudonymMapSource>();
        mapSource.GetDictionaryAsync(Arg.Any<CancellationToken>()).Returns(dictionary);
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        var services = new ServiceCollection().AddSingleton(concrete).BuildServiceProvider();
        return new LLMProviderFactory(
            services, repository, NullLogger<LLMProviderFactory>.Instance,
            Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundMode>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundTokenBudget>(),
            settings, mapSource, clock);
    }
}
