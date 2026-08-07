// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the DeleteHolidayWorkExemptionCommandHandler: removes an existing exemption and
/// commits; a repeated delete on an already-gone Id returns false without touching the unit of work.
/// SchedulingRulesController maps that false to a 404, so at the API surface a repeated delete IS an
/// error - only the handler itself treats a miss as a plain negative result rather than an exception.
/// </summary>

using Klacks.Api.Application.Commands.SchedulingRules;
using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class DeleteHolidayWorkExemptionCommandHandlerTests
{
    private IHolidayWorkExemptionRuleRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteHolidayWorkExemptionCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _handler = new DeleteHolidayWorkExemptionCommandHandler(
            _repository, _unitOfWork, Substitute.For<ILogger<DeleteHolidayWorkExemptionCommandHandler>>());
    }

    [Test]
    public async Task Handle_ExistingRule_DeletesAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        var existing = new HolidayWorkExemptionRule { Id = id, Description = "Security detail" };
        _repository.DeleteAsync(id).Returns(existing);

        var result = await _handler.Handle(new DeleteHolidayWorkExemptionCommand(id), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).DeleteAsync(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_UnknownId_ReturnsFalse_NoCommit()
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id).Returns((HolidayWorkExemptionRule?)null);

        var result = await _handler.Handle(new DeleteHolidayWorkExemptionCommand(id), CancellationToken.None);

        result.ShouldBeFalse();
        await _repository.Received(1).DeleteAsync(id);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
