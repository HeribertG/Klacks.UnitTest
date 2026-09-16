// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Persistence behaviour of the previous-action record against a real DataBaseContext: one row per
/// conversation, overwrite on the next tool-calling turn, supersede without losing the record, the
/// clarification pins, and the TTL. Substitute repositories are deliberately not used here - they
/// cannot see the EF identity conflicts a read-modify-write inside one scope can produce.
/// AssistantLastActionRepository's insert-vs-update race retry (two turns of one conversation hitting
/// the unique index concurrently) is NOT covered here: the InMemory provider does not raise the
/// Postgres-specific unique-violation the retry's exception filter checks for, and the two-step
/// read-then-write UpsertAsync gives no hook to interleave a second write between its own read and its
/// own write. That path is covered by the code's structure (the same IsUniqueViolation/DbUpdateException
/// pattern already used by SkillPhraseRepository) and by the integration test suite.
/// The same applies to PruneExpiredAsync's own concurrent-prune tolerance (every tool-calling turn of
/// every user prunes globally, so two overlapping prunes can both select the same expired row): a query
/// against the InMemory provider always reflects the CURRENT store, never a stale view, so a second
/// context's delete lands before or after the first context's query ever runs - there is no way to make
/// the first context's read observe the row and then have the row vanish underneath before that same
/// context's SaveChanges, which is exactly the ordering DbUpdateConcurrencyException requires. Covered
/// by the code's structure (a plain try/catch around one SaveChangesAsync call) and the integration
/// test suite.
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

    [Test]
    public void Save_CapsAllFourBoundedFieldsInOneCall()
    {
        var action = Action(new string('u', GracefulCorrectionDefaults.UserMessageMaxLength + 500), "add_shift_to_group");
        action.AssistantAnswerExcerpt = new string('e', GracefulCorrectionDefaults.AnswerExcerptMaxLength + 50);
        action.Calls[0].SkillDisplayLabel = new string('l', GracefulCorrectionDefaults.SkillDisplayLabelMaxLength + 50);
        action.Calls[0].ArgumentsJson = new string('a', GracefulCorrectionDefaults.CallJsonMaxLength + 50);
        action.Calls[0].ResultDataJson = new string('r', GracefulCorrectionDefaults.CallJsonMaxLength + 50);

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.UserMessage.Length.ShouldBe(GracefulCorrectionDefaults.UserMessageMaxLength);
        peeked.AssistantAnswerExcerpt.Length.ShouldBe(GracefulCorrectionDefaults.AnswerExcerptMaxLength);
        peeked.Calls[0].SkillDisplayLabel!.Length.ShouldBe(GracefulCorrectionDefaults.SkillDisplayLabelMaxLength);
        peeked.Calls[0].ArgumentsJson.Length.ShouldBe(GracefulCorrectionDefaults.CallJsonMaxLength);
        peeked.Calls[0].ResultDataJson.Length.ShouldBe(GracefulCorrectionDefaults.CallJsonMaxLength);
    }

    /// <summary>
    /// CapCalls rebuilds every call field by field, so a property that is not listed there is dropped on
    /// Save while every composer-level test still passes. This pins the authored labels the correction
    /// turn resolves its question from: without them the question is bound to the language of the turn
    /// that stored the record.
    /// </summary>
    [Test]
    public void Save_ThenPeek_ReturnsTheAuthoredSkillLabels()
    {
        var action = Action("first", "add_shift_to_group");
        action.Calls[0].SkillLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "Schicht einer Gruppe zuweisen",
            ["fr"] = "Affecter un service à un groupe"
        };

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.Calls[0].SkillLabels.ShouldNotBeNull();
        peeked.Calls[0].SkillLabels!["de"].ShouldBe("Schicht einer Gruppe zuweisen");
        peeked.Calls[0].SkillLabels!["fr"].ShouldBe("Affecter un service à un groupe");
    }

    [Test]
    public void Save_CapsEveryAuthoredSkillLabel()
    {
        var action = Action("first", "add_shift_to_group");
        action.Calls[0].SkillLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new string('d', GracefulCorrectionDefaults.SkillDisplayLabelMaxLength + 50),
            ["fr"] = new string('f', GracefulCorrectionDefaults.SkillDisplayLabelMaxLength + 50)
        };

        _store.Save(action);

        var peeked = _store.Peek(_userId, ConversationId)!;
        peeked.Calls[0].SkillLabels!["de"].Length.ShouldBe(GracefulCorrectionDefaults.SkillDisplayLabelMaxLength);
        peeked.Calls[0].SkillLabels!["fr"].Length.ShouldBe(GracefulCorrectionDefaults.SkillDisplayLabelMaxLength);
    }

    [Test]
    public void Save_WithoutAuthoredLabels_PeeksBackNone()
    {
        _store.Save(Action("first", "add_shift_to_group"));

        _store.Peek(_userId, ConversationId)!.Calls[0].SkillLabels.ShouldBeNull();
    }

    [Test]
    public void Peek_ForADifferentConversationOfTheSameUser_ReturnsNull()
    {
        _store.Save(Action("first", "add_shift_to_group"));

        _store.Peek(_userId, "conv-2").ShouldBeNull();
    }
}
