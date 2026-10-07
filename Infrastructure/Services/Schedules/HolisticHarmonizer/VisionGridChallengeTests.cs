// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;
using Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class VisionGridChallengeTests
{
    private const int HeaderSize = 32;
    private const int CellSize = 24;
    private const int CellInset = 4;
    private const int ProductionWidth = HeaderSize + (31 * CellSize);
    private const int ProductionHeight = HeaderSize + (16 * CellSize);

    [Test]
    public void Create_RendersPlanInProductionSize()
    {
        var challenge = VisionGridChallengeFactory.Create(new Random(1));

        using var bitmap = SKBitmap.Decode(challenge.Png);
        bitmap.Width.ShouldBe(ProductionWidth);
        bitmap.Height.ShouldBe(ProductionHeight);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(42)]
    public void Create_AsksThreeCellsInDistinctRowsAndDaysWithAtMostOneFree(int seed)
    {
        var challenge = VisionGridChallengeFactory.Create(new Random(seed));

        challenge.Questions.Count.ShouldBe(VisionGridChallengeFactory.QuestionCount);
        challenge.Questions.Select(q => q.RowLabel).Distinct().Count().ShouldBe(challenge.Questions.Count);
        challenge.Questions.Select(q => q.Day).Distinct().Count().ShouldBe(challenge.Questions.Count);
        challenge.Questions.Count(q => q.Expected == VisionGridChallengeFactory.FreeAnswer).ShouldBeLessThanOrEqualTo(1);
        challenge.Questions.ShouldAllBe(q => q.Day >= 1 && q.Day <= 31 && "ELNOB-".Contains(q.Expected));
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(13)]
    [TestCase(21)]
    public void Create_ExpectedAnswersMatchThePaintedCellColours(int seed)
    {
        AssertAnswersMatchPixels(VisionGridChallengeFactory.Create(new Random(seed)), 1);
    }

    [TestCase(3)]
    [TestCase(8)]
    public void Create_UpscaledWithMarkers_ExpectedAnswersMatchThePaintedCellColours(int seed)
    {
        var challenge = VisionGridChallengeFactory.Create(new Random(seed), 2f, markAskedCells: true);

        AssertAnswersMatchPixels(challenge, 2);
        challenge.UserMessage.ShouldContain("the cell marked 1");
    }

    private static void AssertAnswersMatchPixels(VisionGridChallenge challenge, int scale)
    {
        using var bitmap = SKBitmap.Decode(challenge.Png);
        bitmap.Width.ShouldBe(ProductionWidth * scale);

        foreach (var question in challenge.Questions)
        {
            var row = question.RowLabel[0] - 'A';
            var x = (scale * (HeaderSize + ((question.Day - 1) * CellSize))) + (CellInset * scale) - 1;
            var y = (scale * (HeaderSize + (row * CellSize))) + (CellInset * scale) - 1;
            var pixel = bitmap.GetPixel(x, y);

            var expectedColours = question.Expected switch
            {
                "E" => new[] { new SKColor(0xFF, 0xD7, 0x00) },
                "L" => [new SKColor(0xFF, 0x8C, 0x00)],
                "N" => [new SKColor(0x1E, 0x3A, 0x8A)],
                "O" => [new SKColor(0x4B, 0x55, 0x63)],
                "-" => [SKColors.White, new SKColor(0xF5, 0xF5, 0xDC)],
                _ => [],
            };
            if (expectedColours.Length > 0)
            {
                expectedColours.ShouldContain(pixel, $"{question.RowLabel}/{question.Day} expected {question.Expected}");
            }
        }
    }

    [Test]
    public void Create_SameSeed_IsReproducible()
    {
        var first = VisionGridChallengeFactory.Create(new Random(9));
        var second = VisionGridChallengeFactory.Create(new Random(9));

        second.Questions.ShouldBe(first.Questions);
        second.Png.SequenceEqual(first.Png).ShouldBeTrue();
    }

    [Test]
    public void Create_UserMessageNamesEveryAskedCell()
    {
        var challenge = VisionGridChallengeFactory.Create(new Random(5));

        foreach (var question in challenge.Questions)
        {
            challenge.UserMessage.ShouldContain($"row {question.RowLabel}, day {question.Day}");
        }
    }

    [Test]
    public void Evaluate_AllCellsCorrect_Passes()
    {
        var challenge = Challenge("E", "-", "B");

        var verdict = VisionGridResponseEvaluator.Evaluate(Answer("{\"answers\":[\"e\",\"free\",\"B (hatched)\"]}"), challenge);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Passed);
        verdict.CorrectAnswers.ShouldBe(3);
    }

    [Test]
    public void Evaluate_OneCellWrong_IsMisreadWithCount()
    {
        var challenge = Challenge("E", "L", "N");

        var verdict = VisionGridResponseEvaluator.Evaluate(Answer("{\"answers\":[\"E\",\"O\",\"N\"]}"), challenge);

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Misread);
        verdict.CorrectAnswers.ShouldBe(2);
        verdict.Error!.ShouldContain("2 of 3");
    }

    [Test]
    public void Evaluate_TooFewAnswers_CountsMissingAsWrong()
    {
        var verdict = VisionGridResponseEvaluator.Evaluate(Answer("{\"answers\":[\"E\"]}"), Challenge("E", "L", "N"));

        verdict.Outcome.ShouldBe(VisionCapabilityOutcome.Misread);
        verdict.CorrectAnswers.ShouldBe(1);
    }

    [Test]
    public void Evaluate_EmptyAnswers_IsMisread()
    {
        VisionGridResponseEvaluator.Evaluate(Answer("{\"answers\":[]}"), Challenge("E", "L", "N")).Outcome
            .ShouldBe(VisionCapabilityOutcome.Misread);
    }

    [Test]
    public void Evaluate_Prose_IsMisread()
    {
        VisionGridResponseEvaluator.Evaluate(Answer("I cannot view images."), Challenge("E", "L", "N")).Outcome
            .ShouldBe(VisionCapabilityOutcome.Misread);
    }

    [Test]
    public void Evaluate_EmptyContent_IsInconclusive()
    {
        VisionGridResponseEvaluator.Evaluate(Answer(string.Empty), Challenge("E", "L", "N")).Outcome
            .ShouldBe(VisionCapabilityOutcome.Inconclusive);
    }

    [Test]
    public void Evaluate_ProviderError_IsInconclusive()
    {
        var response = new LLMProviderResponse { Success = false, Error = "429 rate limit" };

        VisionGridResponseEvaluator.Evaluate(response, Challenge("E", "L", "N")).Outcome
            .ShouldBe(VisionCapabilityOutcome.Inconclusive);
    }

    [TestCase("E", "E")]
    [TestCase(" l ", "L")]
    [TestCase("\"N\"", "N")]
    [TestCase("O.", "O")]
    [TestCase("B (red hatched)", "B")]
    [TestCase("-", "-")]
    [TestCase("Free", "-")]
    [TestCase("", "-")]
    [TestCase("Early", null)]
    [TestCase("X", null)]
    public void Normalise_MapsAnswerVariants(string raw, string? expected)
    {
        VisionGridResponseEvaluator.Normalise(raw).ShouldBe(expected);
    }

    private static VisionGridChallenge Challenge(params string[] expected) =>
        new([1], expected.Select((e, i) => new VisionGridQuestion($"R{i}", i + 1, e)).ToList(), "question");

    private static LLMProviderResponse Answer(string content) => new() { Success = true, Content = content };
}
