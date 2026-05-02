using Shouldly;
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
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(ids.Select(_ => new Work { LockLevel = WorkLockLevel.None }).ToList());
        SetupNonAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = ids });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public async Task Validate_SomeSealedNonAdmin_ShouldBeInvalid()
    {
        // Arrange
        var unsealedId = Guid.NewGuid();
        var sealedId = Guid.NewGuid();
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new List<Work>
            {
                new() { LockLevel = WorkLockLevel.None },
                new() { LockLevel = WorkLockLevel.Confirmed }
            });
        SetupNonAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest
        {
            WorkIds = [unsealedId, sealedId]
        });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == "Cannot delete sealed work entries.");
    }

    [Test]
    public async Task Validate_SomeSealedAdmin_ShouldBeValid()
    {
        // Arrange
        var sealedId = Guid.NewGuid();
        _workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new List<Work> { new() { LockLevel = WorkLockLevel.Closed } });
        SetupAdminUser();
        var command = new BulkDeleteWorksCommand(new BulkDeleteWorksRequest
        {
            WorkIds = [sealedId]
        });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.ShouldBeTrue();
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
