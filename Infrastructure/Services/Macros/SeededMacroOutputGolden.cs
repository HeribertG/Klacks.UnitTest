// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Golden record of every macro script Klacks has shipped, recorded on 2026-09-26 with the interpreter as it was before
/// the OUTPUT stack fix (every OUTPUT statement still discarded one more stack entry). Keys name the source and the
/// position of the script literal (MacrosSeed row order, or migration, direction and literal index), so they do not
/// depend on the line endings of the checkout. Each entry covers all inputs of the regression grid.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

public static class SeededMacroOutputGolden
{
    public static IReadOnlyDictionary<string, MacroOutputGoldenEntry> Entries { get; } =
        new Dictionary<string, MacroOutputGoldenEntry>
        {
            ["MacrosSeed:0"] = new("0FBCA1C9B8F06804FD636D027F22C66429733B5A253823C619499CA0D640808F", 0, 672, "1=1632.96"),
            ["MacrosSeed:1"] = new("966304FF0142C353313EE589541126CD339AE4A1CB436B56068A3C3A562C5263", 0, 672, "1=816.48"),
            ["MacrosSeed:2"] = new("F8E1B2A151449543C52F4FE6011624BA6814B41482D5C6CF1D60CFC1236DE370", 0, 4032, "1=1108.20;10=410.4000000000000036;11=102.0;12=141.00;13=64.7999999999999992;14=390.0"),
            ["MacrosSeed:3"] = new("6CD0C4A59ABF5D603C1786A67E7CB228795E96E4F6DA54CD82E982B069F85D13", 0, 672, "1=699.84"),
            ["MacrosSeed:4"] = new("BE9508A71A15AD0B119AFA17FFCCFF5E345C41E49E456D59A12A5BC87676AF25", 0, 672, "1=0"),
            ["MacrosSeed:5"] = new("6CD0C4A59ABF5D603C1786A67E7CB228795E96E4F6DA54CD82E982B069F85D13", 0, 672, "1=699.84"),
            ["MacrosSeed:6"] = new("68E4F3FCB3963111572640B0449BD54E2BC094726286666D7F712208A9258611", 0, 672, "1=349.92"),
            ["MacrosSeed:7"] = new("BECA8DE16201F69BCFBB5BC83E78B06A9749A3204A36568858D5599F7D2045CE", 0, 672, "1=4032"),
            ["AddSurchargeNightWindow:Up:0"] = new("F8E1B2A151449543C52F4FE6011624BA6814B41482D5C6CF1D60CFC1236DE370", 0, 4032, "1=1108.20;10=410.4000000000000036;11=102.0;12=141.00;13=64.7999999999999992;14=390.0"),
            ["AddSurchargeNightWindow:Up:1"] = new("F8E1B2A151449543C52F4FE6011624BA6814B41482D5C6CF1D60CFC1236DE370", 0, 4032, "1=1108.20;10=410.4000000000000036;11=102.0;12=141.00;13=64.7999999999999992;14=390.0"),
            ["AddAllShiftAdditiveMacro:Up:0"] = new("EDF7F0BCEACC6BC8DAFC0D525308FFA5229D4B3463E095141225DDED70D62EB8", 0, 4032, "1=1399.2;10=453.6000000000000084;11=158.3999999999999976;12=216.0;13=100.8000000000000012;14=470.4000000000000056"),
            ["WireAbsenceMacrosToPercentVariable:Up:0"] = new("5A108C6DFD142EDC35C1B02F4FC41EEA1EAEB2424A52630B773CA47051910E45", 0, 672, "1=777.60"),
            ["WireAbsenceMacrosToPercentVariable:Up:1"] = new("5A108C6DFD142EDC35C1B02F4FC41EEA1EAEB2424A52630B773CA47051910E45", 0, 672, "1=777.60"),
            ["WireAbsenceMacrosToPercentVariable:Up:2"] = new("0FBCA1C9B8F06804FD636D027F22C66429733B5A253823C619499CA0D640808F", 0, 672, "1=1632.96"),
            ["WireAbsenceMacrosToPercentVariable:Up:3"] = new("966304FF0142C353313EE589541126CD339AE4A1CB436B56068A3C3A562C5263", 0, 672, "1=816.48"),
            ["WireAbsenceMacrosToPercentVariable:Up:4"] = new("AC7E5E656B9EAAA5B29EEBC7DD3B00739B003382929568FD6AD13FC64923A421", 0, 672, "1=388.80"),
            ["SplitTrainingAndWirePaidAbsence:Up:0"] = new("6CD0C4A59ABF5D603C1786A67E7CB228795E96E4F6DA54CD82E982B069F85D13", 0, 672, "1=699.84"),
            ["SplitTrainingAndWirePaidAbsence:Up:1"] = new("68E4F3FCB3963111572640B0449BD54E2BC094726286666D7F712208A9258611", 0, 672, "1=349.92"),
            ["SplitTrainingAndWirePaidAbsence:Up:2"] = new("BECA8DE16201F69BCFBB5BC83E78B06A9749A3204A36568858D5599F7D2045CE", 0, 672, "1=4032")
        };
}
