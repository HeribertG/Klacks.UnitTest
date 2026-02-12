using FluentValidation.Results;
using Klacks.Api.Application.Commands.Settings.Branch;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Branches;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Branches;

[TestFixture]
public class PostCommandValidatorTests
{
    private PostCommandValidator _validator;
    private IBranchRepository _branchRepository;

    [SetUp]
    public void Setup()
    {
        _branchRepository = Substitute.For<IBranchRepository>();
        _validator = new PostCommandValidator(_branchRepository);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenNameIsEmpty()
    {
        var branch = new Api.Domain.Models.Settings.Branch { Name = string.Empty };
        var command = new PostCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Name is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenNameAlreadyExists()
    {
        _branchRepository.ExistsByNameAsync("Filiale Berlin", null).Returns(true);

        var branch = new Api.Domain.Models.Settings.Branch { Name = "Filiale Berlin" };
        var command = new PostCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "A branch with this name already exists."));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenNameIsUnique()
    {
        _branchRepository.ExistsByNameAsync("Filiale München", null).Returns(false);

        var branch = new Api.Domain.Models.Settings.Branch { Name = "Filiale München" };
        var command = new PostCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }
}
