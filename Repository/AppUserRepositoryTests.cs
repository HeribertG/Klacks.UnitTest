// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests AppUserRepository.ApplyDisplayOrderAsync: each submitted id gets its 1-based position as
/// DisplayOrder, unknown ids are skipped but still consume their position, and the change is committed.
/// </summary>

using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Repositories.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class AppUserRepositoryTests
{
    private DataBaseContext _context = null!;
    private AppUserRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new AppUserRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ApplyDisplayOrderAsync_SetsDisplayOrderToSubmittedPosition()
    {
        // Arrange
        var userA = new AppUser { Id = "user-a", FirstName = "A", LastName = "A", DisplayOrder = 5 };
        var userB = new AppUser { Id = "user-b", FirstName = "B", LastName = "B", DisplayOrder = 6 };
        var userC = new AppUser { Id = "user-c", FirstName = "C", LastName = "C", DisplayOrder = 7 };
        _context.AppUser.AddRange(userA, userB, userC);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _repository.ApplyDisplayOrderAsync(["user-c", "user-a", "user-b"]);

        // Assert
        _context.ChangeTracker.Clear();
        var reordered = await _context.AppUser.ToDictionaryAsync(u => u.Id);
        reordered["user-c"].DisplayOrder.ShouldBe(1);
        reordered["user-a"].DisplayOrder.ShouldBe(2);
        reordered["user-b"].DisplayOrder.ShouldBe(3);
    }

    [Test]
    public async Task ApplyDisplayOrderAsync_UnknownUserIdInList_IsSkippedButKeepsItsPosition()
    {
        // Arrange
        var userA = new AppUser { Id = "user-a", FirstName = "A", LastName = "A", DisplayOrder = 1 };
        var userB = new AppUser { Id = "user-b", FirstName = "B", LastName = "B", DisplayOrder = 9 };
        _context.AppUser.AddRange(userA, userB);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        await _repository.ApplyDisplayOrderAsync(["user-ghost", "user-a"]);

        // Assert
        _context.ChangeTracker.Clear();
        (await _context.AppUser.SingleAsync(u => u.Id == "user-a")).DisplayOrder.ShouldBe(2);
        (await _context.AppUser.SingleAsync(u => u.Id == "user-b")).DisplayOrder.ShouldBe(9);
    }
}
