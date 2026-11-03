// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The owner change: the work and its children carry the new owner before anything is recalculated, because the macros read
/// that owner's contract. The marker changes only when an absence cover names the person it took the work from.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WorkHandOverServiceTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly Guid Elsa = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private IContainerWorkCascadeService _cascade = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private WorkHandOverService _service = null!;
    private Work _work = null!;
    private CancellationToken _token;

    [SetUp]
    public void SetUp()
    {
        _cascade = Substitute.For<IContainerWorkCascadeService>();
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _childrenManager.RecalculateForNewOwnerAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>()).Returns(true);
        _service = new WorkHandOverService(_cascade, _childrenManager);
        _work = new Work { Id = Guid.NewGuid(), ClientId = Elsa, CurrentDate = Day };
        _token = new CancellationTokenSource().Token;
    }

    [Test]
    public async Task Transfer_MovesTheChildrenFirst_AndRecalculatesForTheNewOwner()
    {
        Guid? ownerWhenRecalculated = null;
        _childrenManager.RecalculateForNewOwnerAsync(_work, _token)
            .Returns(_ =>
            {
                ownerWhenRecalculated = _work.ClientId;
                return true;
            });

        await _service.TransferAsync(_work, Bob, Elsa, _token);

        ownerWhenRecalculated.ShouldBe(Bob);
        Received.InOrder(() =>
        {
            _cascade.MoveChildrenAsync(_work.Id, Day, Elsa, Bob);
            _childrenManager.RecalculateForNewOwnerAsync(_work, _token);
        });
    }

    [Test]
    public async Task Transfer_ReportsWhetherTheRecalculationWorked()
    {
        _childrenManager.RecalculateForNewOwnerAsync(_work, _token).Returns(false);

        (await _service.TransferAsync(_work, Bob, Elsa, _token)).ShouldBeFalse();
    }

    [Test]
    public async Task Transfer_FromACover_MarksThePersonItCameFrom()
    {
        await _service.TransferAsync(_work, Bob, Elsa, _token);

        _work.MovedFromClientId.ShouldBe(Elsa);
    }

    [Test]
    public async Task Transfer_WithoutAnOrigin_KeepsTheStoredMarker()
    {
        var earlierOwner = Guid.NewGuid();
        _work.MovedFromClientId = earlierOwner;

        await _service.TransferAsync(_work, Bob, null, _token);

        _work.ClientId.ShouldBe(Bob);
        _work.MovedFromClientId.ShouldBe(earlierOwner);
    }
}
