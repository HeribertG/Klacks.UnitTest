using System;
using System.Threading.Tasks;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence
{
    [TestFixture]
    public class DataBaseContextMacroCategoryUniquenessTests
    {
        private DataBaseContext _context = null!;

        [SetUp]
        public void Setup()
        {
            var options = new DbContextOptionsBuilder<DataBaseContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
            _context = new DataBaseContext(options, mockHttpContextAccessor);
        }

        [TearDown]
        public void TearDown()
        {
            _context.Dispose();
        }

        [Test]
        public async Task SaveChanges_TwoMacrosSameCategoryDifferentFunction_ShouldKeepBothCategories()
        {
            var standard = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "Standard",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "Standard" },
                Type = (int)MacroFunctionEnum.Standard,
                Category = MacroCategoryEnum.Shift,
            };
            var additive = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "StandardAdditive",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "StandardAdditive" },
                Type = (int)MacroFunctionEnum.StandardAdditive,
                Category = MacroCategoryEnum.Shift,
            };

            _context.Macro.Add(standard);
            await _context.SaveChangesAsync();

            _context.Macro.Add(additive);
            await _context.SaveChangesAsync();

            var reloadedStandard = await _context.Macro.FindAsync(standard.Id);
            reloadedStandard!.Category.ShouldBe(MacroCategoryEnum.Shift);
        }

        [Test]
        public async Task SaveChanges_TwoMacrosSameCategorySameFunction_ShouldDemoteThePreviousOne()
        {
            var first = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "First",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "First" },
                Type = (int)MacroFunctionEnum.Standard,
                Category = MacroCategoryEnum.Shift,
            };
            var second = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "Second",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "Second" },
                Type = (int)MacroFunctionEnum.Standard,
                Category = MacroCategoryEnum.Shift,
            };

            _context.Macro.Add(first);
            await _context.SaveChangesAsync();

            _context.Macro.Add(second);
            await _context.SaveChangesAsync();

            var reloadedFirst = await _context.Macro.FindAsync(first.Id);
            reloadedFirst!.Category.ShouldBe(MacroCategoryEnum.Unspecified);
        }

        [Test]
        public async Task SaveChanges_TwoCustomMacrosSameCategory_ShouldKeepBothCategories()
        {
            var first = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "FirstCustom",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "FirstCustom" },
                Type = (int)MacroFunctionEnum.Custom,
                Category = MacroCategoryEnum.Vacation,
            };
            var second = new Macro
            {
                Id = Guid.NewGuid(),
                Name = "SecondCustom",
                Content = "OUTPUT 1, 0",
                Description = new MultiLanguage { De = "SecondCustom" },
                Type = (int)MacroFunctionEnum.Custom,
                Category = MacroCategoryEnum.Vacation,
            };

            _context.Macro.Add(first);
            await _context.SaveChangesAsync();

            _context.Macro.Add(second);
            await _context.SaveChangesAsync();

            var reloadedFirst = await _context.Macro.FindAsync(first.Id);
            reloadedFirst!.Category.ShouldBe(MacroCategoryEnum.Vacation);
        }
    }
}
