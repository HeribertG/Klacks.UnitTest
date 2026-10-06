// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

/// <summary>
/// One cell lookup of the Wizard 3 grid vision check.
/// </summary>
/// <param name="RowLabel">Initials printed in the row header of the asked cell</param>
/// <param name="Day">Day number printed in the column header of the asked cell</param>
/// <param name="Expected">The letter painted in the cell (E, L, N, O, B) or "-" for a free cell</param>
public sealed record VisionGridQuestion(string RowLabel, int Day, string Expected);
