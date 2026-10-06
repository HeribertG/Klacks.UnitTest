// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class VisionCapabilityResponseEvaluatorTests
{
    private const string Token = "KXN";

    [TestCase("{\"token\":\"KXN\"}")]
    [TestCase("{\"token\":\"kxn\"}")]
    [TestCase("{\"token\":\"K X N\"}")]
    [TestCase("```json\n{\"token\":\"KXN\"}\n```")]
    public void Evaluate_JsonWithExpectedToken_Passes(string content)
    {
        var verdict = VisionCapabilityResponseEvaluator.Evaluate(Answer(content), Token);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Passed);
        verdict.Error.ShouldBeNull();
    }

    [TestCase("KXN")]
    [TestCase("The token is KXN.")]
    [TestCase("I can read the letters KXN in the yellow box")]
    public void Evaluate_PlainTextNamingOnlyTheToken_Passes(string content)
    {
        VisionCapabilityResponseEvaluator.Evaluate(Answer(content), Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Passed);
    }

    [Test]
    public void Evaluate_PlainTextWithOtherUpperCaseWord_PassesWhenAlphabetExcludesIt()
    {
        var response = Answer("The PNG shows KXN");

        VisionCapabilityResponseEvaluator.Evaluate(response, Token, "EFHKLNPTXZ").Outcome
            .ShouldBe(VisionCapabilityOutcome.Passed);
    }

    [Test]
    public void Evaluate_PlainTextWithSeveralCandidateWords_IsMisread()
    {
        var verdict = VisionCapabilityResponseEvaluator.Evaluate(Answer("Either KXN or EFH, not sure"), Token);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Misread);
    }

    [Test]
    public void Evaluate_JsonWithWrongToken_IsMisreadAndNamesBothTokens()
    {
        var verdict = VisionCapabilityResponseEvaluator.Evaluate(Answer("{\"token\":\"KXH\"}"), Token);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Misread);
        verdict.Error!.ShouldContain("KXH");
        verdict.Error!.ShouldContain(Token);
    }

    [Test]
    public void Evaluate_JsonWithEmptyToken_IsMisread()
    {
        VisionCapabilityResponseEvaluator.Evaluate(Answer("{\"token\":\"\"}"), Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Misread);
    }

    [Test]
    public void Evaluate_ProseWithoutToken_IsMisread()
    {
        VisionCapabilityResponseEvaluator.Evaluate(Answer("I cannot view images."), Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Misread);
    }

    [Test]
    public void Evaluate_ProviderError_IsInconclusive()
    {
        var response = new LLMProviderResponse { Success = false, Error = "HTTP 529 overloaded" };

        var verdict = VisionCapabilityResponseEvaluator.Evaluate(response, Token);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Inconclusive);
        verdict.Error.ShouldBe("HTTP 529 overloaded");
    }

    [Test]
    public void Evaluate_EmptyContent_IsInconclusive()
    {
        VisionCapabilityResponseEvaluator.Evaluate(Answer(string.Empty), Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Inconclusive);
    }

    [Test]
    public void Evaluate_AnswerCutOffByOutputLimit_IsInconclusive()
    {
        var response = Answer("{\"token\":\"K");
        response.OutputTruncated = true;
        response.ReasoningTokens = 1000;

        VisionCapabilityResponseEvaluator.Evaluate(response, Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Inconclusive);
    }

    [Test]
    public void Evaluate_ReasoningWithoutContent_IsInconclusive()
    {
        var response = Answer("Let me think about the image");
        response.ReasoningWithoutContent = true;

        VisionCapabilityResponseEvaluator.Evaluate(response, Token).Outcome
            .ShouldBe(VisionCapabilityOutcome.Inconclusive);
    }

    private static LLMProviderResponse Answer(string content) =>
        new() { Success = true, Content = content };
}
