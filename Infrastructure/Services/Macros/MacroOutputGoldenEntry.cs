// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The recorded output of one macro script on the whole regression grid.
/// </summary>
/// <param name="Hash">SHA-256 over the canonical record of every input: completion, error, the ordered raw OUTPUT messages and their aggregated reading</param>
/// <param name="FailedRuns">Number of grid inputs on which the script did not complete</param>
/// <param name="MessageCount">Number of OUTPUT messages the script emitted over the whole grid</param>
/// <param name="ChannelSums">Sum of the numeric message values per channel over the whole grid, as readable evidence next to the hash</param>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

public record MacroOutputGoldenEntry(string Hash, int FailedRuns, int MessageCount, string ChannelSums);
