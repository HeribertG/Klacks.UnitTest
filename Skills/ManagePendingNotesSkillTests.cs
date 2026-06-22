// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for manage_pending_notes: action 'read' lists the current user's pending notes,
/// action 'mark_delivered' archives the given note ids, a missing action defaults to read, and
/// mark_delivered without note ids returns an error without touching the repository.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ManagePendingNotesSkillTests
{
    private readonly Guid _agentId = Guid.NewGuid();
    private IPendingUserNoteRepository _notes = null!;
    private IAgentRepository _agents = null!;
    private ManagePendingNotesSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _notes = Substitute.For<IPendingUserNoteRepository>();
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = _agentId });
        _notes.GetPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<PendingUserNote>());

        _skill = new ManagePendingNotesSkill(_notes, _agents);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new List<string>()
        };
    }

    [Test]
    public async Task Read_ListsPendingNotesForCurrentUser()
    {
        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            ["action"] = "read"
        });

        result.Success.ShouldBeTrue();
        await _notes.Received(1).GetPendingAsync(_agentId, _context.UserId, Arg.Any<CancellationToken>());
        await _notes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingAction_DefaultsToRead()
    {
        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await _notes.Received(1).GetPendingAsync(_agentId, _context.UserId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MarkDelivered_ArchivesGivenNoteIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _notes.MarkDeliveredAsync(_agentId, _context.UserId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            ["action"] = "mark_delivered",
            ["noteIds"] = $"{id1}, {id2}"
        });

        result.Success.ShouldBeTrue();
        await _notes.Received(1).MarkDeliveredAsync(
            _agentId,
            _context.UserId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(id1) && ids.Contains(id2) && ids.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MarkDelivered_WithoutNoteIds_ReturnsError_NoRepositoryCall()
    {
        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            ["action"] = "mark_delivered"
        });

        result.Success.ShouldBeFalse();
        await _notes.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }
}
