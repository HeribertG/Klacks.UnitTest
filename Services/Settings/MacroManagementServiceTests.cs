using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Scripting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class MacroManagementServiceTests
{
    private DataBaseContext _context;
    private MacroManagementService _service;
    private MacroCache _macroCache;
    private ILogger<MacroManagementService> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<MacroManagementService>>();
        _macroCache = new MacroCache();
        _service = new MacroManagementService(_context, _macroCache, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddMacroAsync_WithValidMacro_ShouldAddToContext()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        await _service.AddMacroAsync(macro);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.FindAsync(macro.Id);
        result.ShouldNotBeNull();
        result.Name.ShouldBe("TestMacro");
        result.Content.ShouldBe("TestContent");
    }

    [Test]
    public async Task UpdateMacroAsync_WithExistingMacro_ShouldUpdateProperties()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "OriginalName",
            Content = "OriginalContent",
            Description = new MultiLanguage { De = "Original Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        macro.Name = "UpdatedName";
        macro.Content = "UpdatedContent";

        await _service.UpdateMacroAsync(macro, byAssistant: false);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.FindAsync(macro.Id);
        result!.Name.ShouldBe("UpdatedName");
        result.Content.ShouldBe("UpdatedContent");
    }

    [Test]
    public async Task DeleteMacroAsync_WithExistingCustomMacro_ShouldRemoveFromContext()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "ToDelete",
            Content = "DeleteContent",
            Description = new MultiLanguage { De = "Delete Description" },
            Type = (int)MacroFunctionEnum.Custom
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        await _service.DeleteMacroAsync(macro.Id);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == macro.Id);
        result.ShouldNotBeNull();
        result.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task DeleteMacroAsync_ReferencedByActiveShifts_ThrowsWithShiftCountInMessage()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Referenced",
            Content = "Content",
            Description = new MultiLanguage { De = "Desc" },
            Type = (int)MacroFunctionEnum.Custom
        };
        _context.Macro.Add(macro);
        _context.Shift.AddRange(
            new Shift { Id = Guid.NewGuid(), Name = "Shift1", MacroId = macro.Id },
            new Shift { Id = Guid.NewGuid(), Name = "Shift2", MacroId = macro.Id });
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => _service.DeleteMacroAsync(macro.Id));

        ex.Message.ShouldContain("2");

        var result = await _context.Macro.FindAsync(macro.Id);
        result.ShouldNotBeNull();
        result!.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteMacroAsync_WithStandardFunctionType_Throws()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Content = "Content",
            Description = new MultiLanguage { De = "Desc" },
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<InvalidRequestException>(() => _service.DeleteMacroAsync(macro.Id));

        var result = await _context.Macro.FindAsync(macro.Id);
        result.ShouldNotBeNull();
        result!.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteMacroAsync_UnreferencedCustomMacro_Succeeds()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Custom",
            Content = "Content",
            Description = new MultiLanguage { De = "Desc" },
            Category = MacroCategoryEnum.Unspecified,
            Type = (int)MacroFunctionEnum.Custom
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        await _service.DeleteMacroAsync(macro.Id);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == macro.Id);
        result.ShouldNotBeNull();
        result!.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task UpdateMacroAsync_WithCachedCompilation_InvalidatesCache()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Cached",
            Content = "output 1, 1",
            Description = new MultiLanguage { De = "Desc" },
            Type = (int)MacroFunctionEnum.Custom
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();
        _macroCache.GetOrCompile(macro.Id, macro.Content);
        _macroCache.Contains(macro.Id).ShouldBeTrue();

        macro.Content = "output 1, 2";
        await _service.UpdateMacroAsync(macro, byAssistant: false);

        _macroCache.Contains(macro.Id).ShouldBeFalse();
    }

    [Test]
    public async Task DeleteMacroAsync_WithCachedCompilation_InvalidatesCache()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "CachedDelete",
            Content = "output 1, 1",
            Description = new MultiLanguage { De = "Desc" },
            Category = MacroCategoryEnum.Unspecified,
            Type = (int)MacroFunctionEnum.Custom
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();
        _macroCache.GetOrCompile(macro.Id, macro.Content);
        _macroCache.Contains(macro.Id).ShouldBeTrue();

        await _service.DeleteMacroAsync(macro.Id);

        _macroCache.Contains(macro.Id).ShouldBeFalse();
    }

    [Test]
    public async Task GetMacroAsync_WithExistingId_ShouldReturnMacro()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroAsync(macro.Id);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(macro.Id);
        result.Name.ShouldBe("TestMacro");
    }

    [Test]
    public async Task GetMacroAsync_WithNonExistingId_ShouldThrowException()
    {
        var nonExistingId = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(() => _service.GetMacroAsync(nonExistingId));
    }

    [Test]
    public async Task GetMacroListAsync_WithMultipleMacros_ShouldReturnAll()
    {
        var macros = new[]
        {
            new Macro { Id = Guid.NewGuid(), Name = "Macro1", Content = "Content1", Description = new MultiLanguage { De = "Desc1" }, Type = 1 },
            new Macro { Id = Guid.NewGuid(), Name = "Macro2", Content = "Content2", Description = new MultiLanguage { De = "Desc2" }, Type = 2 },
            new Macro { Id = Guid.NewGuid(), Name = "Macro3", Content = "Content3", Description = new MultiLanguage { De = "Desc3" }, Type = 3 }
        };

        _context.Macro.AddRange(macros);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroListAsync();

        result.Count().ShouldBe(3);
        result.ShouldContain(m => m.Name == "Macro1");
        result.ShouldContain(m => m.Name == "Macro2");
        result.ShouldContain(m => m.Name == "Macro3");
    }

    [Test]
    public async Task MacroExistsAsync_WithExistingId_ShouldReturnTrue()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        var result = await _service.MacroExistsAsync(macro.Id);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task MacroExistsAsync_WithNonExistingId_ShouldReturnFalse()
    {
        var nonExistingId = Guid.NewGuid();

        var result = await _service.MacroExistsAsync(nonExistingId);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task UpdateMacroAsync_DetachedPayloadWithOtherOrigin_KeepsPersistedOrigin()
    {
        var id = Guid.NewGuid();
        _context.Macro.Add(new Macro
        {
            Id = id,
            Name = "AllShift",
            Content = "output 1, 1",
            Description = new MultiLanguage { De = "Desc" },
            Origin = MacroOrigin.Seed
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var returned = await _service.UpdateMacroAsync(new Macro
        {
            Id = id,
            Name = "AllShift renamed",
            Content = "output 1, 2",
            Description = new MultiLanguage { De = "Desc" },
            Origin = MacroOrigin.User
        }, byAssistant: false);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        returned.Origin.ShouldBe(MacroOrigin.Seed);
        var persisted = await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == id);
        persisted.Origin.ShouldBe(MacroOrigin.Seed);
        persisted.Name.ShouldBe("AllShift renamed");
    }

    [TestCase(MacroOrigin.Assistant, false, MacroOrigin.User)]
    [TestCase(MacroOrigin.AssistantExtension, false, MacroOrigin.User)]
    [TestCase(MacroOrigin.Assistant, true, MacroOrigin.Assistant)]
    [TestCase(MacroOrigin.AssistantExtension, true, MacroOrigin.AssistantExtension)]
    [TestCase(MacroOrigin.Seed, false, MacroOrigin.Seed)]
    [TestCase(MacroOrigin.Import, false, MacroOrigin.Import)]
    [TestCase(MacroOrigin.User, true, MacroOrigin.User)]
    public async Task UpdateMacroAsync_AssistantMacroEditedByTheAdministrator_BecomesUser_OtherwiseOriginIsKept(
        MacroOrigin persistedOrigin, bool byAssistant, MacroOrigin expectedOrigin)
    {
        var id = Guid.NewGuid();
        _context.Macro.Add(new Macro
        {
            Id = id,
            Name = "Sunday rate",
            Content = "output 1, 1",
            Description = new MultiLanguage { De = "Desc" },
            Origin = persistedOrigin
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var returned = await _service.UpdateMacroAsync(new Macro
        {
            Id = id,
            Name = "Sunday rate",
            Content = "output 1, 2",
            Description = new MultiLanguage { De = "Desc" },
            Origin = MacroOrigin.Assistant
        }, byAssistant);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        returned.Origin.ShouldBe(expectedOrigin);
        (await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == id)).Origin.ShouldBe(expectedOrigin);
    }

    [Test]
    public async Task UpdateMacroAsync_NewInstanceAfterTheSameMacroWasReadTrackedById_UpdatesWithoutTrackingConflict()
    {
        var macro = await AddCustomMacroAsync("Sunday rate");
        _context.ChangeTracker.Clear();
        await _service.GetMacroAsync(macro.Id);

        await _service.UpdateMacroAsync(RenamedCopyOf(macro, "Sunday rate renamed"), byAssistant: true);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == macro.Id)).Name.ShouldBe("Sunday rate renamed");
    }

    [Test]
    public async Task UpdateMacroAsync_NewInstanceAfterTheMacroListWasReadTracked_UpdatesWithoutTrackingConflict()
    {
        var macro = await AddCustomMacroAsync("Night rate");
        _context.ChangeTracker.Clear();
        await _service.GetMacroListAsync();

        await _service.UpdateMacroAsync(RenamedCopyOf(macro, "Night rate renamed"), byAssistant: true);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == macro.Id)).Name.ShouldBe("Night rate renamed");
    }

    [Test]
    public async Task UpdateMacroAsync_NewInstanceWhileTheTrackedMacroHasPendingChanges_FailsInsteadOfDroppingThem()
    {
        var macro = await AddCustomMacroAsync("Holiday rate");
        _context.ChangeTracker.Clear();
        var tracked = await _service.GetMacroAsync(macro.Id);
        tracked.Content = "output 1, 3";

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UpdateMacroAsync(RenamedCopyOf(macro, "Holiday rate renamed"), byAssistant: true));

        _context.Entry(tracked).State.ShouldBe(EntityState.Modified);
    }

    private static Macro RenamedCopyOf(Macro macro, string name) => new()
    {
        Id = macro.Id,
        Name = name,
        Content = macro.Content,
        Description = new MultiLanguage { De = "Desc" },
        Type = macro.Type,
        Origin = MacroOrigin.Assistant
    };

    private async Task<Macro> AddCustomMacroAsync(string name)
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = name,
            Content = "Content",
            Description = new MultiLanguage { De = "Desc" },
            Type = (int)MacroFunctionEnum.Custom
        };
        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();
        return macro;
    }

    private static Absence AbsenceUsing(Guid macroId, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = new MultiLanguage { De = "Ferien" },
        Description = new MultiLanguage(),
        Abbreviation = new MultiLanguage(),
        MacroId = macroId,
        IsDeleted = isDeleted
    };

    [Test]
    public async Task DeleteMacroAsync_ReferencedByAbsenceType_ThrowsWithAbsenceCount()
    {
        var macro = await AddCustomMacroAsync("UsedByAbsence");
        _context.Absence.AddRange(AbsenceUsing(macro.Id), AbsenceUsing(macro.Id), AbsenceUsing(macro.Id));
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => _service.DeleteMacroAsync(macro.Id));

        ex.Message.ShouldContain("3 absence type(s)");
        (await _context.Macro.FindAsync(macro.Id))!.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteMacroAsync_OnlySoftDeletedAbsenceReference_Succeeds()
    {
        var macro = await AddCustomMacroAsync("FormerlyUsedByAbsence");
        _context.Absence.Add(AbsenceUsing(macro.Id, isDeleted: true));
        await _context.SaveChangesAsync();

        await _service.DeleteMacroAsync(macro.Id);
        await _context.SaveChangesAsync();

        (await _context.Macro.IgnoreQueryFilters().SingleAsync(m => m.Id == macro.Id)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task DeleteMacroAsync_OnlySoftDeletedShiftReference_Succeeds()
    {
        var macro = await AddCustomMacroAsync("FormerlyUsedByShift");
        _context.Shift.Add(new Shift { Id = Guid.NewGuid(), Name = "Old", MacroId = macro.Id, IsDeleted = true });
        await _context.SaveChangesAsync();

        await _service.DeleteMacroAsync(macro.Id);
        await _context.SaveChangesAsync();

        (await _context.Macro.IgnoreQueryFilters().SingleAsync(m => m.Id == macro.Id)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task AddMacroAsync_CustomUnspecifiedCopy_LeavesTheStandardHolderUntouched()
    {
        var holder = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Content = "output 1, 1",
            Description = new MultiLanguage { De = "Desc" },
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard
        };
        _context.Macro.Add(holder);
        await _context.SaveChangesAsync();

        await _service.AddMacroAsync(new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift copy",
            Content = "output 1, 1",
            Description = new MultiLanguage(),
            Category = MacroCategoryEnum.Unspecified,
            Type = (int)MacroFunctionEnum.Custom,
            Origin = MacroOrigin.Assistant
        });
        await _context.SaveChangesAsync();

        (await _context.Macro.AsNoTracking().SingleAsync(m => m.Id == holder.Id)).Category.ShouldBe(MacroCategoryEnum.Shift);
    }
}