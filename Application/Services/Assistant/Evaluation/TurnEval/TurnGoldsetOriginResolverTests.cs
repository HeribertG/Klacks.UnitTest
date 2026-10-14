// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the split between handwritten and synthetic goldset items by the Source field, including the cases
/// that must not be silently counted as handwritten (missing or unknown source).
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnGoldsetOriginResolverTests
{
    [TestCase("curated", TurnGoldsetOrigin.Handwritten)]
    [TestCase("CURATED", TurnGoldsetOrigin.Handwritten)]
    [TestCase("w0.5-seed-coverage", TurnGoldsetOrigin.Synthetic)]
    [TestCase("w0.5-recipe", TurnGoldsetOrigin.Synthetic)]
    [TestCase("w0.5-hijack", TurnGoldsetOrigin.Synthetic)]
    [TestCase("w0.5-live", TurnGoldsetOrigin.Synthetic)]
    [TestCase("paraphrase", TurnGoldsetOrigin.Synthetic)]
    [TestCase("translation", TurnGoldsetOrigin.Synthetic)]
    [TestCase(null, TurnGoldsetOrigin.Unknown)]
    [TestCase("", TurnGoldsetOrigin.Unknown)]
    [TestCase("   ", TurnGoldsetOrigin.Unknown)]
    [TestCase("something-new", TurnGoldsetOrigin.Unknown)]
    public void Resolve_MapsTheSourceToItsOrigin(string? source, TurnGoldsetOrigin expected)
    {
        TurnGoldsetOriginResolver.Resolve(source).ShouldBe(expected);
    }
}
