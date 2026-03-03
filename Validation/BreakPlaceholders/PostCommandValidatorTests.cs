using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.BreakPlaceholders;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Validation.BreakPlaceholders;

[TestFixture]
public class PostCommandValidatorTests
{
    private PostCommandValidator _validator;
    private IClientRepository _clientRepository;
    private IAbsenceRepository _absenceRepository;
    private ILogger<PostCommandValidator> _logger;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _logger = Substitute.For<ILogger<PostCommandValidator>>();
        _validator = new PostCommandValidator(_clientRepository, _absenceRepository, _logger);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientIdIsEmpty()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            ClientId = Guid.Empty,
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "ClientId is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenAbsenceIdIsEmpty()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.Empty,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "AbsenceId is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenFromDateIsDefault()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = default,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "From date is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUntilDateIsDefault()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = default
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Until date is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUntilDateIsBeforeFromDate()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now.AddDays(1),
            Until = DateTime.Now
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Until date must be on or after From date"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientDoesNotExist()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns((Client?)null);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break must be within the client's membership validity period (ValidFrom to ValidUntil)"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientHasNoMembership()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var client = new Client { Id = clientId, Name = "Test Employee", Membership = null };
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break must be within the client's membership validity period (ValidFrom to ValidUntil)"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenBreakStartsBeforeMembershipValidFrom()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now.AddDays(5);
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(-1),
            Until = membershipValidFrom.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break must be within the client's membership validity period (ValidFrom to ValidUntil)"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenBreakEndsAfterMembershipValidUntil()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var membershipValidUntil = DateTime.Now.AddDays(30);
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidUntil);
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidUntil.AddDays(-1),
            Until = membershipValidUntil.AddDays(1)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break must be within the client's membership validity period (ValidFrom to ValidUntil)"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenAbsenceDoesNotExist()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(false);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(1),
            Until = membershipValidFrom.AddDays(2)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Invalid AbsenceId - absence type does not exist"));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenBreakOverlapsWithExistingBreak()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(7),
            Until = membershipValidFrom.AddDays(12)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenAllConditionsAreMet()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(10)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenMembershipHasNoValidUntil()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var client = CreateClientWithMembership(clientId, membershipValidFrom, null);
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(100)
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenFromAndUntilDatesAreEqual()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var breakDate = membershipValidFrom.AddDays(5);
        var client = CreateClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        _clientRepository.GetWithMembershipAsync(clientId, Arg.Any<CancellationToken>()).Returns(client);
        _absenceRepository.Exists(absenceId).Returns(true);

        var breakResource = new BreakPlaceholderResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = breakDate,
            Until = breakDate
        };
        var command = new PostCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    private static Client CreateClientWithMembership(Guid clientId, DateTime validFrom, DateTime? validUntil)
    {
        return new Client
        {
            Id = clientId,
            Name = "Test Employee",
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = validFrom,
                ValidUntil = validUntil
            }
        };
    }
}
