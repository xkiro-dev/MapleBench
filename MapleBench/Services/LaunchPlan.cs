namespace MapleBench.Services;

/// <summary>
/// The launch-time decisions that used to live as private helpers in
/// <c>Program</c>: which port this copy asked for, and whether an already-running
/// copy is in our way and may be ended.
///
/// It is a separate, dependency-free class so those decisions can be tested. They
/// could not be before, and the two bugs below both shipped:
///
///   * <c>--port=5901</c> was not recognised at all. The port was read with
///     <c>Array.IndexOf(args, "--port")</c>, which only ever matches the
///     two-token form, so the equals form fell through to the default and the
///     launch took port 5100 — the port belonging to the instance the user was
///     trying not to disturb.
///   * The kill ran before the port was resolved and never consulted it, so a
///     launch that wanted its own port still ended every other copy. "Closed 1
///     earlier MapleBench instance" on a launch that was explicitly told to go
///     somewhere else is a lost session, and the session it takes is the one
///     the user is looking at.
///
/// The port each copy is listening on is published as a file in that copy's own
/// scratch folder, next to the <c>.allow-multiple</c> marker that was already
/// there — the same trick, for the same reason: a process cannot read another
/// process's command line without WMI, and the case that matters most is the one
/// where the other copy is too busy to answer.
/// </summary>
public static class LaunchPlan
{
    /// <summary>The port a launch takes when it was not told otherwise.</summary>
    public const int PreferredPort = 5100;

    /// <summary>Name of the file an <c>--allow-multiple</c> instance drops in its scratch folder.</summary>
    public const string ProtectedMarker = ".allow-multiple";

    /// <summary>Name of the file every instance drops naming the port it listens on.</summary>
    public const string PortMarker = ".port";

    /// <summary>
    /// The port the command line asked for, or null when it did not ask.
    ///
    /// Both spellings, because both are what people type and one of them used to
    /// be silently ignored: <c>--port 5901</c> and <c>--port=5901</c>. A value
    /// that is not a usable TCP port is treated as "not asked" rather than as an
    /// error, so a typo cannot stop the app starting.
    /// </summary>
    public static int? ExplicitPort(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? value = null;

            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                value = args[i + 1];
            else if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                value = arg["--port=".Length..];

            if (value != null && int.TryParse(value, out int port) && port is > 0 and <= 65535)
                return port;
        }
        return null;
    }

    /// <summary>
    /// Whether an already-running copy is in this launch's way.
    ///
    /// Three inputs, and the order they are considered in is the whole rule:
    ///
    ///   * a copy started with <c>--allow-multiple</c> is never ended, whatever
    ///     port it is on. That flag is the user saying "leave this one alone" and
    ///     it is the only thing standing between a benchmark run and the editor
    ///     the results are being compared against;
    ///   * a launch that named its own port only ever ends a copy *on that port*.
    ///     Anything else is not in its way, and ending it buys this launch
    ///     nothing at all;
    ///   * a plain launch wants <see cref="PreferredPort"/>, so it ends copies on
    ///     that port and copies whose port cannot be established — the latter
    ///     because a copy that died before publishing its port is exactly the
    ///     stale one holding file handles that this mechanism exists to clear.
    /// </summary>
    /// <param name="requestedPort">The port this launch was told to use, or null.</param>
    /// <param name="otherPort">The other copy's published port, or null if unknown.</param>
    /// <param name="otherIsProtected">Whether the other copy published the --allow-multiple marker.</param>
    public static bool Contends(int? requestedPort, int? otherPort, bool otherIsProtected)
    {
        if (otherIsProtected)
            return false;

        if (requestedPort is int wanted)
            return otherPort == wanted;

        return otherPort is null || otherPort == PreferredPort;
    }

    /// <summary>The scratch folder a given process id owns.</summary>
    public static string ScratchFolder(int pid) =>
        Path.Combine(Path.GetTempPath(), "MapleBench", pid.ToString());

    /// <summary>True when that process asked not to be ended.</summary>
    public static bool IsProtected(int pid)
    {
        try
        {
            return File.Exists(Path.Combine(ScratchFolder(pid), ProtectedMarker));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The port that process published, or null when it published none.</summary>
    public static int? PublishedPort(int pid)
    {
        try
        {
            string marker = Path.Combine(ScratchFolder(pid), PortMarker);
            if (!File.Exists(marker))
                return null;
            return int.TryParse(File.ReadAllText(marker).Trim(), out int port) ? port : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Publishes the port this copy is on, best effort.
    ///
    /// Written before the kill sweep and again once the port is final, so a
    /// sibling launching at the same moment sees an intent rather than nothing.
    /// Never allowed to fail a launch: the marker is an optimisation for other
    /// processes, not state this one depends on.
    /// </summary>
    public static void PublishPort(string scratch, int port)
    {
        try { File.WriteAllText(Path.Combine(scratch, PortMarker), port.ToString()); }
        catch { /* best effort */ }
    }

    /// <summary>Publishes the "do not end me" marker, best effort.</summary>
    public static void PublishProtection(string scratch)
    {
        try { File.WriteAllText(Path.Combine(scratch, ProtectedMarker), ""); }
        catch { /* best effort */ }
    }
}
