// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateShiftSkill's asDraft parameter: a draft order is created with status
/// OriginalOrder and skips the sealed-order reuse lookup (which only makes sense once an order is
/// already sealed), while omitting asDraft keeps the pre-existing sealed-on-create behavior unchanged.
/// </summary>

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateShiftSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CreateShiftSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new CreateShiftSkill(_shiftRepository, _groupRepository, _clientRepository, _mediator, _unitOfWork);

        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = ClientId, Name = "Müller", Type = EntityTypeEnum.Customer } });
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>().AsEnumerable());
        _shiftRepository.FindReusableUncutOrderAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>())
            .Returns((Shift?)null);
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci => ci.Arg<Shift>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params(bool? asDraft = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Reinigung Müller",
            ["clientId"] = ClientId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "15:00",
            ["fromDate"] = "2026-06-01"
        };

        if (asDraft.HasValue)
        {
            parameters["asDraft"] = asDraft.Value;
        }

        return parameters;
    }

    [Test]
    public async Task CreatesDraft_WithOriginalOrderStatus_AndSkipsReuseLookup_WhenAsDraftTrue()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(asDraft: true));

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("DRAFT"));
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Status == ShiftStatus.OriginalOrder));
        await _shiftRepository.DidNotReceive().FindReusableUncutOrderAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreatesSealedOrder_AsBefore_WhenAsDraftOmitted()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Not.Contain("DRAFT"));
        Assert.That(result.Message, Does.Contain("NEXT STEP"));
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Status == ShiftStatus.SealedOrder));
        await _shiftRepository.Received(1).FindReusableUncutOrderAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>());
    }
}
