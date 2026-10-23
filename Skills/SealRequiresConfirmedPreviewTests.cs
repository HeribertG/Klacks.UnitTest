// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Sealing can never be undone, so seal_open_orders (apply=true) and seal_shift run only as the confirmed replay of the
/// server preview the user was shown: a call that reaches the skill without a confirmed binding (a plan step, any context
/// with the autonomy gate bypassed) refuses with BindingRequired and seals nothing, both skills carry a server preview
/// (which also keeps them out of every agent plan), and the chat requests the welcome toast, the open-orders summary and
/// the setup goals send are never read as the user's explicit yes to a held action.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealRequiresConfirmedPreviewTests
{
    private const string SealOpenOrders = "seal_open_orders";
    private const string SealShift = "seal_shift";

    private static readonly Guid OrderId = Guid.NewGuid();

    private static SkillExecutionContext BypassedContext() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "plan-step",
        UserLanguage = "en",
        UserPermissions = Permissions.ExpandRoles([Roles.Admin]),
        BypassAutonomyGate = true
    };

    [Test]
    public async Task SealOpenOrders_ApplyWithoutAConfirmedBinding_RefusesAndSealsNothing()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SealOpenOrdersResult(
                Applied: false, TotalOrders: 1, SealableCount: 1, SealedCount: 0, BlockedCount: 0, FailedCount: 0,
                BlockedOnlyByMissingGroupCount: 0, AutoAssignedCount: 0, AutoAssignRequested: false,
                SealedSample: [], BlockedSample: [], Failures: []));
        var queue = Substitute.For<ISealOpenOrdersJobQueue>();
        var skill = SealOpenOrdersSkillFactory.Create(mediator, queue);

        var result = await skill.ExecuteAsync(BypassedContext(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, SealOpenOrders));
        await mediator.DidNotReceive().Send(
            Arg.Is<SealOpenOrdersCommand>(command => command.Apply), Arg.Any<CancellationToken>());
        queue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());
    }

    [Test]
    public async Task SealShift_WithoutAConfirmedBinding_RefusesAndSealsNothing()
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        shiftRepository.Get(OrderId).Returns(SealableOrder());
        var skill = new SealShiftSkill(new OrderSealingService(shiftRepository, unitOfWork, ShiftVisibilityGuards.AllowAll()));

        var result = await skill.ExecuteAsync(
            BypassedContext(), new Dictionary<string, object> { ["shiftId"] = OrderId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, SealShift));
        await shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [TestCase(SealOpenOrders)]
    [TestCase(SealShift)]
    public void EverySealSkill_ShowsAServerPreview_SoNoAgentPlanCanRunIt(string skillName)
    {
        var catalog = new ConfirmationPreviewCatalog(ConfirmationPreviewProviderFixtures.Providers());

        catalog.HasPreview(skillName).ShouldBeTrue($"'{skillName}' has no server preview provider.");
    }

    [TestCase("Geh die offenen Bestellungen mit mir durch, bevor etwas versiegelt wird.", "de")]
    [TestCase("Go through the open orders with me before anything is sealed.", "en")]
    [TestCase("Passe en revue les commandes ouvertes avec moi avant de sceller quoi que ce soit.", "fr")]
    [TestCase("Esamina con me gli ordini aperti prima di sigillare qualsiasi cosa.", "it")]
    [TestCase("Versiegle alle offenen Bestellungen.", "de")]
    [TestCase("Seal all open orders.", "en")]
    public void TheReviewRequest_IsNeverAnExplicitYes(string request, string language)
    {
        AffirmationDetector.IsExplicitConfirmation(request, language).ShouldBeFalse();
    }

    private static Shift SealableOrder() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.OriginalOrder,
        Name = "Early Bern",
        Abbreviation = "EB",
        FromDate = new DateOnly(2026, 6, 1),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 2,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), IsDeleted = false } }
    };
}
