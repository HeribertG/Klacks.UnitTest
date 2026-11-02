// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the delete of a single shift of a variant (key): it needs an Active, visible variant and a shift that carries the
/// token of exactly that variant, and then hands the family delete to the shift DeleteCommand. A missing, hidden or
/// consumed variant, a missing shift and a shift of another variant, of a scenario or of the real plan are all answered
/// with false (404) and dispatch nothing.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.ScenarioVariants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScenarioVariants;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ScenarioVariants;

[TestFixture]
public class DeleteScenarioVariantShiftCommandHandlerTests
{
    private IScenarioVariantRepository _variantRepository = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private Guid _groupId;
    private AnalyseScenario _variant = null!;

    [SetUp]
    public void SetUp()
    {
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mediator = Substitute.For<IMediator>();
        _groupId = Guid.NewGuid();

        _groupGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(true);
        _variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Large order",
            GroupId = _groupId,
            Kind = AnalyseScenarioKind.Variant,
            Status = AnalyseScenarioStatus.Active
        };
        _variantRepository.GetVariantAsync(_variant.Id, CancellationToken.None).Returns(_variant);
    }

    [Test]
    public async Task Handle_ForAShiftOfTheVariant_DispatchesTheShiftDeleteAndAnswersTrue()
    {
        var shift = StoredShift(_variant.Token);
        DeleteCommandReturns(shift.Id, new ShiftResource { Id = shift.Id });

        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, shift.Id), CancellationToken.None);

        result.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<IRequest<ShiftResource?>>(request => request is DeleteCommand<ShiftResource> && ((DeleteCommand<ShiftResource>)request).Id == shift.Id),
            CancellationToken.None);
    }

    [Test]
    public async Task Handle_ForAShiftOfAnotherVariantOrAScenarioOrTheRealPlan_AnswersFalseAndDispatchesNothing()
    {
        var otherVariant = StoredShift(Guid.NewGuid());
        var realShift = StoredShift(null);

        (await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, otherVariant.Id), CancellationToken.None)).ShouldBeFalse();
        (await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, realShift.Id), CancellationToken.None)).ShouldBeFalse();

        await NothingWasDispatchedAsync();
    }

    [Test]
    public async Task Handle_ForAMissingShift_AnswersFalseAndDispatchesNothing()
    {
        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, Guid.NewGuid()), CancellationToken.None);

        result.ShouldBeFalse();
        await NothingWasDispatchedAsync();
    }

    [Test]
    public async Task Handle_ForAMissingVariant_AnswersFalseAndDispatchesNothing()
    {
        var shift = StoredShift(_variant.Token);

        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(Guid.NewGuid(), shift.Id), CancellationToken.None);

        result.ShouldBeFalse();
        await NothingWasDispatchedAsync();
    }

    [Test]
    public async Task Handle_ForAVariantOfAHiddenGroup_AnswersFalseAndDispatchesNothing()
    {
        var shift = StoredShift(_variant.Token);
        _groupGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, shift.Id), CancellationToken.None);

        result.ShouldBeFalse();
        await NothingWasDispatchedAsync();
    }

    [Test]
    public async Task Handle_ForAConsumedVariant_AnswersFalseAndDispatchesNothing()
    {
        var shift = StoredShift(_variant.Token);
        _variant.Status = AnalyseScenarioStatus.Accepted;

        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, shift.Id), CancellationToken.None);

        result.ShouldBeFalse();
        await NothingWasDispatchedAsync();
    }

    [Test]
    public async Task Handle_WhenTheShiftDeleteFindsNothing_AnswersFalse()
    {
        var shift = StoredShift(_variant.Token);
        DeleteCommandReturns(shift.Id, null);

        var result = await Handler().Handle(new DeleteScenarioVariantShiftCommand(_variant.Id, shift.Id), CancellationToken.None);

        result.ShouldBeFalse();
    }

    private Shift StoredShift(Guid? token)
    {
        var shift = new Shift { Id = Guid.NewGuid(), Name = "Key shift", AnalyseToken = token };
        _shiftRepository.GetNoTracking(shift.Id).Returns(shift);
        return shift;
    }

    private void DeleteCommandReturns(Guid shiftId, ShiftResource? resource) =>
        _mediator.Send(
                Arg.Is<IRequest<ShiftResource?>>(request => request is DeleteCommand<ShiftResource> && ((DeleteCommand<ShiftResource>)request).Id == shiftId),
                CancellationToken.None)
            .Returns(resource);

    private async Task NothingWasDispatchedAsync() =>
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<IRequest<ShiftResource?>>(), CancellationToken.None);

    private DeleteScenarioVariantShiftCommandHandler Handler() => new(
        _variantRepository,
        _groupGuard,
        _shiftRepository,
        _mediator,
        Substitute.For<ILogger<DeleteScenarioVariantShiftCommandHandler>>());
}
