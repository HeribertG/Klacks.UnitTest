using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Groups;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Groups;

[TestFixture]
public class PutCommandValidatorTests
{
    private PutCommandValidator _validator;
    private IShiftRepository _shiftRepository;
    private IGroupRepository _groupRepository;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();

        _shiftRepository.HasWorksForGroupAsync(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _groupRepository.Get(Arg.Any<Guid>())
            .Returns(Task.FromException<Group>(new KeyNotFoundException()));

        _validator = new PutCommandValidator(_shiftRepository, _groupRepository);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenNameIsEmpty()
    {
        var groupResource = new GroupResource { Name = string.Empty, ValidFrom = DateTime.Now };
        var command = new PutCommand<GroupResource>(groupResource);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Name is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenValidFromIsDefault()
    {
        var groupResource = new GroupResource { Name = "Valid Name", ValidFrom = default };
        var command = new PutCommand<GroupResource>(groupResource);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "ValidFrom: Valid date is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenWorksExistAfterValidUntil()
    {
        var validUntil = DateTime.Today.AddDays(30);
        _shiftRepository.HasWorksForGroupAsync(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var groupResource = new GroupResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = DateTime.Today,
            ValidUntil = validUntil
        };
        var command = new PutCommand<GroupResource>(groupResource);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.works-exist-after-valid-until"));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenNoWorksExistAfterValidUntil()
    {
        _shiftRepository.HasWorksForGroupAsync(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var groupResource = new GroupResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30)
        };
        var command = new PutCommand<GroupResource>(groupResource);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenRemovedMemberHasPlannedWorks()
    {
        var existingMemberId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var existingGroup = new Group
        {
            Id = groupId,
            Name = "Test",
            GroupItems = new List<GroupItem>
            {
                new GroupItem { ClientId = existingMemberId, GroupId = groupId }
            }
        };
        _groupRepository.Get(groupId).Returns(existingGroup);
        _shiftRepository.HasWorksForClientInGroupAsync(existingMemberId, groupId, null, Arg.Any<CancellationToken>())
            .Returns(true);

        var groupResource = new GroupResource
        {
            Id = groupId,
            Name = "Test Group",
            ValidFrom = DateTime.Today,
            GroupItems = new List<GroupItemResource>()
        };
        var command = new PutCommand<GroupResource>(groupResource);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.member-has-planned-works"));
    }
}
