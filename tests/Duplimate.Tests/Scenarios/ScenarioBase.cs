using System;
using System.Threading.Tasks;
using Xunit;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Base class every scenario inherits. Owns the boilerplate so the
/// scenario itself reads as a flat user-flow narrative.
///
/// To add a scenario:
///   1. Subclass this and pass a sensible <c>Name</c>.
///   2. Override <see cref="RunAsync"/>; the body gets a fresh
///      <see cref="ScenarioContext"/> with throwaway dirs, a
///      pre-wired <see cref="UiDriver"/>, and access to
///      <see cref="SecretsLoader"/>.
///   3. Mark the class with <c>[Collection(ScenarioCollection.Name)]</c>
///      so xUnit runs scenarios serially under the assembly fixture
///      that owns SUMMARY.md.
///   4. Add an <c>[AvaloniaFact]</c> that calls <c>await Run();</c>.
///
/// Skipped scenarios should call <c>ctx.Skip("reason")</c> rather
/// than throw — that path records the skip + reason in the suite
/// report and short-circuits the body cleanly.
/// </summary>
public abstract class ScenarioBase
{
    private static readonly SecretsLoader _secrets = new();
    static ScenarioBase() => ScenarioRunSummary.AttachSecrets(_secrets);

    protected abstract string ScenarioName { get; }
    protected SecretsLoader Secrets => _secrets;

    protected async Task Run()
    {
        using var ctx = new ScenarioContext(ScenarioName, _secrets);
        try
        {
            await RunAsync(ctx);
            if (ctx.Report.Status == ScenarioStatus.Pending)
                ctx.Report.MarkPassed();
        }
        catch (ScenarioSkippedException)
        {
            // ctx.Skip already stamped the report with the reason.
            // xUnit v2 has no portable Assert.Skip, so we just
            // return cleanly — the test passes (no failure) and
            // SUMMARY.md carries the skip status. The scenario name
            // also gets recorded in MissingSecretsByName so the
            // suite summary surfaces what to do about it.
        }
        catch (Exception ex)
        {
            ctx.Report.MarkFailed(ex.Message);
            throw;
        }
    }

    protected abstract Task RunAsync(ScenarioContext ctx);
}

/// <summary>
/// xUnit collection name shared by every scenario test. The
/// matching <see cref="ScenarioCollectionDefinition"/> is the
/// assembly-level fixture that writes SUMMARY.md when the suite
/// finishes.
/// </summary>
public static class ScenarioCollection
{
    public const string Name = "Scenarios";
}

[CollectionDefinition(ScenarioCollection.Name)]
public sealed class ScenarioCollectionDefinition : ICollectionFixture<ScenarioSuiteFixture> { }

/// <summary>
/// Lifecycle fixture for the scenario collection. Constructor wipes
/// stale reports + recreates the artifacts dir. Dispose writes
/// SUMMARY.md so a CI run leaves the human-readable report on disk.
/// </summary>
public sealed class ScenarioSuiteFixture : IDisposable
{
    public string ArtifactsRoot { get; }

    public ScenarioSuiteFixture()
    {
        ScenarioRunSummary.Reset();
        ArtifactsRoot = ScenarioContext.ResolveArtifactsRoot();
        System.IO.Directory.CreateDirectory(ArtifactsRoot);
    }

    public void Dispose()
    {
        try
        {
            var path = ScenarioRunSummary.WriteSummary(ArtifactsRoot);
            // Print a relative breadcrumb so xUnit's stdout shows
            // where to find the report.
            Console.WriteLine($"[scenarios] SUMMARY → {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[scenarios] failed to write SUMMARY.md: {ex}");
        }
    }
}
