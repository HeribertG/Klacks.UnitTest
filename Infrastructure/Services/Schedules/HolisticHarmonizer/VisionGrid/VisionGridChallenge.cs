// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

/// <summary>
/// A rendered random schedule plus the cell lookups the model has to answer from the image alone.
/// </summary>
/// <param name="Png">The schedule rendered by the production Wizard 3 renderer</param>
/// <param name="Questions">Cells to read back, with the expected answers</param>
/// <param name="UserMessage">The question text sent alongside the image</param>
public sealed record VisionGridChallenge(byte[] Png, IReadOnlyList<VisionGridQuestion> Questions, string UserMessage);
