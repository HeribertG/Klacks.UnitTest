// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the RestrictedTimeWindowRule PostCommandHandler: season and daily-window validation
/// runs before persistence, and that new rows always get a fresh Id and an empty
/// ImportSourceKey/ImportContentHash regardless of what the caller sends.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class PostCommandHandlerTests
{
    private IRestrictedTimeWindowRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PostCommandHandler(_repository, _mapper, _unitOfWork, Substitute.For<ILogger<PostCommandHandler>>());
    }

    private static RestrictedTimeWindowRuleResource ValidResource() => new()
    {
        SeasonFromMonth = 6,
        SeasonFromDay = 15,
        SeasonToMonth = 9,
        SeasonToDay = 15,
        DailyStart = new TimeOnly(12, 30),
        DailyEnd = new TimeOnly(15, 0),
        AppliesToGroupTag = "outdoor",
    };

    [Test]
    public async Task Handle_ValidResource_PersistsAndReturnsResource()
    {
        var result = await _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(ValidResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.AppliesToGroupTag.ShouldBe("outdoor");
        _repository.Received(1).Add(Arg.Any<RestrictedTimeWindowRule>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_ValidResource_AssignsNewId()
    {
        RestrictedTimeWindowRule? added = null;
        _repository.When(r => r.Add(Arg.Any<RestrictedTimeWindowRule>())).Do(ci => added = ci.Arg<RestrictedTimeWindowRule>());

        await _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(ValidResource()), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.Id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task Handle_ValidResource_ForcesEmptyImportKeys()
    {
        RestrictedTimeWindowRule? added = null;
        _repository.When(r => r.Add(Arg.Any<RestrictedTimeWindowRule>())).Do(ci => added = ci.Arg<RestrictedTimeWindowRule>());

        await _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(ValidResource()), CancellationToken.None);

        added.ShouldNotBeNull();
        added!.ImportSourceKey.ShouldBe(string.Empty);
        added.ImportContentHash.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_InvalidMonth_ThrowsInvalidRequest_NoPersist()
    {
        var resource = ValidResource();
        resource.SeasonFromMonth = 13;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("SeasonFromMonth must be between 1 and 12.");
        _repository.DidNotReceive().Add(Arg.Any<RestrictedTimeWindowRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmptyDailyWindow_ThrowsInvalidRequest_NoPersist()
    {
        var resource = ValidResource();
        resource.DailyEnd = resource.DailyStart;

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe("DailyStart and DailyEnd must differ; an empty daily window is not allowed.");
        _repository.DidNotReceive().Add(Arg.Any<RestrictedTimeWindowRule>());
    }

    [Test]
    public async Task Handle_NullResource_ThrowsInvalidRequest()
    {
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<RestrictedTimeWindowRuleResource>(null!), CancellationToken.None));
    }
}
