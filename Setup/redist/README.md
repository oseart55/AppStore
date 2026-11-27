# Setup/redist

`AppStoreSetup.exe` carries Microsoft's .NET Compact Framework 3.5 installer for
Windows Mobile. It isn't in this repository: copy it here from your Visual
Studio 2008 installation before building the setup.

```
copy "C:\Program Files (x86)\Microsoft.NET\SDK\CompactFramework\v3.5\WindowsCE\NETCFv35.wm.armv4i.cab" Setup\redist\
```

(`C:\Program Files\...` on 32-bit Windows.) It's Microsoft's redistributable,
under Microsoft's licence terms. Check them before distributing the setup.
