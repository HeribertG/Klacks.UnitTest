// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Commit of an employee import: refuses while rows have errors, refuses a token that was already
/// committed before evaluating (also when the token index trips inside the transaction, while any other
/// unique violation is not reported as "already committed"), writes the batch and every ready client
/// inside one transaction with entry and exit date on membership, contract, group and address, skips
/// duplicate rows, then re-assigns inbox mail and offers the addresses to the geocoding queue without
/// waiting and without failing the committed import.
/// </summary>

using Klacks.Api.Application.Commands.ClientImport;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ClientImport;
using Klacks.Api.Application.Interfaces.ClientImport;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Email;
using Microsoft.Extensions.Logging.Abstractions;
using T = Klacks.Api.Domain.Enums.ClientImportTarget;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class CommitClientImportCommandHandlerTests
{
    private IClientRepository _clientRepository = null!;
    private IClientImportBatchRepository _batchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IEmailClientAssignmentService _emailAssignment = null!;
    private IAddressGeocodingQueue _queue = null!;
    private IClientImportLookupRepository _lookup = null!;
    private CommitClientImportCommandHandler _handler = null!;
    private List<Client> _added = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _batchRepository = Substitute.For<IClientImportBatchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _emailAssignment = Substitute.For<IEmailClientAssignmentService>();
        _queue = Substitute.For<IAddressGeocodingQueue>();
        _added = [];

        _clientRepository.Add(Arg.Do<Client>(_added.Add)).Returns(Task.CompletedTask);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>()).Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _queue.TryQueue(Arg.Any<Guid>()).Returns(true);

        var (evaluator, lookup) = ClientImportTestData.Evaluator();
        _lookup = lookup;
        _handler = new CommitClientImportCommandHandler(
            evaluator, _clientRepository, _batchRepository, _unitOfWork, _emailAssignment, _queue,
            NullLogger<CommitClientImportCommandHandler>.Instance);
    }

    [Test]
    public async Task ReadyRows_AreWrittenInOneTransaction()
    {
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Street, T.ZipCity, T.Email, T.EntryDate, T.Contract, T.Group, T.Note],
            ["Anna", "Muster", "w", "Hauptstrasse 1", "8000 Zürich", "anna@example.com", "01.03.2024", "Vollzeit", "Zürich", "Teamleiterin"],
            ["", "", "", "", "", "", "", "", "", ""]);
        request.RowOverrides.Add(new() { RowIndex = 1, Skip = true });

        var result = await _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        result.GeocodingQueued.ShouldBe(1);
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _unitOfWork.Received(1).CompleteAsync();
        await _batchRepository.Received(1).AddAsync(
            Arg.Is<ClientImportBatch>(b => b.Token == request.Token && b.RowCount == 2 && b.CreatedCount == 1 && b.FileName == "staff.csv"),
            Arg.Any<CancellationToken>());

        var client = _added.Single();
        var entry = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        client.Type.ShouldBe(EntityTypeEnum.Employee);
        client.Gender.ShouldBe(GenderEnum.Female);
        client.IdNumber.ShouldBe(0);
        client.Membership!.ValidFrom.ShouldBe(entry);
        client.Addresses.Single().ValidFrom.ShouldBe(entry);
        client.Addresses.Single().Country.ShouldBe("CH");
        client.ClientContracts.Single().FromDate.ShouldBe(new DateOnly(2024, 3, 1));
        client.ClientContracts.Single().IsActive.ShouldBeTrue();
        client.GroupItems.Single().ValidFrom.ShouldBe(entry);
        client.Communications.Single().Type.ShouldBe(CommunicationTypeEnum.PrivateMail);
        client.Annotations.Single().Note.ShouldBe("Teamleiterin");

        await _emailAssignment.Received(1).AssignInboxEmailsToClientsAsync();
        _queue.Received(1).TryQueue(client.Addresses.Single().Id);
    }

    [Test]
    public async Task RowsWithErrors_BlockTheWholeCommit()
    {
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender], ["Anna", "Muster", "w"], ["", "Ohne", ""]);

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(
            () => _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None));

        exception.Code.ShouldBe(ClientImportErrorCodes.RowsHaveErrors);
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task AlreadyCommittedToken_IsAConflict()
    {
        _batchRepository.ExistsByTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender], ["Anna", "Muster", "w"]);

        var exception = await Should.ThrowAsync<ClientImportConflictException>(
            () => _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None));

        exception.Code.ShouldBe(ClientImportErrorCodes.AlreadyCommitted);
        _added.ShouldBeEmpty();
        await _lookup.DidNotReceive().GetContractsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingToken_IsRejectedBeforeTheDatabaseIsAsked()
    {
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender], ["Anna", "Muster", "w"]);
        request.Token = Guid.Empty;

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(
            () => _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None));

        exception.Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);
        await _batchRepository.DidNotReceive().ExistsByTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UniqueTokenViolationInsideTheTransaction_IsAConflict()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(
            new DatabaseUpdateException("duplicate", isDuplicate: true, constraintName: ClientImportDatabaseNames.TokenIndex)));
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender], ["Anna", "Muster", "w"]);

        await Should.ThrowAsync<ClientImportConflictException>(
            () => _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None));

        await _emailAssignment.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task OtherUniqueViolation_IsNotReportedAsAlreadyCommitted()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(
            new DatabaseUpdateException("duplicate", isDuplicate: true, constraintName: "ix_client_id_number")));
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender], ["Anna", "Muster", "w"]);

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None));

        exception.ShouldNotBeOfType<ClientImportConflictException>();
        exception.ShouldNotBeOfType<ClientImportRejectedException>();
        await _emailAssignment.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task ExitDate_EndsMembershipContractAndGroupMembership()
    {
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.EntryDate, T.ExitDate, T.Contract, T.Group],
            ["Anna", "Muster", "w", "01.03.2024", "31.12.2026", "Vollzeit", "Zürich"]);

        await _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        var client = _added.Single();
        var exit = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        client.Membership!.ValidUntil.ShouldBe(exit);
        client.ClientContracts.Single().UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
        client.GroupItems.Single().ValidUntil.ShouldBe(exit);
    }

    [Test]
    public async Task DuplicateRow_IsSkipped_AndCountedInTheBatch()
    {
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Email],
            ["Anna", "Muster", "w", "anna@example.com"],
            ["Anna", "Muster-Meier", "w", "ANNA@example.com"]);

        var result = await _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        _added.Single().Name.ShouldBe("Muster");
        await _batchRepository.Received(1).AddAsync(
            Arg.Is<ClientImportBatch>(b => b.RowCount == 2 && b.CreatedCount == 1 && b.SkippedCount == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingFollowUps_DoNotFailTheCommittedImport()
    {
        _emailAssignment.AssignInboxEmailsToClientsAsync().Returns(Task.FromException(new InvalidOperationException("mail down")));
        _queue.TryQueue(Arg.Any<Guid>()).Returns(_ => throw new InvalidOperationException("queue down"));
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.City, T.Email], ["Anna", "Muster", "w", "Bern", "anna@example.com"]);

        var result = await _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        result.Created.ShouldBe(1);
        result.GeocodingQueued.ShouldBe(0);
    }

    [Test]
    public async Task DisabledGeocodingQueue_IsReportedAsNothingQueued()
    {
        _queue.TryQueue(Arg.Any<Guid>()).Returns(false);
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.City], ["Anna", "Muster", "w", "Bern"]);

        var result = await _handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        result.GeocodingQueued.ShouldBe(0);
        await _emailAssignment.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }
}
