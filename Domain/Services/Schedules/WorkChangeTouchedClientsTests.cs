// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The single source of "whose visibility a WorkChange depends on": the parent Work's owner plus the replacement
/// client, without nulls and duplicates; further ids (old owner, old replacement on a move) join the same set.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class WorkChangeTouchedClientsTests
{
    [Test]
    public void Of_OwnerAndReplacement_AreBothTouched()
    {
        var owner = Guid.NewGuid();
        var replacement = Guid.NewGuid();

        WorkChangeTouchedClients.Of(new Work { ClientId = owner }, replacement)
            .ShouldBe(new[] { owner, replacement }, ignoreOrder: true);
    }

    [Test]
    public void Of_MissingWorkAndNoReplacement_TouchesNobody()
    {
        WorkChangeTouchedClients.Of(null, null).ShouldBeEmpty();
    }

    [Test]
    public void Collect_DropsNullsAndDuplicates()
    {
        var owner = Guid.NewGuid();
        var replacement = Guid.NewGuid();

        WorkChangeTouchedClients.Collect(owner, owner, null, replacement, replacement)
            .ShouldBe(new[] { owner, replacement }, ignoreOrder: true);
    }
}
