// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetProactiveGovernanceQueryHandler. The handler folds three resolver reads into one DTO:
/// the kill switch, the global autonomy level (with the cap it maps to) and the per-kind decisions.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetProactiveGovernanceQueryHandlerTests
{
    private IProactiveGovernanceResolver _resolver = null!;
    private GetProactiveGovernanceQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<IProactiveGovernanceResolver>();
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>());

        _sut = new GetProactiveGovernanceQueryHandler(_resolver);
    }

    [Test]
    public async Task Handle_MapsTheGlobalAutonomyLevelAndItsCapIntoTheDto()
    {
        // Arrange
        _resolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Autonomous);
        _resolver.ResolveAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProactiveGovernanceDecision>
            {
                new(TriggerKind: "unstaffed_shift",
                    GroupId: null,
                    EffectiveMaxAction: ProactiveMaxAction.Execute,
                    ConfiguredMaxAction: ProactiveMaxAction.Execute,
                    Enabled: true,
                    KillSwitchActive: false,
                    ResponsibleOwnerUserId: null,
                    DailyActionBudget: 5,
                    WindowActionLimit: 3,
                    WindowMinutes: 60,
                    IsStored: false,
                    GlobalAutonomyCap: ProactiveGovernanceDefaults.MapAutonomyLevel(AutonomyLevel.Autonomous))
            });

        // Act
        var dto = await _sut.Handle(new GetProactiveGovernanceQuery(), CancellationToken.None);

        // Assert
        dto.GlobalAutonomyLevel.ShouldBe((int)AutonomyLevel.Autonomous);
        dto.GlobalAutonomyCap.ShouldBe((int)ProactiveMaxAction.Execute);
        dto.Rules.Count.ShouldBe(1);
        dto.Rules[0].GlobalAutonomyCap.ShouldBe((int)ProactiveMaxAction.Execute);
    }
}
