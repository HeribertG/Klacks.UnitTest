// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PostHolidayWorkExemptionCommandHandler: persists new exemptions scoped to a
/// scheduling rule or global (null SchedulingRuleId), and ignores any import identity the caller
/// supplies since a row created through the API is customer-owned.
/// </summary>

using Klacks.Api.Application.Commands.SchedulingRules;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class PostHolidayWorkExemptionCommandHandlerTests
{
    private IHolidayWorkExemptionRuleRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostHolidayWorkExemptionCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _handler = new PostHolidayWorkExemptionCommandHandler(
            _repository, _unitOfWork, Substitute.For<ILogger<PostHolidayWorkExemptionCommandHandler>>());
    }

    private static HolidayWorkExemptionResource ValidResource() => new()
    {
        Description = "Care operations run on holidays by statute",
    };

    [Test]
    public async Task Handle_ValidResource_PersistsAndReturnsResource()
    {
        var result = await _handler.Handle(new PostHolidayWorkExemptionCommand(ValidResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Description.ShouldBe("Care operations run on holidays by statute");
        result.Id.ShouldNotBe(Guid.Empty);
        _repository.Received(1).Add(Arg.Any<HolidayWorkExemptionRule>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_WithSchedulingRuleId_ScopesTheExemptionToThatRule()
    {
        var ruleId = Guid.NewGuid();
        var resource = ValidResource();
        resource.SchedulingRuleId = ruleId;
        HolidayWorkExemptionRule? added = null;
        _repository.When(r => r.Add(Arg.Any<HolidayWorkExemptionRule>())).Do(ci => added = ci.Arg<HolidayWorkExemptionRule>());

        var result = await _handler.Handle(new PostHolidayWorkExemptionCommand(resource), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.SchedulingRuleId.ShouldBe(ruleId);
        result.SchedulingRuleId.ShouldBe(ruleId);
    }

    [Test]
    public async Task Handle_WithNullSchedulingRuleId_CreatesGlobalExemption()
    {
        var resource = ValidResource();
        resource.SchedulingRuleId = null;
        HolidayWorkExemptionRule? added = null;
        _repository.When(r => r.Add(Arg.Any<HolidayWorkExemptionRule>())).Do(ci => added = ci.Arg<HolidayWorkExemptionRule>());

        var result = await _handler.Handle(new PostHolidayWorkExemptionCommand(resource), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.SchedulingRuleId.ShouldBeNull();
        result.SchedulingRuleId.ShouldBeNull();
    }

    [Test]
    public async Task Handle_CallerSuppliedImportSourceKey_IsIgnored()
    {
        var resource = ValidResource();
        resource.ImportSourceKey = "region-setup:industryProfiles:healthcare:spoofed";
        HolidayWorkExemptionRule? added = null;
        _repository.When(r => r.Add(Arg.Any<HolidayWorkExemptionRule>())).Do(ci => added = ci.Arg<HolidayWorkExemptionRule>());

        await _handler.Handle(new PostHolidayWorkExemptionCommand(resource), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.ImportSourceKey.ShouldBeEmpty();
        added.ImportContentHash.ShouldBeEmpty();
    }
}
