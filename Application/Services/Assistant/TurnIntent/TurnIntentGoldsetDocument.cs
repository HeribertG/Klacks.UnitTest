// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

public sealed class TurnIntentGoldsetDocument
{
    public int Version { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string LabelRules { get; init; } = string.Empty;
    public List<TurnIntentGoldsetItem> Items { get; init; } = [];
}
