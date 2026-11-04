// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Expenses and allowances belong to the employee, never to the customer: a closed work with an expense
/// on a sealed customer order is loaded through the real OrderExportDataLoader (in-memory EF Core) and
/// rendered by every order export formatter; no file may contain the expense. The client period export
/// keeps its expenses (WorkSubEntryLoader still loads them by default).
/// </summary>
using System.Text;
using ClosedXML.Excel;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Exports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class OrderExportExpensesExclusionTests
{
    private const string ExpenseMarker = "ZQSPX7";
    private const decimal ExpenseAmount = 12.5m;
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly DateOnly WorkDate = new(2026, 1, 10);

    private DataBaseContext _context = null!;
    private OrderExportDataLoader _loader = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _employeeId = Guid.NewGuid();
    private readonly Guid _workId = Guid.NewGuid();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var descendantResolver = Substitute.For<IShiftDescendantResolver>();
        descendantResolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, HashSet<Guid>> { [_sealedOrderId] = [_sealedOrderId] });

        _loader = new OrderExportDataLoader(
            _context, descendantResolver, new FixedCompanyClock(new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero)));

        _context.Client.Add(new Client
        {
            Id = _customerId,
            Type = EntityTypeEnum.Customer,
            IdNumber = 900,
            Name = "Customer",
            FirstName = "Corp",
        });
        _context.Client.Add(new Client
        {
            Id = _employeeId,
            Type = EntityTypeEnum.Employee,
            IdNumber = 101,
            Name = "Worker",
            FirstName = "Anna",
        });
        _context.Shift.Add(new Shift
        {
            Id = _sealedOrderId,
            Status = ShiftStatus.SealedOrder,
            Name = "Night Watch",
            Abbreviation = "NW",
            ClientId = _customerId,
            FromDate = new DateOnly(2026, 1, 1),
        });
        _context.Work.Add(new Work
        {
            Id = _workId,
            ShiftId = _sealedOrderId,
            ClientId = _employeeId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.Closed,
        });
        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Amount = ExpenseAmount,
            Description = ExpenseMarker,
            Taxable = false,
        });
        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Amount = ExpenseAmount,
            Description = ExpenseMarker,
            Taxable = true,
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    public static IEnumerable<IExportFormatter> OrderFormatters()
    {
        return typeof(IExportFormatter).Assembly.GetTypes()
            .Where(t => typeof(IExportFormatter).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName)
            .Select(t => (IExportFormatter)Activator.CreateInstance(t)!);
    }

    [TestCaseSource(nameof(OrderFormatters))]
    public async Task OrderExport_WorkWithExpense_WritesNoExpenseIntoTheCustomerFile(IExportFormatter formatter)
    {
        var data = await _loader.LoadAsync([_sealedOrderId]);

        data.Orders.ShouldHaveSingleItem().WorkEntries.ShouldHaveSingleItem().WorkId.ShouldBe(_workId);

        var content = formatter.Format(data, ExportSampleDataFactory.CreateSampleOptions());

        ReadAllText(content).Contains(ExpenseMarker, StringComparison.Ordinal)
            .ShouldBeFalse($"Format '{formatter.FormatKey}' exports an expense to the customer.");
    }

    [Test]
    public async Task OrderExport_Json_HasNoExpensesField()
    {
        var data = await _loader.LoadAsync([_sealedOrderId]);

        var json = Encoding.UTF8.GetString(new JsonExportFormatter().Format(data, ExportSampleDataFactory.CreateSampleOptions()));

        json.ShouldContain("\"workEntries\"");
        json.Contains("\"expenses\"", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Test]
    public async Task WorkSubEntryLoader_WithoutExpenses_LeavesExpensesEmpty_AndStillLoadsThemByDefault()
    {
        var withoutExpenses = await WorkSubEntryLoader.LoadAsync(
            _context, [_workId], [_employeeId], [WorkDate], CancellationToken.None, loadExpenses: false);
        var byDefault = await WorkSubEntryLoader.LoadAsync(
            _context, [_workId], [_employeeId], [WorkDate], CancellationToken.None);

        withoutExpenses.Expenses.ShouldBeEmpty();
        byDefault.Expenses[_workId].Count.ShouldBe(2);
    }

    private static string ReadAllText(byte[] content)
    {
        if (content.Length >= ZipMagic.Length && content.Take(ZipMagic.Length).SequenceEqual(ZipMagic))
        {
            using var stream = new MemoryStream(content);
            using var workbook = new XLWorkbook(stream);
            return string.Join("\n", workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetString()));
        }

        return Encoding.Latin1.GetString(content);
    }
}
