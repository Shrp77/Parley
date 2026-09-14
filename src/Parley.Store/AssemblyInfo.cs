using System.Runtime.CompilerServices;

// Parley.Store.Tests reaches the store's own readings of what it wrote -- a stored state token, the
// items a closed Result answers for -- so a test can plant a row and check exactly what the store
// makes of it, rather than restating the construction by hand and asserting that two copies agree.
[assembly: InternalsVisibleTo("Parley.Store.Tests")]
