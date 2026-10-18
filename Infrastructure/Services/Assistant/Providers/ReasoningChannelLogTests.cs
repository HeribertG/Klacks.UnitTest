// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ReasoningChannelLog writes only the reasoning length at Debug level, never the text (it is chain-of-thought and
/// may quote user data; owner decision 2026-10-18), and raises a Warning with the finish_reason only when the call ended with reasoning but no
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
    public void ReasoningWithoutContent_WarnsWithTheFinishReasonAndLogsOnlyTheLengthAtDebug()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", Reasoning, true, FinishReason);

        var warning = logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        warning.Message.ShouldContain(FinishReason);
        warning.Message.ShouldNotContain(Reasoning);
        var debug = logger.Entries.Single(entry => entry.Level == LogLevel.Debug).Message;
        debug.ShouldNotContain(Reasoning);
        debug.ShouldContain(Reasoning.Length.ToString());
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
    public void LongReasoning_IsNotWrittenToTheDebugEntry()
    {
        var logger = new RecordingLogger<ReasoningChannelLogTests>();
        var longReasoning = new string('x', 5000);

        ReasoningChannelLog.Write(logger, "DeepSeek", "deepseek-v4-pro", longReasoning, false, FinishReason);

        logger.Entries.Single().Message.ShouldNotContain("xxx");
    }
}
