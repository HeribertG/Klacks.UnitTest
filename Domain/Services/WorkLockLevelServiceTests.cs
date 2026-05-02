using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services;

[TestFixture]
public class WorkLockLevelServiceTests
{
    private WorkLockLevelService _service = null!;

    [SetUp]
    public void Setup()
    {
        _service = new WorkLockLevelService();
    }

    [Test]
    public void CanModifyWork_AdminAtAnyLevel_ReturnsTrue()
    {
        // Arrange
        var levels = new[] { WorkLockLevel.None, WorkLockLevel.Confirmed, WorkLockLevel.Approved, WorkLockLevel.Closed };

        // Act & Assert
        foreach (var level in levels)
        {
            _service.CanModifyWork(level, isAdmin: true).ShouldBeTrue();
        }
    }

    [Test]
    public void CanModifyWork_NonAdminAtNone_ReturnsTrue()
    {
        // Arrange & Act
        var result = _service.CanModifyWork(WorkLockLevel.None, isAdmin: false);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public void CanModifyWork_NonAdminAboveNone_ReturnsFalse(WorkLockLevel level)
    {
        // Arrange & Act
        var result = _service.CanModifyWork(level, isAdmin: false);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void CanSeal_UserToConfirmed_ReturnsTrue()
    {
        // Arrange & Act
        var result = _service.CanSeal(WorkLockLevel.None, WorkLockLevel.Confirmed, isAdmin: false, isAuthorised: false);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanSeal_UserToApproved_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanSeal(WorkLockLevel.None, WorkLockLevel.Approved, isAdmin: false, isAuthorised: false);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void CanSeal_UserToClosed_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, isAdmin: false, isAuthorised: false);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(WorkLockLevel.None)]
    [TestCase(WorkLockLevel.Confirmed)]
    public void CanSeal_SupervisorToApproved_ReturnsTrue(WorkLockLevel currentLevel)
    {
        // Arrange & Act
        var result = _service.CanSeal(currentLevel, WorkLockLevel.Approved, isAdmin: false, isAuthorised: true);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanSeal_SupervisorToClosed_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanSeal(WorkLockLevel.None, WorkLockLevel.Closed, isAdmin: false, isAuthorised: true);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(WorkLockLevel.None, WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.None, WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.None, WorkLockLevel.Closed)]
    [TestCase(WorkLockLevel.Confirmed, WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Confirmed, WorkLockLevel.Closed)]
    [TestCase(WorkLockLevel.Approved, WorkLockLevel.Closed)]
    public void CanSeal_AdminToAnyHigherLevel_ReturnsTrue(WorkLockLevel currentLevel, WorkLockLevel targetLevel)
    {
        // Arrange & Act
        var result = _service.CanSeal(currentLevel, targetLevel, isAdmin: true, isAuthorised: false);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanSeal_SameLevelOrLower_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanSeal(WorkLockLevel.Confirmed, WorkLockLevel.Confirmed, isAdmin: true, isAuthorised: true);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void CanUnseal_UserAtConfirmed_ReturnsTrue()
    {
        // Arrange & Act
        var result = _service.CanUnseal(WorkLockLevel.Confirmed, isAdmin: false, isAuthorised: false);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanUnseal_UserAtApproved_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanUnseal(WorkLockLevel.Approved, isAdmin: false, isAuthorised: false);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void CanUnseal_SupervisorAtApproved_ReturnsTrue()
    {
        // Arrange & Act
        var result = _service.CanUnseal(WorkLockLevel.Approved, isAdmin: false, isAuthorised: true);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanUnseal_SupervisorAtClosed_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanUnseal(WorkLockLevel.Closed, isAdmin: false, isAuthorised: true);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public void CanUnseal_AdminAtAnyLevel_ReturnsTrue(WorkLockLevel level)
    {
        // Arrange & Act
        var result = _service.CanUnseal(level, isAdmin: true, isAuthorised: false);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanUnseal_AtNone_ReturnsFalse()
    {
        // Arrange & Act
        var result = _service.CanUnseal(WorkLockLevel.None, isAdmin: true, isAuthorised: true);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void Seal_ValidPermission_SetsProperties()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.None, ShiftId = Guid.NewGuid() };

        // Act
        _service.Seal(entity, WorkLockLevel.Confirmed, "testuser", isAdmin: false, isAuthorised: false);

        // Assert
        entity.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        entity.SealedBy.ShouldBe("testuser");
        entity.SealedAt.ShouldNotBeNull();
        entity.SealedAt.Value.ShouldBe(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Seal_InsufficientPermission_ThrowsInvalidRequestException()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.None, ShiftId = Guid.NewGuid() };

        // Act
        var act = () => _service.Seal(entity, WorkLockLevel.Closed, "testuser", isAdmin: false, isAuthorised: false);

        // Assert
        act.ShouldThrow<InvalidRequestException>();
    }

    [Test]
    public void Seal_SameLevel_ThrowsInvalidRequestException()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.Confirmed, ShiftId = Guid.NewGuid() };

        // Act
        var act = () => _service.Seal(entity, WorkLockLevel.Confirmed, "testuser", isAdmin: true, isAuthorised: true);

        // Assert
        act.ShouldThrow<InvalidRequestException>();
    }

    [Test]
    public void Seal_AdminToApproved_SetsProperties()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.Confirmed, ShiftId = Guid.NewGuid() };

        // Act
        _service.Seal(entity, WorkLockLevel.Approved, "admin", isAdmin: true, isAuthorised: false);

        // Assert
        entity.LockLevel.ShouldBe(WorkLockLevel.Approved);
        entity.SealedBy.ShouldBe("admin");
        entity.SealedAt.ShouldNotBeNull();
    }

    [Test]
    public void Unseal_ValidPermission_ClearsProperties()
    {
        // Arrange
        var entity = new Work
        {
            LockLevel = WorkLockLevel.Confirmed,
            SealedAt = DateTime.UtcNow,
            SealedBy = "testuser",
            ShiftId = Guid.NewGuid()
        };

        // Act
        _service.Unseal(entity, isAdmin: false, isAuthorised: false);

        // Assert
        entity.LockLevel.ShouldBe(WorkLockLevel.None);
        entity.SealedAt.ShouldBeNull();
        entity.SealedBy.ShouldBeNull();
    }

    [Test]
    public void Unseal_InsufficientPermission_ThrowsInvalidRequestException()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.Approved, ShiftId = Guid.NewGuid() };

        // Act
        var act = () => _service.Unseal(entity, isAdmin: false, isAuthorised: false);

        // Assert
        act.ShouldThrow<InvalidRequestException>();
    }

    [Test]
    public void Unseal_AtNone_ThrowsInvalidRequestException()
    {
        // Arrange
        var entity = new Work { LockLevel = WorkLockLevel.None, ShiftId = Guid.NewGuid() };

        // Act
        var act = () => _service.Unseal(entity, isAdmin: true, isAuthorised: true);

        // Assert
        act.ShouldThrow<InvalidRequestException>();
    }

    [Test]
    public void Unseal_AdminAtClosed_ClearsProperties()
    {
        // Arrange
        var entity = new Work
        {
            LockLevel = WorkLockLevel.Closed,
            SealedAt = DateTime.UtcNow,
            SealedBy = "admin",
            ShiftId = Guid.NewGuid()
        };

        // Act
        _service.Unseal(entity, isAdmin: true, isAuthorised: false);

        // Assert
        entity.LockLevel.ShouldBe(WorkLockLevel.None);
        entity.SealedAt.ShouldBeNull();
        entity.SealedBy.ShouldBeNull();
    }
}
