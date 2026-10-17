// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the set-qualification upsert handlers: when no active row exists a new one is added;
/// when one exists it is updated in place (no second insert — which would violate the partial unique
/// index) and the same id is returned.
/// </summary>

using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Qualifications;

[TestFixture]
public class SetQualificationCommandHandlerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualId = Guid.NewGuid();

    [Test]
    public async Task SetClientQualification_NoExisting_Adds()
    {
        var repo = Substitute.For<IClientQualificationRepository>();
        repo.GetActiveAsync(ClientId, QualId, Arg.Any<CancellationToken>()).Returns((ClientQualification?)null);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetClientQualificationCommandHandler(repo, TestGroupWriteVisibility.AllClientsVisible(), uow);

        var id = await handler.Handle(
            new SetClientQualificationCommand(ClientId, QualId, QualificationLevel.Proficient, null, null, null),
            CancellationToken.None);

        await repo.Received(1).Add(Arg.Is<ClientQualification>(cq =>
            cq.ClientId == ClientId && cq.QualificationId == QualId && cq.Level == QualificationLevel.Proficient));
        await uow.Received(1).CompleteAsync();
        id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task SetClientQualification_Existing_UpdatesInPlace_NoAdd()
    {
        var existing = new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = ClientId, QualificationId = QualId, Level = QualificationLevel.Low
        };
        var repo = Substitute.For<IClientQualificationRepository>();
        repo.GetActiveAsync(ClientId, QualId, Arg.Any<CancellationToken>()).Returns(existing);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetClientQualificationCommandHandler(repo, TestGroupWriteVisibility.AllClientsVisible(), uow);

        var id = await handler.Handle(
            new SetClientQualificationCommand(ClientId, QualId, QualificationLevel.Expert, new(2026, 1, 1), null, "promoted"),
            CancellationToken.None);

        id.ShouldBe(existing.Id);
        existing.Level.ShouldBe(QualificationLevel.Expert);
        existing.ValidFrom.ShouldBe(new DateOnly(2026, 1, 1));
        existing.Note.ShouldBe("promoted");
        await repo.DidNotReceive().Add(Arg.Any<ClientQualification>());
        await uow.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SetClientQualification_HiddenClient_RefusedLikeUnknownClient_NothingWritten()
    {
        var repo = Substitute.For<IClientQualificationRepository>();
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetClientQualificationCommandHandler(repo, TestGroupWriteVisibility.ClientsHidden(ClientId), uow);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new SetClientQualificationCommand(ClientId, QualId, QualificationLevel.Proficient, null, null, null),
            CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {ClientId} not found");
        await repo.DidNotReceive().GetActiveAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().Add(Arg.Any<ClientQualification>());
        await uow.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task SetShiftRequiredQualification_Existing_UpdatesInPlace_NoAdd()
    {
        var existing = new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = ShiftId, QualificationId = QualId, IsMandatory = false, MinLevel = QualificationLevel.Low
        };
        var repo = Substitute.For<IShiftRequiredQualificationRepository>();
        repo.GetActiveAsync(ShiftId, QualId, Arg.Any<CancellationToken>()).Returns(existing);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetShiftRequiredQualificationCommandHandler(repo, uow);

        var id = await handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualId, true, QualificationLevel.Advanced, null),
            CancellationToken.None);

        id.ShouldBe(existing.Id);
        existing.IsMandatory.ShouldBeTrue();
        existing.MinLevel.ShouldBe(QualificationLevel.Advanced);
        await repo.DidNotReceive().Add(Arg.Any<ShiftRequiredQualification>());
        await uow.Received(1).CompleteAsync();
    }

    [TestCase(null, false, null)]
    [TestCase(false, false, false)]
    [TestCase(null, true, true)]
    [TestCase(false, true, true)]
    public async Task SetShiftRequiredQualification_Existing_LevelBindingOverride_ReplacedUnlessKept(
        bool? sent, bool keep, bool? expected)
    {
        // Arrange
        var existing = new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = ShiftId, QualificationId = QualId, IsMandatory = true,
            MinLevel = QualificationLevel.Low, LevelBindingOverride = true
        };
        var repo = Substitute.For<IShiftRequiredQualificationRepository>();
        repo.GetActiveAsync(ShiftId, QualId, Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new SetShiftRequiredQualificationCommandHandler(repo, Substitute.For<IUnitOfWork>());

        // Act
        await handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualId, true, QualificationLevel.Basic, sent, keep),
            CancellationToken.None);

        // Assert
        existing.LevelBindingOverride.ShouldBe(expected);
    }

    [Test]
    public async Task SetShiftRequiredQualification_New_StoresTheOverride()
    {
        // Arrange
        var repo = Substitute.For<IShiftRequiredQualificationRepository>();
        repo.GetActiveAsync(ShiftId, QualId, Arg.Any<CancellationToken>()).Returns((ShiftRequiredQualification?)null);
        repo.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement>());
        var handler = new SetShiftRequiredQualificationCommandHandler(repo, Substitute.For<IUnitOfWork>());

        // Act
        await handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualId, true, QualificationLevel.Proficient, true),
            CancellationToken.None);

        // Assert
        await repo.Received(1).Add(Arg.Is<ShiftRequiredQualification>(r =>
            r.QualificationId == QualId && r.LevelBindingOverride == true));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SetShiftRequiredQualification_NewRowKeepingTheBinding_TakesTheInheritedOverride(bool inheritedOverride)
    {
        // Arrange
        var orderShiftId = Guid.NewGuid();
        var inheritedRow = new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = orderShiftId, QualificationId = QualId, IsMandatory = true,
            MinLevel = QualificationLevel.Low, LevelBindingOverride = inheritedOverride
        };
        var repo = Substitute.For<IShiftRequiredQualificationRepository>();
        repo.GetActiveAsync(ShiftId, QualId, Arg.Any<CancellationToken>()).Returns((ShiftRequiredQualification?)null);
        repo.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement> { new(ShiftId, "Piece", orderShiftId, inheritedRow) });
        var handler = new SetShiftRequiredQualificationCommandHandler(repo, Substitute.For<IUnitOfWork>());

        // Act
        await handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualId, true, QualificationLevel.Advanced, null, KeepExistingLevelBinding: true),
            CancellationToken.None);

        // Assert
        await repo.Received(1).Add(Arg.Is<ShiftRequiredQualification>(r =>
            r.ShiftId == ShiftId && r.QualificationId == QualId && r.LevelBindingOverride == inheritedOverride));
    }

    [Test]
    public async Task SetShiftRequiredQualification_NewRowKeepingTheBinding_WithoutInheritedRow_Inherits()
    {
        // Arrange
        var repo = Substitute.For<IShiftRequiredQualificationRepository>();
        repo.GetActiveAsync(ShiftId, QualId, Arg.Any<CancellationToken>()).Returns((ShiftRequiredQualification?)null);
        repo.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement>());
        var handler = new SetShiftRequiredQualificationCommandHandler(repo, Substitute.For<IUnitOfWork>());

        // Act
        await handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, QualId, true, QualificationLevel.Advanced, true, KeepExistingLevelBinding: true),
            CancellationToken.None);

        // Assert
        await repo.Received(1).Add(Arg.Is<ShiftRequiredQualification>(r => r.LevelBindingOverride == null));
    }
}
