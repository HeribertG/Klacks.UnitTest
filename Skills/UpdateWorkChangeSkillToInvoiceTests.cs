// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The billable flag of a WorkChange changed through UpdateWorkChangeSkill: switching it off (bool or text) is refused
/// before the entry is even read, switching it on is still allowed.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateWorkChangeSkillToInvoiceTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static IMediator MediatorReturning(WorkChangeResource change)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetWorkChangeInScopeQuery>(), Arg.Any<CancellationToken>()).Returns(change);
        mediator.Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(call => ((PutCommand<WorkChangeResource>)call[0]).Resource);
        return mediator;
    }

    [TestCase(false)]
    [TestCase("false")]
    public async Task ToInvoiceFalse_IsRefusedWithoutReadOrPut(object value)
    {
        var mediator = MediatorReturning(new WorkChangeResource { Id = Guid.NewGuid(), ToInvoice = true });
        var skill = new UpdateWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = Guid.NewGuid().ToString(),
            ["toInvoice"] = value
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("planner", Case.Insensitive);
        result.Message.ShouldContain("supervisor", Case.Insensitive);
        result.Message.ShouldContain("admin", Case.Insensitive);
        await mediator.DidNotReceive().Send(Arg.Any<GetWorkChangeInScopeQuery>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToInvoiceTrue_SwitchesAnUnbillableEntryOn()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorReturning(new WorkChangeResource { Id = id, ToInvoice = false });
        var skill = new UpdateWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = id.ToString(),
            ["toInvoice"] = true
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c => c.Resource.Id == id && c.Resource.ToInvoice),
            Arg.Any<CancellationToken>());
    }
}
