// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every way of taking a move back refuses a work already back, a held one and a busy origin person, each with its code.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class MoveReversalCheckTests
{
    private readonly Guid _workId = Guid.NewGuid();
    private readonly Guid _origin = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    private ICoverMoveFactsReader _facts = null!;
    private MoveReversalCheck _check = null!;
    private Work _work = null!;
    private CoverMoveFacts _fact = null!;

    [SetUp]
    public void SetUp()
    {
        _work = new Work { Id = _workId, ClientId = _owner, MovedFromClientId = _origin };
        _fact = new CoverMoveFacts(
            _workId, _owner, null, new DateOnly(2026, 11, 4), new TimeOnly(8, 0), new TimeOnly(16, 0), WorkLockLevel.None, false, false, true);
        _facts = Substitute.For<ICoverMoveFactsReader>();
        _facts.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, CoverMoveFacts>)new Dictionary<Guid, CoverMoveFacts> { [_workId] = _fact });
        _facts.IsFreeAsync(Arg.Any<Guid?>(), _origin, _workId, Arg.Any<CancellationToken>()).Returns(true);
        _check = new MoveReversalCheck(_facts);
    }

    private async Task<string> RefusalCode()
        => (await Should.ThrowAsync<MoveReversalRefusedException>(() => _check.EnsureReversibleAsync(_work, CancellationToken.None))).ErrorCode;

    [Test]
    public async Task AFreeOrigin_LetsTheMoveGoBack()
        => await _check.EnsureReversibleAsync(_work, CancellationToken.None);

    [Test]
    public async Task AWorkWithoutOrigin_IsAlreadyBack()
    {
        _work.MovedFromClientId = null;

        (await RefusalCode()).ShouldBe(MoveReversalRefusalCodes.AlreadyBack);
    }

    [Test]
    public async Task AHeldWork_IsRefused()
    {
        _fact = _fact with { LockLevel = WorkLockLevel.Confirmed };

        (await RefusalCode()).ShouldBe(MoveReversalRefusalCodes.Held);
    }

    [Test]
    public async Task ABusyOrigin_IsRefused_InThePlanOfTheWork()
    {
        var token = Guid.NewGuid();
        _work.AnalyseToken = token;
        _facts.IsFreeAsync(token, _origin, _workId, Arg.Any<CancellationToken>()).Returns(false);

        (await RefusalCode()).ShouldBe(MoveReversalRefusalCodes.OriginBusy);
    }
}
