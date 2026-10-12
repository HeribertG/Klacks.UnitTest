// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Reads the turn-intent labelset (turn-intent-v1.json) from the source tree the tests were built from, and the
/// message set of the turn-selection goldset it must stay disjoint from.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

public static class TurnIntentGoldsetReader
{
    public const string TurnIntentFileName = "turn-intent-v1.json";
    public const string TurnSelectionFileName = "turn-selection-v1.json";
    public const string ExpectedKind = "turn-intent";

    private const string ItemsProperty = "items";
    private const string MessageProperty = "message";

    private static readonly string[] GoldsetRelativePath = ["Klacks.Api", "Application", "Skills", "Goldsets"];

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public static TurnIntentGoldsetDocument Load()
    {
        var path = RepositoryRootLocator.RequireFile([.. GoldsetRelativePath, TurnIntentFileName]);
        return JsonSerializer.Deserialize<TurnIntentGoldsetDocument>(File.ReadAllText(path), SerializerOptions)
               ?? throw new InvalidDataException($"{TurnIntentFileName} is empty.");
    }

    public static IReadOnlyList<TurnIntentGoldsetItem> LoadTrainable() =>
        [.. Load().Items.Where(item => !item.Ambiguous)];

    public static HashSet<string> LoadTurnSelectionMessages()
    {
        var path = RepositoryRootLocator.RequireFile([.. GoldsetRelativePath, TurnSelectionFileName]);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty(ItemsProperty).EnumerateArray()
            .Select(item => item.GetProperty(MessageProperty).GetString() ?? string.Empty)
            .Select(Normalize)];
    }

    public static string Normalize(string text) => text.Trim().ToLowerInvariant();
}
