// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetEscalationRosterQueryHandler — verifies the mapping from
/// EscalationRosterEntryDetail to EscalationRosterEntryResource is field-for-field faithful.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetEscalationRosterQueryHandlerTests
{
    private IEscalationRosterService _rosterService = null!;
    private GetEscalationRosterQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rosterService = Substitute.For<IEscalationRosterService>();
        _sut = new GetEscalationRosterQueryHandler(_rosterService);
    }

    [Test]
    public async Task Handle_MapsEntryDetailToResource()
    {
        var groupId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var detail = new EscalationRosterEntryDetail(entryId, "user-a", "Alice Adler", 2, true, false);

        _rosterService.GetRosterEntriesAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterEntryDetail> { detail });

        var result = await _sut.Handle(new GetEscalationRosterQuery(groupId), CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        var resource = result[0];
        Assert.That(resource.Id, Is.EqualTo(entryId));
        Assert.That(resource.UserId, Is.EqualTo("user-a"));
        Assert.That(resource.DisplayName, Is.EqualTo("Alice Adler"));
        Assert.That(resource.EffectiveRank, Is.EqualTo(2));
        Assert.That(resource.HasOverride, Is.True);
        Assert.That(resource.IsOrphaned, Is.False);
    }

    [Test]
    public async Task Handle_PassesGroupIdThrough()
    {
        var groupId = Guid.NewGuid();
        _rosterService.GetRosterEntriesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterEntryDetail>());

        await _sut.Handle(new GetEscalationRosterQuery(groupId), CancellationToken.None);

        await _rosterService.Received(1).GetRosterEntriesAsync(groupId, Arg.Any<CancellationToken>());
    }
}
