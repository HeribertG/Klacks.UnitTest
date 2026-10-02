// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Schedules;

/// <summary>
/// Covers that the apply endpoint hands the planner's language on to the apply service, so the generated
/// scenario name comes out in that language; without one the service falls back to the installation language.
/// </summary>
[TestFixture]
public sealed class HarmonizerControllerTests
{
    private IHarmonizerApplyService _applyService = null!;
    private HarmonizerController _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _applyService = Substitute.For<IHarmonizerApplyService>();
        _sut = new HarmonizerController(
            Substitute.For<IHarmonizerJobRunner>(),
            _applyService,
            JobTerminalStateCacheTestFactory.Create<HarmonizerJobResultDto>());
    }

    [Test]
    public async Task ApplyAsScenario_ForwardsLanguageToApplyService()
    {
        var jobId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _applyService
            .ApplyAsScenarioAsync(jobId, groupId, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), "de", Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((new AnalyseScenarioResource { Name = "Harmonisiert" }, (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        var result = await _sut.ApplyAsScenario(new ApplyHarmonizerAsScenarioRequest(jobId, groupId, "de"), CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _applyService.Received(1)
            .ApplyAsScenarioAsync(jobId, groupId, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), "de", Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ApplyAsScenario_WithoutLanguage_PassesNullSoTheInstallationLanguageApplies()
    {
        var jobId = Guid.NewGuid();
        _applyService
            .ApplyAsScenarioAsync(jobId, null, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns((new AnalyseScenarioResource { Name = "Harmonized" }, (IReadOnlyList<Guid>)[], (ScenarioComplianceReport?)null));

        await _sut.ApplyAsScenario(new ApplyHarmonizerAsScenarioRequest(jobId, null), CancellationToken.None);

        await _applyService.Received(1)
            .ApplyAsScenarioAsync(jobId, null, Arg.Any<CancellationToken>(), Arg.Any<ScenarioNameKind?>(), null, Arg.Any<bool>(), Arg.Any<bool>());
    }
}
