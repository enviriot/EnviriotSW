using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Logram")]
[assembly: AssemblyDescription("Logram plugin for Enviriot")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("54a8f051-59cf-473e-919d-bee35808dfb1")]

// Everything in this plugin is internal, so without this the test project could reference the
// assembly and still see nothing - which is exactly why the LoVariable.Tick1 fix shipped with
// no automated test. Same declaration Server/Properties/AssemblyInfo.cs carries.
[assembly: InternalsVisibleTo("X13.Tests")]
