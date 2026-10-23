// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shrink-only baseline of MagicNumberGuardTests: "path relative to Klacks.Api -> number of magic-number
/// literals the file had when it was listed". An entry is a ceiling AND a floor - a file above its number
/// fails, and so does a file below it, so every cleanup lowers (or removes) its entry in the same change.
/// Never add an entry and never raise a number: name the value as a constant instead.
///
/// Start (2026-10-23): 647 literals in 175 files on Api 669bcab18; the change that introduced the guard
/// named 165 of them (28 files), leaving 482 in 147 files. ClientFilterService (1) then moved from Domain to
/// Infrastructure, out of the guarded directories, leaving 481 in 146 files. Round 12 (a1-magic2) named the
/// business-rule values (trigger severity thresholds, memory importance scale, day and week units, planning
/// context window, autofill tolerances, token and password sizes, list and search limits, report and model
/// defaults, cache lifetimes, option defaults) and left 242 in 13 files. What remains is deliberate for now:
/// calendar algorithms (HolidaysListCalculator, HijriCalendar), sample and evaluation data (ExportSampleDataFactory,
/// HarmonizerEvalScenarioFactory, CreateTestEnvironmentSkill), the feature-vector slots of TurnIntentHandFeatures,
/// the Haversine and Wilson-interval formula coefficients, the median halving of two statistics helpers, and the
/// files other branches still own (AddClientToNearestGroupSkill, UpdateAbsenceTypeSkill, TurnEvalMemoryProbe).
/// </summary>

namespace Klacks.UnitTest.Architecture;

public static class MagicNumberGuardAllowlist
{
    public static readonly IReadOnlyDictionary<string, int> Entries = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Application/Services/Assistant/Evaluation/TurnEval/TurnEvalBaseline.cs"] = 3,
        ["Application/Services/Assistant/Evaluation/TurnEval/TurnEvalMemoryProbe.cs"] = 1,
        ["Application/Services/ClientImport/ClientImportColumnDetector.cs"] = 1,
        ["Application/Services/Exports/ExportSampleDataFactory.cs"] = 39,
        ["Application/Services/Schedules/HolisticHarmonizer/HarmonizerEvalScenarioFactory.cs"] = 7,
        ["Application/Skills/AddClientToNearestGroupSkill.cs"] = 3,
        ["Application/Skills/CreateTestEnvironmentSkill.cs"] = 44,
        ["Application/Skills/UpdateAbsenceTypeSkill.cs"] = 1,
        ["Domain/Services/Assistant/TurnIntentHandFeatures.cs"] = 7,
        ["Domain/Services/Assistant/TurnIntentLocaleGate.cs"] = 2,
        ["Domain/Services/Geo/HaversineDistanceCalculator.cs"] = 6,
        ["Domain/Services/Holidays/HijriCalendar.cs"] = 52,
        ["Domain/Services/Holidays/HolidaysListCalculator.cs"] = 76,
    };
}
