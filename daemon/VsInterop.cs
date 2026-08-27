using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Te1000Daemon
{
    // Runtime loader for the EnvDTE PIAs used by the typed DTE paths (see
    // XaeActions.ReadErrorList). The daemon exe lives under C:\ProgramData and the
    // PIAs (EnvDTE / EnvDTE80 / Microsoft.VisualStudio.Interop) are neither copied
    // local nor in a GAC view the runtime probes, so we resolve them from the
    // TcXaeShell PublicAssemblies dir on demand — resolving the XAE public
    // assemblies path.
    public static class VsInterop
    {
        private static int _installed;

        // Same candidate roots as build.ps1 / Get-XaePublicAssembliesPath: prefer
        // the 64-bit install, fall back to x86.
        private static readonly string[] Roots = BuildRoots();

        // Local change (AU-tomation): TcXaeShell is not the only host. When XAE is
        // integrated into a full Visual Studio 2022 (TE1000 + VS2022, no separate
        // shell), the TcXaeShell PublicAssemblies dir does not exist and the typed
        // DTE2 paths (XaeActions.ReadErrorList) die with "Could not load file or
        // assembly 'EnvDTE80'". Probe the VS2022 PublicAssemblies too, and allow an
        // explicit override via TE1000_PIA_DIR.
        private static string[] BuildRoots()
        {
            var roots = new System.Collections.Generic.List<string>();
            var overridden = Environment.GetEnvironmentVariable("TE1000_PIA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) roots.Add(overridden);
            roots.Add(@"C:\Program Files\Beckhoff\TcXaeShell\Common7\IDE\PublicAssemblies");
            roots.Add(@"C:\Program Files (x86)\Beckhoff\TcXaeShell\Common7\IDE\PublicAssemblies");
            foreach (var pf in new[] { @"C:\Program Files\Microsoft Visual Studio\2022",
                                       @"C:\Program Files (x86)\Microsoft Visual Studio\2022" })
                foreach (var ed in new[] { "Enterprise", "Professional", "Community", "BuildTools" })
                    roots.Add(Path.Combine(pf, ed, @"Common7\IDE\PublicAssemblies"));
            return roots.ToArray();
        }

        private static readonly string[] Wanted =
        {
            "EnvDTE", "EnvDTE80", "Microsoft.VisualStudio.Interop",
        };

        // Idempotent; safe to call from any thread before the first typed-DTE use.
        public static void EnsureResolver()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 1) return;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name;
            try { name = new AssemblyName(e.Name).Name; }
            catch { return null; }

            bool wanted = false;
            for (int i = 0; i < Wanted.Length; i++)
                if (string.Equals(name, Wanted[i], StringComparison.OrdinalIgnoreCase)) { wanted = true; break; }
            if (!wanted) return null;

            foreach (var root in Roots)
            {
                try
                {
                    var path = Path.Combine(root, name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                catch (Exception ex) { Log.Error("VsInterop.Resolve failed for " + name + " in " + root, ex); }
            }
            return null;
        }
    }
}
