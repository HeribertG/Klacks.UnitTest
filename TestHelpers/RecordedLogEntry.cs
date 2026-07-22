// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

public sealed record RecordedLogEntry(LogLevel Level, string Message, Exception? Exception);
