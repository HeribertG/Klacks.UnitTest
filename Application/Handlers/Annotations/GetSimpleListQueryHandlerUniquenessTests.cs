// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The mediator registers every IRequestHandler implementation it finds, and with two handlers for the same
/// request the one registered last wins. Until 2026-10-01 GET api/backend/Annotations/GetSimpleAnnotation/{id}
/// had two: GetSimpleQueryHandler (the notes of one client) and a stale GetListQueryHandler that ignored the
/// client id and returned the notes of the whole tenant. Which one answered depended on type enumeration
/// order. Exactly one handler may exist for this query.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Annotations;
using Klacks.Api.Application.Queries.Annotation;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Handlers.Annotations;

[TestFixture]
public class GetSimpleListQueryHandlerUniquenessTests
{
    [Test]
    public void GetSimpleListQuery_HasExactlyOneHandler()
    {
        var handlerInterface = typeof(IRequestHandler<GetSimpleListQuery, IEnumerable<AnnotationResource>>);

        var implementations = typeof(GetSimpleQueryHandler).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && handlerInterface.IsAssignableFrom(type))
            .ToList();

        implementations.ShouldBe(new[] { typeof(GetSimpleQueryHandler) });
    }
}
