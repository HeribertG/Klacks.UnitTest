// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

using ClientDeleteCommandHandler = Klacks.Api.Application.Handlers.Clients.DeleteCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class DeleteClientCommandHandlerStandInTests
{
    private IClientRepository _clientRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ClientDeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _handler = new ClientDeleteCommandHandler(
            _clientRepository,
            _workRepository,
            Substitute.For<IEmailClientAssignmentService>(),
            Substitute.For<ClientMapper>(),
            _unitOfWork,
            Substitute.For<ILogger<ClientDeleteCommandHandler>>());
    }

    private Client ClientOf(EntityTypeEnum type, int realPlanWorks)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "Fair 1", Type = type };
        _clientRepository.Get(client.Id).Returns(client);
        _workRepository.CountRealPlanWorksOfClientAsync(client.Id, Arg.Any<CancellationToken>()).Returns(realPlanWorks);
        return client;
    }

    [Test]
    public async Task Delete_StandInWithRealPlanWorks_IsRefused_NothingDeleted()
    {
        var standIn = ClientOf(EntityTypeEnum.StandIn, realPlanWorks: 3);

        var refusal = await Should.ThrowAsync<StandInHasWorksException>(
            () => _handler.Handle(new DeleteCommand<ClientResource>(standIn.Id), CancellationToken.None));

        refusal.WorkCount.ShouldBe(3);
        refusal.ConflictCode.ShouldBe(StandInHasWorksException.ErrorCode);
        refusal.ConflictDetails![StandInHasWorksException.StandInNameKey].ShouldBe("Fair 1");
        await _clientRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_StandInWithoutRealPlanWorks_IsDeleted()
    {
        var standIn = ClientOf(EntityTypeEnum.StandIn, realPlanWorks: 0);

        await _handler.Handle(new DeleteCommand<ClientResource>(standIn.Id), CancellationToken.None);

        await _clientRepository.Received(1).Delete(standIn.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_EmployeeWithRealPlanWorks_IsDeletedAsBefore()
    {
        var employee = ClientOf(EntityTypeEnum.Employee, realPlanWorks: 3);

        await _handler.Handle(new DeleteCommand<ClientResource>(employee.Id), CancellationToken.None);

        await _clientRepository.Received(1).Delete(employee.Id);
        await _workRepository.DidNotReceiveWithAnyArgs().CountRealPlanWorksOfClientAsync(default, default);
    }
}
