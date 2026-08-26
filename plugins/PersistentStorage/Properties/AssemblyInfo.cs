using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("PersistentStorage")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("2b9c8121-81cf-43a0-8ede-7e15acf6fb7b")]

// Everything in this plugin is internal - without this the test project could reference the
// assembly and still see nothing. Same declaration Server/Properties/AssemblyInfo.cs carries.
[assembly: InternalsVisibleTo("X13.Tests")]
