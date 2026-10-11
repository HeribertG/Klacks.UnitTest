// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Recovery endpoints take an analyseToken from the client: a token of a scenario the caller may not see is
/// answered like an unknown one (empty list), and the request book of that scenario is never read.
/// </summary>

using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ReplacementScenarioTokenVisibilityTests
{
    [Test]
    public async Task ListReplacementRequests_OfHiddenScenario_IsEmptyAndDoesNotQuery()
    {
        var hiddenToken = Guid.NewGuid();
        var repository = Substitute.For<IReplacementRequestRepository>();
        var handler = new ListReplacementRequestsQueryHandler(
            repository,
            TestGroupWriteVisibility.AllClientsVisible(),
            Substitute.For<ISettingsReader>(),
            TestScenarioTokenAccess.Hiding(hiddenToken));

        var result = await handler.Handle(
            new ListReplacementRequestsQuery(null, null, null, hiddenToken), CancellationToken.None);

        result.ShouldBeEmpty();
        await repository.DidNotReceiveWithAnyArgs().ListAsync(default!, default, default);
    }
}
