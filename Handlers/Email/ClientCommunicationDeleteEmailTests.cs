// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class ClientDeleteReassignEmailTests
{
    private IClientRepository _clientRepository = null!;
    private IEmailClientAssignmentService _emailAssignmentService = null!;
    private ClientMapper _clientMapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Api.Application.Handlers.Clients.DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _emailAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _clientMapper = Substitute.For<ClientMapper>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<Api.Application.Handlers.Clients.DeleteCommandHandler>>();
        _handler = new Api.Application.Handlers.Clients.DeleteCommandHandler(
            _clientRepository, _emailAssignmentService, _clientMapper, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_DeleteClient_CallsReassignOrphanedEmails()
    {
        var clientId = Guid.NewGuid();
        var client = new Client { Id = clientId, Name = "Test Client" };
        _clientRepository.Get(clientId).Returns(client);

        await _handler.Handle(
            new DeleteCommand<ClientResource>(clientId), CancellationToken.None);

        await _clientRepository.Received(1).Delete(clientId);
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.Received(1).ReassignOrphanedEmailsAsync();
    }

    [Test]
    public async Task Handle_DeleteClient_ReassignsAfterUnitOfWorkComplete()
    {
        var clientId = Guid.NewGuid();
        var client = new Client { Id = clientId, Name = "Test Client" };
        _clientRepository.Get(clientId).Returns(client);

        var callOrder = new List<string>();
        _unitOfWork.CompleteAsync()
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("unitOfWork"));
        _emailAssignmentService.ReassignOrphanedEmailsAsync()
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("reassign"));

        await _handler.Handle(
            new DeleteCommand<ClientResource>(clientId), CancellationToken.None);

        callOrder.ShouldBe(new[] {"unitOfWork", "reassign"});
    }

    [Test]
    public async Task Handle_ClientNotFound_ThrowsKeyNotFoundException()
    {
        var clientId = Guid.NewGuid();
        _clientRepository.Get(clientId).Returns((Client?)null);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteCommand<ClientResource>(clientId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _emailAssignmentService.DidNotReceive().ReassignOrphanedEmailsAsync();
    }
}

[TestFixture]
public class CommunicationDeleteReassignEmailTests
{
    private ICommunicationRepository _communicationRepository = null!;
    private IEmailClientAssignmentService _emailAssignmentService = null!;
    private AddressCommunicationMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Api.Application.Handlers.Communications.DeleteCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _communicationRepository = Substitute.For<ICommunicationRepository>();
        _emailAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _mapper = new AddressCommunicationMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<Api.Application.Handlers.Communications.DeleteCommandHandler>>();
        _handler = new Api.Application.Handlers.Communications.DeleteCommandHandler(
            _communicationRepository, _emailAssignmentService, _mapper, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_DeleteCommunication_CallsReassignOrphanedEmails()
    {
        var commId = Guid.NewGuid();
        var communication = new Communication
        {
            Id = commId,
            Type = CommunicationTypeEnum.PrivateMail,
            Value = "client@test.com"
        };
        _communicationRepository.Get(commId).Returns(communication);

        await _handler.Handle(
            new DeleteCommand<CommunicationResource>(commId), CancellationToken.None);

        await _communicationRepository.Received(1).Delete(commId);
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.Received(1).ReassignOrphanedEmailsAsync();
    }

    [Test]
    public async Task Handle_DeleteCommunication_ReassignsAfterUnitOfWorkComplete()
    {
        var commId = Guid.NewGuid();
        var communication = new Communication
        {
            Id = commId,
            Type = CommunicationTypeEnum.OfficeMail,
            Value = "office@test.com"
        };
        _communicationRepository.Get(commId).Returns(communication);

        var callOrder = new List<string>();
        _unitOfWork.CompleteAsync()
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("unitOfWork"));
        _emailAssignmentService.ReassignOrphanedEmailsAsync()
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("reassign"));

        await _handler.Handle(
            new DeleteCommand<CommunicationResource>(commId), CancellationToken.None);

        callOrder.ShouldBe(new[] {"unitOfWork", "reassign"});
    }

    [Test]
    public async Task Handle_CommunicationNotFound_ThrowsKeyNotFoundException()
    {
        var commId = Guid.NewGuid();
        _communicationRepository.Get(commId).Returns((Communication?)null);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteCommand<CommunicationResource>(commId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _emailAssignmentService.DidNotReceive().ReassignOrphanedEmailsAsync();
    }
}
