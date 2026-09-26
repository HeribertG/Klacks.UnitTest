// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Golden-master tests for every macro script Klacks has shipped (MacrosSeed and the migrations that insert or rewrite
/// macro content): each script runs on all inputs of the regression grid through MacroScriptRunner, and its ordered raw
/// OUTPUT messages plus their aggregated reading must match the record taken before the OUTPUT stack fix, message by
/// message. The shipped scripts only OUTPUT at their end and never read a variable after an OUTPUT, so fixing the
/// interpreter must not change a single value. A readable sample of AllShift pins concrete messages next to the hashes.
/// </summary>

using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
public class SeededMacroOutputGoldenTests
{
    private const int ExpectedGridSize = 672;
    private const int ExpectedSeedRows = 8;
    private const string SeedKeyPrefix = "MacrosSeed:";

    private static IEnumerable<TestCaseData> ShippedScripts() =>
        SeededMacroScripts.ShippedScriptVariants().Select(variant =>
            new TestCaseData(variant.Key, variant.Content).SetName("Golden_" + variant.Key.Replace(':', '_')));

    [Test]
    public void TheGridHasTheSizeTheGoldenWasRecordedOn()
    {
        MacroRegressionGrid.Samples.Count.ShouldBe(ExpectedGridSize);
    }

    [Test]
    public void EveryShippedScriptHasAGoldenEntry_AndEveryGoldenEntryAShippedScript()
    {
        var keys = SeededMacroScripts.ShippedScriptVariants().Select(variant => variant.Key).ToList();

        keys.ShouldBeUnique();
        keys.OrderBy(key => key).ShouldBe(SeededMacroOutputGolden.Entries.Keys.OrderBy(key => key));
        keys.Count(key => key.StartsWith(SeedKeyPrefix, StringComparison.Ordinal)).ShouldBe(ExpectedSeedRows);
    }

    [TestCaseSource(nameof(ShippedScripts))]
    public void TheOutputOnTheWholeGrid_MatchesTheRecordTakenBeforeTheFix(string key, string content)
    {
        var expected = SeededMacroOutputGolden.Entries[key];

        var actual = MacroOutputGoldenRecorder.Record(content);

        actual.FailedRuns.ShouldBe(0);
        actual.FailedRuns.ShouldBe(expected.FailedRuns);
        actual.MessageCount.ShouldBe(expected.MessageCount);
        actual.ChannelSums.ShouldBe(expected.ChannelSums);
        actual.Hash.ShouldBe(expected.Hash);
    }

    [TestCase(0, new[] { "1:0", "10:0", "11:0", "12:0", "13:0", "14:0" })]
    [TestCase(400, new[] { "1:3.1", "10:0.1", "11:0", "12:0", "13:0", "14:3" })]
    [TestCase(504, new[] { "1:0.7", "10:0.7000000000000001", "11:0", "12:0", "13:0", "14:0" })]
    [TestCase(671, new[] { "1:10.3", "10:3.5", "11:0", "12:0", "13:6.800000000000001", "14:0" })]
    public void AllShift_EmitsTheRecordedMessagesInOrder(int sampleIndex, string[] expectedMessages)
    {
        var (compiled, error) = MacroScriptRunner.TryCompile(SeededMacroScripts.AllShiftScript());
        compiled.ShouldNotBeNull(error);

        var run = MacroScriptRunner.Run(compiled, MacroRegressionGrid.Samples[sampleIndex].Data, CancellationToken.None);

        run.IsCompleted.ShouldBeTrue(run.Error);
        MacroOutputGoldenRecorder.RawMessages(run).ShouldBe(expectedMessages);
    }
}
