using FluentValidation.Results;
using Klacks.Api.Application.Commands.Settings.Branch;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Application.Validation.Branches;
using NSubstitute;

namespace Klacks.UnitTest.Validation.Branches;

[TestFixture]
public class PutCommandValidatorTests
{
    private PutCommandValidator _validator;
    private IBranchRepository _branchRepository;

    [SetUp]
    public void Setup()
    {
        _branchRepository = Substitute.For<IBranchRepository>();
        _validator = new PutCommandValidator(_branchRepository);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenNameIsEmpty()
    {
        var branch = new Api.Domain.Models.Settings.Branch { Id = Guid.NewGuid(), Name = string.Empty };
        var command = new PutCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Name is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenNameAlreadyExistsOnDifferentBranch()
    {
        var branchId = Guid.NewGuid();
        _branchRepository.ExistsByNameAsync("Filiale Berlin", branchId).Returns(true);

        var branch = new Api.Domain.Models.Settings.Branch { Id = branchId, Name = "Filiale Berlin" };
        var command = new PutCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "A branch with this name already exists."));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenNameIsUnique()
    {
        var branchId = Guid.NewGuid();
        _branchRepository.ExistsByNameAsync("Filiale München", branchId).Returns(false);

        var branch = new Api.Domain.Models.Settings.Branch { Id = branchId, Name = "Filiale München" };
        var command = new PutCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenSameBranchKeepsItsName()
    {
        var branchId = Guid.NewGuid();
        _branchRepository.ExistsByNameAsync("Filiale Berlin", branchId).Returns(false);

        var branch = new Api.Domain.Models.Settings.Branch { Id = branchId, Name = "Filiale Berlin" };
        var command = new PutCommand(branch);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }
}
