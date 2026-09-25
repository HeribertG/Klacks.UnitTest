// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the confirmation preview hook of AutonomyGateService: a held Sensitive skill with a registered preview
/// provider gets the server-computed preview appended to its confirmation request and a token; a refusing or failing
/// provider turns the hold into an error without a token (the failure text does not leak the exception); a cancellation
/// propagates; providers are consulted neither for other risk classes, nor for skills they do not support, nor when a valid
/// token is redeemed in a later turn; and a confirmation carrying the longest macro preview still fits the smallest
/// per-result cap of the tool loop (2000 characters, ContextBudgetPolicy tiny profile; relay prefix from
/// LLMFunctionExecutor). The preview intro asks the model to relay every name and number unchanged. That is the only
/// mitigation of the residual risk the owner accepted on 2026-09-25 (F1): the user sees the model's wording, not a
/// server-rendered confirmation.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AutonomyGatePreviewTests
{
    private const string SkillName = "previewed_skill";
    private const string OtherSkillName = "other_skill";
    private const string PreviewText = "Switch the calculation macro of the shift 'Night' from 'AllShift' to 'AllShift+'.";
    private const string RefusalText = "Switching the calculation macro is reserved for administrators.";
    private const string ProviderFailureText = "database exploded";
    private const string PreviewIntroMarker = "Server-computed preview";
    private const string PreviewFailedMarker = "could not compute the preview";
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string ParameterName = "shiftId";
    private const string ParameterValue = "3f2c7a10-0000-4000-8000-000000000001";
    private const string ConfirmationRelayPrefix = "Confirmation required: ";
    private const int SmallestToolResultCap = 2000;
    private const string RelayUnchangedMarker = "keep every name and number unchanged";

    private IAgentAutonomyPreferenceRepository _preferenceRepository = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private ISkillConfirmationPreviewProvider _provider = null!;
    private AutonomyGateService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _preferenceRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();
        _provider = Substitute.For<ISkillConfirmationPreviewProvider>();
        _provider.Supports(SkillName).Returns(true);
        _sut = CreateGate(new TurnConfirmationScope());
    }

    [Test]
    public async Task SensitiveWithProvider_AppendsThePreview_AndIssuesAToken()
    {
        SetRisk(SkillRiskClass.Sensitive);
        GivenPreview(SkillConfirmationPreview.Show(PreviewText));

        var result = await _sut.CheckAsync(Descriptor(), Context(), new Dictionary<string, object>());

        result!.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message!.ShouldContain(PreviewIntroMarker);
        result.Message.ShouldContain(PreviewText);
        result.Metadata!.ShouldContainKey(ConfirmationTokenKey);
    }

    [Test]
    public async Task ProviderRefuses_ReturnsAnErrorWithoutToken()
    {
        SetRisk(SkillRiskClass.Sensitive);
        GivenPreview(SkillConfirmationPreview.Refuse(RefusalText));

        var result = await _sut.CheckAsync(Descriptor(), Context(), new Dictionary<string, object>());

        result!.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldBe(RefusalText);
        result.Metadata.ShouldBeNull();
    }

    [Test]
    public async Task ProviderThrows_ReturnsAnErrorWithoutToken_AndHidesTheException()
    {
        SetRisk(SkillRiskClass.Sensitive);
        _provider.BuildAsync(SkillName, Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns<SkillConfirmationPreview>(_ => throw new InvalidOperationException(ProviderFailureText));

        var result = await _sut.CheckAsync(Descriptor(), Context(), new Dictionary<string, object>());

        result!.Type.ShouldBe(SkillResultType.Error);
        result.Message!.ShouldContain(PreviewFailedMarker);
        result.Message.ShouldContain(SkillName);
        result.Message.ShouldNotContain(ProviderFailureText);
        result.Metadata.ShouldBeNull();
    }

    [Test]
    public async Task ProviderCancelled_Propagates()
    {
        SetRisk(SkillRiskClass.Sensitive);
        _provider.BuildAsync(SkillName, Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns<SkillConfirmationPreview>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.CheckAsync(Descriptor(), Context(), new Dictionary<string, object>()));
    }

    [Test]
    public async Task IrreversibleHeldAtPropose_DoesNotConsultTheProvider()
    {
        SetRisk(SkillRiskClass.Irreversible);
        var context = Context();
        _preferenceRepository.GetAsync(context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = context.UserId.ToString(), Level = AutonomyLevel.Propose });

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        result!.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message!.ShouldNotContain(PreviewIntroMarker);
        await _provider.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task SensitiveWithoutASupportingProvider_KeepsTheStandardConfirmation()
    {
        SetRisk(SkillRiskClass.Sensitive);

        var result = await _sut.CheckAsync(Descriptor(OtherSkillName), Context(), new Dictionary<string, object>());

        result!.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message!.ShouldNotContain(PreviewIntroMarker);
        await _provider.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task RedeemingAValidTokenInALaterTurn_DoesNotConsultTheProviderAgain()
    {
        SetRisk(SkillRiskClass.Sensitive);
        GivenPreview(SkillConfirmationPreview.Show(PreviewText));
        var context = Context();
        var parameters = new Dictionary<string, object> { [ParameterName] = ParameterValue };
        var held = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>(parameters));
        var token = (string)held!.Metadata![ConfirmationTokenKey];
        var laterTurn = CreateGate(new TurnConfirmationScope());
        var replay = new Dictionary<string, object>(parameters) { [AutonomyDefaults.ConfirmationTokenParameter] = token };

        var result = await laterTurn.CheckAsync(Descriptor(), context, replay);

        result.ShouldBeNull();
        await _provider.Received(1).BuildAsync(
            Arg.Any<string>(), Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfirmationWithTheLongestMacroPreview_FitsTheSmallestToolResultCap()
    {
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor(MacroAssignmentSkillNames.AssignToAbsenceType);
        _provider.Supports(descriptor.Name).Returns(true);
        _provider.BuildAsync(descriptor.Name, Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillConfirmationPreview.Show(new string('x', MacroAssignmentTextFormatter.MaxPreviewChars)));

        var result = await _sut.CheckAsync(descriptor, Context(), new Dictionary<string, object>());

        (ConfirmationRelayPrefix + result!.Message).Length.ShouldBeLessThanOrEqualTo(SmallestToolResultCap);
    }

    [Test]
    public async Task PreviewIntro_AsksTheModelToRelayNamesAndNumbersUnchanged_AcceptedRiskF1()
    {
        SetRisk(SkillRiskClass.Sensitive);
        GivenPreview(SkillConfirmationPreview.Show(PreviewText));

        var result = await _sut.CheckAsync(Descriptor(), Context(), new Dictionary<string, object>());

        result!.Message!.ShouldContain(RelayUnchangedMarker);
    }

    private AutonomyGateService CreateGate(TurnConfirmationScope turnScope) => new(
        _preferenceRepository,
        _riskClassifier,
        _confirmationStore,
        turnScope,
        [_provider],
        NullLogger<AutonomyGateService>.Instance);

    private void SetRisk(SkillRiskClass riskClass) =>
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(riskClass);

    private void GivenPreview(SkillConfirmationPreview preview) =>
        _provider.BuildAsync(SkillName, Arg.Any<SkillExecutionContext>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(preview);

    private static SkillDescriptor Descriptor(string name = SkillName) =>
        new(name, "test", SkillCategory.Crud, [], [], [], null);

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };
}
