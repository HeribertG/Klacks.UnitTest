// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The requirement rows of a shift that carries an analyse token (a shift of a variant or a scenario clone) are written
/// through the shift only: the two requirement commands answer such a shift exactly like a hidden one, for every caller,
/// so the freeze of a key in use cannot be bypassed here.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Qualifications;

[TestFixture]
public class RequiredQualificationVariantShiftTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();

    private IShiftRequiredQualificationRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftVisibilityGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IShiftRequiredQualificationRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _guard = ShiftVisibilityGuards.AllowAll();
        _guard.FindFirstTokenedAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(ShiftId)), Arg.Any<CancellationToken>())
            .Returns(ShiftId);
    }

    [Test]
    public async Task Set_OnATokenedShift_IsAnsweredLikeAHiddenShift_AndWritesNothing()
    {
        var handler = new SetShiftRequiredQualificationCommandHandler(_repository, _unitOfWork, _guard);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualificationId, true, QualificationLevel.Basic, null),
            CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, ShiftId));
        await _repository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_OnATokenedShift_IsLeftUntouched_LikeARowOfAHiddenShift()
    {
        var rowId = Guid.NewGuid();
        _repository.Get(rowId).Returns(new ShiftRequiredQualification { Id = rowId, ShiftId = ShiftId, QualificationId = QualificationId });
        var handler = new DeleteShiftRequiredQualificationCommandHandler(_repository, _unitOfWork, _guard);

        await handler.Handle(new DeleteShiftRequiredQualificationCommand(rowId), CancellationToken.None);

        await _repository.DidNotReceive().Delete(rowId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
