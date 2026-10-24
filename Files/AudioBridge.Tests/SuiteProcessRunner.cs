using System.Diagnostics;

internal sealed record SuiteProcessResult(int ExitCode, bool TimedOut, string Output, string Error);

internal static class SuiteProcessRunner
{
    internal static async Task<SuiteProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test host.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SuiteProcessRunner).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start isolated test suite.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var deadline = new CancellationTokenSource(timeout);
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            timedOut = true;
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (child.HasExited) { }
            // Do not continue on cleanup failure: running another suite could overlap an abandoned one.
            await child.WaitForExitAsync();
        }
        return new(child.ExitCode, timedOut, await stdout, await stderr);
    }
}
