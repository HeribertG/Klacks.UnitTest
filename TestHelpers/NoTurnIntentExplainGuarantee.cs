// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.TestHelpers;

public sealed class NoTurnIntentExplainGuarantee : ITurnIntentExplainGuarantee
{
    public Task<IReadOnlyList<string>> ResolveAsync(SkillToolsetGuaranteeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
