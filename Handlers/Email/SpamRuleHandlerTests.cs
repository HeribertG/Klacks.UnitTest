// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class CreateSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IEmailReclassificationTrigger _reclassificationTrigger = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CreateSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _reclassificationTrigger = Substitute.For<IEmailReclassificationTrigger>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<CreateSpamRuleCommandHandler>>();
        _handler = new CreateSpamRuleCommandHandler(
            _repository, _reclassificationTrigger, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_NewRule_CreatesRuleAndTriggersReclassification()
    {
        _repository.GetAllAsync().Returns(new List<SpamRule>());

        var result = await _handler.Handle(
            new CreateSpamRuleCommand(SpamRuleType.SenderContains, "spam@test.com"), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).AddAsync(Arg.Is<SpamRule>(r =>
            r.RuleType == SpamRuleType.SenderContains &&
            r.Pattern == "spam@test.com" &&
            r.IsActive &&
            r.SortOrder == 1));
        await _unitOfWork.Received(1).CompleteAsync();
        _reclassificationTrigger.Received(1).TriggerReclassification();
    }

    [Test]
    public async Task Handle_NewRule_SetsSortOrderToMaxPlusOne()
    {
        _repository.GetAllAsync().Returns(new List<SpamRule>
        {
            new() { SortOrder = 3 },
            new() { SortOrder = 5 }
        });

        var result = await _handler.Handle(
            new CreateSpamRuleCommand(SpamRuleType.SenderDomain, "test.com"), CancellationToken.None);

        result.ShouldNotBeNull();
        await _repository.Received(1).AddAsync(Arg.Is<SpamRule>(r => r.SortOrder == 6));
    }
}

[TestFixture]
public class DeleteSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IEmailReclassificationTrigger _reclassificationTrigger = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _reclassificationTrigger = Substitute.For<IEmailReclassificationTrigger>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<DeleteSpamRuleCommandHandler>>();
        _handler = new DeleteSpamRuleCommandHandler(
            _repository, _reclassificationTrigger, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_DeleteRule_DeletesAndTriggersReclassification()
    {
        var ruleId = Guid.NewGuid();

        var result = await _handler.Handle(new DeleteSpamRuleCommand(ruleId), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).DeleteAsync(ruleId);
        await _unitOfWork.Received(1).CompleteAsync();
        _reclassificationTrigger.Received(1).TriggerReclassification();
    }
}

[TestFixture]
public class UpdateSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IEmailReclassificationTrigger _reclassificationTrigger = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _reclassificationTrigger = Substitute.For<IEmailReclassificationTrigger>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<UpdateSpamRuleCommandHandler>>();
        _handler = new UpdateSpamRuleCommandHandler(
            _repository, _reclassificationTrigger, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_UpdateRule_UpdatesAndTriggersReclassification()
    {
        var ruleId = Guid.NewGuid();
        var rule = new SpamRule
        {
            Id = ruleId,
            RuleType = SpamRuleType.SenderContains,
            Pattern = "old@test.com",
            IsActive = true,
            SortOrder = 1
        };
        _repository.GetByIdAsync(ruleId).Returns(rule);

        var result = await _handler.Handle(
            new UpdateSpamRuleCommand(ruleId, SpamRuleType.SenderDomain, "new.com", true, 2),
            CancellationToken.None);

        result.ShouldNotBeNull();
        rule.RuleType.ShouldBe(SpamRuleType.SenderDomain);
        rule.Pattern.ShouldBe("new.com");
        rule.SortOrder.ShouldBe(2);
        await _repository.Received(1).UpdateAsync(rule);
        await _unitOfWork.Received(1).CompleteAsync();
        _reclassificationTrigger.Received(1).TriggerReclassification();
    }

    [Test]
    public async Task Handle_RuleNotFound_ThrowsKeyNotFoundException()
    {
        var ruleId = Guid.NewGuid();
        _repository.GetByIdAsync(ruleId).Returns((SpamRule?)null);

        Func<Task> act = async () => await _handler.Handle(
            new UpdateSpamRuleCommand(ruleId, SpamRuleType.SenderContains, "test", true, 1),
            CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<SpamRule>());
        _reclassificationTrigger.DidNotReceive().TriggerReclassification();
    }
}
