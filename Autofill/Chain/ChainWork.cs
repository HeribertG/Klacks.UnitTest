// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// One work of the chain: the Wizard 1 token under a stable work id. The bitmap stages move a cell together with its
/// work ids (like production re-points a saved work), so the id maps every cell of a later stage back to its work.
/// </summary>
/// <param name="WorkId">Stable id of the work over all stages</param>
/// <param name="Token">The work as the analyzer reads it; AgentId is the holder in the current stage</param>
public sealed record ChainWork(Guid WorkId, CoreToken Token);
