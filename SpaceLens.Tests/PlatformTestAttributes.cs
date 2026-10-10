namespace SpaceLens.Tests;

/// <summary>A test that needs Windows itself (drives, ACLs, junctions, the registry). Reported as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows";
        }
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows";
        }
    }
}
