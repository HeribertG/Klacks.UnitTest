// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the only part of the loop that talks to a language model. What matters here is what the loop
/// does with an answer it cannot trust: a skill name the model invented would produce a phrase for a skill
/// that does not exist, and a "phrase" that is really a sentence would dilute the skill's index text. Both
/// are dropped rather than passed on. The provider is a stub, because the loop must behave the same way
/// whichever model an installation configured.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class LearnedArtifactGeneratorTests
{
    private static readonly Guid ClusterId = Guid.NewGuid();

    private FakeLLMProvider _provider = null!;
    private ICheapestModelResolver _resolver = null!;
    private LearnedArtifactGenerator _generator = null!;

    [SetUp]
    public void SetUp()
    {
        _provider = new FakeLLMProvider();
        _resolver = Substitute.For<ICheapestModelResolver>();
        GivenModel(_provider);

        _generator = new LearnedArtifactGenerator(
            _resolver, Substitute.For<ILogger<LearnedArtifactGenerator>>());
    }

    private void GivenModel(ILLMProvider? provider) =>
        _resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns((provider == null ? null : new LLMModel { ApiModelId = "fake-1" }, provider));

    private static SkillLearningClusterContext Cluster(string? expectedSkill = null) =>
        new(ClusterId, "Zeige mir die Umsatzstatistik pro Kunde", "de", expectedSkill, null, [], 0, null);

    private static IReadOnlyList<SkillLearningTriageInput> OneCase(
        string? expectedSkill = null, params string[] candidates) =>
        [new SkillLearningTriageInput(Cluster(expectedSkill), candidates)];

    [Test]
    public async Task WithNoEnabledModel_NothingIsClassified()
    {
        GivenModel(null);

        (await _generator.ClassifyAsync(OneCase(null, "list_clients"))).ShouldBeEmpty();
    }

    [Test]
    public async Task NoClusters_AsksNoModelAtAll()
    {
        (await _generator.ClassifyAsync([])).ShouldBeEmpty();

        _provider.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task EveryClusterOfARun_IsClassifiedInASingleCall()
    {
        _provider.Answering(
            "{\"cases\":[{\"index\":0,\"kind\":\"phrase_gap\",\"skill\":\"list_clients\",\"reason\":\"exists\"},"
            + "{\"index\":1,\"kind\":\"needs_code\",\"reason\":\"nothing fits\"}]}");

        var inputs = new List<SkillLearningTriageInput>
        {
            new(Cluster(), ["list_clients"]),
            new(new SkillLearningClusterContext(Guid.NewGuid(), "etwas anderes", "de", null, null, [], 0, null), [])
        };

        var results = await _generator.ClassifyAsync(inputs);

        _provider.Requests.Count.ShouldBe(1);
        results.Count.ShouldBe(2);
        results[0].Kind.ShouldBe(SkillLearningClassifications.PhraseGap);
        results[0].TargetSkill.ShouldBe("list_clients");
        results[1].Kind.ShouldBe(SkillLearningClassifications.NeedsCode);
    }

    // A name the model made up cannot be verified by the routing oracle - it would simply never be found,
    // and the cluster would burn its attempt budget on a skill that never existed.
    [Test]
    public async Task ASkillOutsideTheOfferedList_IsDropped()
    {
        _provider.Answering(
            "{\"cases\":[{\"index\":0,\"kind\":\"phrase_gap\",\"skill\":\"invent_revenue_report\"}]}");

        var results = await _generator.ClassifyAsync(OneCase(null, "list_clients"));

        results.ShouldHaveSingleItem().TargetSkill.ShouldBeNull();
    }

    [Test]
    public async Task TheSkillTheUserCorrectedTo_IsAcceptedEvenWhenRetrievalNeverOfferedIt()
    {
        _provider.Answering(
            "{\"cases\":[{\"index\":0,\"kind\":\"phrase_gap\",\"skill\":\"revenue_per_client\"}]}");

        var results = await _generator.ClassifyAsync(OneCase("revenue_per_client", "list_clients"));

        results.ShouldHaveSingleItem().TargetSkill.ShouldBe("revenue_per_client");
    }

    [Test]
    public async Task AnUnknownKind_IsDropped()
    {
        _provider.Answering("{\"cases\":[{\"index\":0,\"kind\":\"maybe\",\"skill\":\"list_clients\"}]}");

        (await _generator.ClassifyAsync(OneCase(null, "list_clients"))).ShouldBeEmpty();
    }

    [Test]
    public async Task AnAnswerThatIsNotJson_IsDropped()
    {
        _provider.Answering("I could not decide.");

        (await _generator.ClassifyAsync(OneCase(null, "list_clients"))).ShouldBeEmpty();
    }

    [Test]
    public async Task AnIndexOutsideTheBatch_IsDropped()
    {
        _provider.Answering("{\"cases\":[{\"index\":7,\"kind\":\"needs_code\"}]}");

        (await _generator.ClassifyAsync(OneCase(null, "list_clients"))).ShouldBeEmpty();
    }

    [Test]
    public async Task ThePhrasePrompt_CarriesTheLanguageTheTargetAndTheWish()
    {
        _provider.Answering("{\"phrases\":[\"umsatz pro kunde\"]}");

        await _generator.GeneratePhrasesAsync(
            Cluster(), "revenue_per_client", "Reports revenue grouped by client.", ["umsatzliste"], null);

        var request = _provider.Requests.ShouldHaveSingleItem();
        request.Message.ShouldContain("revenue_per_client");
        request.Message.ShouldContain("de");
        request.Message.ShouldContain("Umsatzstatistik");
        request.Message.ShouldContain("umsatzliste");
        request.SystemPrompt.ShouldNotBeNullOrWhiteSpace();
    }

    // The failure of the previous round is the only thing that makes a second round different from a
    // repeat of the first.
    [Test]
    public async Task TheFailureOfThePreviousRound_ReachesTheNextPrompt()
    {
        _provider.Answering("{\"phrases\":[\"umsatz pro kunde\"]}");

        await _generator.GeneratePhrasesAsync(
            Cluster(), "revenue_per_client", "d", [], "list_clients was offered instead");

        _provider.Requests.ShouldHaveSingleItem().Message.ShouldContain("list_clients was offered instead");
    }

    [Test]
    public async Task PhrasesAreTrimmedDeduplicatedAndCappedAtTheRoundSize()
    {
        _provider.Answering(
            "{\"phrases\":[\"  umsatz pro kunde \",\"Umsatz pro Kunde\",\"kundenumsatz\",\"umsatzbericht\",\"jahresumsatz\"]}");

        var phrases = await _generator.GeneratePhrasesAsync(Cluster(), "revenue_per_client", "d", [], null);

        phrases.Count.ShouldBe(SkillLearningDefaults.PhraseVariantsPerRound);
        phrases[0].ShouldBe("umsatz pro kunde");
    }

    [Test]
    public async Task APhraseLongerThanAnExcerpt_IsDropped()
    {
        var sentence = new string('a', SkillLearningDefaults.MaxPhraseLength + 1);
        _provider.Answering("{\"phrases\":[\"" + sentence + "\",\"ok\",\"kundenumsatz\"]}");

        var phrases = await _generator.GeneratePhrasesAsync(Cluster(), "revenue_per_client", "d", [], null);

        phrases.ShouldHaveSingleItem().ShouldBe("kundenumsatz");
    }

    // Every provider in this project takes a cancellation token, and a background loop that ignores it
    // keeps a shutdown waiting on a network call.
    [Test]
    public async Task TheCancellationTokenIsPassedToTheProvider()
    {
        _provider.Answering("{\"phrases\":[\"kundenumsatz\"]}");
        using var source = new CancellationTokenSource();

        await _generator.GeneratePhrasesAsync(Cluster(), "s", "d", [], null, source.Token);

        _provider.LastCancellationTokenWasCancellable.ShouldBe(true);
    }
}
