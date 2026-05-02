// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Infrastructure.KnowledgeIndex.Presentation.Attributes;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class ExposesEndpointAttributeTests
{
    [Test]
    public void Attribute_StoresMethodAndRoute()
    {
        var attr = new ExposesEndpointAttribute("GET", "/api/backend/shifts/{id}");

        attr.HttpMethod.ShouldBe("GET");
        attr.RouteTemplate.ShouldBe("/api/backend/shifts/{id}");
        attr.EndpointKey.ShouldBe("GET /api/backend/shifts/{id}");
    }

    [Test]
    public void Attribute_NormalizesHttpMethodToUppercase()
    {
        var attr = new ExposesEndpointAttribute("post", "/api/backend/employees");

        attr.HttpMethod.ShouldBe("POST");
        attr.EndpointKey.ShouldBe("POST /api/backend/employees");
    }

    [Test]
    public void Attribute_AllowsMultipleOnSameClass()
    {
        var usage = typeof(ExposesEndpointAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        usage.AllowMultiple.ShouldBeTrue();
    }

    [Test]
    public void Attribute_TargetsClassOnly()
    {
        var usage = typeof(ExposesEndpointAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        (usage.ValidOn & AttributeTargets.Class).ShouldBe(AttributeTargets.Class);
    }
}
