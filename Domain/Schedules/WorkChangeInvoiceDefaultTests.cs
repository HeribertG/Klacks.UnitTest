// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A new WorkChange is billable by default, on the entity and on the resource that the request binder fills when a client omits the flag.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Schedules;

[TestFixture]
public class WorkChangeInvoiceDefaultTests
{
    [Test]
    public void NewEntity_IsBillable() => new WorkChange().ToInvoice.ShouldBeTrue();

    [Test]
    public void NewResource_IsBillable() => new WorkChangeResource().ToInvoice.ShouldBeTrue();

    [Test]
    public void SharedConstant_IsOn() => WorkChangeDefaults.ToInvoice.ShouldBeTrue();
}
