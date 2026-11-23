namespace Parley.Configuration;

/// <summary>
/// Where the web UI listens when nothing says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>One number, in the one project every tool already reads.</b> Four things state this
/// address: the host that binds it, the broker that starts it, the autostart entry that registers
/// it, and the terminal's action that opens a browser at it. They are in three packages now, so
/// there is nowhere else all four can see.
/// </para>
/// <para>
/// <b>A single source matters.</b> A hand-copied literal that drifts makes the terminal's
/// open-in-browser action open a port nothing listens on and still report success, since from the
/// terminal's side the browser started, which is all it can observe.
/// </para>
/// <para>
/// Loopback. SPEC.md §16 confines the transport to a trusted path because anything able to reach a
/// broker can claim to be any agent, and the web UI is a second door onto that same path — it is
/// not an authentication boundary and must not be mistaken for one. So the safe default is the one
/// where the question does not arise, and widening it announces itself rather than happening
/// quietly.
/// </para>
/// </remarks>
public static class WebUiAddress
{
    /// <summary>The address the web UI listens on unless told otherwise.</summary>
    public const string Loopback = "http://127.0.0.1:5280";

    /// <summary>
    /// The start of the one line a web UI started by a broker writes to its standard output once it is serving,
    /// followed by the addresses it bound, separated by <c>;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Readiness comes from the child, not from the address.</b> A broker used to call its web UI ready when a
    /// connection to the address was accepted. Another program that took the address after the broker looked and before
    /// the web UI bound accepted that connection too, and the broker sent the person to it while its own web UI failed
    /// to bind and exited. Only the web UI writes to its own standard output, so this line cannot come from anyone else.
    /// </para>
    /// <para>
    /// Here because both ends read it: the web UI writes it and the broker's tool waits for it, and the two are in
    /// packages that reference nothing of each other's.
    /// </para>
    /// </remarks>
    public const string ServingMarker = "parley-web-ui serving ";
}
