using Xunit;

namespace Snip.Tests;

/// Skipped (not silently passed) unless the environment variable is 1.
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1") Skip = $"opt-in: set {variable}=1";
    }
}
