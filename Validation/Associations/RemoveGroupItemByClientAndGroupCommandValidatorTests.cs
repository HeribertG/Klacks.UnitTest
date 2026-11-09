using FluentValidation.Results;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Associations;

[TestFixture]
public class RemoveGroupItemByClientAndGroupCommandValidatorTests
{
    private RemoveGroupItemByClientAndGroupCommandValidator _validator;
    private IShiftRepository _shiftRepository;
    private IGroupVisibilityGuard _groupVisibilityGuard;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _validator = new RemoveGroupItemByClientAndGroupCommandValidator(_shiftRepository, _groupVisibilityGuard);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WithoutReadingWorks_WhenTheGroupIsHidden()
    {
        var hiddenGroupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        _groupVisibilityGuard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);
        _shiftRepository.HasWorksForClientInGroupAsync(clientId, hiddenGroupId, Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _validator.ValidateAsync(
            new RemoveGroupItemByClientAndGroupCommand { ClientId = clientId, GroupId = hiddenGroupId });

        Assert.That(result.IsValid, Is.True);
        await _shiftRepository.DidNotReceive()
            .HasWorksForClientInGroupAsync(clientId, hiddenGroupId, Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenClientHasNoPlannedWorksInGroup()
    {
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new RemoveGroupItemByClientAndGroupCommand
        {
            ClientId = Guid.NewGuid(),
            GroupId = Guid.NewGuid()
        };

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientHasPlannedWorksInGroup()
    {
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new RemoveGroupItemByClientAndGroupCommand
        {
            ClientId = Guid.NewGuid(),
            GroupId = Guid.NewGuid()
        };

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "group.validation.member-has-planned-works"));
    }
}
