using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Validation.BreakPlaceholders;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Validation.BreakPlaceholders;

[TestFixture]
public class PutCommandValidatorTests
{
    private PutCommandValidator _validator;
    private DataBaseContext _context;
    private ILogger<PutCommandValidator> _logger;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _logger = Substitute.For<ILogger<PutCommandValidator>>();
        _validator = new PutCommandValidator(_context, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenBreakPlaceholderIdIsEmpty()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            Id = Guid.Empty,
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break Id is required"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientIdIsEmpty()
    {
        // Arrange
        var breakResource = new BreakPlaceholderResource
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.Empty,
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.Empty,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = default,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now,
            Until = default
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = Guid.NewGuid(),
            From = DateTime.Now.AddDays(1),
            Until = DateTime.Now
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Until date must be on or after From date"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenBreakDoesNotExist()
    {
        // Arrange
        var absenceId = Guid.NewGuid();
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = Guid.NewGuid(), // Non-existent break
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "Break not found"));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenClientDoesNotExist()
    {
        // Arrange
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        
        await SeedExistingBreakPlaceholder(breakPlaceholderId, Guid.NewGuid(), absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = Guid.NewGuid(), // Different client (non-existent)
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        
        await SeedClientWithoutMembership(clientId);
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = DateTime.Now,
            Until = DateTime.Now.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now.AddDays(5);
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(-1), // Before membership starts
            Until = membershipValidFrom.AddDays(1)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var membershipValidUntil = DateTime.Now.AddDays(30);
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidUntil);
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidUntil.AddDays(-1),
            Until = membershipValidUntil.AddDays(1) // After membership ends
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, Guid.NewGuid());

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(), // Non-existent absence
            From = membershipValidFrom.AddDays(1),
            Until = membershipValidFrom.AddDays(2)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var otherBreakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);
        
        // Add another existing break that will overlap
        var otherBreak = new BreakPlaceholder
        {
            Id = otherBreakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(10)
        };
        _context.BreakPlaceholder.Add(otherBreak);
        await _context.SaveChangesAsync();

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(7), // Overlaps with other break - jetzt erlaubt
            Until = membershipValidFrom.AddDays(12)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(10)
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        
        await SeedClientWithMembership(clientId, membershipValidFrom, null); // No ValidUntil
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = membershipValidFrom.AddDays(5),
            Until = membershipValidFrom.AddDays(100) // Far in the future
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenUpdatingBreakWithSameDates()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var breakFrom = membershipValidFrom.AddDays(5);
        var breakUntil = membershipValidFrom.AddDays(10);
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholderWithDates(breakPlaceholderId, clientId, absenceId, breakFrom, breakUntil);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = breakFrom, // Same dates as existing break
            Until = breakUntil
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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
        var breakPlaceholderId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        var membershipValidFrom = DateTime.Now;
        var breakDate = membershipValidFrom.AddDays(5);
        
        await SeedClientWithMembership(clientId, membershipValidFrom, membershipValidFrom.AddDays(30));
        await SeedExistingBreakPlaceholder(breakPlaceholderId, clientId, absenceId);
        await SeedAbsence(absenceId);

        var breakResource = new BreakPlaceholderResource
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = breakDate,
            Until = breakDate // Same date - should be valid for single day break
        };
        var command = new PutCommand<BreakPlaceholderResource>(breakResource);

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

    private async Task SeedExistingBreakPlaceholder(Guid breakPlaceholderId, Guid clientId, Guid absenceId)
    {
        var existingBreak = new BreakPlaceholder
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = DateTime.Now.AddDays(1),
            Until = DateTime.Now.AddDays(2)
        };

        _context.BreakPlaceholder.Add(existingBreak);
        await _context.SaveChangesAsync();
    }

    private async Task SeedExistingBreakPlaceholderWithDates(Guid breakPlaceholderId, Guid clientId, Guid absenceId, DateTime from, DateTime until)
    {
        var existingBreak = new BreakPlaceholder
        {
            Id = breakPlaceholderId,
            ClientId = clientId,
            AbsenceId = absenceId,
            From = from,
            Until = until
        };

        _context.BreakPlaceholder.Add(existingBreak);
        await _context.SaveChangesAsync();
    }
}