// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerTemplateRoutes: the container page route as the Angular router registers it ("container-template/:id"
/// directly under /workplace), built from the one ProactiveActionRoutes literal.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Constants;

[TestFixture]
public class ContainerTemplateRoutesTests
{
    [Test]
    public void ForContainer_IsTheWorkplaceContainerTemplateRouteWithTheId()
    {
        var id = Guid.Parse("f0000000-0000-0000-0000-000000000001");

        ContainerTemplateRoutes.ForContainer(id).ShouldBe($"/workplace/container-template/{id}");
        ContainerTemplateRoutes.ContainerTemplateRoute.ShouldBe(ProactiveActionRoutes.ContainerTemplate);
    }
}
