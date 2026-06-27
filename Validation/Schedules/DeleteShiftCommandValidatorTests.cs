using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Schedules;

[TestFixture]
public class DeleteShiftCommandValidatorTests
{
    private DeleteShiftCommandValidator _validator;
    private IShiftRepository _shiftRepository;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.HasActiveWorksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _validator = new DeleteShiftCommandValidator(_shiftRepository);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenShiftHasNoActiveWorks()
    {
        _shiftRepository.HasActiveWorksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var command = new DeleteCommand<ShiftResource>(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenShiftHasActiveWorks()
    {
        _shiftRepository.HasActiveWorksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var command = new DeleteCommand<ShiftResource>(Guid.NewGuid());

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "shift.validation.has-active-works"));
    }
}
