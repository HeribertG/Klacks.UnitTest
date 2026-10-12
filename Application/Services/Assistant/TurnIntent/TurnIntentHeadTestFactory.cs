// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Assistant.TurnIntent;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

public static class TurnIntentHeadTestFactory
{
    public const string SpaceId = "onnx:test@2";
    public const int EmbeddingDimension = 2;

    public static TurnIntentHeadWeights Create(params TurnIntentLocaleVerdict[] locales)
    {
        var columns = EmbeddingDimension + TurnIntentHandFeatures.Names.Length;
        var means = new double[columns];
        var scales = Enumerable.Repeat(1.0, columns).ToArray();
        var ask = new double[columns];
        var doRow = new double[columns];
        var other = new double[columns];
        ask[0] = 10.0;
        doRow[1] = 10.0;

        return new TurnIntentHeadWeights
        {
            Version = 1,
            EmbeddingSpaceId = SpaceId,
            Classes = Enum.GetNames<TurnIntentLabel>(),
            HandFeatureNames = TurnIntentHandFeatures.Names,
            Means = means,
            Scales = scales,
            Weights = [ask, doRow, other],
            Bias = [0.0, 0.0, 0.0],
            Temperature = 1.0,
            AskThreshold = 0.8,
            Locales = [.. locales],
        };
    }

    public static float[] AskEmbedding => [1f, 0f];

    public static float[] DoEmbedding => [0f, 1f];

    public static TurnIntentLocaleVerdict Verdict(string locale, bool enabled) =>
        new(locale, enabled, 100, 40, 38, 10, 30, 29, 40, 20, 18, 30, 3, 3);
}
