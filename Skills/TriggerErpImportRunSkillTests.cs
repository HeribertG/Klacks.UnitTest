// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for trigger_erp_import_run: sends TriggerErpImportRunCommand and reports success.
/// </summary>

using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class TriggerErpImportRunSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [Test]
    public async Task Execute_SendsTriggerCommand_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new TriggerErpImportRunSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }
}
