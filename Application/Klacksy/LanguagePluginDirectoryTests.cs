// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// LanguagePlugins:Directory has to move EVERY runtime reader of the language packs, not only the installer. The
/// resolver is tested for a missing, blank, relative and absolute value; then each reader is pointed at a temporary pack
/// directory that no shipped file can satisfy and must be seen reading it there: the vocabulary loaders through the
/// error callback of a deliberately unreadable pack file (so no detector is configured), ReadPacks through the pack it
/// returns, UtteranceNormalizer and PhoneticConfigProvider through a value only the temporary pack carries.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class LanguagePluginDirectoryTests
{
    private const string PackCode = "xx";
    private const string BrokenJson = "{ this is not json";
    private const string BaseDirectoryName = "app-base";
    private const string ConfiguredDirectoryName = "relocated-packs";
    private const string NavigationIntentFileName = "navigation-intent.json";
    private const string WakeWordsFileName = "wake-words.json";
    private const string Salutation = "zzhallo";
    private const int RelocatedMinWordLength = 7;

    private string _root = string.Empty;
    private string _baseDirectory = string.Empty;
    private string _configuredDirectory = string.Empty;
    private string _packDirectory = string.Empty;

    [SetUp]
    public void CreateTemporaryDirectories()
    {
        _root = Path.Combine(Path.GetTempPath(), "klacks-lang-dir-" + Guid.NewGuid().ToString("N"));
        _baseDirectory = Path.Combine(_root, BaseDirectoryName);
        _configuredDirectory = Path.Combine(_root, ConfiguredDirectoryName);
        _packDirectory = Path.Combine(_configuredDirectory, PackCode);
        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_packDirectory);
    }

    [TearDown]
    public void DeleteTemporaryDirectories()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Resolve_FallsBackToTheShippedFolderUnderTheBaseDirectory_WhenNothingIsConfigured(string? configured)
    {
        var resolved = LanguagePluginDirectoryResolver.Resolve(_baseDirectory, configured);

        resolved.ShouldBe(Path.Combine(_baseDirectory, LanguagePluginConstants.PluginDirectory));
    }

    [Test]
    public void Resolve_ResolvesARelativeValueAgainstTheBaseDirectory()
    {
        var resolved = LanguagePluginDirectoryResolver.Resolve(_baseDirectory, Path.Combine("custom", "packs"));

        resolved.ShouldBe(Path.Combine(_baseDirectory, "custom", "packs"));
    }

    [Test]
    public void Resolve_TakesAnAbsoluteValueAsIs_IgnoringTheBaseDirectory()
    {
        var resolved = LanguagePluginDirectoryResolver.Resolve(_baseDirectory, _configuredDirectory);

        resolved.ShouldBe(_configuredDirectory);
    }

    [Test]
    public void ReadPacks_ReadsThePacksOfTheConfiguredDirectory_AndSkipsWhatIsNotAUsablePack()
    {
        WritePack("aa", """{"affirmations":["ja aa"],"negations":["nein aa"],"declines":["nope aa"]}""");
        WritePack("bb", BrokenJson);
        Directory.CreateDirectory(Path.Combine(_configuredDirectory, "cc"));
        WritePack("de", """{"affirmations":["never read"]}""");
        var errors = new List<string>();

        var packs = ConversationSignalsPluginLoader.ReadPacks(
            _baseDirectory, (file, _) => errors.Add(file), _configuredDirectory);

        packs.Select(pack => pack.Language).ShouldBe(["aa"]);
        packs[0].Affirmations.ShouldBe(["ja aa"]);
        packs[0].Negations.ShouldBe(["nein aa"]);
        packs[0].Declines.ShouldBe(["nope aa"]);
        errors.ShouldBe([Path.Combine(_configuredDirectory, "bb", LanguagePluginConstants.ConversationSignalsFileName)]);
    }

    [Test]
    public void ReadPacks_ResolvesARelativeConfiguredDirectoryAgainstTheBaseDirectory()
    {
        var relocated = Path.Combine(_baseDirectory, "relative-packs", "aa");
        Directory.CreateDirectory(relocated);
        File.WriteAllText(
            Path.Combine(relocated, LanguagePluginConstants.ConversationSignalsFileName), """{"affirmations":["ja aa"]}""");

        var packs = ConversationSignalsPluginLoader.ReadPacks(_baseDirectory, null, "relative-packs");

        packs.Select(pack => pack.Language).ShouldBe(["aa"]);
    }

    [Test]
    public void ReadPacks_ReturnsNothing_WhenTheDirectoryDoesNotExist()
    {
        ConversationSignalsPluginLoader.ReadPacks(_baseDirectory, null, Path.Combine(_root, "missing")).ShouldBeEmpty();
        ConversationSignalsPluginLoader.ReadPacks(_baseDirectory).ShouldBeEmpty();
    }

    [Test]
    public void TheNavigationMutationGroupingAndCompletionClaimLoaders_ReadTheConfiguredDirectory()
    {
        Write(NavigationIntentFileName, BrokenJson);
        Write(LanguagePluginConstants.MutationIntentFileName, BrokenJson);
        Write(LanguagePluginConstants.GroupingIntentFileName, BrokenJson);
        Write(LanguagePluginConstants.CompletionClaimFileName, BrokenJson);

        ErroredFilesOf(callback => NavigationIntentPluginLoader.Load(_baseDirectory, callback, _configuredDirectory))
            .ShouldBe([PackFile(NavigationIntentFileName)]);
        ErroredFilesOf(callback => MutationIntentPluginLoader.Load(_baseDirectory, callback, _configuredDirectory))
            .ShouldBe([PackFile(LanguagePluginConstants.MutationIntentFileName)]);
        ErroredFilesOf(callback => GroupingIntentPluginLoader.Load(_baseDirectory, callback, _configuredDirectory))
            .ShouldBe([PackFile(LanguagePluginConstants.GroupingIntentFileName)]);
        ErroredFilesOf(callback => CompletionClaimPluginLoader.Load(_baseDirectory, callback, _configuredDirectory))
            .ShouldBe([PackFile(LanguagePluginConstants.CompletionClaimFileName)]);
    }

    [Test]
    public void TheAssistantTextsLoader_ReadsTheConfiguredDirectory()
    {
        var missing = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, null, missing.Add, _configuredDirectory);

        missing.ShouldBe([PackCode]);
    }

    [Test]
    public void TheStartupLoader_RunsEveryLoaderAgainstTheConfiguredDirectory()
    {
        Write(NavigationIntentFileName, BrokenJson);
        var logger = new CapturingLogger();

        LanguagePluginStartupLoader.LoadAll(_baseDirectory, _configuredDirectory, logger);

        logger.Messages.ShouldContain(message => message.Contains(PackFile(NavigationIntentFileName)));
        logger.Messages.ShouldContain(message => message.Contains(PackCode) && message.Contains(LanguagePluginConstants.AssistantTextsFileName));
    }

    [Test]
    public void TheUtteranceNormalizer_ReadsThePackWakeWordsFromTheConfiguredDirectory()
    {
        File.WriteAllText(
            Path.Combine(_packDirectory, WakeWordsFileName),
            JsonSerializer.Serialize(new { salutations = new[] { Salutation } }));
        var utterance = $"{Salutation} klacksy zeig";

        var shipped = new UtteranceNormalizer().Normalize(utterance, PackCode);
        var relocated = new UtteranceNormalizer(_configuredDirectory).Normalize(utterance, PackCode);

        shipped.WakeWordStripped.ShouldBeFalse();
        relocated.WakeWordStripped.ShouldBeTrue();
        relocated.Normalized.ShouldBe("zeig");
    }

    [Test]
    public void ThePhoneticConfigProvider_ReadsThePackPhoneticsFromTheConfiguredDirectory()
    {
        File.WriteAllText(
            Path.Combine(_packDirectory, LanguagePluginConstants.PhoneticsFileName),
            JsonSerializer.Serialize(new { enabled = true, minWordLength = RelocatedMinWordLength }));

        new PhoneticConfigProvider(_configuredDirectory).GetForLocale(PackCode).MinWordLength.ShouldBe(RelocatedMinWordLength);
        new PhoneticConfigProvider().GetForLocale(PackCode).MinWordLength.ShouldNotBe(RelocatedMinWordLength);
    }

    private string PackFile(string fileName) => Path.Combine(_packDirectory, fileName);

    private void Write(string fileName, string content) => File.WriteAllText(PackFile(fileName), content);

    private void WritePack(string code, string json)
    {
        var directory = Path.Combine(_configuredDirectory, code);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, LanguagePluginConstants.ConversationSignalsFileName), json);
    }

    private static List<string> ErroredFilesOf(Action<Action<string, Exception>> load)
    {
        var files = new List<string>();
        load((file, _) => files.Add(file));
        return files;
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
