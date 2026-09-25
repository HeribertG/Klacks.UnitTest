// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssignmentHistoryRepository: the rows added for one switch are stored with the audit fields the
/// context stamps and are returned tracked, all of them and only them, by their shared switch id; the latest row of a
/// holder is the one with the newest creation time for exactly that holder kind and id, and on equal creation times the
/// one with the higher id (deterministic, not chronological); soft-deleted rows are invisible.
/// </summary>

using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Settings;

[TestFixture]
public class MacroAssignmentHistoryRepositoryTests
{
    private const string LowerTieId = "00000000-0000-0000-0000-000000000001";
    private const string HigherTieId = "00000000-0000-0000-0000-000000000002";

    private DataBaseContext _context = null!;
    private MacroAssignmentHistoryRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new MacroAssignmentHistoryRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Add_ThenGetSwitch_ReturnsEveryTrackedRowOfThatSwitchOnly()
    {
        var switchId = Guid.NewGuid();
        var firstCut = Entry(MacroAssignmentTarget.Shift, Guid.NewGuid(), switchId);
        var secondCut = Entry(MacroAssignmentTarget.Shift, Guid.NewGuid(), switchId);
        _sut.Add(firstCut);
        _sut.Add(secondCut);
        _sut.Add(Entry(MacroAssignmentTarget.Shift, Guid.NewGuid()));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var rows = await _sut.GetSwitchAsync(switchId);

        rows.Select(row => row.Id).ShouldBe(new[] { firstCut.Id, secondCut.Id }, ignoreOrder: true);
        rows.ShouldAllBe(row => row.CreateTime != null);
        rows.ShouldAllBe(row => _context.Entry(row).State == EntityState.Unchanged);
    }

    [Test]
    public async Task GetLatest_ReturnsTheNewestRowOfExactlyThatHolder()
    {
        var holderId = Guid.NewGuid();
        var older = Entry(MacroAssignmentTarget.Shift, holderId);
        var newer = Entry(MacroAssignmentTarget.Shift, holderId);
        var otherKind = Entry(MacroAssignmentTarget.AbsenceType, holderId);
        await AddAndSaveAsync(older);
        await AddAndSaveAsync(newer);
        await AddAndSaveAsync(otherKind);
        newer.CreateTime!.Value.ShouldBeGreaterThan(older.CreateTime!.Value);
        otherKind.CreateTime!.Value.ShouldBeGreaterThan(newer.CreateTime.Value);
        _context.ChangeTracker.Clear();

        var latest = await _sut.GetLatestAsync(MacroAssignmentTarget.Shift, holderId);

        latest!.Id.ShouldBe(newer.Id);
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task GetLatest_RowsWithTheSameCreationTime_AreDecidedByTheHigherId()
    {
        var holderId = Guid.NewGuid();
        var lowerId = Entry(MacroAssignmentTarget.Shift, holderId);
        lowerId.Id = new Guid(LowerTieId);
        var higherId = Entry(MacroAssignmentTarget.Shift, holderId);
        higherId.Id = new Guid(HigherTieId);
        _sut.Add(lowerId);
        _sut.Add(higherId);
        await _context.SaveChangesAsync();
        higherId.CreateTime = lowerId.CreateTime;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        (await _context.MacroAssignmentHistory.AsNoTracking().Select(h => h.CreateTime).Distinct().CountAsync())
            .ShouldBe(1);

        var latest = await _sut.GetLatestAsync(MacroAssignmentTarget.Shift, holderId);

        latest!.Id.ShouldBe(higherId.Id);
    }

    [Test]
    public async Task SoftDeletedRows_AreInvisible()
    {
        var holderId = Guid.NewGuid();
        var entry = Entry(MacroAssignmentTarget.Shift, holderId);
        entry.IsDeleted = true;
        _sut.Add(entry);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _sut.GetSwitchAsync(entry.SwitchId)).ShouldBeEmpty();
        (await _sut.GetLatestAsync(MacroAssignmentTarget.Shift, holderId)).ShouldBeNull();
    }

    private async Task AddAndSaveAsync(MacroAssignmentHistory entry)
    {
        _sut.Add(entry);
        await _context.SaveChangesAsync();
    }

    private static MacroAssignmentHistory Entry(MacroAssignmentTarget target, Guid holderId, Guid? switchId = null) => new()
    {
        Id = Guid.NewGuid(),
        SwitchId = switchId ?? Guid.NewGuid(),
        Target = target,
        TargetId = holderId,
        PreviousMacroId = Guid.NewGuid(),
        NewMacroId = Guid.NewGuid(),
        ChangedByUserId = Guid.NewGuid()
    };
}
