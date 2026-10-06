// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Judges one answer to the Wizard 3 grid vision check. Passed only when every asked cell is read correctly: a model
/// that does not see the image guesses all cells with probability (1/6)^3. Answers are tolerant to case and short
/// descriptions ("e", "B (hatched)", "free"). Provider errors and answers swallowed by reasoning are inconclusive,
/// like in the single-token check.
/// </summary>
/// <param name="response">The provider response to the grid request</param>
/// <param name="challenge">The challenge that was sent, carrying the expected answers</param>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

public static class VisionGridResponseEvaluator
{
    private const string AnswersProperty = "answers";
    private const string CellLetters = "ELNOB";
    private const int ResponsePreviewLength = 120;
    private const string PreviewEllipsis = "...";
    private const string ProviderRejectedError = "Provider rejected the request.";
    private const string NoVisibleAnswerError = "Model returned no visible answer (output budget likely consumed by internal reasoning).";
    private const string NoAnswersError = "Model returned no answers - it does not see the attached image.";
    private const string NoJsonErrorFormat = "Model answered without the answers JSON. Preview: {0}";
    private const string WrongCellsErrorFormat = "Model read {0} of {1} schedule cells correctly ({2}).";

    private static readonly HashSet<string> FreeSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        VisionGridChallengeFactory.FreeAnswer, "", "FREE", "NONE", "EMPTY", "WHITE", "BLANK",
    };

    public static VisionGridVerdict Evaluate(LLMProviderResponse response, VisionGridChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(challenge);

        if (!response.Success)
        {
            return new VisionGridVerdict(VisionCapabilityOutcome.Inconclusive, 0, response.Error ?? ProviderRejectedError);
        }

        var content = response.Content ?? string.Empty;
        var answers = TryReadAnswers(content);
        if (answers is null)
        {
            return string.IsNullOrWhiteSpace(content) || response.ReasoningWithoutContent || response.OutputTruncated
                ? new VisionGridVerdict(VisionCapabilityOutcome.Inconclusive, 0, NoVisibleAnswerError)
                : new VisionGridVerdict(VisionCapabilityOutcome.Misread, 0, string.Format(NoJsonErrorFormat, Preview(content)));
        }

        if (answers.Count == 0)
        {
            return new VisionGridVerdict(VisionCapabilityOutcome.Misread, 0, NoAnswersError);
        }

        var questions = challenge.Questions;
        var correct = 0;
        var details = new List<string>(questions.Count);
        for (var i = 0; i < questions.Count; i++)
        {
            var given = i < answers.Count ? Normalise(answers[i]) : null;
            var isCorrect = string.Equals(given, questions[i].Expected, StringComparison.Ordinal);
            correct += isCorrect ? 1 : 0;
            details.Add($"{questions[i].RowLabel}/{questions[i].Day}: expected {questions[i].Expected}, got {given ?? "nothing"}");
        }

        return correct == questions.Count
            ? new VisionGridVerdict(VisionCapabilityOutcome.Passed, correct, null)
            : new VisionGridVerdict(
                VisionCapabilityOutcome.Misread,
                correct,
                string.Format(WrongCellsErrorFormat, correct, questions.Count, string.Join("; ", details)));
    }

    internal static string? Normalise(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim().Trim('"', '\'', '.', '`');
        if (FreeSynonyms.Contains(trimmed))
        {
            return VisionGridChallengeFactory.FreeAnswer;
        }

        var first = char.ToUpperInvariant(trimmed[0]);
        var standsAlone = trimmed.Length == 1 || !char.IsLetter(trimmed[1]);
        return standsAlone && CellLetters.Contains(first) ? first.ToString() : null;
    }

    private static List<string?>? TryReadAnswers(string content)
    {
        var json = HarmonyJsonParser.ExtractJsonObject(content);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(AnswersProperty, out var answersElement)
                || answersElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return answersElement.EnumerateArray()
                .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : a.ToString())
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Preview(string content) =>
        content.Length > ResponsePreviewLength ? content[..ResponsePreviewLength] + PreviewEllipsis : content;
}
