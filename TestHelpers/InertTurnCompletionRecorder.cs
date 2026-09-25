// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Builds a real TurnCompletionRecorder whose collaborators are all null, for LLMService tests that never
/// look at what a turn persists. A turn that reaches the persistence tail then fails inside the recorder's
/// own error handling, which logs and swallows it - exactly what the same null collaborators did when the
/// tail was inline in LLMService.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

internal static class InertTurnCompletionRecorder
{
    internal static TurnCompletionRecorder Create() => new(
        Substitute.For<ILogger<TurnCompletionRecorder>>(), null!, null!, null!, null!, new TurnRunState());
}
