// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
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

        attr.HttpMethod.Should().Be("GET");
        attr.RouteTemplate.Should().Be("/api/backend/shifts/{id}");
        attr.EndpointKey.Should().Be("GET /api/backend/shifts/{id}");
    }

    [Test]
    public void Attribute_NormalizesHttpMethodToUppercase()
    {
        var attr = new ExposesEndpointAttribute("post", "/api/backend/employees");

        attr.HttpMethod.Should().Be("POST");
        attr.EndpointKey.Should().Be("POST /api/backend/employees");
    }

    [Test]
    public void Attribute_AllowsMultipleOnSameClass()
    {
        var usage = typeof(ExposesEndpointAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        usage.AllowMultiple.Should().BeTrue();
    }

    [Test]
    public void Attribute_TargetsClassOnly()
    {
        var usage = typeof(ExposesEndpointAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        (usage.ValidOn & AttributeTargets.Class).Should().Be(AttributeTargets.Class);
    }
}
