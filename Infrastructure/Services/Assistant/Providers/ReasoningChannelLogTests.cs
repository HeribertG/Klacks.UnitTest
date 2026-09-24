// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// ReasoningChannelLog writes the reasoning text at Debug level only (it is chain-of-thought and may quote
/// user data) and raises a Warning with the finish_reason only when the call ended with reasoning but no
/// content and no tool call.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant.Providers.Shared;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Providers;

[TestFixture]
public class ReasoningChannelLogTests
{
    private const string Reasoning = "Should I call manage_pending_notes? No tools are available.";
    private const string FinishReason = "stop";

    [Test]
    public void ReasoningWithoutContent_WarnsWithTheFinishReasonAndKeepsTheTextAtDebug()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", Reasoning, true, FinishReason);

        var warning = logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        warning.Message.ShouldContain(FinishReason);
        warning.Message.ShouldNotContain(Reasoning);
        logger.Entries.Single(entry => entry.Level == LogLevel.Debug).Message.ShouldContain(Reasoning);
    }

    [Test]
    public void ReasoningBesideContent_LogsTheTextAtDebugOnlyAndDoesNotWarn()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", Reasoning, false, FinishReason);

        logger.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Information);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Debug);
    }

    [Test]
    public void NoReasoning_WritesNothing()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", string.Empty, false, FinishReason);

        logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public void LongReasoning_IsTruncatedInTheDebugEntry()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();
        var longReasoning = new string('x', 5000);

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", longReasoning, false, FinishReason);

        logger.Entries.Single().Message.ShouldNotContain(longReasoning);
    }
}
