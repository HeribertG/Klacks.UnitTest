// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ConversationSignalsPluginLoader.Install is the one wiring of read signals into the process, shared by Load at startup and
/// by the unit tests. It is run here on synthetic vocabulary no shipped pack contains, so that every static detector, the
/// core refusal vocabulary and the collision holder are each proven to be fed, and Load is run end to end against a
/// temporary application directory with a relocated pack directory. All process-wide state is put back afterwards.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class ConversationSignalsPluginLoaderInstallTests
{
    private const string CoreRefusal = "zzcoreno";
    private const string PackYes = "zzyes";

    [TearDown]
    public void RestoreTheSharedDetectorState()
    {
        ShippedConversationSignals.Uninstall();
        DeclineDetector.ResetCore();
        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);
    }

    [Test]
    public void Install_FeedsTheCoreRefusalsEveryDetectorAndTheCollisionHolder()
    {
        var core = new[] { new ConversationLanguageSignals("cc") { Negations = [CoreRefusal] } };
        var packs = new[]
        {
            new ConversationLanguageSignals("yy")
            {
                Affirmations = [PackYes],
                Negations = ["zzno"],
                Declines = ["zzdecline"],
                Corrections = ["zzfix"],
                Cancellations = ["zzstop"],
                GapIndicators = ["zzcannotdo"]
            },
            new ConversationLanguageSignals("ww") { Negations = [PackYes] }
        };

        DeclineDetector.LeadsWithNegation(CoreRefusal).ShouldBeFalse();

        ConversationSignalsPluginLoader.Install(core, packs);

        DeclineDetector.LeadsWithNegation(CoreRefusal).ShouldBeTrue("the core refusal vocabulary");
        DeclineDetector.LeadsWithNegation("zzdecline").ShouldBeTrue("the pack declines");
        DeclineDetector.LeadsWithNegation("zzno").ShouldBeTrue("the pack negations");
        AffirmationDetector.IsAffirmation("zzno nothing").ShouldBeFalse();
        AffirmationDetector.AssessAffirmation(PackYes, "yy").IsConfirmed.ShouldBeTrue("the pack affirmations, settled by the language");
        ImplicitCorrectionDetector.IsCorrectionSignal("zzfix").ShouldBeTrue("the pack corrections");
        RecipeCancellationDetector.IsCancellation("zzstop").ShouldBeTrue("the pack cancellations");
        RefusalSignalDetector.IsRefusal("sorry, zzcannotdo").ShouldBeTrue("the pack gap indicators");
        ConversationSignalCollisions.Current.Entries.Keys.ShouldBe([PackYes]);
    }

    [Test]
    public void Install_IsRepeatable_AndEndsInTheSameCollisionTable()
    {
        var packs = new[]
        {
            new ConversationLanguageSignals("yy") { Affirmations = [PackYes] },
            new ConversationLanguageSignals("ww") { Negations = [PackYes] }
        };

        ConversationSignalsPluginLoader.Install([], packs);
        var once = ConversationSignalCollisions.Current.Describe();
        ConversationSignalsPluginLoader.Install([], packs);

        ConversationSignalCollisions.Current.Describe().ShouldBe(once);
    }

    [Test]
    public void Load_ReadsTheCoreFromTheBaseDirectoryAndThePacksFromTheConfiguredDirectory_AndLogsTheCollisionTable()
    {
        var root = Path.Combine(Path.GetTempPath(), "klacks-signals-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            var coreFile = Path.Combine(root, "Application", "Klacksy", "conversation-signals-core.json");
            Directory.CreateDirectory(Path.GetDirectoryName(coreFile)!);
            File.WriteAllText(coreFile, "{\"cc\":{\"negations\":[\"" + CoreRefusal + "\"]}}");
            WritePack(root, "relocated", "yy", $$"""{"affirmations":["{{PackYes}}"]}""");
            WritePack(root, "relocated", "ww", $$"""{"negations":["{{PackYes}}"]}""");
            var logger = new CapturingLogger();
            var errors = new List<string>();

            ConversationSignalsPluginLoader.Load(root, (file, _) => errors.Add(file), "relocated", logger);

            errors.ShouldBeEmpty();
            DeclineDetector.LeadsWithNegation(CoreRefusal).ShouldBeTrue();
            ConversationSignalCollisions.Current.Entries.Keys.ShouldBe([PackYes]);
            logger.Messages.ShouldContain(message => message.Contains("collisions: 1") && message.Contains(PackYes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Load_ReportsAMissingCoreFile_AndStillInstallsThePacks()
    {
        var root = Path.Combine(Path.GetTempPath(), "klacks-signals-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            WritePack(root, "relocated", "yy", $$"""{"affirmations":["{{PackYes}}"]}""");
            var errors = new List<Exception>();

            ConversationSignalsPluginLoader.Load(root, (_, exception) => errors.Add(exception), "relocated");

            errors.ShouldHaveSingleItem().ShouldBeOfType<FileNotFoundException>();
            AffirmationDetector.IsAffirmation(PackYes).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WritePack(string root, string packsDirectory, string code, string json)
    {
        var directory = Path.Combine(root, packsDirectory, code);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "conversation-signals.json"), json);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
