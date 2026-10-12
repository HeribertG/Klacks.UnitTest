// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnIntentHeadTests
{
    [Test]
    public void Probabilities_SumToOne_AndFollowTheWeights()
    {
        var head = TurnIntentHeadTestFactory.Create();
        var hand = new double[TurnIntentHandFeatures.Names.Length];

        var ask = TurnIntentHead.Probabilities(head, TurnIntentHeadTestFactory.AskEmbedding, hand);
        var doing = TurnIntentHead.Probabilities(head, TurnIntentHeadTestFactory.DoEmbedding, hand);

        ask.Sum().ShouldBe(1.0, 1e-9);
        ask[(int)TurnIntentLabel.Ask].ShouldBeGreaterThan(0.99);
        doing[(int)TurnIntentLabel.Do].ShouldBeGreaterThan(0.99);
    }

    [Test]
    public void Probabilities_ApplyStandardisationAndTemperature()
    {
        var head = TurnIntentHeadTestFactory.Create();
        head.Means[0] = 1.0;
        var hand = new double[TurnIntentHandFeatures.Names.Length];

        var centred = TurnIntentHead.Probabilities(head, TurnIntentHeadTestFactory.AskEmbedding, hand);

        centred[(int)TurnIntentLabel.Ask].ShouldBe(1.0 / 3.0, 1e-9);
    }

    [Test]
    public void Probabilities_RejectAColumnCountMismatch()
    {
        var head = TurnIntentHeadTestFactory.Create();

        Should.Throw<InvalidOperationException>(() =>
            TurnIntentHead.Probabilities(head, [1f, 0f, 0f], new double[TurnIntentHandFeatures.Names.Length]));
    }

    [Test]
    public void HandFeatures_HaveOneColumnPerName_AndExactlyOneTokenBucket()
    {
        var features = TurnIntentHandFeatures.Extract("Wie erstelle ich einen Vertrag?");

        features.Length.ShouldBe(TurnIntentHandFeatures.Names.Length);
        features[0].ShouldBe(1.0);
        features[1].ShouldBe(1.0);
        features.Skip(8).Sum().ShouldBe(1.0);
    }
}
