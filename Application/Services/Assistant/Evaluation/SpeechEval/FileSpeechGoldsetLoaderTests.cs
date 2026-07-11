// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.SpeechEval;

using Klacks.Api.Application.Services.Assistant.Evaluation.SpeechEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class FileSpeechGoldsetLoaderTests
{
    private const string RealGoldsetName = "speech-wer-v1";
    private const string WrongKindGoldsetName = "speecheval-test-wrong-kind";
    private const string ParsingGoldsetName = "speecheval-test-parsing";
    private const string JsonExtension = ".json";

    private static readonly string[] GoldsetRelativePath = ["Application", "Skills", "Goldsets"];

    private static readonly string[] RepoGoldsetRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private const string WrongKindJson = """
        {
          "version": 1,
          "kind": "turn-selection",
          "items": []
        }
        """;

    private const string ParsingJson = """
        {
          "version": 1,
          "kind": "speech-wer",
          "items": [
            {
              "id": "sw-901",
              "locale": "de",
              "referenceText": "Bitte trage die Frühschicht für Hans-Peter Brönnimann ein.",
              "expectedNames": ["Brönnimann"],
              "audioFile": "SpeechAudio/sw-901-de.wav"
            }
          ]
        }
        """;

    private FileSpeechGoldsetLoader _loader = null!;
    private readonly List<string> _tempFiles = new();

    private static string GoldsetDirectory =>
        Path.Combine([AppContext.BaseDirectory, .. GoldsetRelativePath]);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Directory.CreateDirectory(GoldsetDirectory);
        EnsureRealGoldsetPresent();
        WriteTempGoldset(Path.Combine(GoldsetDirectory, WrongKindGoldsetName + JsonExtension), WrongKindJson);
        WriteTempGoldset(Path.Combine(GoldsetDirectory, ParsingGoldsetName + JsonExtension), ParsingJson);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _loader = new FileSpeechGoldsetLoader();
    }

    [Test]
    public async Task LoadAsync_RealGoldset_LoadsItemsWithNamesAndAudioFiles()
    {
        var items = await _loader.LoadAsync(RealGoldsetName);

        items.Count.ShouldBeGreaterThan(0);
        items.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i.Id));
        items.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i.ReferenceText));
        items.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i.Locale));
        items.ShouldAllBe(i => !string.IsNullOrWhiteSpace(i.AudioFile));
        items.ShouldAllBe(i => i.ExpectedNames.Count > 0);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void LoadAsync_BlankName_ThrowsArgumentException(string goldset)
    {
        Should.ThrowAsync<ArgumentException>(() => _loader.LoadAsync(goldset));
    }

    [Test]
    public void LoadAsync_UnknownName_ThrowsFileNotFoundException()
    {
        Should.ThrowAsync<FileNotFoundException>(() => _loader.LoadAsync("speecheval-test-does-not-exist"));
    }

    [Test]
    public void LoadAsync_WrongKind_ThrowsInvalidDataException()
    {
        Should.ThrowAsync<InvalidDataException>(() => _loader.LoadAsync(WrongKindGoldsetName));
    }

    [Test]
    public async Task LoadAsync_ParsingGoldset_MapsAllFields()
    {
        var items = await _loader.LoadAsync(ParsingGoldsetName);

        items.Count.ShouldBe(1);
        var item = items[0];
        item.Id.ShouldBe("sw-901");
        item.Locale.ShouldBe("de");
        item.ReferenceText.ShouldContain("Brönnimann");
        item.ExpectedNames.ShouldBe(["Brönnimann"]);
        item.AudioFile.ShouldBe("SpeechAudio/sw-901-de.wav");
    }

    [Test]
    public void ResolveAudioPath_RelativeFile_ResolvesInsideGoldsetFolder()
    {
        var resolved = _loader.ResolveAudioPath("SpeechAudio/sw-901-de.wav");

        resolved.ShouldStartWith(Path.GetFullPath(GoldsetDirectory));
        resolved.ShouldEndWith("sw-901-de.wav");
    }

    [Test]
    public void ResolveAudioPath_PathTraversal_Throws()
    {
        Should.Throw<InvalidOperationException>(() => _loader.ResolveAudioPath("../../secrets.wav"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ResolveAudioPath_Blank_ThrowsArgumentException(string audioFile)
    {
        Should.Throw<ArgumentException>(() => _loader.ResolveAudioPath(audioFile));
    }

    private void EnsureRealGoldsetPresent()
    {
        var target = Path.Combine(GoldsetDirectory, RealGoldsetName + JsonExtension);
        if (File.Exists(target))
        {
            return;
        }

        var source = LocateRepoGoldset(RealGoldsetName + JsonExtension);
        File.Copy(source, target);
        _tempFiles.Add(target);
    }

    private void WriteTempGoldset(string path, string content)
    {
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
    }

    private static string LocateRepoGoldset(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. RepoGoldsetRelativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', RepoGoldsetRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}
