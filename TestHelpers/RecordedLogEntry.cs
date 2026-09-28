// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

public sealed record RecordedLogEntry(LogLevel Level, string Message, Exception? Exception);
