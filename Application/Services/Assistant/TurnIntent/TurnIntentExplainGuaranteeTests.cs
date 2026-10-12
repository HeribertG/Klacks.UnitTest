// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

[TestFixture]
public class TurnIntentExplainGuaranteeTests
{
    private const string Message = "Wie funktioniert der Periodenabschluss?";
    private const string VetoMessage = "Explain to me how to create a new contract";

    private ITurnIntentClassifier _classifier = null!;

    [SetUp]
    public void SetUp()
    {
        _classifier = Substitute.For<ITurnIntentClassifier>();
    }

    [Test]
    public async Task ActiveConfidentAskInEnabledLocale_GuaranteesTheTwoBestRankedExplainSkills()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        var outcome = await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(), CancellationToken.None);

        outcome.ExplainSkillNames.ShouldBe(["explain_period_closing", "explain_contracts"]);
    }

    [Test]
    public async Task Shadow_LogsButGuaranteesNothing()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Shadow).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
        await _classifier.Received(1).ClassifyAsync(Message, "de", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisabledLocale_GuaranteesNothing()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: false);

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task UnconfidentAsk_GuaranteesNothing()
    {
        Classify(TurnIntentLabel.Ask, confident: false, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task Do_NeverGuaranteesAnything()
    {
        Classify(TurnIntentLabel.Do, confident: false, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ClassifierFailure_GuaranteesNothing()
    {
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<TurnIntentPrediction?>(_ => throw new InvalidOperationException("boom"));

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOff_DoesNotClassify()
    {
        (await Create(TurnIntentClassifierModes.Off).ResolveAsync(Request(), CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
        await _classifier.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private void Classify(TurnIntentLabel label, bool confident, bool localeEnabled) =>
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TurnIntentPrediction(label, 0.9, 0.05, 0.05, confident, "de", localeEnabled));

    private TurnIntentExplainGuarantee Create(
        string mode, bool veto = false, int timeoutMilliseconds = TurnIntentDefaults.DefaultTimeoutMilliseconds) =>
        new(_classifier, new TurnIntentClassifierOptions(mode, timeoutMilliseconds, veto),
            Substitute.For<ILogger<TurnIntentExplainGuarantee>>());

    private static SkillToolsetGuaranteeRequest Request(string message = Message)
    {
        var retrieved = new List<AgentSkill>
        {
            Skill("explain_absence_model", SkillEffect.Explain),
            Skill("explain_contracts", SkillEffect.Explain),
            Skill("create_contract", SkillEffect.Mutate),
            Skill("explain_period_closing", SkillEffect.Explain),
        };
        var scores = new Dictionary<string, double>
        {
            ["explain_absence_model"] = 0.41,
            ["explain_contracts"] = 0.55,
            ["create_contract"] = 0.99,
            ["explain_period_closing"] = 0.83,
        };

        return new SkillToolsetGuaranteeRequest(
            Guid.NewGuid(), retrieved, retrieved, message, "conversation-1", null, Guid.NewGuid().ToString(), "de",
            [], null, null, null, false, scores);
    }

    [Test]
    public async Task ClarificationAnswer_IsNotClassified()
    {
        var request = Request() with { PinnedSkillNames = ["create_contract"] };

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(request, CancellationToken.None)).ExplainSkillNames.ShouldBeEmpty();
        await _classifier.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [TestCase("Active", 500, "Active", 500)]
    [TestCase("shadow", 500, "Shadow", 500)]
    [TestCase("Activ", 500, "Off", 500)]
    [TestCase(null, 0, "Off", 50)]
    [TestCase("Active", 999999, "Active", 5000)]
    public void Options_FromConfiguration_NormaliseModeAndTimeout(string? mode, int timeout, string expectedMode, int expectedTimeout)
    {
        var options = TurnIntentClassifierOptions.FromConfiguration(mode, timeout);

        options.Mode.ShouldBe(expectedMode);
        options.TimeoutMilliseconds.ShouldBe(expectedTimeout);
    }

    [Test]
    public void VetoMessage_IsAMutationIntentForTheDetector()
    {
        MutationIntentDetector.IsMutationIntent(VetoMessage).ShouldBeTrue();
        MutationIntentDetector.IsMutationIntent(Message).ShouldBeFalse();
    }

    [Test]
    public async Task Veto_ConfidentAskWithMutationWord_InEnabledLocale_LiftsTheForcedToolCall()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        var outcome = await Create(TurnIntentClassifierModes.Active, veto: true)
            .ResolveAsync(Request(VetoMessage), CancellationToken.None);

        outcome.VetoesForcedToolCall.ShouldBeTrue();
    }

    [Test]
    public async Task Veto_NotConfigured_NeverLifts()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Active).ResolveAsync(Request(VetoMessage), CancellationToken.None))
            .VetoesForcedToolCall.ShouldBeFalse();
    }

    [Test]
    public async Task Veto_ShadowMode_NeverLifts()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Shadow, veto: true).ResolveAsync(Request(VetoMessage), CancellationToken.None))
            .VetoesForcedToolCall.ShouldBeFalse();
    }

    [TestCase(TurnIntentLabel.Ask, true, false)]
    [TestCase(TurnIntentLabel.Ask, false, true)]
    [TestCase(TurnIntentLabel.Do, false, true)]
    public async Task Veto_NeedsAConfidentAskInAnEnabledLocale(TurnIntentLabel label, bool confident, bool localeEnabled)
    {
        Classify(label, confident, localeEnabled);

        (await Create(TurnIntentClassifierModes.Active, veto: true).ResolveAsync(Request(VetoMessage), CancellationToken.None))
            .VetoesForcedToolCall.ShouldBeFalse();
    }

    [Test]
    public async Task Veto_WithoutMutationWord_HasNothingToLift()
    {
        Classify(TurnIntentLabel.Ask, confident: true, localeEnabled: true);

        (await Create(TurnIntentClassifierModes.Active, veto: true).ResolveAsync(Request(), CancellationToken.None))
            .VetoesForcedToolCall.ShouldBeFalse();
    }

    [Test]
    public async Task EarlyStartedClassification_IsAwaitedInsteadOfClassifyingAgain()
    {
        var started = Task.FromResult<TurnIntentPrediction?>(
            new TurnIntentPrediction(TurnIntentLabel.Ask, 0.9, 0.05, 0.05, true, "de", true));
        var request = Request(VetoMessage) with { PendingTurnIntent = started };

        var outcome = await Create(TurnIntentClassifierModes.Active, veto: true).ResolveAsync(request, CancellationToken.None);

        outcome.VetoesForcedToolCall.ShouldBeTrue();
        await _classifier.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EarlyStartedClassification_NotReadyWithinTheTimeout_ContributesNothing()
    {
        var neverCompletes = new TaskCompletionSource<TurnIntentPrediction?>();
        var request = Request(VetoMessage) with { PendingTurnIntent = neverCompletes.Task };

        var outcome = await Create(TurnIntentClassifierModes.Active, veto: true, TurnIntentDefaults.MinimumTimeoutMilliseconds)
            .ResolveAsync(request, CancellationToken.None);

        outcome.ShouldBe(TurnIntentGuaranteeOutcome.None);
    }

    [Test]
    public async Task Start_ClassifiesUnboundedWhenEnabled()
    {
        var started = Create(TurnIntentClassifierModes.Shadow).Start(Message, "de", null, CancellationToken.None);

        started.ShouldNotBeNull();
        await started;
        await _classifier.Received(1).ClassifyUnboundedAsync(Message, "de", Arg.Any<CancellationToken>());
    }

    [Test]
    public void Start_ReturnsNullWhenOffOrPinned()
    {
        Create(TurnIntentClassifierModes.Off).Start(Message, "de", null, CancellationToken.None).ShouldBeNull();
        Create(TurnIntentClassifierModes.Active).Start(Message, "de", ["create_contract"], CancellationToken.None).ShouldBeNull();

        _classifier.DidNotReceive().ClassifyUnboundedAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [TestCase("Active", true, true)]
    [TestCase("Active", false, false)]
    [TestCase("Shadow", true, false)]
    [TestCase("Off", true, false)]
    public void Options_FromConfiguration_VetoOnlyInActiveMode(string mode, bool veto, bool expected)
    {
        TurnIntentClassifierOptions.FromConfiguration(mode, TurnIntentDefaults.DefaultTimeoutMilliseconds, veto)
            .MayVetoForcedToolCall.ShouldBe(expected);
    }

    private static AgentSkill Skill(string name, SkillEffect effect) => new() { Name = name, Effect = effect };
}
