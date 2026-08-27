using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Te1000Daemon
{
    // Acquires and CACHES the DTE + ITcSysManager once, with a cheap health-check
    // and transparent reconnect. Ports Get-PreferredDteFromRot (bridge L387-419),
    // Get-Dte (L533-579), and Get-SysManager (L623-676). All members are called
    // on the owning STA thread (ComWorker.Pump).
    public sealed class ComSession
    {
        private dynamic _dte;
        private dynamic _sysManager;
        private string _progId;
        private string _mode;
        private bool _stale;

        // Identity of the IDE currently attached, so a caller can be told which one it
        // got and so a request for a DIFFERENT one is not silently served the cached
        // instance. _currentPid is 0 when the identity could not be resolved -- in which
        // case a pid-targeted request never matches and we reattach, which is the safe
        // direction to fail.
        private int _currentPid;
        private string _currentMoniker;
        private bool _ownedByUs;

        public int CurrentPid { get { return _currentPid; } }
        public bool OwnedByUs { get { return _ownedByUs; } }

        public void MarkStale() { _stale = true; _dte = null; _sysManager = null; _currentPid = 0; _currentMoniker = null; _ownedByUs = false; }

        // Best-effort release of a COM RCW. Guards against already-released RCWs
        // and non-COM objects (Marshal.ReleaseComObject throws ArgumentException
        // on a non-COM object, InvalidComObjectException on an already-released
        // one). Loops ReleaseComObject down to a zero ref count so the underlying
        // proxy is actually torn down (a dynamic/dispatch RCW can hold >1 ref).
        private static void SafeRelease(object com)
        {
            if (com == null) return;
            try
            {
                if (!Marshal.IsComObject(com)) return;
                int rc;
                do { rc = Marshal.ReleaseComObject(com); } while (rc > 0);
            }
            catch { /* already released / not a COM RCW — ignore */ }
        }

        // Return a live, cached DTE for (progId, mode); reconnect if stale/dead.
        public dynamic GetDte(string progId, string mode, bool visible = true)
        {
            return GetDte(progId, mode, visible, null);
        }

        // Same, with an optional explicit target: attach.Pid or attach.SolutionPath
        // names WHICH running IDE to work with.
        //
        // The cache used to key on progId alone, so once a DTE was attached every later
        // call got that one back whatever it asked for: `mode` was documented per-call
        // but behaved per-session, and a request for a different instance was answered
        // with the current one. Now the cached instance is reused only when it actually
        // SATISFIES the request (see SatisfiesRequest), and otherwise we reattach.
        public dynamic GetDte(string progId, string mode, bool visible, InstanceRequest attach)
        {
            if (string.IsNullOrWhiteSpace(progId)) progId = "TcXaeShell.DTE.17.0";
            if (string.IsNullOrWhiteSpace(mode)) mode = "active";

            if (_dte != null && !_stale && _progId == progId)
            {
                if (IsDteAlive(_dte) && SatisfiesRequest(mode, attach)) return _dte;
                // Either dead, or alive but not what was asked for. Drop the reference
                // WITHOUT releasing: SafeRelease drives the RCW ref count to zero, and
                // the CLR hands out the SAME RCW for a given COM identity -- so releasing
                // it here would tear down an object the very next ROT walk may return.
                // Let the GC reclaim it instead.
                _dte = null; _sysManager = null; _currentPid = 0; _currentMoniker = null;
                _ownedByUs = false;
            }

            bool created;
            _dte = AcquireDte(progId, mode, visible, attach, out created);
            _progId = progId;
            _mode = mode;
            _sysManager = null;
            _stale = false;
            _ownedByUs = created;
            ResolveCurrentIdentity(progId);
            return _dte;
        }

        // Does the instance we already hold answer this request?
        //  - a pid target: only that exact process;
        //  - a solution target: the solution it has open right now (read live, because
        //    an IDE can be made to open a different one);
        //  - mode "create": only an IDE we created ourselves, so repeated create calls
        //    reuse OUR instance instead of spawning one per call;
        //  - otherwise: anything live will do, which is the old behaviour.
        private bool SatisfiesRequest(string mode, InstanceRequest attach)
        {
            // "give me a new one" can never be answered with the one already held.
            if (attach != null && attach.ForceNew) return false;
            if (attach != null && attach.Pid > 0) return _currentPid == attach.Pid;
            if (attach != null && !string.IsNullOrWhiteSpace(attach.SolutionPath))
            {
                string open = null;
                try { open = _dte.Solution != null ? (string)_dte.Solution.FullName : null; }
                catch { open = null; }
                return PathUtil.SamePath(open, attach.SolutionPath);
            }
            if (mode == "create") return _ownedByUs;
            return true;
        }

        private static bool IsDteAlive(dynamic dte)
        {
            try { var _ = dte.Name; return true; }   // cheap property read
            catch { return false; }
        }

        // Get-Dte (L533-579): modes active / create / activeOrCreate.
        // `created` reports whether this call started the IDE (as opposed to attaching
        // to one that was already running) -- the difference decides whether we may
        // release its RCW and whether a later mode:"create" can reuse it.
        private dynamic AcquireDte(string progId, string mode, bool visible, InstanceRequest attach, out bool created)
        {
            created = false;

            // An explicit target overrides the mode heuristics entirely: the caller has
            // named the instance, so either it is there or this is an error. Falling back
            // to "some other IDE" would be the very silent mis-attach this exists to stop.
            if (attach != null && attach.NamesExisting)
            {
                dynamic targeted = AttachToTarget(progId, attach);
                if (targeted == null) throw new BridgeException(DescribeMissingTarget(progId, attach));
                return targeted;
            }

            // An unconditional new instance, whatever is already running and whatever
            // this session is currently attached to. Without it, mode:"create" reuses
            // an IDE we started (so that passing create on every call does not spawn one
            // per call), which leaves no way to ask for a SECOND instance on purpose.
            if (attach != null && attach.ForceNew)
            {
                created = true;
                return CreateDte(progId, visible);
            }

            switch (mode)
            {
                case "active":
                {
                    dynamic d = GetPreferredDteFromRot(progId);
                    if (d != null) return d;
                    return Marshal.GetActiveObject(progId);
                }
                case "create":
                    created = true;
                    return CreateDte(progId, visible);
                case "activeOrCreate":
                    try
                    {
                        dynamic d = GetPreferredDteFromRot(progId);
                        if (d != null) return d;
                        return Marshal.GetActiveObject(progId);
                    }
                    catch
                    {
                        created = true;
                        return CreateDte(progId, visible);
                    }
                default:
                    throw new BridgeException("Unsupported DTE mode: " + mode);
            }
        }

        // Find the one running IDE the caller named. Returns null if there is no match.
        private dynamic AttachToTarget(string progId, InstanceRequest attach)
        {
            var entries = ListRunningDte(progId);
            RotEntry chosen = null;
            foreach (var e in entries)
            {
                bool hit;
                if (attach.Pid > 0) hit = (PidFromMoniker(e.DisplayName) == attach.Pid);
                else hit = PathUtil.SamePath(e.Solution, attach.SolutionPath);
                if (hit) { chosen = e; break; }
            }
            foreach (var e in entries)
                if (!ReferenceEquals(e, chosen)) SafeRelease((object)e.Dte);
            return chosen == null ? null : chosen.Dte;
        }

        // Name what WAS running, so a failed attach says which instances exist instead of
        // just reporting that the wanted one does not.
        private string DescribeMissingTarget(string progId, InstanceRequest attach)
        {
            string wanted = attach.Pid > 0
                ? "pid " + attach.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "solution '" + attach.SolutionPath + "'";
            var running = new List<string>();
            try
            {
                foreach (var i in ListInstances(progId))
                {
                    running.Add("pid " + i.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                (string.IsNullOrWhiteSpace(i.Solution) ? " (no solution)" : " -> " + i.Solution));
                }
            }
            catch { }
            string list = running.Count == 0 ? "none" : string.Join("; ", running.ToArray());
            return "No running " + progId + " instance matches " + wanted + ". Running: " + list +
                   ". Use xae list_instances to see them, or mode:\"create\" to start a new one.";
        }

        // The ROT display name for a DTE is "!VisualStudio.DTE.17.0:<pid>". Returns 0
        // when it does not end in a pid.
        private static int PidFromMoniker(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return 0;
            int colon = displayName.LastIndexOf(':');
            if (colon < 0 || colon == displayName.Length - 1) return 0;
            int pid;
            if (!int.TryParse(displayName.Substring(colon + 1), out pid)) return 0;
            return pid;
        }

        // After acquiring, work out WHICH process we ended up on by finding our own DTE
        // back in the ROT by COM identity. A freshly created instance has no moniker of
        // its own to hand us, so this is also how a created IDE gets its pid.
        private void ResolveCurrentIdentity(string progId)
        {
            _currentPid = 0;
            _currentMoniker = null;
            if (_dte == null) return;
            IntPtr mine = IntPtr.Zero;
            try
            {
                mine = Marshal.GetIUnknownForObject((object)_dte);
                var entries = ListRunningDte(progId);
                foreach (var e in entries)
                {
                    IntPtr theirs = IntPtr.Zero;
                    try
                    {
                        theirs = Marshal.GetIUnknownForObject((object)e.Dte);
                        if (theirs == mine)
                        {
                            _currentMoniker = e.DisplayName;
                            _currentPid = PidFromMoniker(e.DisplayName);
                        }
                    }
                    catch { }
                    finally { if (theirs != IntPtr.Zero) Marshal.Release(theirs); }
                    // Releasing the entry that IS our own DTE would destroy the shared
                    // RCW we just acquired -- same object, same RCW.
                    if (!ReferenceEquals((object)e.Dte, (object)_dte)) SafeRelease((object)e.Dte);
                }
            }
            catch { }
            finally { if (mine != IntPtr.Zero) Marshal.Release(mine); }
        }

        private dynamic CreateDte(string progId, bool visible)
        {
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) throw new BridgeException("ProgID not registered: " + progId);
            dynamic dte = Activator.CreateInstance(t);
            try { dte.SuppressUI = true; } catch { }
            try { dte.MainWindow.Visible = visible; } catch { }
            return dte;
        }

        // Get-PreferredDteFromRot (L387-419) + DteRotProbe (L286-371): enumerate
        // the Running Object Table for monikers matching the progId, prefer one
        // with an open solution (or TE1000_MCP_SOLUTION_PATH).
        private dynamic GetPreferredDteFromRot(string progId)
        {
            var entries = ListRunningDte(progId);
            if (entries.Count == 0) return null;

            RotEntry chosen = null;
            string preferred = Environment.GetEnvironmentVariable("TE1000_MCP_SOLUTION_PATH");
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                foreach (var e in entries)
                    if (!string.IsNullOrWhiteSpace(e.Solution) && string.Equals(e.Solution, preferred, StringComparison.OrdinalIgnoreCase))
                    { chosen = e; break; }
            }
            if (chosen == null)
            {
                foreach (var e in entries)
                    if (!string.IsNullOrWhiteSpace(e.Solution)) { chosen = e; break; }
            }
            if (chosen == null) chosen = entries[0];

            // Release the DTE RCWs of every ROT entry we did NOT return; only the
            // chosen one survives (the caller takes ownership of it).
            foreach (var e in entries)
                if (!ReferenceEquals(e, chosen)) SafeRelease((object)e.Dte);

            return chosen.Dte;
        }

        private sealed class RotEntry { public string DisplayName; public string Solution; public dynamic Dte; }

        // Which IDE this call wants. Either an existing one -- named by Pid (which wins
        // when both are set) or by the SolutionPath it has open -- or, with ForceNew, a
        // brand new one regardless of what is already running.
        public sealed class InstanceRequest
        {
            public int Pid;
            public string SolutionPath;
            public bool ForceNew;

            public bool NamesExisting
            {
                get { return Pid > 0 || !string.IsNullOrWhiteSpace(SolutionPath); }
            }
        }

        // One running IDE, as reported to the caller.
        public sealed class InstanceInfo
        {
            public int Pid;
            public string DisplayName;
            public string Solution;
            public bool IsCurrent;
            public bool OwnedByUs;
        }

        // Every running IDE for this progId, with the solution each has open.
        //
        // The ROT walk already read exactly this to pick an instance by heuristic; it was
        // used internally and thrown away. Handing it to the caller is what lets an agent
        // SEE what is running and choose, instead of hoping the heuristic picks the same
        // one it had in mind. Releases every DTE it touches: this is a read, not an attach.
        public List<InstanceInfo> ListInstances(string progId)
        {
            if (string.IsNullOrWhiteSpace(progId)) progId = "TcXaeShell.DTE.17.0";
            var result = new List<InstanceInfo>();

            IntPtr mine = IntPtr.Zero;
            try
            {
                if (_dte != null && !_stale)
                {
                    try { mine = Marshal.GetIUnknownForObject((object)_dte); }
                    catch { mine = IntPtr.Zero; }
                }

                foreach (var e in ListRunningDte(progId))
                {
                    var info = new InstanceInfo();
                    info.DisplayName = e.DisplayName;
                    info.Pid = PidFromMoniker(e.DisplayName);
                    info.Solution = e.Solution;

                    if (mine != IntPtr.Zero)
                    {
                        IntPtr theirs = IntPtr.Zero;
                        try
                        {
                            theirs = Marshal.GetIUnknownForObject((object)e.Dte);
                            info.IsCurrent = (theirs == mine);
                        }
                        catch { }
                        finally { if (theirs != IntPtr.Zero) Marshal.Release(theirs); }
                    }
                    info.OwnedByUs = info.IsCurrent && _ownedByUs;

                    result.Add(info);
                    // Same rule as ResolveCurrentIdentity: this is a read, so release
                    // every instance we touched EXCEPT the one this session is using.
                    if (!info.IsCurrent) SafeRelease((object)e.Dte);
                }
            }
            finally { if (mine != IntPtr.Zero) Marshal.Release(mine); }

            return result;
        }

        [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

        private List<RotEntry> ListRunningDte(string progId)
        {
            var result = new List<RotEntry>();
            IRunningObjectTable rot;
            if (GetRunningObjectTable(0, out rot) != 0 || rot == null) return result;
            IBindCtx bindCtx;
            if (CreateBindCtx(0, out bindCtx) != 0 || bindCtx == null) return result;
            IEnumMoniker enumMoniker;
            rot.EnumRunning(out enumMoniker);
            if (enumMoniker == null) return result;

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                string displayName = "";
                try { monikers[0].GetDisplayName(bindCtx, null, out displayName); }
                catch { displayName = ""; }

                if (string.IsNullOrWhiteSpace(displayName) ||
                    displayName.IndexOf(progId, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    object raw;
                    rot.GetObject(monikers[0], out raw);
                    if (raw == null) continue;
                    dynamic dte = raw;
                    string solution = "";
                    try { solution = dte.Solution != null ? (string)dte.Solution.FullName : ""; }
                    catch { solution = ""; }
                    result.Add(new RotEntry { DisplayName = displayName, Solution = solution, Dte = dte });
                }
                catch { }
            }
            return result;
        }

        // Get-SysManager (L623-676): prefer the loaded .tsproj project Object
        // (stays bound to the live config), else DTE.GetObject('TcSysManager').
        // Cached; retries transient RPC busy.
        public dynamic GetSysManager()
        {
            if (_sysManager != null && !_stale)
            {
                if (IsSysManagerAlive(_sysManager)) return _sysManager;
                _sysManager = null;
            }
            if (_dte == null) throw new BridgeException("DTE not acquired");

            for (int attempt = 1; attempt <= 40; attempt++)
            {
                try
                {
                    dynamic solution = _dte.Solution;
                    if (solution != null && solution.Projects != null)
                    {
                        int count = (int)solution.Projects.Count;
                        for (int i = 1; i <= count; i++)
                        {
                            dynamic project = solution.Projects.Item(i);
                            if (project == null) continue;
                            string fullName = null;
                            try { fullName = (string)project.FullName; } catch { }
                            if (string.IsNullOrWhiteSpace(fullName) ||
                                !fullName.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase))
                                continue;
                            dynamic projectObject = null;
                            try { projectObject = project.Object; } catch { }
                            if (projectObject == null) continue;
                            // Probe GetTargetNetId() — proves it's the live config surface.
                            try
                            {
                                var probe = projectObject.GetTargetNetId();
                                if (probe != null) { _sysManager = projectObject; return _sysManager; }
                            }
                            catch { }
                        }
                    }
                    dynamic sm = _dte.GetObject("TcSysManager");
                    if (sm == null) throw new BridgeException("TcSysManager is null");
                    _sysManager = sm;
                    return _sysManager;
                }
                catch (BridgeException) { if (attempt >= 40) throw; System.Threading.Thread.Sleep(500); }
                catch (Exception ex)
                {
                    if (ComHelpers.IsRetryableComError(ex) && attempt < 40)
                    {
                        System.Threading.Thread.Sleep(500);
                        continue;
                    }
                    throw;
                }
            }
            throw new BridgeException("TcSysManager not available");
        }

        private static bool IsSysManagerAlive(dynamic sm)
        {
            try { var _ = sm.GetTargetNetId(); return true; }
            catch
            {
                // Some sysmanagers are the GetObject wrapper without GetTargetNetId;
                // fall back to a cheap call that always exists.
                try { var __ = sm.LookupTreeItem("TIID"); return true; } catch { return false; }
            }
        }

        public void Dispose()
        {
            try { Te1000MessageFilter.Revoke(); } catch { }
            // Release the cached proxies before dropping references so the COM RCWs
            // are torn down rather than leaked until GC. MarkStale() may already
            // have nulled these (recycle path) — SafeRelease(null) is a no-op.
            object sm = (object)_sysManager;
            object dte = (object)_dte;
            _sysManager = null; _dte = null;
            SafeRelease(sm);
            // Only release the DTE if WE own it (created via 'create'); an 'active'
            // DTE is the live IDE shared across the ROT, and over-releasing its RCW
            // can disturb the running IDE. We acquired our own RCW ref though, so a
            // single balanced release is correct either way — release it.
            SafeRelease(dte);
        }
    }
}
