// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// seal_shift on an order without a group (owner decision 2026-10-25): the preview derives the group from the customer's
/// address with the rule of assign_orders_to_groups (IOrderGroupPlacementResolver), shows it and binds it, and the
/// confirmed replay attaches exactly that group and seals. A derived group that changed since the preview, or a group the
/// order got of its own in the meantime, voids the confirmation. When no group can be derived, the preview refuses with
/// the reason and tells the model to ask the user for the group (list_groups, add_shift_to_group, seal_shift again).
/// An order that already has a group is sealed as before: the resolver is never asked and the binding names the order only.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealShiftDerivedGroupTests
{
    private const string BernGroupName = "Bern";
    private const string CityMatch = "city name (main address)";
    private const string UnplacedReason = "the customer has no address with a city, a state or coordinates";

    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid PlannableShiftId = Guid.NewGuid();
    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid OtherGroupId = Guid.NewGuid();
    private static readonly DateOnly FromDate = new(2026, 6, 1);

    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOrderGroupPlacementResolver _placement = null!;
    private SealShiftSkill _skill = null!;
    private SealShiftConfirmationPreviewProvider _preview = null!;
    private Shift? _sealed;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>()).Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        _placement = Substitute.For<IOrderGroupPlacementResolver>();
        _placement.ResolveAsync(OrderId, Arg.Any<CancellationToken>()).Returns(Placed(BernGroupId));

        var sealing = new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll());
        var targets = new SealShiftTargetResolver(sealing, _placement);
        _skill = new SealShiftSkill(targets, sealing);
        _preview = new SealShiftConfirmationPreviewProvider(targets);

        _shiftRepository.Get(OrderId).Returns(_ => OrderWithoutGroup());
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns(ci =>
        {
            _sealed = ci.Arg<Shift>();
            return new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift, Name = _sealed.Name };
        });
        _shiftRepository.GetNoTracking(OrderId).Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(PlannableShiftId)
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift });
        _shiftRepository.GetGroupsForShift(PlannableShiftId).Returns(new List<Group> { new() { Id = BernGroupId, Name = BernGroupName } });
    }

    private static OrderGroupPlacement Placed(Guid groupId) => new(
        new OrderGroupAssignment(OrderId, "Nachtdienst Weiss", "Weiss AG", groupId, BernGroupName, CityMatch, null), null);

    private static Shift OrderWithoutGroup() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.OriginalOrder,
        Name = "Nachtdienst Weiss",
        Abbreviation = "ND",
        FromDate = FromDate,
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
        StartShift = new TimeOnly(7, 0),
        EndShift = new TimeOnly(7, 0),
        GroupItems = new List<GroupItem>()
    };

    private static SkillExecutionContext Ctx(ConfirmationBinding? binding) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        ConfirmedActionBinding = binding
    };

    private static Dictionary<string, object> Params() => new() { [SealShiftSkill.ShiftIdParameter] = OrderId.ToString() };

    [Test]
    public async Task Preview_ShowsTheDerivedGroup_AndBindsIt()
    {
        var preview = await _preview.BuildAsync(SealShiftSkill.SkillName, Ctx(null), Params());

        preview.IsRefusal.ShouldBeFalse();
        preview.Text.ShouldContain($"'{BernGroupName}'");
        preview.Text.ShouldContain(CityMatch);
        preview.Binding.ShouldNotBeNull();
        preview.Binding!.Matches(SealShiftSkill.BindingFor(OrderId, BernGroupId)).ShouldBeTrue();
        preview.Binding.Matches(SealShiftSkill.BindingFor(OrderId)).ShouldBeFalse("the derived group is part of the binding");
    }

    [Test]
    public async Task Preview_OfAnOrderWithItsOwnGroup_BindsTheOrderOnly_AndDerivesNothing()
    {
        var order = OrderWithoutGroup();
        order.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = OtherGroupId });
        _shiftRepository.Get(OrderId).Returns(order);

        var preview = await _preview.BuildAsync(SealShiftSkill.SkillName, Ctx(null), Params());

        preview.IsRefusal.ShouldBeFalse();
        preview.Binding!.Matches(SealShiftSkill.BindingFor(OrderId)).ShouldBeTrue();
        await _placement.DidNotReceive().ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_RefusesWithTheAskForTheGroupHint_WhenNoGroupCanBeDerived()
    {
        _placement.ResolveAsync(OrderId, Arg.Any<CancellationToken>()).Returns(new OrderGroupPlacement(null, UnplacedReason));

        var preview = await _preview.BuildAsync(SealShiftSkill.SkillName, Ctx(null), Params());

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldContain("at least one group");
        preview.Text.ShouldContain(UnplacedReason);
        preview.Text.ShouldContain("list_groups");
        preview.Text.ShouldContain("add_shift_to_group");
    }

    [Test]
    public async Task ConfirmedReplay_AttachesTheDerivedGroupFromTheOrderStart_AndSeals()
    {
        var result = await _skill.ExecuteAsync(Ctx(SealShiftSkill.BindingFor(OrderId, BernGroupId)), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain(BernGroupName);
        _sealed.ShouldNotBeNull();
        _sealed!.Status.ShouldBe(ShiftStatus.SealedOrder);
        var link = _sealed.GroupItems.ShouldHaveSingleItem();
        link.GroupId.ShouldBe(BernGroupId);
        link.ValidFrom.ShouldBe(FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Test]
    public async Task ConfirmedReplay_Refuses_WhenTheDerivedGroupChangedSinceThePreview()
    {
        _placement.ResolveAsync(OrderId, Arg.Any<CancellationToken>()).Returns(Placed(OtherGroupId));

        var result = await _skill.ExecuteAsync(Ctx(SealShiftSkill.BindingFor(OrderId, BernGroupId)), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing was changed");
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task ConfirmedReplay_Refuses_WhenTheOrderGotAGroupOfItsOwnSinceThePreview()
    {
        var order = OrderWithoutGroup();
        order.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = OtherGroupId });
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(SealShiftSkill.BindingFor(OrderId, BernGroupId)), Params());

        result.Success.ShouldBeFalse();
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task SealWithAGroupToAttach_RefusesAnOrderThatAlreadyHasAGroup_AndNeverAddsASecondOne()
    {
        var order = OrderWithoutGroup();
        order.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = OtherGroupId });
        _shiftRepository.Get(OrderId).Returns(order);
        var sealing = new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll());

        var outcome = await sealing.SealAsync(OrderId, BernGroupId);

        outcome.IsSealed.ShouldBeFalse();
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task ConfirmedReplay_RollsBack_WhenTheGroupLinkOfThePlannableShiftCannotBeConfirmed()
    {
        _shiftRepository.GetGroupsForShift(PlannableShiftId).Returns(new List<Group>());

        var result = await _skill.ExecuteAsync(Ctx(SealShiftSkill.BindingFor(OrderId, BernGroupId)), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("rolled back");
    }
}
