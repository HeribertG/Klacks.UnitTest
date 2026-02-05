using FluentAssertions;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Validation.Schedules;

[TestFixture]
public class DeleteBreakCommandValidatorTests
{
    private DeleteBreakCommandValidator _validator = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void Setup()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new DeleteBreakCommandValidator(
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor);
    }

    [Test]
    public async Task Validate_UnsealedBreak_ShouldBeValid()
    {
        // Arrange
        var breakId = Guid.NewGuid();
        var breakEntry = new Break { LockLevel = WorkLockLevel.None };
        _breakRepository.Get(breakId).Returns(breakEntry);
        var command = new DeleteBreakCommand(breakId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Validate_BreakNotFound_ShouldBeValid()
    {
        // Arrange
        var breakId = Guid.NewGuid();
        _breakRepository.Get(breakId).Returns((Break?)null);
        var command = new DeleteBreakCommand(breakId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedBreak_NonAdmin_ShouldBeInvalid(WorkLockLevel level)
    {
        // Arrange
        var breakId = Guid.NewGuid();
        var breakEntry = new Break { LockLevel = level };
        _breakRepository.Get(breakId).Returns(breakEntry);
        SetupNonAdminUser();
        var command = new DeleteBreakCommand(breakId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Cannot delete a sealed break entry.");
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedBreak_Admin_ShouldBeValid(WorkLockLevel level)
    {
        // Arrange
        var breakId = Guid.NewGuid();
        var breakEntry = new Break { LockLevel = level };
        _breakRepository.Get(breakId).Returns(breakEntry);
        SetupAdminUser();
        var command = new DeleteBreakCommand(breakId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    private void SetupAdminUser()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Admin")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }

    private void SetupNonAdminUser()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "User")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
