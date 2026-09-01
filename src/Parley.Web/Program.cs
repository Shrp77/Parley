using Parley.Web;

// The console as a program of its own, for `dotnet run` in this folder. Everything it does lives
// in ConsoleHost, because `parley console` starts the same thing and two callers each building a
// host is two hosts that differ the first time one of them is edited.

await ConsoleHost.RunAsync(args).ConfigureAwait(false);

/// <summary>The console's entry point.</summary>
/// <remarks>
/// Declared so that the test project can reference the host through
/// <c>WebApplicationFactory</c>; a top-level program's generated class is internal otherwise.
/// </remarks>
public partial class Program;
