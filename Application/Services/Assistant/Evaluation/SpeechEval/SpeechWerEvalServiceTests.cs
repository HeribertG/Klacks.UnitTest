// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.SpeechEval;

using System.Text;
using Klacks.Api.Application.Services.Assistant.Evaluation.SpeechEval;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SpeechWerEvalServiceTests
{
    private const string ProviderId = "groq-whisper";
    private const double Tolerance = 0.0001;

    private ISpeechGoldsetLoader _goldsetLoader = null!;
    private ISpeechTranscriptionService _transcriptionService = null!;
    private IEvalRunRepository _evalRunRepository = null!;
    private SpeechWerEvalService _service = null!;
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"speech-wer-eval-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        _goldsetLoader = Substitute.For<ISpeechGoldsetLoader>();
        _transcriptionService = Substitute.For<ISpeechTranscriptionService>();
        _evalRunRepository = Substitute.For<IEvalRunRepository>();

        _goldsetLoader.ResolveAudioPath(Arg.Any<string>())
            .Returns(callInfo => Path.Combine(_tempDirectory, Path.GetFileName(callInfo.Arg<string>())));

        _transcriptionService.TranscribeAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Encoding.UTF8.GetString(callInfo.Arg<byte[]>()));

        _service = new SpeechWerEvalService(
            _goldsetLoader,
            _transcriptionService,
            _evalRunRepository,
            Substitute.For<ILogger<SpeechWerEvalService>>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_PerfectTranscripts_PersistsEvalRunWithFullScore()
    {
        var items = new List<SpeechGoldsetItem>
        {
            CreateItem("sw-001", "de", "Bitte trage die Schicht für Hans-Peter Brönnimann ein", ["Brönnimann"]),
            CreateItem("sw-002", "en", "Sarah Johnson called in sick for Thursday", ["Johnson"])
        };
        SetUpGoldset(items);
        WriteAudioFileWithTranscript(items[0]);
        WriteAudioFileWithTranscript(items[1]);

        var result = await _service.RunAsync(ProviderId);

        result.Run.ShouldNotBeNull();
        result.Run!.Goldset.ShouldBe(SpeechEvalConstants.GoldsetName);
        result.Run.Model.ShouldBe(ProviderId);
        result.Run.Provider.ShouldBe(ProviderId);
        result.Run.CompositeScore.ShouldBe(1.0m);
        result.Run.ItemsTotal.ShouldBe(2);
        result.Run.ItemsPassed.ShouldBe(2);
        result.Run.DimensionsJson.ShouldContain("\"ItemsMeasured\":2");
        result.Run.DimensionsJson.ShouldContain("\"ItemsSkipped\":0");

        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.AvgWer!.Value.ShouldBe(0.0, Tolerance);
        result.Dimensions.NameAccuracy!.Value.ShouldBe(1.0, Tolerance);
        result.Dimensions.ItemsMeasured.ShouldBe(2);

        await _evalRunRepository.Received(1).AddAsync(
            Arg.Is<EvalRun>(r => r.Goldset == SpeechEvalConstants.GoldsetName && r.Model == ProviderId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ImperfectTranscript_ScoresWerAndNameAccuracy()
    {
        var item = CreateItem("sw-001", "de", "eins zwei drei Nussbaumer", ["Nussbaumer"]);
        SetUpGoldset([item]);
        WriteAudioFile(item, "eins zwei falsch Nussbaumer");

        var result = await _service.RunAsync(ProviderId);

        var itemResult = result.Items.Single();
        itemResult.Wer!.Value.ShouldBe(0.25, Tolerance);
        itemResult.NameAccuracy!.Value.ShouldBe(1.0, Tolerance);
        itemResult.Composite!.Value.ShouldBe(0.825, Tolerance);
        itemResult.Transcript.ShouldBe("eins zwei falsch Nussbaumer");
    }

    [Test]
    public async Task RunAsync_MissingAudioFile_SkipsItemAndStillPersists()
    {
        var items = new List<SpeechGoldsetItem>
        {
            CreateItem("sw-001", "de", "eins zwei drei Grüter", ["Grüter"]),
            CreateItem("sw-002", "de", "vier fünf sechs Fässler", ["Fässler"])
        };
        SetUpGoldset(items);
        WriteAudioFileWithTranscript(items[0]);

        var result = await _service.RunAsync(ProviderId);

        result.Run.ShouldNotBeNull();
        result.Run!.ItemsTotal.ShouldBe(2);
        result.Run.ItemsPassed.ShouldBe(1);
        result.Dimensions!.ItemsMeasured.ShouldBe(1);
        result.Dimensions.ItemsSkipped.ShouldBe(1);
        result.Items.Single(i => i.ItemId == "sw-002").Skipped.ShouldBeTrue();

        await _transcriptionService.Received(1).TranscribeAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NoMeasurableItems_ReturnsMessageAndDoesNotPersist()
    {
        var items = new List<SpeechGoldsetItem>
        {
            CreateItem("sw-001", "de", "eins zwei drei", ["Grüter"]),
            CreateItem("sw-002", "en", "four five six", ["Miller"])
        };
        SetUpGoldset(items);

        var result = await _service.RunAsync(ProviderId);

        result.Run.ShouldBeNull();
        result.Message.ShouldBe(SpeechEvalConstants.NoMeasurableItemsMessage);
        result.Dimensions!.ItemsTotal.ShouldBe(2);
        result.Dimensions.ItemsMeasured.ShouldBe(0);
        result.Dimensions.ItemsSkipped.ShouldBe(2);
        result.Items.ShouldAllBe(i => i.Skipped);

        await _evalRunRepository.DidNotReceive().AddAsync(Arg.Any<EvalRun>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WithBaseline_ComputesRegression()
    {
        var item = CreateItem("sw-001", "de", "eins zwei drei Grüter", ["Grüter"]);
        SetUpGoldset([item]);
        WriteAudioFileWithTranscript(item);

        _evalRunRepository.GetLatestAsync(SpeechEvalConstants.GoldsetName, ProviderId, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { CompositeScore = 0.4m });

        var result = await _service.RunAsync(ProviderId);

        result.Run.ShouldNotBeNull();
        result.Run!.RegressionVsBaseline.ShouldBe(0.6m);
    }

    [Test]
    public async Task RunAsync_PassesProviderIdAndLocaleToTranscriptionSeam()
    {
        var item = CreateItem("sw-001", "fr", "absence pour François Dubois", ["Dubois"]);
        SetUpGoldset([item]);
        WriteAudioFileWithTranscript(item);

        await _service.RunAsync(ProviderId);

        await _transcriptionService.Received(1).TranscribeAsync(
            ProviderId, Arg.Any<byte[]>(), "fr", Arg.Any<CancellationToken>());
    }

    private void SetUpGoldset(List<SpeechGoldsetItem> items)
    {
        _goldsetLoader.LoadAsync(SpeechEvalConstants.GoldsetName, Arg.Any<CancellationToken>())
            .Returns(items);
    }

    private static SpeechGoldsetItem CreateItem(string id, string locale, string referenceText, List<string> expectedNames)
    {
        return new SpeechGoldsetItem
        {
            Id = id,
            Locale = locale,
            ReferenceText = referenceText,
            ExpectedNames = expectedNames,
            AudioFile = $"SpeechAudio/{id}.wav"
        };
    }

    private void WriteAudioFileWithTranscript(SpeechGoldsetItem item)
    {
        WriteAudioFile(item, item.ReferenceText);
    }

    private void WriteAudioFile(SpeechGoldsetItem item, string transcript)
    {
        var path = Path.Combine(_tempDirectory, Path.GetFileName(item.AudioFile));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(transcript));
    }
}
