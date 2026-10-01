// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Common;

namespace Klacks.UnitTest.Domain.Common;

[TestFixture]
public class ExecutionPrincipalTests
{
    [Test]
    public void Begin_SetsTheOwnerAndRestoresThePreviousValueOnDispose()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (ExecutionPrincipal.Begin(outer))
        {
            using (ExecutionPrincipal.Begin(inner))
            {
                ExecutionPrincipal.CurrentUserId.ShouldBe(inner.ToString());
            }

            ExecutionPrincipal.CurrentUserId.ShouldBe(outer.ToString());
        }

        ExecutionPrincipal.CurrentUserId.ShouldBeNull();
    }

    [Test]
    public void Begin_WithEmptyId_LeavesTheFlowWithoutOwner()
    {
        using (ExecutionPrincipal.Begin(Guid.Empty))
        {
            ExecutionPrincipal.CurrentUserId.ShouldBeNull();
        }
    }

    [Test]
    public async Task Begin_FlowsIntoBackgroundContinuationsOfTheSameFlow()
    {
        var owner = Guid.NewGuid();
        string? seen;

        using (ExecutionPrincipal.Begin(owner))
        {
            seen = await Task.Run(() => ExecutionPrincipal.CurrentUserId);
        }

        seen.ShouldBe(owner.ToString());
    }
}
