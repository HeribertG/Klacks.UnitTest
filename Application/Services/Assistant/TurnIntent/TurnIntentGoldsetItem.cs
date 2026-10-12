// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

public sealed class TurnIntentGoldsetItem
{
    public string Id { get; init; } = string.Empty;
    public string TemplateId { get; init; } = string.Empty;
    public string Locale { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? Subkind { get; init; }
    public string? PairId { get; init; }
    public string? Note { get; init; }
    public bool Ambiguous { get; init; }
}
