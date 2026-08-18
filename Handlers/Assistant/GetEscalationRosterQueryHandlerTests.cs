// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetEscalationRosterQueryHandler — verifies the mapping from
/// EscalationRosterMember to EscalationRosterMemberResource is field-for-field faithful, and that the
/// query forwards to the group-agnostic GetRosterMembersAsync overload (no group id).
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
    public async Task Handle_MapsMemberToResource()
    {
        var member = new EscalationRosterMember("user-a", "Alice Adler", false);

        _rosterService.GetRosterMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterMember> { member });

        var result = await _sut.Handle(new GetEscalationRosterQuery(), CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        var resource = result[0];
        Assert.That(resource.UserId, Is.EqualTo("user-a"));
        Assert.That(resource.DisplayName, Is.EqualTo("Alice Adler"));
        Assert.That(resource.IsCurrentlyAbsent, Is.False);
    }

    [Test]
    public async Task Handle_ReturnsEmptyList_WhenNoMembersEligible()
    {
        _rosterService.GetRosterMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EscalationRosterMember>());

        var result = await _sut.Handle(new GetEscalationRosterQuery(), CancellationToken.None);

        Assert.That(result, Is.Empty);
    }
}
