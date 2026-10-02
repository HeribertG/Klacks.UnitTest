// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Schedules;

/// <summary>
/// Covers that the cover-absence endpoint hands the planner's language on to the command, so the proposal
/// scenario name comes out in that language; without one the handler falls back to the installation language.
/// </summary>
[TestFixture]
public sealed class RecoveryControllerTests
{
    private IMediator _mediator = null!;
    private RecoveryController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator
            .Send(Arg.Any<CoverAbsenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CoverAbsenceOutcome(Guid.NewGuid(), Guid.NewGuid(), "Cover", [], [], []));
        _sut = new RecoveryController(_mediator);
    }

    [Test]
    public async Task CoverAbsence_ForwardsLanguageToCommand()
    {
        var request = new CoverAbsenceRequest(Guid.NewGuid(), new DateOnly(2026, 10, 5), Guid.NewGuid(), Guid.NewGuid(), Language: "it");

        var result = await _sut.CoverAbsence(request, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c => c.Language == "it" && c.ClientId == request.ClientId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CoverAbsence_WithoutLanguage_PassesNullSoTheInstallationLanguageApplies()
    {
        var request = new CoverAbsenceRequest(Guid.NewGuid(), new DateOnly(2026, 10, 5), Guid.NewGuid(), Guid.NewGuid());

        await _sut.CoverAbsence(request, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c => c.Language == null),
            Arg.Any<CancellationToken>());
    }
}
