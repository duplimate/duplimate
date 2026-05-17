using System;
using System.IO;
using Xunit;

namespace Duplimate.Tests.Cloud;

/// <summary>
/// [InteractiveFact] — a test that is skipped by default and only runs when
/// DUPLIMATE_TEST_INTERACTIVE=1. These tests print an OAuth URL to stderr and
/// wait on stdin for a token the user pasted in a browser session.
///
/// Usage:
///   setx DUPLIMATE_TEST_INTERACTIVE 1   (once)
///   dotnet test --filter "Category=Interactive"
///
/// Once a token is cached under tests/Duplimate.Tests/.auth-cache/
/// subsequent runs reuse it without prompting. Delete the cache file to
/// force a fresh auth flow.
/// </summary>
public sealed class InteractiveFactAttribute : FactAttribute
{
    public InteractiveFactAttribute()
    {
        var flag = Environment.GetEnvironmentVariable("DUPLIMATE_TEST_INTERACTIVE");
        if (flag != "1")
        {
            Skip = "Interactive test. Set DUPLIMATE_TEST_INTERACTIVE=1 to enable.";
        }
    }
}
