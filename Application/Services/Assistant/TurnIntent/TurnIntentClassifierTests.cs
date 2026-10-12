// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

[TestFixture]
public class TurnIntentClassifierTests
{
    private const string Message = "Wie lege ich einen Vertrag an?";

    private IEmbeddingProvider _embedding = null!;
    private ITurnIntentHeadProvider _heads = null!;

    [SetUp]
    public void SetUp()
    {
        _embedding = Substitute.For<IEmbeddingProvider>();
        _embedding.EmbeddingSpaceId.Returns(TurnIntentHeadTestFactory.SpaceId);
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TurnIntentHeadTestFactory.AskEmbedding);

        _heads = Substitute.For<ITurnIntentHeadProvider>();
        _heads.Current.Returns(TurnIntentHeadTestFactory.Create(
            TurnIntentHeadTestFactory.Verdict("de", true), TurnIntentHeadTestFactory.Verdict("fr", false)));
    }

    [Test]
    public async Task ConfidentAsk_InAnEnabledLocale_MayAct()
    {
        var prediction = await Create().ClassifyAsync(Message, "de-CH", CancellationToken.None);

        prediction.ShouldNotBeNull();
        prediction.Label.ShouldBe(TurnIntentLabel.Ask);
        prediction.IsConfidentAsk.ShouldBeTrue();
        prediction.Locale.ShouldBe("de");
        prediction.MayActOnAsk.ShouldBeTrue();
    }

    [Test]
    public async Task ConfidentAsk_InADisabledLocale_MayNotAct()
    {
        var prediction = await Create().ClassifyAsync(Message, "fr", CancellationToken.None);

        prediction!.IsConfidentAsk.ShouldBeTrue();
        prediction.LocaleEnabled.ShouldBeFalse();
        prediction.MayActOnAsk.ShouldBeFalse();
    }

    [Test]
    public async Task UnknownLanguage_IsNeverEnabled()
    {
        var prediction = await Create().ClassifyAsync(Message, "ko", CancellationToken.None);

        prediction!.Locale.ShouldBeNull();
        prediction.MayActOnAsk.ShouldBeFalse();
    }

    [Test]
    public async Task ModeOff_ClassifiesNothing()
    {
        var prediction = await Create(TurnIntentClassifierModes.Off).ClassifyAsync(Message, "de", CancellationToken.None);

        prediction.ShouldBeNull();
        await _embedding.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingHead_ClassifiesNothing()
    {
        _heads.Current.Returns((TurnIntentHeadWeights?)null);

        (await Create().ClassifyAsync(Message, "de", CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task ForeignEmbeddingSpace_ClassifiesNothing()
    {
        _embedding.EmbeddingSpaceId.Returns("openai:text-embedding-3-small@768");

        (await Create().ClassifyAsync(Message, "de", CancellationToken.None)).ShouldBeNull();
        await _embedding.DidNotReceive().EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EmbeddingFailure_ClassifiesNothing()
    {
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<float[]>(_ => throw new InvalidOperationException("model not loaded"));

        (await Create().ClassifyAsync(Message, "de", CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task SlowEmbedding_TimesOutToNoClassification()
    {
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<float[]>().Task);

        (await Create(TurnIntentClassifierModes.Active, timeoutMilliseconds: 20)
            .ClassifyAsync(Message, "de", CancellationToken.None)).ShouldBeNull();
    }

    private TurnIntentClassifier Create(string mode = TurnIntentClassifierModes.Active, int timeoutMilliseconds = 1000) =>
        new(_embedding, _heads, new TurnIntentClassifierOptions(mode, timeoutMilliseconds),
            Substitute.For<ILogger<TurnIntentClassifier>>());
}
