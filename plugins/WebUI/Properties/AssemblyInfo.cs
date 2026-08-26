using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("WebUI")]
[assembly: AssemblyDescription("WebUI plugin for Enviriot")]
[assembly: AssemblyConfiguration("")]
[assembly: ComVisible(false)]
[assembly: Guid("c4f81a20-7b36-4e5d-9a11-2e7d8f6b31c5")]
// Without this the 100+ WebUI tests cannot see anything: every type in this assembly is
// internal, and they were only reachable while these files were compiled into Server.
[assembly: InternalsVisibleTo("X13.Tests")]
