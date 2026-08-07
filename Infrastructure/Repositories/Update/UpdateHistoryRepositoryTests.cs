// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for UpdateHistoryRepository against a real DataBaseContext (InMemory provider): the rollback
/// anchor must survive an admin deleting the row from the history, while the history listing itself
/// must keep hiding deleted rows.
/// </summary>

using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Repositories.Update;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Update;

[TestFixture]
public class UpdateHistoryRepositoryTests
{
    private static readonly DateTime OlderCompletion = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerCompletion = new(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private UpdateHistoryRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new UpdateHistoryRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task GetLastSuccessfulUpdateAsync_still_finds_the_newest_update_after_it_was_deleted_from_the_history()
    {
        _context.UpdateHistory.Add(SucceededUpdate("1.0.0", "1.1.0", OlderCompletion, "backup-older", isDeleted: false));
        _context.UpdateHistory.Add(SucceededUpdate("1.1.0", "1.2.0", NewerCompletion, "backup-newer", isDeleted: true));
        await _context.SaveChangesAsync();

        var anchor = await _repository.GetLastSuccessfulUpdateAsync();

        anchor.ShouldNotBeNull();
        anchor.TargetVersion.ShouldBe("1.2.0");
        anchor.FromVersion.ShouldBe("1.1.0");
        anchor.BackupRef.ShouldBe("backup-newer");
    }

    [Test]
    public async Task GetRecentAsync_hides_rows_that_were_deleted_from_the_history()
    {
        _context.UpdateHistory.Add(SucceededUpdate("1.0.0", "1.1.0", OlderCompletion, "backup-older", isDeleted: false));
        _context.UpdateHistory.Add(SucceededUpdate("1.1.0", "1.2.0", NewerCompletion, "backup-newer", isDeleted: true));
        await _context.SaveChangesAsync();

        var recent = await _repository.GetRecentAsync(10);

        recent.Count.ShouldBe(1);
        recent[0].TargetVersion.ShouldBe("1.1.0");
    }

    private static UpdateHistory SucceededUpdate(string fromVersion, string targetVersion, DateTime completedAt, string backupRef, bool isDeleted)
    {
        return new UpdateHistory
        {
            Id = Guid.NewGuid(),
            OperationType = UpdateOperationType.Update,
            Status = UpdateOperationStatus.Succeeded,
            Channel = UpdateChannel.Stable,
            FromVersion = fromVersion,
            TargetVersion = targetVersion,
            BackupRef = backupRef,
            RequestedAt = completedAt,
            CompletedAt = completedAt,
            IsDeleted = isDeleted,
        };
    }
}
