using FluentAssertions;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Validation.Schedules;

[TestFixture]
public class BulkDeleteWorksCommandValidatorTests
{
    private BulkDeleteWorksCommandValidator _validator = null!;
    private IWorkRepository _workRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void Setup()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new BulkDeleteWorksCommandValidator(
            _workRepository,
            new WorkLockLevelService(),
            _httpContextAccessor);
    }

    [Test]
    public async Task Validate_AllUnsealed_ShouldBeValid()
    {
        // Arrange
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
            _workRepository.Get(id).Returns(new Work { LockLevel = WorkLockLevel.None });
        SetupNonAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = ids });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Test]
    public async Task Validate_SomeSealedNonAdmin_ShouldBeInvalid()
    {
        // Arrange
        var unsealedId = Guid.NewGuid();
        var sealedId = Guid.NewGuid();
        _workRepository.Get(unsealedId).Returns(new Work { LockLevel = WorkLockLevel.None });
        _workRepository.Get(sealedId).Returns(new Work { LockLevel = WorkLockLevel.Confirmed });
        SetupNonAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest
        {
            WorkIds = [unsealedId, sealedId]
        });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Cannot delete sealed work entries.");
    }

    [Test]
    public async Task Validate_SomeSealedAdmin_ShouldBeValid()
    {
        // Arrange
        var sealedId = Guid.NewGuid();
        _workRepository.Get(sealedId).Returns(new Work { LockLevel = WorkLockLevel.Closed });
        SetupAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest
        {
            WorkIds = [sealedId]
        });

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
