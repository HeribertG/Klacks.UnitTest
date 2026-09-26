// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Runs a macro script on every input of <see cref="MacroRegressionGrid"/> through <see cref="MacroScriptRunner"/>, the way
/// the regression check reads it, and condenses the result into a <see cref="MacroOutputGoldenEntry"/>. The canonical
/// record of one input holds whether the run completed, its error, the raw OUTPUT messages in emission order and their
/// <see cref="MacroResultAggregator"/> reading (result value and typed surcharges), so any change of a single message,
/// of the message order or of the aggregation changes the hash.
/// </summary>
/// <param name="content">The macro script to record</param>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

public static class MacroOutputGoldenRecorder
{
    private const string Completed = "C";
    private const string Failed = "F";
    private const string FieldSeparator = "|";
    private const string ItemSeparator = ";";
    private const string PairSeparator = ":";
    private const string ChannelSumSeparator = "=";
    private const string NoValue = "-";
    private const char RecordSeparator = '\n';

    public static MacroOutputGoldenEntry Record(string content)
    {
        var (compiled, compileError) = MacroScriptRunner.TryCompile(content);
        if (compiled == null)
        {
            throw new InvalidOperationException(compileError);
        }

        var canonical = new StringBuilder();
        var failedRuns = 0;
        var messageCount = 0;
        var channelSums = new SortedDictionary<int, decimal>();
        var samples = MacroRegressionGrid.Samples;

        for (var index = 0; index < samples.Count; index++)
        {
            var run = MacroScriptRunner.Run(compiled, samples[index].Data, CancellationToken.None);
            if (!run.IsCompleted)
            {
                failedRuns++;
            }

            var messages = run.Messages ?? [];
            messageCount += messages.Count;
            foreach (var message in messages)
            {
                if (decimal.TryParse(message.Message, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    channelSums[message.Type] = channelSums.GetValueOrDefault(message.Type) + value;
                }
            }

            canonical.Append(Canonical(index, run)).Append(RecordSeparator);
        }

        return new MacroOutputGoldenEntry(
            Hash(canonical.ToString()),
            failedRuns,
            messageCount,
            string.Join(ItemSeparator, channelSums.Select(sum =>
                sum.Key.ToString(CultureInfo.InvariantCulture) + ChannelSumSeparator + sum.Value.ToString(CultureInfo.InvariantCulture))));
    }

    public static IReadOnlyList<string> RawMessages(MacroScriptRun run) =>
        (run.Messages ?? []).Select(Describe).ToList();

    private static string Canonical(int index, MacroScriptRun run)
    {
        var messages = run.Messages ?? [];
        var aggregated = MacroResultAggregator.Aggregate(messages);
        return string.Join(
            FieldSeparator,
            index.ToString(CultureInfo.InvariantCulture),
            run.IsCompleted ? Completed : Failed,
            run.Error ?? string.Empty,
            string.Join(ItemSeparator, messages.Select(Describe)),
            aggregated.ResultValue?.ToString(CultureInfo.InvariantCulture) ?? NoValue,
            string.Join(ItemSeparator, aggregated.Surcharges.Select(item =>
                item.Type + PairSeparator + item.Amount.ToString(CultureInfo.InvariantCulture))));
    }

    private static string Describe(ResultMessage message) =>
        message.Type.ToString(CultureInfo.InvariantCulture) + PairSeparator + message.Message;

    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
