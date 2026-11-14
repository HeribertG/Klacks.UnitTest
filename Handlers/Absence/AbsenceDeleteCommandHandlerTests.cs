// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Deleting an absence type that is still referenced is refused, and the message names the type by its text.
/// Nothing is deleted in that case.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Absences;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using AbsenceType = Klacks.Api.Domain.Models.Schedules.Absence;

namespace Klacks.UnitTest.Handlers.Absence;

[TestFixture]
public class AbsenceDeleteCommandHandlerTests
{
    private const string GermanName = "Ferien";
    private const int BreakCount = 2;

    private IAbsenceRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AbsenceType _type = null!;

    [SetUp]
    public void SetUp()
    {
        _type = new AbsenceType
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = GermanName },
            Abbreviation = new MultiLanguage { De = "FE" },
            Description = new MultiLanguage()
        };
        _repository = Substitute.For<IAbsenceRepository>();
        _repository.Get(_type.Id).Returns(_ => _type);
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    [Test]
    public async Task Delete_AbsenceInUse_NamesTheTypeByItsTextAndDeletesNothing()
    {
        _repository.CountActiveBreaksByAbsenceAsync(_type.Id, Arg.Any<CancellationToken>()).Returns(BreakCount);
        var handler = NewHandler();

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => handler.Handle(new DeleteCommand<AbsenceResource>(_type.Id), CancellationToken.None));

        ex.Message.ShouldContain($"'{GermanName}'");
        ex.Message.ShouldNotContain(nameof(MultiLanguage));
        await _repository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    private DeleteCommandHandler NewHandler() => new(
        new SettingsMapper(), _repository, _unitOfWork, Substitute.For<ILogger<DeleteCommandHandler>>());
}
