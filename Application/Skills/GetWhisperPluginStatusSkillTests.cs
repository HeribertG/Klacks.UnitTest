// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GetWhisperPluginStatusSkill: the result carries the engines this installation can run
/// (SupportedAliases) and the Parakeet language coverage next to the installed state.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class GetWhisperPluginStatusSkillTests
{
    [Test]
    public async Task ExecuteAsync_ExposesSupportedAliasesAndParakeetLanguages()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetWhisperPluginStatusQuery>(), Arg.Any<CancellationToken>()).Returns(new WhisperPluginStatus
        {
            SupportedAliases = [WhisperPluginConstants.ModelAliasParakeet],
            ParakeetLanguages = [.. ParakeetLanguageCoverage.Languages],
        });
        var skill = new GetWhisperPluginStatusSkill(mediator);

        var result = await skill.ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.Empty, UserName = "admin", UserPermissions = new List<string>() },
            new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = result.Data.ShouldNotBeNull();
        var type = data.GetType();
        ((List<string>)type.GetProperty("SupportedAliases")!.GetValue(data)!).ShouldBe([WhisperPluginConstants.ModelAliasParakeet]);
        ((List<string>)type.GetProperty("ParakeetLanguages")!.GetValue(data)!).Count.ShouldBe(25);
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasParakeet);
    }
}
