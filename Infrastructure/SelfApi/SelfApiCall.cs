// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Infrastructure.SelfApi;

public sealed record SelfApiCall(
    HttpMethod Method,
    string Route,
    string? Body,
    string? SkillName,
    string? BearerToken,
    string? CorrelationId);
