using Shouldly;
using Klacks.Api.Application.Commands.Breaks;
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
public class BulkDeleteBreaksCommandValidatorTests
{
    private BulkDeleteBreaksCommandValidator _validator = null!;
    private IBreakRepository _breakRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void Setup()
    {
        _breakRepository = Substitute.For<IBreakRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new BulkDeleteBreaksCommandValidator(
            _breakRepository,
            new WorkLockLevelService(),
            _httpContextAccessor);
    }

    [Test]
    public async Task Validate_AllUnsealed_ShouldBeValid()
    {
        // Arrange
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(ids.Select(_ => new Break { LockLevel = WorkLockLevel.None }).ToList());
        SetupNonAdminUser();
        var command = new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
        {
            BreakIds = ids,
            PeriodStart = new DateOnly(2026, 1, 1),
            PeriodEnd = new DateOnly(2026, 1, 31)
        });

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
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new List<Break>
            {
                new() { LockLevel = WorkLockLevel.None },
                new() { LockLevel = WorkLockLevel.Approved }
            });
        SetupNonAdminUser();
        var command = new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
        {
            BreakIds = [unsealedId, sealedId],
            PeriodStart = new DateOnly(2026, 1, 1),
            PeriodEnd = new DateOnly(2026, 1, 31)
        });

        // Act
        var result = await _validator.ValidateAsync(command);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage == "Cannot delete sealed break entries.");
    }

    [Test]
    public async Task Validate_SomeSealedAdmin_ShouldBeValid()
    {
        // Arrange
        var sealedId = Guid.NewGuid();
        _breakRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new List<Break> { new() { LockLevel = WorkLockLevel.Closed } });
        SetupAdminUser();
        var command = new BulkDeleteBreaksCommand(new BulkDeleteBreaksRequest
        {
            BreakIds = [sealedId],
            PeriodStart = new DateOnly(2026, 1, 1),
            PeriodEnd = new DateOnly(2026, 1, 31)
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
