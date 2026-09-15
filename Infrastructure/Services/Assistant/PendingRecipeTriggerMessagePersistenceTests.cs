// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The persisted triggering message, through the real store against an in-memory DataBaseContext.
///
/// The case that matters is the second one. PendingRecipeRepository.UpsertAsync copies fields explicitly,
/// so a column added to the row without being added there is dropped on every re-persist - and re-persist
/// is the normal path, not the exception: Persist runs on every ask-pause, so from turn two onwards the row
/// already exists and only the update branch runs. A roundtrip test that saves once would pass with the
/// copy missing and would not notice until a real conversation reached its second turn.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class PendingRecipeTriggerMessagePersistenceTests
{
    private const string ConversationId = "conv-trigger-message";
    private const string RecipeName = "add-extern-employee-to-nearest-group";
    private const string TriggerMessage = "Füge alle externen Mitarbeiter der nächsten Gruppe hinzu";

    private static readonly Guid UserId = Guid.NewGuid();

    private DbContextOptions<DataBaseContext> _options = null!;
    private PersistentPendingRecipeStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPendingRecipeRepository))
            .Returns(_ => new PendingRecipeRepository(CreateContext()));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentPendingRecipeStore(scopeFactory);
    }

    private DataBaseContext CreateContext() => new(_options, Substitute.For<IHttpContextAccessor>());

    private static PendingRecipe Pending(string? triggerMessage, int stepIndex = 0) => new()
    {
        UserId = UserId,
        ConversationId = ConversationId,
        RecipeName = RecipeName,
        StepIndex = stepIndex,
        Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        AwaitingConfirmation = false,
        CaptureRewindUsed = false,
        TriggerMessage = triggerMessage
    };

    [Test]
    public void Save_then_Peek_round_trips_the_triggering_message()
    {
        _store.Save(Pending(TriggerMessage));

        _store.Peek(UserId, ConversationId)!.TriggerMessage.ShouldBe(TriggerMessage);
    }

    /// <summary>
    /// The update path, where an explicit field copy drops anything it does not list. Also advances the step
    /// index so the assertion cannot pass by reading a stale row that was never updated at all.
    /// </summary>
    [Test]
    public void Saving_again_over_the_update_path_keeps_the_triggering_message()
    {
        _store.Save(Pending(TriggerMessage, stepIndex: 0));
        _store.Save(Pending(TriggerMessage, stepIndex: 1));

        var peeked = _store.Peek(UserId, ConversationId);
        peeked!.StepIndex.ShouldBe(1, "the second Save must have taken the update path");
        peeked.TriggerMessage.ShouldBe(TriggerMessage);
    }

    /// <summary>
    /// A null has to be written too, not skipped: leaving the previous value behind would resurrect the
    /// triggering message of a recipe the conversation has already moved on from.
    /// </summary>
    [Test]
    public void Saving_again_without_a_triggering_message_clears_it()
    {
        _store.Save(Pending(TriggerMessage));
        _store.Save(Pending(null));

        _store.Peek(UserId, ConversationId)!.TriggerMessage.ShouldBeNull();
    }

    /// <summary>
    /// The cap is enforced in code rather than in the column configuration, because EF InMemory ignores
    /// HasMaxLength - so this store cannot prove the constraint and the composer has to. Asserted here as
    /// the composition a caller would actually persist, not as a second test of the composer itself.
    /// </summary>
    [Test]
    public void A_capped_triggering_message_survives_the_round_trip_intact()
    {
        var longTrigger = new string('x', RecipeEngineDefaults.PendingRecipeTriggerMessageMaxLength + 500);
        var capped = RecipeCorrectionComposer.CapForStorage(longTrigger);

        _store.Save(Pending(capped));

        var peeked = _store.Peek(UserId, ConversationId)!.TriggerMessage;
        peeked!.Length.ShouldBe(RecipeEngineDefaults.PendingRecipeTriggerMessageMaxLength);
        peeked.ShouldBe(capped);
    }
}
