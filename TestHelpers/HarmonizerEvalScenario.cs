// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// One in-memory scenario of <see cref="HarmonizerEvalScenarioFactory"/>.
/// </summary>
/// <param name="Name">Stable scenario label used in assertion messages.</param>
/// <param name="Input">Fully in-memory bitmap input (agents + assignments); never touches the database.</param>
public sealed record HarmonizerEvalScenario(string Name, BitmapInput Input);
