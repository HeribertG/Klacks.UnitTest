// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_expense: the skill builds an ExpensesResource from the parameters and sends
/// PostCommand&lt;ExpensesResource&gt;; a null handler result is reported as an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddExpenseSkillTests
{
    private static readonly Guid WorkId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task Add_SendsPostCommandWithSuppliedFields()
    {
        var mediator = Substitute.For<IMediator>();
        ExpensesResource? captured = null;
        mediator.Send(Arg.Do<PostCommand<ExpensesResource>>(c => captured = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var resource = ((PostCommand<ExpensesResource>)ci[0]).Resource;
                resource.Id = Guid.NewGuid();
                return resource;
            });
        var skill = new AddExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = WorkId.ToString(),
            ["amount"] = 25.50m,
            ["description"] = "Taxi to client site",
            ["taxable"] = true
        });

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.WorkId.ShouldBe(WorkId);
        captured.Amount.ShouldBe(25.50m);
        captured.Description.ShouldBe("Taxi to client site");
        captured.Taxable.ShouldBeTrue();
        result.Message.ShouldContain("added");
    }

    [Test]
    public async Task Add_DefaultsDescriptionAndTaxable()
    {
        var mediator = Substitute.For<IMediator>();
        ExpensesResource? captured = null;
        mediator.Send(Arg.Do<PostCommand<ExpensesResource>>(c => captured = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => ((PostCommand<ExpensesResource>)ci[0]).Resource);
        var skill = new AddExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = WorkId.ToString(),
            ["amount"] = 10m
        });

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Description.ShouldBe(string.Empty);
        captured.Taxable.ShouldBeFalse();
    }

    [Test]
    public async Task Add_NullHandlerResult_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<ExpensesResource>>(), Arg.Any<CancellationToken>())
            .Returns((ExpensesResource?)null);
        var skill = new AddExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = WorkId.ToString(),
            ["amount"] = 10m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no result");
    }
}
