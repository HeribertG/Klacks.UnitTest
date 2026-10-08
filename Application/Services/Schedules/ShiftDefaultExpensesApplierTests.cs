// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Default expenses of a shift used to be copied only by the single Work POST; every bulk path (wizards,
/// harmonizer, propose plan, place-work skill, POST /Works/Bulk) created Works without them. The shared applier
/// copies them for top-level Works only, keeps the Work's scenario token, and loads all templates in one call.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ShiftDefaultExpensesApplierTests
{
    private IShiftExpensesRepository _shiftExpensesRepository = null!;
    private IExpensesRepository _expensesRepository = null!;
    private List<ExpensesEntity> _added = null!;
    private ShiftDefaultExpensesApplier _applier = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftExpensesRepository = Substitute.For<IShiftExpensesRepository>();
        _expensesRepository = Substitute.For<IExpensesRepository>();
        _added = [];
        _expensesRepository.Add(Arg.Do<ExpensesEntity>(e => _added.Add(e))).Returns(Task.CompletedTask);
        _applier = new ShiftDefaultExpensesApplier(_shiftExpensesRepository, _expensesRepository);
    }

    [Test]
    public async Task ApplyAsync_CopiesTemplatesOntoTopLevelWorks_WithTheWorksToken_InOneBatchRead()
    {
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();
        var token = Guid.NewGuid();
        var realWork = NewWork(shiftA, null, null);
        var scenarioWork = NewWork(shiftB, token, null);
        _shiftExpensesRepository.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftExpenses>
            {
                NewTemplate(shiftA, 10m, "Lunch", false),
                NewTemplate(shiftA, 25m, "Bonus", true),
                NewTemplate(shiftB, 7m, "Parking", false)
            });

        await _applier.ApplyAsync([realWork, scenarioWork]);

        await _shiftExpensesRepository.Received(1)
            .GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        _added.Count.ShouldBe(3);
        _added.Where(e => e.WorkId == realWork.Id).Select(e => (e.Amount, e.Description, e.Taxable, e.AnalyseToken))
            .ShouldBe(new[] { (10m, "Lunch", false, (Guid?)null), (25m, "Bonus", true, (Guid?)null) }, ignoreOrder: true);
        _added.Single(e => e.WorkId == scenarioWork.Id).AnalyseToken.ShouldBe(token);
    }

    [Test]
    public async Task ApplyAsync_ContainerChildren_NeverReceiveTemplateExpenses()
    {
        var shift = Guid.NewGuid();
        var parent = NewWork(shift, null, null);
        var child = NewWork(shift, null, parent.Id);
        _shiftExpensesRepository.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftExpenses> { NewTemplate(shift, 10m, "Lunch", false) });

        await _applier.ApplyAsync([parent, child]);

        _added.Select(e => e.WorkId).ShouldBe(new[] { parent.Id });
    }

    [Test]
    public async Task ApplyAsync_OnlyChildren_DoesNotQueryTemplates()
    {
        var child = NewWork(Guid.NewGuid(), null, Guid.NewGuid());

        await _applier.ApplyAsync([child]);

        await _shiftExpensesRepository.DidNotReceiveWithAnyArgs().GetByShiftIdsAsync(default!, default);
        _added.ShouldBeEmpty();
    }

    private static Work NewWork(Guid shiftId, Guid? analyseToken, Guid? parentWorkId)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = shiftId,
            AnalyseToken = analyseToken,
            ParentWorkId = parentWorkId
        };

    private static ShiftExpenses NewTemplate(Guid shiftId, decimal amount, string description, bool taxable)
        => new() { Id = Guid.NewGuid(), ShiftId = shiftId, Amount = amount, Description = description, Taxable = taxable };
}
