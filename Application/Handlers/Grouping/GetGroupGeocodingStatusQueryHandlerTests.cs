// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit test for GetGroupGeocodingStatusQueryHandler: delegates straight to
/// IGroupRepository.GetGeocodingStatusAsync and returns its result unchanged.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Handlers.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Grouping;

namespace Klacks.UnitTest.Application.Handlers.Grouping;

[TestFixture]
public class GetGroupGeocodingStatusQueryHandlerTests
{
    [Test]
    public async Task Handle_ReturnsRepositoryResultUnchanged()
    {
        var groupRepository = Substitute.For<IGroupRepository>();
        var expected = new GroupGeocodingStatus(65, 4, 10, 51);
        groupRepository.GetGeocodingStatusAsync(Arg.Any<CancellationToken>()).Returns(expected);
        var handler = new GetGroupGeocodingStatusQueryHandler(groupRepository);

        var result = await handler.Handle(new GetGroupGeocodingStatusQuery(), CancellationToken.None);

        result.ShouldBe(expected);
    }
}
