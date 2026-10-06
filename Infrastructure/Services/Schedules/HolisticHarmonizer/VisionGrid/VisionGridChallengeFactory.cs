// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the Wizard 3 grid vision check: a random schedule in production size (16 employees x 31 days), drawn by
/// the production <see cref="HarmonyBitmapPngRenderer"/>, plus cell lookups by row initials and day number. Unlike
/// a big single token it fails models whose provider downscales the image until the 18 px cell letters blur.
/// Names are invented; the first names start with distinct letters so every row has unique initials.
/// </summary>
/// <param name="random">Source of the plan and the asked cells; seeded in tests and calibration</param>

using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Bitmap;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

public static class VisionGridChallengeFactory
{
    public const string FreeAnswer = "-";
    internal const int QuestionCount = 3;

    private const int RowCount = 16;
    private const int DayCount = 31;
    private const double FreeShare = 0.35;
    private const double BreakShare = 0.05;
    private const double LockedShare = 0.06;
    private const int MaxFreeQuestions = 1;
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static readonly string[] FirstNames =
    [
        "Anna", "Ben", "Clara", "David", "Eva", "Felix", "Gina", "Hans",
        "Ida", "Jan", "Kim", "Lea", "Max", "Nina", "Olaf", "Paula",
    ];

    private static readonly string[] LastNames =
    [
        "Keller", "Huber", "Meier", "Roth", "Brunner", "Wolf", "Frei", "Bader",
        "Graf", "Kunz", "Lang", "Moser", "Nef", "Ott", "Pfister", "Steiner",
    ];

    private static readonly CellSymbol[] WorkSymbols = [CellSymbol.Early, CellSymbol.Late, CellSymbol.Night, CellSymbol.Other];

    public static VisionGridChallenge Create(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        var rows = Enumerable.Range(0, RowCount)
            .Select(i => new BitmapAgent(
                i.ToString(CultureInfo.InvariantCulture),
                $"{FirstNames[i]} {LastNames[random.Next(LastNames.Length)]}",
                0m,
                new HashSet<CellSymbol>()))
            .ToList();
        var days = Enumerable.Range(0, DayCount).Select(FirstDay.AddDays).ToList();
        var cells = new Cell[RowCount, DayCount];
        for (var r = 0; r < RowCount; r++)
        {
            for (var d = 0; d < DayCount; d++)
            {
                cells[r, d] = RandomCell(random);
            }
        }

        var bitmap = new HarmonyBitmap(rows, days, cells);
        var questions = PickQuestions(random, bitmap);
        var png = new HarmonyBitmapPngRenderer().Render(bitmap);
        return new VisionGridChallenge(png, questions, BuildUserMessage(questions));
    }

    internal static string Initials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0]));
    }

    internal static string Answer(CellSymbol symbol) => symbol switch
    {
        CellSymbol.Early => "E",
        CellSymbol.Late => "L",
        CellSymbol.Night => "N",
        CellSymbol.Other => "O",
        CellSymbol.Break => "B",
        _ => FreeAnswer,
    };

    private static Cell RandomCell(Random random)
    {
        var roll = random.NextDouble();
        if (roll < FreeShare)
        {
            return Cell.Free();
        }

        if (roll < FreeShare + BreakShare)
        {
            return new Cell(CellSymbol.Break, null, [], true);
        }

        return new Cell(WorkSymbols[random.Next(WorkSymbols.Length)], null, [], random.NextDouble() < LockedShare);
    }

    private static List<VisionGridQuestion> PickQuestions(Random random, HarmonyBitmap bitmap)
    {
        var questions = new List<VisionGridQuestion>(QuestionCount);
        var usedRows = new HashSet<int>();
        var usedDays = new HashSet<int>();
        var freeQuestions = 0;
        while (questions.Count < QuestionCount)
        {
            var r = random.Next(bitmap.RowCount);
            var d = random.Next(bitmap.DayCount);
            if (usedRows.Contains(r) || usedDays.Contains(d))
            {
                continue;
            }

            var answer = Answer(bitmap.GetCell(r, d).Symbol);
            if (answer == FreeAnswer && freeQuestions >= MaxFreeQuestions)
            {
                continue;
            }

            freeQuestions += answer == FreeAnswer ? 1 : 0;
            usedRows.Add(r);
            usedDays.Add(d);
            questions.Add(new VisionGridQuestion(Initials(bitmap.Rows[r].DisplayName), bitmap.Days[d].Day, answer));
        }

        return questions;
    }

    private static string BuildUserMessage(IReadOnlyList<VisionGridQuestion> questions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Read these cells of the attached schedule image:");
        for (var i = 0; i < questions.Count; i++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. row {questions[i].RowLabel}, day {questions[i].Day}");
        }

        builder.Append("Reply with the JSON object only.");
        return builder.ToString();
    }
}
