// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroReferenceRepository: a shift is read as a holder with name, macro, status, scenario flag and cut
/// group (its cut group key is the order id), an absence type with its first non-empty core-language name (else any
/// stored name, ordered by language code), soft-deleted
/// holders and macros are invisible; the cut group of an order holds its live non-scenario shifts without the sealed order
/// row, and a shift without an order is its own group; a macro is read as an untracked snapshot, and a switch marks exactly
/// the MacroId column of one tracked holder row as modified.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Settings;

[TestFixture]
public class MacroReferenceRepositoryTests
{
    private const string NonCoreLanguage = "ja";
    private const string NonCoreName = "Yukyu";

    private DataBaseContext _context = null!;
    private MacroReferenceRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new MacroReferenceRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task FindHolder_Shift_ReadsNameMacroStatusAndCutGroup()
    {
        var macroId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shift = AddShift("Night", macroId, ShiftStatus.SplitShift, orderId);
        await SaveAndClearAsync();

        var holder = await _sut.FindHolderAsync(MacroAssignmentTarget.Shift, shift.Id);

        holder.ShouldBe(new MacroReferenceHolder(
            shift.Id, MacroAssignmentTarget.Shift, "Night", macroId, ShiftStatus.SplitShift, false, orderId));
        holder!.CutGroupKey.ShouldBe(orderId);
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task FindHolder_ScenarioShift_IsFlagged()
    {
        var shift = AddShift("Night", null, ShiftStatus.OriginalShift, null);
        shift.AnalyseToken = Guid.NewGuid();
        await SaveAndClearAsync();

        (await _sut.FindHolderAsync(MacroAssignmentTarget.Shift, shift.Id))!.IsScenario.ShouldBeTrue();
    }

    [Test]
    public async Task FindHolder_AbsenceTypeWithoutCoreLanguageName_FallsBackToAnyStoredName()
    {
        var name = new MultiLanguage();
        name.SetValue(NonCoreLanguage, NonCoreName);
        var absence = AddAbsence(name, Guid.NewGuid());
        await SaveAndClearAsync();

        var holder = await _sut.FindHolderAsync(MacroAssignmentTarget.AbsenceType, absence.Id);

        holder!.Name.ShouldBe(NonCoreName);
    }

    [Test]
    public async Task FindHolder_AbsenceType_UsesTheFirstNonEmptyCoreLanguageName()
    {
        var absence = AddAbsence(new MultiLanguage { En = "Vacation" }, Guid.NewGuid());
        await SaveAndClearAsync();

        var holder = await _sut.FindHolderAsync(MacroAssignmentTarget.AbsenceType, absence.Id);

        holder!.Name.ShouldBe("Vacation");
        holder.Target.ShouldBe(MacroAssignmentTarget.AbsenceType);
        holder.ShiftStatus.ShouldBeNull();
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task FindHolder_SoftDeleted_IsNull()
    {
        var shift = AddShift("Old", null, ShiftStatus.OriginalShift, null);
        shift.IsDeleted = true;
        await SaveAndClearAsync();

        (await _sut.FindHolderAsync(MacroAssignmentTarget.Shift, shift.Id)).ShouldBeNull();
    }

    [Test]
    public async Task FindCutGroup_ReturnsTheLiveNonScenarioShiftsOfTheOrder_WithoutTheSealedOrderRow()
    {
        var orderId = Guid.NewGuid();
        AddShift("Order", null, ShiftStatus.SealedOrder, null, orderId);
        var firstCut = AddShift("Cut 1", null, ShiftStatus.SplitShift, orderId);
        var secondCut = AddShift("Cut 2", null, ShiftStatus.SplitShift, orderId);
        AddShift("Scenario cut", null, ShiftStatus.SplitShift, orderId).AnalyseToken = Guid.NewGuid();
        AddShift("Deleted cut", null, ShiftStatus.SplitShift, orderId).IsDeleted = true;
        AddShift("Other order", null, ShiftStatus.SplitShift, Guid.NewGuid());
        await SaveAndClearAsync();

        var group = await _sut.FindCutGroupAsync(orderId);

        group.Select(member => member.Id).ShouldBe(new[] { firstCut.Id, secondCut.Id }, ignoreOrder: true);
        group.ShouldAllBe(member => member.Target == MacroAssignmentTarget.Shift && !member.IsScenario);
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task FindCutGroup_ShiftWithoutAnOrder_IsItsOwnGroup()
    {
        var draft = AddShift("Draft", null, ShiftStatus.OriginalOrder, null);
        await SaveAndClearAsync();

        (await _sut.FindCutGroupAsync(draft.Id)).Select(member => member.Id).ShouldBe(new[] { draft.Id });
    }

    [Test]
    public async Task FindMacro_ReturnsAnUntrackedSnapshot_DeletedIsNull()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Content = "OUTPUT 1, 0",
            Type = (int)MacroFunctionEnum.Standard,
            Category = MacroCategoryEnum.Shift,
            Origin = MacroOrigin.Seed,
            Description = new MultiLanguage()
        };
        var deleted = new Macro { Id = Guid.NewGuid(), Name = "Gone", Description = new MultiLanguage(), IsDeleted = true };
        _context.Macro.AddRange(macro, deleted);
        await SaveAndClearAsync();

        var snapshot = await _sut.FindMacroAsync(macro.Id);

        snapshot.ShouldBe(new MacroSnapshot(
            macro.Id, "AllShift", (int)MacroFunctionEnum.Standard, MacroCategoryEnum.Shift, MacroOrigin.Seed, "OUTPUT 1, 0"));
        (await _sut.FindMacroAsync(deleted.Id)).ShouldBeNull();
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task SetMacroId_Shift_MarksOnlyTheMacroColumnAsModified()
    {
        var newMacroId = Guid.NewGuid();
        var shift = AddShift("Night", Guid.NewGuid(), ShiftStatus.OriginalShift, null);
        await SaveAndClearAsync();

        var written = await _sut.SetMacroIdAsync(MacroAssignmentTarget.Shift, shift.Id, newMacroId);

        written.ShouldBeTrue();
        _context.ChangeTracker.Entries<Shift>().Single().Properties
            .Where(p => p.IsModified)
            .Select(p => p.Metadata.Name)
            .ShouldBe(new[] { nameof(Shift.MacroId) });
        await SaveAndClearAsync();
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == shift.Id)).MacroId.ShouldBe(newMacroId);
    }

    [Test]
    public async Task SetMacroId_AbsenceType_CanRemoveTheReference()
    {
        var absence = AddAbsence(new MultiLanguage { De = "Ferien" }, Guid.NewGuid());
        await SaveAndClearAsync();

        var written = await _sut.SetMacroIdAsync(MacroAssignmentTarget.AbsenceType, absence.Id, null);
        await SaveAndClearAsync();

        written.ShouldBeTrue();
        (await _context.Absence.AsNoTracking().SingleAsync(a => a.Id == absence.Id)).MacroId.ShouldBeNull();
    }

    [Test]
    public async Task SetMacroId_UnknownHolder_ReturnsFalse()
    {
        (await _sut.SetMacroIdAsync(MacroAssignmentTarget.Shift, Guid.NewGuid(), Guid.NewGuid())).ShouldBeFalse();
    }

    private Shift AddShift(string name, Guid? macroId, ShiftStatus status, Guid? orderId, Guid? id = null)
    {
        var shift = new Shift
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            MacroId = macroId,
            Status = status,
            OriginalId = orderId
        };
        _context.Shift.Add(shift);
        return shift;
    }

    private Absence AddAbsence(MultiLanguage name, Guid? macroId)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            MacroId = macroId
        };
        _context.Absence.Add(absence);
        return absence;
    }

    private async Task SaveAndClearAsync()
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
