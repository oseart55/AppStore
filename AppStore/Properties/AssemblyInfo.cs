using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("App Store")]
[assembly: AssemblyDescription("Installs and updates apps from GitHub releases")]
[assembly: AssemblyProduct("App Store")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: ComVisible(false)]
[assembly: Guid("6f1d3a52-8c4e-4b7a-9e21-3d5f0a7c9b14")]

// Release tags are "v" + the first three parts (v1.0.0). release.bat reads this.
[assembly: AssemblyVersion("1.0.3.0")]

// Device apps have no STA thread.
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2232:MarkWindowsFormsEntryPointsWithStaThread")]
