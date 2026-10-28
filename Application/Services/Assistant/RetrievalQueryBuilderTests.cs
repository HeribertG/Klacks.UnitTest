// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the ORDER of the history-anchored retrieval query. Both encoders cap their input at 512 tokens and
/// cut from the tail, so the order decides what a long conversation loses: the current message must lead,
/// the workflow anchor (first user message) must follow, and the recent turns trail newest first.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class RetrievalQueryBuilderTests
{
    private const string ConversationId = "conv-1";
    private const string UserId = "user-1";

    private ILLMRepository _repository = null!;
    private RetrievalQueryBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        var manager = new LLMConversationManager(
            Substitute.For<ILogger<LLMConversationManager>>(), _repository,
            PlaygroundOff.ConversationStore());
        _builder = new RetrievalQueryBuilder(manager, Substitute.For<ILogger<RetrievalQueryBuilder>>());
    }

    [Test]
    public async Task BuildAsync_WithoutConversation_ReturnsMessageOnly()
    {
        var query = await _builder.BuildAsync("Zeig mir die Dienste", null, UserId);

        query.ShouldBe("Zeig mir die Dienste");
    }

    [Test]
    public async Task BuildAsync_WithEmptyHistory_ReturnsMessageOnly()
    {
        StubHistory();

        var query = await _builder.BuildAsync("Zeig mir die Dienste", ConversationId, UserId);

        query.ShouldBe("Zeig mir die Dienste");
    }

    [Test]
    public async Task BuildAsync_PutsCurrentMessageFirst_ThenAnchor_ThenRecentNewestFirst()
    {
        StubHistory(
            ("user", "Neuen Mitarbeiter anlegen"),
            ("assistant", "Wie heisst er?"),
            ("user", "Max Muster"),
            ("assistant", "Welches Pensum?"),
            ("user", "80 Prozent"));

        var query = await _builder.BuildAsync("ja, korrekt", ConversationId, UserId);

        query.Split('\n').ShouldBe(["ja, korrekt", "Neuen Mitarbeiter anlegen", "80 Prozent", "Max Muster"]);
    }

    [Test]
    public async Task BuildAsync_KeepsOnlyTheFourMostRecentTurnsAfterTheAnchor()
    {
        StubHistory(
            ("user", "anchor"),
            ("user", "m1"),
            ("user", "m2"),
            ("user", "m3"),
            ("user", "m4"),
            ("user", "m5"));

        var query = await _builder.BuildAsync("now", ConversationId, UserId);

        query.Split('\n').ShouldBe(["now", "anchor", "m5", "m4", "m3", "m2"]);
    }

    [Test]
    public async Task BuildAsync_DropsRepeatedWordings()
    {
        StubHistory(
            ("user", "Dienstplan zeigen"),
            ("user", "ja"),
            ("user", "ja"));

        var query = await _builder.BuildAsync("ja", ConversationId, UserId);

        query.Split('\n').ShouldBe(["ja", "Dienstplan zeigen"]);
    }

    [Test]
    public async Task BuildAsync_IgnoresAssistantTurns()
    {
        StubHistory(
            ("user", "Ferien eintragen"),
            ("assistant", "Für wen soll ich die Ferien eintragen? Bitte nenne den Namen."));

        var query = await _builder.BuildAsync("für Anna", ConversationId, UserId);

        query.ShouldNotContain("Bitte nenne");
        query.Split('\n').ShouldBe(["für Anna", "Ferien eintragen"]);
    }

    private void StubHistory(params (string Role, string Content)[] messages)
    {
        var start = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var rows = messages
            .Select((m, i) => new LLMMessage
            {
                ConversationId = Guid.NewGuid(),
                Role = m.Role,
                Content = m.Content,
                CreateTime = start.AddMinutes(i)
            })
            .ToList();

        _repository.GetConversationMessagesAsync(ConversationId, UserId, Arg.Any<int>()).Returns(rows);
    }
}
