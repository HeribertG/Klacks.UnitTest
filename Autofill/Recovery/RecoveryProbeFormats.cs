// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: the fixed texts of the recovery probe output, so every line carries the same prefix
/// and the same invariant date and time formats.
/// </summary>
public static class RecoveryProbeFormats
{
    public const string Prefix = "RECOVERY-PROBE ";

    public const string Date = "yyyy-MM-dd";

    public const string Time = "HH:mm";

    public const string Hours = "0.##";

    public const string OpenSlotHolder = "OPEN-SLOT";
}
