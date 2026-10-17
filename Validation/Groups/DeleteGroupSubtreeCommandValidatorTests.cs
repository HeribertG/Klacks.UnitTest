using FluentValidation.Results;
using Klacks.Api.Application.Commands.Associations;
using Klacks.Api.Application.Validation.Groups;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Groups;

[TestFixture]
public class DeleteGroupSubtreeCommandValidatorTests
{
    private DeleteGroupSubtreeCommandValidator _validator;
    private IShiftRepository _shiftRepository;
    private IGroupHierarchyService _groupHierarchyService;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupHierarchyService = Substitute.For<IGroupHierarchyService>();

        var groupId = Guid.NewGuid();
        _groupHierarchyService.GetDescendantsAsync(Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns(new List<Group> { new Group { Id = groupId } });
        _shiftRepository.HasWorksForAnyGroupAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _validator = new DeleteGroupSubtreeCommandValidator(_shiftRepository, _groupHierarchyService);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenGroupHasNoPlannedWorks()
    {
        _shiftRepository.HasWorksForAnyGroupAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new DeleteGroupSubtreeCommand(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenGroupHasPlannedWorks()
    {
        _shiftRepository.HasWorksForAnyGroupAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new DeleteGroupSubtreeCommand(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.has-planned-works"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenSubgroupHasPlannedWorks()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _groupHierarchyService.GetDescendantsAsync(parentId, true)
            .Returns(new List<Group>
            {
                new Group { Id = parentId },
                new Group { Id = childId }
            });
        _shiftRepository.HasWorksForAnyGroupAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new DeleteGroupSubtreeCommand(parentId);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.has-planned-works"));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenGroupNotFound()
    {
        _groupHierarchyService.GetDescendantsAsync(Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns(Task.FromException<IEnumerable<Group>>(new KeyNotFoundException()));

        var command = new DeleteGroupSubtreeCommand(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }
}
