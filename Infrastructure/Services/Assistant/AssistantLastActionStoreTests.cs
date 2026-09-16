// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Persistence behaviour of the previous-action record against a real DataBaseContext: one row per
/// conversation, overwrite on the next tool-calling turn, supersede without losing the record, the
/// clarification pins, and the TTL. Substitute repositories are deliberately not used here - they
/// cannot see the EF identity conflicts a read-modify-write inside one scope can produce.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class AssistantLastActionStoreTests
{
    private const string ConversationId = "conv-1";

    private readonly Guid _userId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private PersistentAssistantLastActionStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();

        var repository = new AssistantLastActionRepository(_context);
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAssistantLastActionRepository)).Returns(repository);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentAssistantLastActionStore(scopeFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private AssistantLastAction Action(string message, string skillName) => new()
    {
        UserId = _userId,
        ConversationId = ConversationId,
        UserMessage = message,
        AssistantAnswerExcerpt = "done",
        CreateTimeUtc = DateTime.UtcNow,
        Calls =
        [
            new AssistantLastActionCall
            {
                SkillName = skillName,
                ArgumentsJson = "{\"groupId\":\"g1\"}",
                ResultDataJson = "{\"GroupId\":\"g1\"}",
                IsReadOnly = false,
                Success = true
            }
        ]
    };

    [Test]
    public void Save_ThenPeek_ReturnsTheRecord()
    {
        _store.Save(Action("first", "add_shift_to_group"));

        var peeked = _store.Peek(_userId, ConversationId);

        peeked.ShouldNotBeNull();
        peeked!.UserMessage.ShouldBe("first");
        peeked.Calls.Count.ShouldBe(1);
        peeked.Calls[0].SkillName.ShouldBe("add_shift_to_group");
        peeked.Calls[0].ResultDataJson.ShouldBe("{\"GroupId\":\"g1\"}");
        peeked.CanAnchorCorrection(DateTime.UtcNow).ShouldBeTrue();
    }

    [Test]
    public void Save_Twice_KeepsExactlyOneRowAndTheNewerContent()
    {
        _store.Save(Action("first", "add_shift_to_group"));
        _store.Save(Action("second", "create_group"));

        _context.AssistantLastActions.Count(r => r.UserId == _userId).ShouldBe(1);
        _store.Peek(_userId, ConversationId)!.UserMessage.ShouldBe("second");
    }

    [Test]
    public void MarkSuperseded_KeepsTheRecordButStopsItFromAnchoring()
    {
        _store.Save(Action("first", "add_shift_to_group"));

        _store.MarkSuperseded(_userId, ConversationId);

        var peeked = _store.Peek(_userId, ConversationId);
        peeked.ShouldNotBeNull();
        peeked!.UserMessage.ShouldBe("first");
        peeked.SupersededAtUtc.ShouldNotBeNull();
        peeked.CanAnchorCorrection(DateTime.UtcNow).ShouldBeFalse();
    }

    [Test]
    public void MarkSuperseded_WithoutARecord_DoesNothing()
    {
        Should.NotThrow(() => _store.MarkSuperseded(_userId, ConversationId));

        _store.Peek(_userId, ConversationId).ShouldBeNull();
    }

    [Test]
    public void SaveClarificationCandidates_StoresPinsAndSupersedesTheAnchor()
    {
        _store.Save(Action("first", "add_shift_to_group"));

        _store.SaveClarificationCandidates(_userId, ConversationId, ["search_employees", "fill_group_by_criteria"]);

        var peeked = _store.Peek(_userId, ConversationId);
        peeked!.ClarificationSkillNames.ShouldBe(new[] { "search_employees", "fill_group_by_criteria" });
        peeked.CanAnchorCorrection(DateTime.UtcNow).ShouldBeFalse();
    }

    [Test]
    public void SaveClarificationCandidates_WithoutARecord_WritesNothing()
    {
        _store.SaveClarificationCandidates(_userId, ConversationId, ["search_employees", "fill_group_by_criteria"]);

        _store.Peek(_userId, ConversationId).ShouldBeNull();
    }

    [Test]
    public void Peek_DropsAnExpiredRecord()
    {
        _store.Save(Action("first", "add_shift_to_group"));
        var row = _context.AssistantLastActions.Single(r => r.UserId == _userId);
        row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        _context.SaveChanges();

        _store.Peek(_userId, ConversationId).ShouldBeNull();
    }

    [Test]
    public void Save_CapsTheStoredTexts()
    {
        var action = Action(new string('x', GracefulCorrectionDefaults.UserMessageMaxLength + 500), "add_shift_to_group");
        action.AssistantAnswerExcerpt = new string('y', GracefulCorrectionDefaults.AnswerExcerptMaxLength + 50);

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.UserMessage.Length.ShouldBe(GracefulCorrectionDefaults.UserMessageMaxLength);
        peeked.AssistantAnswerExcerpt.Length.ShouldBe(GracefulCorrectionDefaults.AnswerExcerptMaxLength);
    }

    [Test]
    public void Save_ThenPeek_ReturnsTheSkillDisplayLabel()
    {
        var action = Action("first", "add_shift_to_group");
        action.Calls[0].SkillDisplayLabel = "Add shift to group";

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.Calls[0].SkillDisplayLabel.ShouldBe("Add shift to group");
    }

    [Test]
    public void Save_CapsTheSkillDisplayLabel()
    {
        var action = Action("first", "add_shift_to_group");
        action.Calls[0].SkillDisplayLabel = new string('z', GracefulCorrectionDefaults.SkillDisplayLabelMaxLength + 50);

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.Calls[0].SkillDisplayLabel!.Length.ShouldBe(GracefulCorrectionDefaults.SkillDisplayLabelMaxLength);
    }
}
