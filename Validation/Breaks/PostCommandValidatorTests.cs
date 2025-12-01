using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Validation.Breaks;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Validation.Breaks;

[TestFixture]
public class PostCommandValidatorTests
{
    private PostCommandValidator _validator;
    private DataBaseContext _context;
    private ILogger<PostCommandValidator> _logger;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _logger = Substitute.For<ILogger<PostCommandValidator>>();
        _validator = new PostCommandValidator(_context, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientIdIsEmpty()
    {
        // Arrange
        var breakResource = new BreakResource
        {
            ClientId = Guid.Empty,
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var breakResource = new BreakResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.Empty,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var breakResource = new BreakResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = default,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var breakResource = new BreakResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = default
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var breakResource = new BreakResource
        {
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now.AddDays(1),
            Until = DateTime.Now
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var absenceId = Guid.NewGuid();
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = Guid.NewGuid(), // Non-existent client
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithoutMembership(clientId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(-1), // Before membership starts
            Until = membershipValidFrom.AddDays(1)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidUntil);
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidUntil.AddDays(-1),
            Until = membershipValidUntil.AddDays(1) // After membership ends
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(), // Non-existent absence
            From = membershipValidFrom.AddDays(1),
            Until = membershipValidFrom.AddDays(2)
        };
        var command = new PostCommand<BreakResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Invalid AbsenceId - absence type does not exist"));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenBreakOverlapsWithExistingBreak()
    {
        // Arrange - Überlappungen sind jetzt erlaubt
        var clientId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedAbsence(absenceId);
        
        // Add existing break
        var existingBreak = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(10)
        };
        _context.Break.Add(existingBreak);
        await _context.SaveChangesAsync();

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(7), // Overlaps with existing break - jetzt erlaubt
            Until = membershipValidFrom.AddDays(12)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(10)
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithMembership(clientId, membershipValidFrom, null); // No ValidUntil
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(100) // Far in the future
        };
        var command = new PostCommand<BreakResource>(breakResource);

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
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedAbsence(absenceId);

        var breakResource = new BreakResource
        {
            ClientId = clientId,
            AbsenceId = absenceId,
            From = breakDate,
            Until = breakDate // Same date - should be valid for single day break
        };
        var command = new PostCommand<BreakResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    private async Task SeedClientWithMembership(Guid clientId, DateTime validFrom, DateTime? validUntil)
    {
        var membership = new Membership
        {
            Id = Guid.NewGuid(),
            ValidFrom = validFrom,
            ValidUntil = validUntil
        };

        var client = new Client
        {
            Id = clientId,
            Membership = membership,
            Name = "Test Employee"
        };

        _context.Membership.Add(membership);
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
    }

    private async Task SeedClientWithoutMembership(Guid clientId)
    {
        var client = new Client
        {
            Id = clientId,
            Name = "Test Employee"
        };

        _context.Client.Add(client);
        await _context.SaveChangesAsync();
    }

    private async Task SeedAbsence(Guid absenceId)
    {
        var absence = new Absence
        {
            Id = absenceId,
            Name = new MultiLanguage { De = "Test Absence", En = "Test Absence" },
            Description = new MultiLanguage { De = "Test Description", En = "Test Description" }
        };

        _context.Absence.Add(absence);
        await _context.SaveChangesAsync();
    }
}