// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// The keyword table of specification scenario 3 (tests/autofill/SPEC-SZENARIO3.md), one named
/// constant per cell. Scenario 3 is scenario 2 plus keyword restrictions, so everything the two
/// share stays in <see cref="Scenario2.Scenario2SpecValues"/> and only the keyword layer lives here.
/// <para>
/// Engine-level shape of the restriction (owner decision S3-1, findings K3/K4): the engine has no
/// keyword model, only the bare ban set <c>CoreWizardContext.IneligibleAssignments</c>, and the
/// temporal validity of a keyword is resolved to day triples at the API border. The engine runs
/// therefore PRESUPPOSE the expiry semantics of MA-2's NACHT-BEF instead of testing it — the proof
/// of the expiry evaluation itself runs one level up against EligibilityMatrixBuilder (assertion
/// A20 and the API half of the suite) and is out of scope here.
/// </para>
/// <para>
/// OBJEKT-A (held by all five employees) and ERSTE-HILFE (required by nobody) generate ZERO ban
/// triples, and an empty contribution is indistinguishable from absence on engine level because
/// <c>IsEligible</c> short-circuits an empty set (finding K3). Their constants exist so the fixture
/// documentation and the API-level tests speak the same names; A21 and A23 run exclusively on API
/// level per the owner consequence to K3 and are deliberately NOT built as engine runs.
/// </para>
/// </summary>
public static class Scenario3SpecValues
{
    /// <summary>Keyword the night shift requires; MA-1 and MA-5 hold it unbounded, MA-2 until 2026-03-20.</summary>
    public const string NightKeyword = "NACHT-BEF";

    /// <summary>Keyword required by the order and held by all five employees — zero engine triples, API-level control case (A23).</summary>
    public const string OrderKeyword = "OBJEKT-A";

    /// <summary>Control keyword no shift requires — zero engine triples, API-level irrelevance proof (A21).</summary>
    public const string ControlKeyword = "ERSTE-HILFE";

    /// <summary>Last day MA-2's NACHT-BEF assignment is valid.</summary>
    public static readonly DateOnly Ma2NightValidUntil = new(2026, 3, 20);

    /// <summary>First day MA-2 is banned from the night shift — the day after the validity ends.</summary>
    public static readonly DateOnly Ma2NightBanFrom = new(2026, 3, 21);

    /// <summary>Night days the ban list leaves MA-2: 2026-03-01..2026-03-20.</summary>
    public const int Ma2EligibleNightDays = 20;

    /// <summary>Night days the ban list leaves MA-1 and MA-5: the whole period.</summary>
    public const int UnrestrictedEligibleNightDays = 31;

    /// <summary>Night shifts of the second half of the month, 21.–31.03., that only MA-1 and MA-5 can hold.</summary>
    public const int SecondHalfNightShifts = 11;

    /// <summary>L1 ban triples: MA-3 x 31 nights + MA-4 x 31 nights + MA-2 x 11 nights.</summary>
    public const int L1TripleCount = 73;

    /// <summary>L4 ban triples: all 5 employees x 31 nights.</summary>
    public const int L4TripleCount = 155;

    /// <summary>L5 ban triples: MA-3 x 31 nights + MA-4 x 31 nights.</summary>
    public const int L5TripleCount = 62;

    /// <summary>L4: every night slot of the period has an empty pool and must stay unstaffed.</summary>
    public const int L4ExpectedUnfilledNights = 31;

    /// <summary>A19: at most this many provably forced shortenings — the one the 11-on-2 arithmetic forces.</summary>
    public const int MaxForcedShortenings = 1;

    /// <summary>A25: no cohort member may deviate more than this share from the cohort mean.</summary>
    public const double CohortRelativeTolerance = 0.20;

    /// <summary>Artifact folder of every scenario 3 run.</summary>
    public const string ScenarioName = "scenario3";

    /// <summary>Artifact file name of L0, the scenario 2 control run re-executed inside scenario 3.</summary>
    public const string L0ArtifactName = "Scenario3L0";

    /// <summary>Artifact file name of L1, the main run with the full 73-triple ban list.</summary>
    public const string L1ArtifactName = "Scenario3L1";

    /// <summary>Artifact file name of L4, the unstaffable-night run with the 155-triple ban list.</summary>
    public const string L4ArtifactName = "Scenario3L4";

    /// <summary>Artifact file name of L5, the diagnostic run banning only MA-3 and MA-4.</summary>
    public const string L5ArtifactName = "Scenario3L5";
}
