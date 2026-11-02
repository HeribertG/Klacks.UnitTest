// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The billable flag of a WorkChange created through AddWorkChangeSkill: omitted means on, true stays on, and an
/// attempt to switch it off (bool or text) is refused with a message naming who may do it, before any lookup or post.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddWorkChangeSkillToInvoiceTests
{
    private static readonly Guid WorkId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IWorkRepository _workRepository = null!;
    private AddWorkChangeSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.Get(WorkId).Returns(new Work { Id = WorkId, ClientId = Guid.NewGuid() });
        _mediator.Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource { Id = Guid.NewGuid() });
        _skill = new AddWorkChangeSkill(
            _mediator, _workRepository, Substitute.For<IClientRepository>(), SkillClientVisibility.AllVisible());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["workId"] = WorkId.ToString(),
        ["type"] = "CorrectionEnd",
        ["startTime"] = "16:00",
        ["endTime"] = "17:00",
        ["changeTime"] = 1m
    };

    [Test]
    public async Task ToInvoiceOmitted_PostsBillableWorkChange()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.ToInvoice),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToInvoiceTrue_PostsBillableWorkChange()
    {
        var parameters = Params();
        parameters["toInvoice"] = true;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.ToInvoice),
            Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase("false")]
    [TestCase("False")]
    public async Task ToInvoiceFalse_IsRefusedWithoutLookupOrPost(object value)
    {
        var parameters = Params();
        parameters["toInvoice"] = value;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("planner", Case.Insensitive);
        result.Message.ShouldContain("supervisor", Case.Insensitive);
        result.Message.ShouldContain("admin", Case.Insensitive);
        await _workRepository.DidNotReceive().Get(Arg.Any<Guid>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }
}
