// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Serialises a metric object to JSON in a form two runs can be diffed against each other: property
/// order follows the record declarations, every dictionary is sorted, numbers are written with the
/// invariant JSON number format and enums as their names rather than as indices.
/// </summary>
public static class MetricsJsonWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="metrics">Measurement to serialise</param>
    public static string ToJson(AutofillMetrics metrics) => JsonSerializer.Serialize(metrics, Options);
}
