// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Whole-turn tests of the proposal a keyword-matched recipe makes in both chat paths. With an authored goal translation in
/// the turn's language the question and its Yes/No chip are written without a model call (the streaming path, which the chat
/// UI uses, ends the turn on the proposal with the usual closing events and a stored pending recipe; the non-streaming path
/// answers the same text), without one the model phrases the question as before, and a proposal that is re-asked after an
/// ambiguous reply stays with the model because it carries the note asking for an unambiguous yes or no.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServiceKeywordRecipeProposalTests
{
    private const string RecipeName = "create-group";
    private const string RecipeGoal = "Create a new group.";
    private const string GermanGoal = "Eine neue Gruppe anlegen";
    private const string GermanChip = "[REPLIES:single \"Ja=yes\" | \"Nein=no\"]";
    private const string Message = "Bitte eine Gruppe anlegen";

    private static RecipeExecutionPlan KeywordPlan(bool withGermanGoal) => new(
        RecipeName,
        [new RecipeStep { Kind = RecipeStepKinds.Mutate, Skill = "create_group" }],
        needsConfirmation: true,
        goal: RecipeGoal,
        goalTranslations: withGermanGoal ? new Dictionary<string, string> { ["de"] = GermanGoal } : null);

    [Test]
    public async Task Streaming_ProposesWithoutAModelCall_EndsOnTheProposalAndStoresThePendingRecipe()
    {
        var harness = new LLMServiceTurnHarness(streaming: true);
        harness.StartsRecipe(KeywordPlan(withGermanGoal: true));

        var chunks = await harness.StreamAsync(LLMServiceTurnHarness.Context(Message, "de"));

        var content = LLMServiceTurnHarness.StreamedContent(chunks);
        content.ShouldContain(GermanGoal);
        content.ShouldEndWith(GermanChip);
        chunks.Count(c => c.Type == SseChunkType.Content).ShouldBe(1, "the proposal is one chunk, not a stream of model tokens");
        harness.Requests.ShouldBeEmpty("no model call may have been put");
        chunks.ShouldNotContain(c => c.Type == SseChunkType.Error);
        chunks.ShouldNotContain(c => c.Type == SseChunkType.Status && c.Stage == SseStatusStages.CallingModel);
        chunks[^1].Type.ShouldBe(SseChunkType.Done);
        harness.PendingRecipes.Received(1).Save(Arg.Is<PendingRecipe>(p =>
            p.AwaitingConfirmation && p.RecipeName == RecipeName));
        harness.PersistedAnswer.ShouldNotBeNull().ShouldContain(GermanGoal);
        harness.Logger.Entries.ShouldNotContain(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Test]
    public async Task NonStreaming_AnswersTheSameProposalWithoutAModelCall()
    {
        var harness = new LLMServiceTurnHarness(streaming: false);
        harness.StartsRecipe(KeywordPlan(withGermanGoal: true));

        var response = await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(Message, "de"));

        response.Message.ShouldContain(GermanGoal);
        response.SuggestedReplies.ShouldNotBeNull();
        harness.Requests.ShouldBeEmpty();
        harness.PendingRecipes.Received(1).Save(Arg.Is<PendingRecipe>(p => p.AwaitingConfirmation));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task WithoutAGoalTranslationInTheLanguage_TheModelPhrasesTheProposal(bool streaming)
    {
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.Text("Soll ich die Gruppe anlegen?"));
        harness.StartsRecipe(KeywordPlan(withGermanGoal: false));

        var answer = streaming
            ? LLMServiceTurnHarness.StreamedContent(await harness.StreamAsync(LLMServiceTurnHarness.Context(Message, "de")))
            : (await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(Message, "de"))).Message;

        answer.ShouldStartWith("Soll ich die Gruppe anlegen?");
        harness.Requests.Count.ShouldBe(1);
        harness.Requests[0].VolatileSystemPrompt!.ShouldContain("matches a guided flow");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AReaskAfterAnAmbiguousReply_StaysWithTheModel_AndCarriesTheNote(bool streaming)
    {
        var plan = KeywordPlan(withGermanGoal: true);
        plan.NoteAmbiguousReply("Ask for an unambiguous yes or no.");
        var harness = new LLMServiceTurnHarness(streaming);
        harness.Script(LLMServiceTurnHarness.Text("Meinst du Ja oder Nein?"));
        harness.StartsRecipe(plan);

        var answer = streaming
            ? LLMServiceTurnHarness.StreamedContent(await harness.StreamAsync(LLMServiceTurnHarness.Context(Message, "de")))
            : (await harness.Service.ProcessAsync(LLMServiceTurnHarness.Context(Message, "de"))).Message;

        answer.ShouldStartWith("Meinst du Ja oder Nein?");
        harness.Requests.Count.ShouldBe(1);
        harness.Requests[0].VolatileSystemPrompt!.ShouldContain("Ask for an unambiguous yes or no.");
    }
}
