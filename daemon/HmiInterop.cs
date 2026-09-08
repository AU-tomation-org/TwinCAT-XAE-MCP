using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Te1000Daemon
{
    // Resolves the TwinCAT HMI (TE2000) automation object's ProgId.
    //
    // The name is NOT guessable: the object is registered by the TE2000 VS package
    // under a versioned ProgId, and eight plausible spellings all answer
    // DISP_E_MEMBERNOTFOUND. The registration lives in the package's pkgdef:
    //
    //   [$RootKey$\Packages\{16a09aeb-7616-4147-ab22-721f8fb090fc}\Automation]
    //   "Beckhoff.TcHmi.1.12"=""
    //
    // The suffix is the TcHmi PLATFORM generation, not the product version, and it
    // does NOT track the framework: measured 2026-09-08 on TE2000 with framework
    // 14.4.70 / Controls 14.6.28 and a VS package stamped 14.3.995.1, the ProgId is
    // still 1.12 -- and the same 1.12 is what TE2000 writes into the pkgdef it ships
    // for VS2026. (The same "1.12" appears as the runtime moniker inside the
    // framework package: runtimes\native1.12-tchmi\.) So it is a frozen name rather
    // than a number to bump, but it is still read from the pkgdef rather than
    // hardcoded: a hardcoded name would break silently on the day it does move, and
    // the failure reads as "the HMI package is not installed".
    //
    // TRAP: the pkgdef files are UTF-16. A naive text read (or a grep) finds nothing
    // in them and the resolution looks like "TE2000 not installed".
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    public static class HmiInterop
    {
        // ProgIds to try when no pkgdef could be read. Only a net: the pkgdef is the
        // authority. 1.12 first because it is what every shipped pkgdef says today.
        private static readonly string[] ProbeSuffixes = { "1.12", "1.14", "1.13", "1.16", "1.10" };

        private static readonly object Gate = new object();
        // Keyed by the IDE executable path: two IDEs from different installs can in
        // principle carry different TE2000 packages.
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The ProgId that answers on THIS ide, or a BridgeException naming what was
        // tried. `probed` reports how it was found, so a caller (and the notes) can
        // tell a pkgdef read from a lucky guess.
        public static string ResolveProgId(dynamic dte, out string how)
        {
            string ideExe = ComHelpers.SafeStr(delegate { return dte.FullName; });
            if (string.IsNullOrWhiteSpace(ideExe)) ideExe = "<unknown-ide>";

            lock (Gate)
            {
                string cached;
                if (Cache.TryGetValue(ideExe, out cached) && !string.IsNullOrWhiteSpace(cached))
                {
                    how = "cached";
                    return cached;
                }
            }

            var tried = new List<string>();

            // 1. Explicit override. The escape hatch for an install this does not know.
            string forced = Environment.GetEnvironmentVariable("TE2000_HMI_PROGID");
            if (!string.IsNullOrWhiteSpace(forced))
            {
                forced = forced.Trim();
                if (Answers(dte, forced)) { Remember(ideExe, forced); how = "TE2000_HMI_PROGID"; return forced; }
                tried.Add(forced + " (from TE2000_HMI_PROGID)");
            }

            // 2. The pkgdef deployed into the IDE we are actually attached to.
            foreach (string name in NamesFromPkgdefs(PkgdefsNearIde(ideExe)))
            {
                if (Answers(dte, name)) { Remember(ideExe, name); how = "pkgdef (IDE extensions)"; return name; }
                tried.Add(name + " (from the IDE's pkgdef)");
            }

            // 3. The copies TE2000 ships, one per shell. Same content, and they are
            //    there even when the IDE's own Extensions dir cannot be walked.
            foreach (string name in NamesFromPkgdefs(PkgdefsInTe2000()))
            {
                if (Answers(dte, name)) { Remember(ideExe, name); how = "pkgdef (TE2000 install)"; return name; }
                tried.Add(name + " (from the TE2000 install)");
            }

            // 4. Last resort.
            for (int i = 0; i < ProbeSuffixes.Length; i++)
            {
                string name = "Beckhoff.TcHmi." + ProbeSuffixes[i];
                if (tried.Contains(name)) continue;
                if (Answers(dte, name)) { Remember(ideExe, name); how = "probe"; return name; }
                tried.Add(name + " (probed)");
            }

            throw new BridgeException(
                "The TwinCAT HMI automation object did not answer on this IDE. TE2000 (HMI Engineering) " +
                "may not be installed in it, or the package failed to load. Tried: " +
                string.Join("; ", tried.ToArray()) +
                ". Set TE2000_HMI_PROGID to force a name.");
        }

        private static void Remember(string ideExe, string progId)
        {
            lock (Gate) { Cache[ideExe] = progId; }
        }

        // A ProgId "answers" when DTE.GetObject returns something for it. A name that
        // is not registered raises DISP_E_MEMBERNOTFOUND (0x80020003) rather than
        // returning null, so both outcomes are treated as "not this one".
        private static bool Answers(dynamic dte, string progId)
        {
            try
            {
                dynamic o = dte.GetObject(progId);
                return o != null;
            }
            catch (Exception ex)
            {
                Log.Write("HmiInterop: " + progId + " did not answer: " + ComHelpers.ErrorCode(ex));
                return false;
            }
        }

        // Every distinct Beckhoff.TcHmi.* name found in the given pkgdefs, in order.
        private static IEnumerable<string> NamesFromPkgdefs(IEnumerable<string> paths)
        {
            var seen = new List<string>();
            foreach (string path in paths)
            {
                string text = ReadPossiblyUtf16(path);
                if (text == null) continue;
                foreach (Match m in Regex.Matches(text, @"Beckhoff\.TcHmi\.[0-9]+(?:\.[0-9]+)*"))
                {
                    if (!seen.Contains(m.Value)) { seen.Add(m.Value); }
                }
            }
            return seen;
        }

        // pkgdef text, whatever its encoding. The files VS deploys are UTF-16LE with a
        // BOM; reading them as UTF-8 yields a string full of NULs in which the regex
        // matches nothing. Try the BOM-honouring read first, then fall back to
        // stripping NULs out of a byte-per-char decode, which turns UTF-16LE ASCII
        // into plain text without guessing an encoding.
        private static string ReadPossiblyUtf16(string path)
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (Exception ex) { Log.Write("HmiInterop: cannot read " + path + ": " + ex.Message); return null; }
            if (raw.Length == 0) return null;

            try
            {
                using (var sr = new StreamReader(new MemoryStream(raw), Encoding.UTF8, true))
                {
                    string s = sr.ReadToEnd();
                    if (s.IndexOf("Beckhoff.TcHmi", StringComparison.OrdinalIgnoreCase) >= 0) return s;
                }
            }
            catch { }

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++) if (raw[i] != 0) sb.Append((char)raw[i]);
            return sb.ToString();
        }

        // TcHmiPackage.pkgdef under the Extensions dir of the attached IDE. The vendor
        // folder name is not assumed -- the file is searched for by name.
        private static IEnumerable<string> PkgdefsNearIde(string ideExe)
        {
            var hits = new List<string>();
            string ideDir = null;
            try { ideDir = Path.GetDirectoryName(ideExe); }
            catch { }
            if (string.IsNullOrWhiteSpace(ideDir)) return hits;

            string ext = Path.Combine(ideDir, "Extensions");
            if (!Directory.Exists(ext)) return hits;
            try { hits.AddRange(Directory.GetFiles(ext, "TcHmiPackage.pkgdef", SearchOption.AllDirectories)); }
            catch (Exception e) { Log.Write("HmiInterop: cannot walk " + ext + ": " + e.Message); }
            return hits;
        }

        // The pkgdefs TE2000 keeps in its own install, one folder per shell.
        private static IEnumerable<string> PkgdefsInTe2000()
        {
            var hits = new List<string>();
            foreach (string root in Te2000Roots())
            {
                string vs = Path.Combine(root, "VisualStudio");
                if (!Directory.Exists(vs)) continue;
                try { hits.AddRange(Directory.GetFiles(vs, "TcHmiPackage.pkgdef", SearchOption.AllDirectories)); }
                catch (Exception e) { Log.Write("HmiInterop: cannot walk " + vs + ": " + e.Message); }
            }
            return hits;
        }

        // TE2000-HMI-Engineering, resolved from the TwinCAT install dir in the
        // registry rather than assumed -- the same discipline the pkgdef itself uses.
        public static IEnumerable<string> Te2000Roots()
        {
            var roots = new List<string>();
            foreach (string install in TwinCatInstallDirs())
            {
                string p = Path.Combine(install, @"Functions\TE2000-HMI-Engineering");
                if (Directory.Exists(p) && !roots.Contains(p)) roots.Add(p);
            }
            foreach (string p in new string[] {
                @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering",
                @"C:\Program Files\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering",
                @"C:\TwinCAT\Functions\TE2000-HMI-Engineering" })
            {
                if (Directory.Exists(p) && !roots.Contains(p)) roots.Add(p);
            }
            return roots;
        }

        private static IEnumerable<string> TwinCatInstallDirs()
        {
            var dirs = new List<string>();
            string[] keys = {
                @"HKEY_CURRENT_USER\Software\Beckhoff\TwinCAT3\3.1",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\3.1",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Beckhoff\TwinCAT3\3.1",
            };
            for (int i = 0; i < keys.Length; i++)
            {
                object v = null;
                try { v = Microsoft.Win32.Registry.GetValue(keys[i], "InstallDir", null); }
                catch { }
                string s = v as string;
                if (string.IsNullOrWhiteSpace(s)) continue;
                // InstallDir points at ...\TwinCAT\3.1\; the Functions tree is its sibling.
                try
                {
                    string trimmed = s.TrimEnd('\\');
                    string parent = Path.GetDirectoryName(trimmed);
                    if (!string.IsNullOrWhiteSpace(parent) && !dirs.Contains(parent)) dirs.Add(parent);
                }
                catch { }
            }
            return dirs;
        }

        // TcHmiAutomation.dll, for the day a typed/early-bound read is needed. Not used
        // by the current verbs: measured 2026-09-08, ITcHmiAutomation and its children
        // dispatch METHOD calls late-bound perfectly well (AddUserControl, SaveAllFiles,
        // IsReady all work through `dynamic`). Kept resolved here because the notes
        // predicted the opposite and the next person will look for it.
        public static string FindAutomationAssembly()
        {
            foreach (string root in Te2000Roots())
            {
                foreach (string sub in new string[] { "MSBuild", "bin", @"VisualStudio\2022" })
                {
                    string p = Path.Combine(Path.Combine(root, sub), "TcHmiAutomation.dll");
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }
}
