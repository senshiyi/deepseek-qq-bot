using System.Net;
using System.Net.Sockets;

internal static class SuiteProcessChecks
{
    internal static async Task RunAsync()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hung = await SuiteProcessRunner.RunAsync(["--runner-probe", "hold", portText], TimeSpan.FromSeconds(3));
        if (!hung.TimedOut || !hung.Output.Contains("probe-listening", StringComparison.Ordinal))
            throw new Exception("Timeout check did not exercise a live process holding a port.");
        var next = await SuiteProcessRunner.RunAsync(["--runner-probe", "bind", portText], TimeSpan.FromSeconds(10));
        if (next.TimedOut || next.ExitCode != 0 || !next.Output.Contains("probe-listening", StringComparison.Ordinal))
            throw new Exception("Timed-out suite retained its port or interfered with the following process: " + next.Error);
    }

    internal static async Task<int> ProbeAsync(string mode, int port)
    {
        if (mode is not ("hold" or "bind")) throw new ArgumentException("Unknown process probe.");
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine("probe-listening");
        if (mode == "hold") await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
