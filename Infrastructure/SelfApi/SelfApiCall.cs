// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.SelfApi;

public sealed record SelfApiCall(
    HttpMethod Method,
    string Route,
    string? Body,
    string? SkillName,
    string? BearerToken,
    string? CorrelationId);
