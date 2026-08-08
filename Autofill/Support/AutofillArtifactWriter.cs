// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Writes the two artifacts every run owes: the metric JSON and the ASCII plan matrix. Files land
/// under <c>Klacks.UnitTest/Autofill/artifacts/&lt;scenario&gt;/</c> and are additionally attached to
/// the NUnit result when a test context is available, so a run on a build agent keeps them too.
/// </summary>
public static class AutofillArtifactWriter
{
    private const string MetricsExtension = ".metrics.json";
    private const string MatrixExtension = ".matrix.txt";

    /// <summary>
    /// Writes both artifacts and returns their full paths.
    /// </summary>
    /// <param name="metrics">Measurement to write</param>
    /// <param name="matrix">Rendered ASCII matrix</param>
    /// <param name="fileNameSuffix">Suffix distinguishing the runs, for example ".run1"</param>
    public static IReadOnlyList<string> Write(AutofillMetrics metrics, string matrix, string fileNameSuffix)
    {
        var directory = AutofillRepositoryPaths.ArtifactDirectory(metrics.Scenario);
        var baseName = metrics.TestName + fileNameSuffix;

        var metricsPath = Path.Combine(directory, baseName + MetricsExtension);
        var matrixPath = Path.Combine(directory, baseName + MatrixExtension);

        File.WriteAllText(metricsPath, MetricsJsonWriter.ToJson(metrics));
        File.WriteAllText(matrixPath, matrix);

        Attach(metricsPath);
        Attach(matrixPath);

        return [metricsPath, matrixPath];
    }

    private static void Attach(string path)
    {
        try
        {
            TestContext.AddTestAttachment(path);
        }
        catch (Exception)
        {
            // Attaching only works inside a running test. Writing the file is the contract; the
            // attachment is a convenience, and losing it must never fail the run.
        }
    }
}
