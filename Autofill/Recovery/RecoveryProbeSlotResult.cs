// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: what the recovery probe found for one open slot under one ruleset.
/// </summary>
/// <param name="EngineClosed">True when the engine's first repair (nothing gated) covered the slot</param>
/// <param name="EngineMoves">Moves of that first repair; 0 when it left the slot open</param>
/// <param name="EngineRefused">True when the autofill filter refused that first repair</param>
/// <param name="LegalClosed">True when some round produced a chain the autofill filter admits</param>
/// <param name="LegalMoves">Moves of that admitted chain; 0 without one</param>
/// <param name="Verdict">Final text: "CLOSED moves=n after k rounds", "OPEN after k rounds ...", or "INCONCLUSIVE ..."</param>
public sealed record RecoveryProbeSlotResult(
    bool EngineClosed,
    int EngineMoves,
    bool EngineRefused,
    bool LegalClosed,
    int LegalMoves,
    string Verdict);
