// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillGapDetector — verifies that short affirmations never become gap
/// reports, that exact-duplicate messages merge via the normalized-hash fallback even when
/// no embedding is available, and that whitespace/case differences still collapse into one
/// record.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillGapDetectorTests
{
    private const string GapResponse = "Das kann ich leider nicht direkt ausführen.";

    private ISkillGapRepository _repository = null!;
    private IEmbeddingService _embeddingService = null!;
    private SkillGapDetector _detector = null!;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillGapRepository>();
        _embeddingService = Substitute.For<IEmbeddingService>();
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((float[]?)null);

        _detector = new SkillGapDetector(
            _repository,
            _embeddingService,
            Substitute.For<ILogger<SkillGapDetector>>());

        _agentId = Guid.NewGuid();
    }

    [Test]
    public async Task ShortAffirmation_NeverCreatesOrUpdatesAGapRecord()
    {
        await _detector.DetectAndSuggestAsync(_agentId, "Ja, mach das.", GapResponse, hadFunctionCalls: false);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!);
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Test]
    public async Task ExactDuplicateMessage_IncrementsExistingRecordViaNormalizedHash_WithNoEmbeddingAvailable()
    {
        var existing = new SkillGapRecord
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            OccurrenceCount = 1,
            NormalizedMessageHash = "irrelevant-for-repo-mock",
        };

        _repository.FindByNormalizedHashAsync(_agentId, Arg.Any<string>())
            .Returns(existing);

        var message = "Ja, bitte jetzt direkt ausfuehren und speichern. Frag nicht weiter nach und navigiere nicht.";

        await _detector.DetectAndSuggestAsync(_agentId, message, GapResponse, hadFunctionCalls: false);

        existing.OccurrenceCount.ShouldBe(2);
        await _repository.Received(1).UpdateAsync(existing);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Test]
    public async Task WhitespaceAndCaseVariant_ProducesSameNormalizedHashAsOriginal()
    {
        string? capturedHashA = null;
        string? capturedHashB = null;

        _repository.FindByNormalizedHashAsync(_agentId, Arg.Do<string>(h => capturedHashA = h))
            .Returns((SkillGapRecord?)null);
        await _detector.DetectAndSuggestAsync(
            _agentId, "Kannst du   das sofort  als CSV Datei exportieren", GapResponse, hadFunctionCalls: false);

        _repository.FindByNormalizedHashAsync(_agentId, Arg.Do<string>(h => capturedHashB = h))
            .Returns((SkillGapRecord?)null);
        await _detector.DetectAndSuggestAsync(
            _agentId, "KANNST DU DAS SOFORT ALS CSV DATEI EXPORTIEREN", GapResponse, hadFunctionCalls: false);

        capturedHashA.ShouldNotBeNull();
        capturedHashB.ShouldBe(capturedHashA);
    }

    [Test]
    public async Task NewGapMessage_IsPersistedWithNormalizedHashSet()
    {
        _repository.FindByNormalizedHashAsync(_agentId, Arg.Any<string>())
            .Returns((SkillGapRecord?)null);

        SkillGapRecord? captured = null;
        await _repository.AddAsync(Arg.Do<SkillGapRecord>(r => captured = r));

        await _detector.DetectAndSuggestAsync(
            _agentId, "Kannst du mir die Wettervorhersage für Bern als PDF exportieren?", GapResponse, hadFunctionCalls: false);

        captured.ShouldNotBeNull();
        captured!.NormalizedMessageHash.ShouldNotBeNullOrEmpty();
        captured.OccurrenceCount.ShouldBe(1);
    }

    [Test]
    public async Task NoGapIndicatorInResponse_NeverPersists()
    {
        await _detector.DetectAndSuggestAsync(
            _agentId, "Kannst du mir die Wettervorhersage für Bern exportieren?", "Klar, hier ist die Vorhersage.", hadFunctionCalls: false);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!);
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Test]
    public async Task HadFunctionCalls_NeverPersists()
    {
        await _detector.DetectAndSuggestAsync(_agentId, "Egal was hier steht", GapResponse, hadFunctionCalls: true);

        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!);
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }
}
