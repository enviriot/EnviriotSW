using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Archivist")]
[assembly: AssemblyDescription("Archive plugin for Enviriot")]
[assembly: AssemblyConfiguration("")]

[assembly: ComVisible(false)]
[assembly: Guid("7c1d6f3a-9b42-4e18-8a55-2f9d0c31ae74")]

// Everything in this plugin is internal; without this the test project could reference the
// assembly and still see nothing. Same declaration the other plugins carry.
[assembly: InternalsVisibleTo("X13.Tests")]

// The one-off migration tool at C:\X13\ArchConv writes the new archive layout. It goes through
// this assembly rather than reimplementing the schema and the fold, so a converted archive is by
// construction identical to one the server would have produced itself.
[assembly: InternalsVisibleTo("ArchConv")]
