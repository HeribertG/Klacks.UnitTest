// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the Contract DeleteCommandHandler: a contract that is still assigned to employees with an active or
/// future assignment is refused with a ConflictException and nothing is deleted or committed; a contract without
/// such assignments is soft-deleted. The company's current day, not the server's, is what the repository counts against.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Contracts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Contracts;

[TestFixture]
public class DeleteCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    private IContractRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private DeleteCommandHandler _handler = null!;
    private Guid _contractId;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IContractRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _contractId = Guid.NewGuid();
        _repository.Get(_contractId).Returns(new Contract { Id = _contractId, Name = "Vollzeit 160" });

        _handler = new DeleteCommandHandler(
            _repository,
            new ScheduleMapper(),
            _unitOfWork,
            _companyClock,
            Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    [Test]
    public async Task ContractWithActiveOrFutureAssignments_IsRefused_NothingDeletedNothingCommitted()
    {
        _repository.CountActiveOrFutureAssignmentsAsync(_contractId, Arg.Any<DateOnly>()).Returns(3);

        var exception = await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(new DeleteCommand<ContractResource>(_contractId), CancellationToken.None));

        exception.Message.ShouldContain("Vollzeit 160");
        exception.Message.ShouldContain("3 employee(s)");
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ContractWithoutAssignmentsInUse_IsSoftDeletedAndCommitted()
    {
        _repository.CountActiveOrFutureAssignmentsAsync(_contractId, Arg.Any<DateOnly>()).Returns(0);

        var result = await _handler.Handle(new DeleteCommand<ContractResource>(_contractId), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Vollzeit 160");
        await _repository.Received(1).Delete(_contractId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task TheRepositoryCountsAgainstTheCompanyDay()
    {
        _repository.CountActiveOrFutureAssignmentsAsync(_contractId, Arg.Any<DateOnly>()).Returns(0);

        await _handler.Handle(new DeleteCommand<ContractResource>(_contractId), CancellationToken.None);

        await _repository.Received(1).CountActiveOrFutureAssignmentsAsync(_contractId, new DateOnly(2026, 10, 26));
    }

    [Test]
    public async Task UnknownContract_IsReportedAsNotFound_BeforeAnyAssignmentCount()
    {
        var unknownId = Guid.NewGuid();
        _repository.Get(unknownId).Returns((Contract?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new DeleteCommand<ContractResource>(unknownId), CancellationToken.None));

        await _repository.DidNotReceive().CountActiveOrFutureAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }
}
