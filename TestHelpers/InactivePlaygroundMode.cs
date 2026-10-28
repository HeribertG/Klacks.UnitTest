// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Playground mode that is always off, the state of every customer installation.
/// </summary>

using Klacks.Api.Domain.Interfaces.Playground;

namespace Klacks.UnitTest.TestHelpers;

public sealed class InactivePlaygroundMode : IPlaygroundMode
{
    public bool IsActive => false;
}
