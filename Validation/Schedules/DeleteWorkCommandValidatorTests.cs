using FluentAssertions;
using Klacks.Api.Application.Commands.Works;
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
public class DeleteWorkCommandValidatorTests
{
    private DeleteWorkCommandValidator _validator = null!;
    private IWorkRepository _workRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new DeleteWorkCommandValidator(
            _workRepository,
            new WorkLockLevelService(),
            _httpContextAccessor);
    }

    [Test]
    public async Task Validate_UnsealedWork_ShouldBeValid()
    {
        // Arrange
        var workId = Guid.NewGuid();
        var work = new Work { LockLevel = WorkLockLevel.None };
        _workRepository.Get(workId).Returns(work);
        var command = new DeleteWorkCommand(workId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Validate_WorkNotFound_ShouldBeValid()
    {
        // Arrange
        var workId = Guid.NewGuid();
        _workRepository.Get(workId).Returns((Work?)null);
        var command = new DeleteWorkCommand(workId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedWork_NonAdmin_ShouldBeInvalid(WorkLockLevel level)
    {
        // Arrange
        var workId = Guid.NewGuid();
        var work = new Work { LockLevel = level };
        _workRepository.Get(workId).Returns(work);
        SetupNonAdminUser();
        var command = new DeleteWorkCommand(workId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Cannot delete a sealed work entry.");
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Validate_SealedWork_Admin_ShouldBeValid(WorkLockLevel level)
    {
        // Arrange
        var workId = Guid.NewGuid();
        var work = new Work { LockLevel = level };
        _workRepository.Get(workId).Returns(work);
        SetupAdminUser();
        var command = new DeleteWorkCommand(workId, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

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
