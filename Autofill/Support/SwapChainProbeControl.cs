// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// One constellation of the swap-chain probe's positive control: employee A gave up shift X and took over shift Y of
/// employee B on the same day, so X is open and two moves restore the engine's plan.
/// </summary>
/// <param name="Plan">Plan with X open</param>
/// <param name="Opened">Shift X as the engine's plan held it, with A as its employee</param>
/// <param name="Taken">Shift Y as the engine's plan held it, with B as its employee</param>
/// <param name="Moved">Shift Y as the control plan holds it, with A as its employee</param>
public sealed record SwapChainProbeControl(CoreScenario Plan, CoreToken Opened, CoreToken Taken, CoreToken Moved);
