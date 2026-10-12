// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;

namespace Klacks.UnitTest.TestHelpers;

public sealed class NoTurnIntentExplainGuarantee : ITurnIntentExplainGuarantee
{
    public Task<TurnIntentPrediction?>? Start(
        string userMessage, string? language, IReadOnlyCollection<string>? pinnedSkillNames, CancellationToken cancellationToken) => null;

    public Task<TurnIntentGuaranteeOutcome> ResolveAsync(SkillToolsetGuaranteeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(TurnIntentGuaranteeOutcome.None);
}
