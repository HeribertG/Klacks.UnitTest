// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: the autofill filter's verdict on one replayed recovery proposal.
/// </summary>
/// <param name="IsOk">True when no touched person got a refusal the plan did not already have</param>
/// <param name="Text">"ok", the refused assignments with the refusing rule, or why the moves could not be replayed</param>
/// <param name="RefusedAgentIds">Context agent ids that got a new refusal; empty when ok or not replayable</param>
public sealed record RecoveryProbeRecheckResult(bool IsOk, string Text, IReadOnlySet<string> RefusedAgentIds);
