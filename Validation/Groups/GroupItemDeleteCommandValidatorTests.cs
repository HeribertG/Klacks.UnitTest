using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Validation.Groups;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Groups;

[TestFixture]
public class GroupItemDeleteCommandValidatorTests
{
    private GroupItemDeleteCommandValidator _validator;
    private IGroupItemRepository _groupItemRepository;
    private IShiftRepository _shiftRepository;

    [SetUp]
    public void Setup()
    {
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();

        _groupItemRepository.Get(Arg.Any<Guid>()).Returns((GroupItem?)null);
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _shiftRepository.HasActiveWorksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _validator = new GroupItemDeleteCommandValidator(_groupItemRepository, _shiftRepository);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenGroupItemNotFound()
    {
        _groupItemRepository.Get(Arg.Any<Guid>()).Returns((GroupItem?)null);

        var command = new DeleteCommand<GroupItemResource>(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientItemHasPlannedWorks()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _groupItemRepository.Get(itemId).Returns(new GroupItem { Id = itemId, ClientId = clientId, GroupId = groupId });
        _shiftRepository.HasWorksForClientInGroupAsync(clientId, groupId, null, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new DeleteCommand<GroupItemResource>(itemId);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.group-item-has-planned-works"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenShiftItemHasActiveWorks()
    {
        var shiftId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _groupItemRepository.Get(itemId).Returns(new GroupItem { Id = itemId, ShiftId = shiftId, GroupId = Guid.NewGuid() });
        _shiftRepository.HasActiveWorksAsync(shiftId, Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new DeleteCommand<GroupItemResource>(itemId);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.group-item-has-planned-works"));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenClientItemHasNoPlannedWorks()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _groupItemRepository.Get(itemId).Returns(new GroupItem { Id = itemId, ClientId = clientId, GroupId = groupId });
        _shiftRepository.HasWorksForClientInGroupAsync(clientId, groupId, null, Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new DeleteCommand<GroupItemResource>(itemId);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }
}
