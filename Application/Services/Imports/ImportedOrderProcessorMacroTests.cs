// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how the ERP importer treats the calculation macro: a newly created draft receives the default
/// Shift-category macro (or none when no default is configured), while a re-delivery that overwrites an
/// existing draft never touches the macro the draft already carries.
/// </summary>

using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ImportedOrderProcessorMacroTests
{
    private const string SourceSystem = "erp-1";
    private const string OrderReference = "ORD-1";

    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid DefaultMacroId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private ImportedOrderProcessor _processor = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();

        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>())
            .Returns(new Client { Id = CustomerId });
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Client>>>())
            .Returns(ci => ci.Arg<Func<Task<Client>>>()());

        var supersession = new OrderSupersessionService(
            _shiftRepository,
            Substitute.For<IWorkRepository>(),
            _clientRepository,
            Substitute.For<IAgentTriggerService>(),
            ShiftGroupScopeReaderStub.WithoutAnyGroups(),
            _unitOfWork,
            new FixedCompanyClock(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero)),
            _defaultShiftMacroResolver,
            NullLogger<OrderSupersessionService>.Instance);

        _processor = new ImportedOrderProcessor(
            new ErpCustomerResolver(_clientRepository), _shiftRepository, supersession, _unitOfWork, _defaultShiftMacroResolver);
    }

    private static ImportedOrderPayload Order(TimeOnly start) => new()
    {
        SourceSystemId = SourceSystem,
        ExternalOrderReference = OrderReference,
        Customer = new ImportedCustomerPayload { Company = "Spitex Musterhausen" },
        FromDate = new DateOnly(2026, 8, 1),
        StartTime = start,
        EndTime = start.AddHours(8),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1
    };

    [Test]
    public async Task ProcessOrderAsync_NewOrder_DraftGetsDefaultShiftMacro()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns(DefaultMacroId);

        var outcome = await _processor.ProcessOrderAsync(Order(new TimeOnly(7, 0)));

        outcome.ShouldBe(ImportedOrderOutcome.Created);
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s =>
            s.Status == ShiftStatus.OriginalOrder && s.MacroId == DefaultMacroId));
    }

    [Test]
    public async Task ProcessOrderAsync_NewOrderWithoutConfiguredDefault_DraftKeepsNoMacro()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var outcome = await _processor.ProcessOrderAsync(Order(new TimeOnly(7, 0)));

        outcome.ShouldBe(ImportedOrderOutcome.Created);
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.MacroId == null));
    }

    [Test]
    public async Task ProcessOrderAsync_ChangedExistingDraft_KeepsItsMacroAndDoesNotResolveDefault()
    {
        var plannerMacroId = Guid.NewGuid();
        var existing = ImportedOrderShiftMapper.BuildDraft(Order(new TimeOnly(7, 0)), CustomerId);
        existing.MacroId = plannerMacroId;
        _shiftRepository.FindActiveByExternalReferenceAsync(SourceSystem, OrderReference, Arg.Any<CancellationToken>())
            .Returns(existing);

        var outcome = await _processor.ProcessOrderAsync(Order(new TimeOnly(9, 0)));

        outcome.ShouldBe(ImportedOrderOutcome.Updated);
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s =>
            s.Id == existing.Id && s.MacroId == plannerMacroId && s.StartShift == new TimeOnly(9, 0)));
        await _defaultShiftMacroResolver.DidNotReceive().ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessOrderAsync_ChangedExistingDraftWithoutMacro_StaysWithoutMacro()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns(DefaultMacroId);
        var existing = ImportedOrderShiftMapper.BuildDraft(Order(new TimeOnly(7, 0)), CustomerId);
        _shiftRepository.FindActiveByExternalReferenceAsync(SourceSystem, OrderReference, Arg.Any<CancellationToken>())
            .Returns(existing);

        await _processor.ProcessOrderAsync(Order(new TimeOnly(9, 0)));

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == existing.Id && s.MacroId == null));
    }
}
