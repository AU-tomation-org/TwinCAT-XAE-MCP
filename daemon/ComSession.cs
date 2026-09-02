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

        // WHICH TwinCAT project (.tsproj) the cached sysmanager belongs to, and which one
        // the caller asked this session to stay on. A solution holds one sysmanager PER
        // .tsproj -- tree, target NetId, boot flags and activation all hang off it -- and
        // the resolver used to take the first one that answered GetTargetNetId(). In every
        // AU-tomation repo that first one is the library, so get_netid answered with the
        // LOCAL NetId and activation targeted the project that has no task to run.
        //
        // _projectName/_projectPath: identity of the sysmanager currently cached.
        // _selectedName/_selectedPath: the session-level choice made by select_project,
        // re-applied on every later call (it survives a worker recycle, because it is
        // held as name/path, not as a COM reference).
        // _projectCandidates: how many .tsproj the last resolution had to choose from,
        // so a caller can be told the pick was ambiguous instead of finding out by
        // activating the wrong project.
        private string _projectName;
        private string _projectPath;
        private string _selectedName;
        private string _selectedPath;
        private int _projectCandidates;
        private bool _touchedProject;

        public int CurrentPid { get { return _currentPid; } }
        public bool OwnedByUs { get { return _ownedByUs; } }
        public string CurrentProjectName { get { return _projectName; } }
        public string CurrentProjectPath { get { return _projectPath; } }
        public bool HasProjectSelection { get { return !string.IsNullOrWhiteSpace(_selectedName) || !string.IsNullOrWhiteSpace(_selectedPath); } }

        // True when the last resolution picked among several .tsproj without being told
        // which -- the case where the old behaviour silently landed on the wrong one.
        public bool ProjectPickWasAmbiguous
        {
            get { return _projectCandidates > 1 && !HasProjectSelection; }
        }

        // Did THIS call reach a sysmanager? Lets the dispatcher annotate the response
        // with the project that was actually worked on, and only then.
        public bool TouchedProject { get { return _touchedProject; } }
        public void BeginCall() { _touchedProject = false; }

        public void MarkStale()
        {
            _stale = true; _dte = null; _sysManager = null; _currentPid = 0; _currentMoniker = null; _ownedByUs = false;
            // The cached sysmanager is gone with the DTE, so its identity goes too -- but
            // NOT the selection: it is a name, it outlives the RCW, and re-applying it is
            // exactly what makes a worker recycle invisible to the caller.
            _projectName = null; _projectPath = null; _projectCandidates = 0;
        }

        // Forget which project was chosen. Called when the ground the choice stood on
        // moves: a different solution is opened, or the session attaches to another IDE.
        // A selection kept across those would name a project that is no longer there and
        // turn every later call into an error about a project the caller never mentioned.
        public void ClearProjectSelection()
        {
            _selectedName = null; _selectedPath = null;
            _projectName = null; _projectPath = null; _projectCandidates = 0;
            _sysManager = null;
        }

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

            int previousPid = _currentPid;

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
            // A different IDE means a different solution, so a project chosen in the old
            // one no longer names anything here. Dropped only on an actual change of
            // instance: a reconnect to the SAME pid (worker recycle, dead RCW) keeps the
            // choice, which is the whole point of holding it by name.
            if (previousPid != 0 && _currentPid != previousPid) ClearProjectSelection();
            else { _projectName = null; _projectPath = null; _projectCandidates = 0; }
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
            return GetSysManager(null, false);
        }

        // Same, for ONE named project of the solution.
        //
        //   request           : the project named on this call, or null.
        //   requireUnambiguous: for the verbs that act on the target (activate, restart,
        //                       boot flags, boot generation). With several .tsproj in the
        //                       solution and no choice on record, these refuse and list
        //                       the projects rather than pick one -- picking is how the
        //                       wrong runtime got activated. Reads keep working and are
        //                       flagged ambiguous instead.
        //
        // Precedence: the per-call request, then the session selection (select_project),
        // then the historical "first .tsproj that answers GetTargetNetId()".
        public dynamic GetSysManager(ProjectRequest request, bool requireUnambiguous)
        {
            _touchedProject = true;

            ProjectRequest target = (request != null && request.NamesTarget) ? request : SessionSelection();

            if (_sysManager != null && !_stale && SatisfiesProject(target))
            {
                if (IsSysManagerAlive(_sysManager))
                {
                    if (requireUnambiguous && target == null) AssertUnambiguous();
                    return _sysManager;
                }
                _sysManager = null;
            }
            if (_dte == null) throw new BridgeException("DTE not acquired");

            for (int attempt = 1; attempt <= 40; attempt++)
            {
                try
                {
                    List<ProjectInfo> projects = ListProjects(false);
                    _projectCandidates = projects.Count;

                    if (target != null)
                    {
                        ProjectInfo hit = MatchProject(projects, target);
                        if (hit == null) throw new BridgeException(DescribeMissingProject(projects, target));
                        BindProject(hit);
                        return _sysManager;
                    }

                    if (requireUnambiguous) AssertUnambiguous(projects);

                    // No choice on record: the historical pick, the first .tsproj whose
                    // Object answers GetTargetNetId() (that probe is what proves it is
                    // the live config surface rather than a plain project node).
                    foreach (ProjectInfo p in projects)
                    {
                        if (p.SysManager == null) continue;
                        try
                        {
                            var probe = p.SysManager.GetTargetNetId();
                            if (probe != null) { BindProject(p); return _sysManager; }
                        }
                        catch { }
                    }

                    // No TwinCAT project in the solution at all: the DTE-wide fallback.
                    // Retried, because a solution still loading legitimately has neither
                    // yet -- that retry is why the original loop caught its own throw.
                    dynamic sm = _dte.GetObject("TcSysManager");
                    if (sm == null)
                    {
                        if (attempt < 40) { System.Threading.Thread.Sleep(500); continue; }
                        throw new BridgeException("TcSysManager is null");
                    }
                    _sysManager = sm;
                    _projectName = null;
                    _projectPath = null;
                    return _sysManager;
                }
                catch (BridgeException) { throw; }
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

        // Bind the session to one project for good: select_project. Resolves it now, so a
        // name that is not in the solution is an error HERE, at the call that named it,
        // rather than at some later tool that never mentioned a project.
        public ProjectInfo SelectProject(ProjectRequest request)
        {
            if (request == null || !request.NamesTarget)
                throw new BridgeException("select_project requires name or path");
            if (_dte == null) throw new BridgeException("DTE not acquired");

            List<ProjectInfo> projects = ListProjects(true);
            _projectCandidates = projects.Count;
            ProjectInfo hit = MatchProject(projects, request);
            if (hit == null) throw new BridgeException(DescribeMissingProject(projects, request));

            BindProject(hit);
            _selectedName = hit.Name;
            _selectedPath = hit.FullName;
            hit.IsCurrent = true;
            return hit;
        }

        private ProjectRequest SessionSelection()
        {
            if (!HasProjectSelection) return null;
            var r = new ProjectRequest();
            r.Name = _selectedName;
            r.Path = _selectedPath;
            return r;
        }

        // Does the sysmanager we already hold answer this request? Mirrors
        // SatisfiesRequest for instances: reuse the cached one only when it IS the one
        // asked for, never merely because it is warm.
        private bool SatisfiesProject(ProjectRequest target)
        {
            if (target == null) return true;
            if (!string.IsNullOrWhiteSpace(target.Path) && PathUtil.SamePath(_projectPath, target.Path)) return true;
            if (!string.IsNullOrWhiteSpace(target.Name) && NameMatches(_projectName, _projectPath, target.Name)) return true;
            return false;
        }

        private void BindProject(ProjectInfo p)
        {
            _sysManager = p.SysManager;
            _projectName = p.Name;
            _projectPath = p.FullName;
        }

        private void AssertUnambiguous()
        {
            if (!ProjectPickWasAmbiguous) return;
            AssertUnambiguous(ListProjects(false));
        }

        private void AssertUnambiguous(List<ProjectInfo> projects)
        {
            if (projects == null || projects.Count <= 1) return;
            if (HasProjectSelection) return;
            var names = new List<string>();
            foreach (ProjectInfo p in projects)
            {
                names.Add(p.Name + (string.IsNullOrWhiteSpace(p.TargetNetId) ? "" : " (target " + p.TargetNetId + ")"));
            }
            throw new BridgeException(
                "This solution has " + projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " TwinCAT projects and none was chosen: " + string.Join("; ", names.ToArray()) +
                ". This action changes the target, so it will not guess. Use xae list_projects, then xae select_project (or pass tsProject on this call).");
        }

        private static bool NameMatches(string projectName, string projectPath, string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return false;
            if (string.Equals(projectName, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                string stem = null;
                try { stem = System.IO.Path.GetFileNameWithoutExtension(projectPath); }
                catch { }
                if (string.Equals(stem, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static ProjectInfo MatchProject(List<ProjectInfo> projects, ProjectRequest target)
        {
            if (!string.IsNullOrWhiteSpace(target.Path))
            {
                foreach (ProjectInfo p in projects)
                    if (PathUtil.SamePath(p.FullName, target.Path)) return p;
                // A relative or partial path is what a caller naturally types after
                // reading list_projects; accept it when it identifies exactly one.
                string tail = NormalizeTail(target.Path);
                ProjectInfo only = null;
                foreach (ProjectInfo p in projects)
                {
                    string full = NormalizeTail(p.FullName);
                    if (full != null && tail != null && full.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    {
                        if (only != null) return null;   // ambiguous suffix: not a match
                        only = p;
                    }
                }
                if (only != null) return only;
            }
            if (!string.IsNullOrWhiteSpace(target.Name))
            {
                foreach (ProjectInfo p in projects)
                    if (NameMatches(p.Name, p.FullName, target.Name)) return p;
            }
            return null;
        }

        private static string NormalizeTail(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Trim().Replace('/', '\\').TrimEnd('\\');
        }

        private static string DescribeMissingProject(List<ProjectInfo> projects, ProjectRequest target)
        {
            var have = new List<string>();
            foreach (ProjectInfo p in projects) have.Add(p.Name + " -> " + p.FullName);
            string list = have.Count == 0 ? "none (no .tsproj in this solution)" : string.Join("; ", have.ToArray());
            return "No TwinCAT project in this solution matches " + target.Describe() +
                   ". Projects: " + list + ". Use xae list_projects to see them.";
        }

        // Every TwinCAT project of the open solution, in solution order.
        //
        // `detailed` adds the reads that cost a COM round trip each (target NetId, PLC
        // project count), so the resolver -- which runs on every sysmanager call -- does
        // not pay for what only list_projects displays.
        public List<ProjectInfo> ListProjects(bool detailed)
        {
            if (_dte == null) throw new BridgeException("DTE not acquired");
            var result = new List<ProjectInfo>();
            dynamic solution = _dte.Solution;
            if (solution == null || solution.Projects == null) return result;

            int count = 0;
            try { count = (int)solution.Projects.Count; }
            catch { count = 0; }
            for (int i = 1; i <= count; i++)
            {
                dynamic project = null;
                try { project = solution.Projects.Item(i); }
                catch { }
                CollectProject(project, result, detailed);
            }
            return result;
        }

        // One solution entry. Solution FOLDERS are projects too, and the real projects
        // then hang off their ProjectItems as SubProject -- a solution that groups the
        // library and its test suite in a folder would otherwise look empty.
        private void CollectProject(dynamic project, List<ProjectInfo> result, bool detailed)
        {
            if (project == null) return;

            string fullName = null;
            try { fullName = (string)project.FullName; }
            catch { }

            if (!string.IsNullOrWhiteSpace(fullName) &&
                fullName.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase))
            {
                var info = new ProjectInfo();
                info.FullName = fullName;
                info.Name = ComHelpers.SafeStr(delegate { return project.Name; });
                info.UniqueName = ComHelpers.SafeStr(delegate { return project.UniqueName; });
                try { info.SysManager = project.Object; }
                catch { info.SysManager = null; }
                info.IsCurrent = PathUtil.SamePath(_projectPath, fullName);

                if (detailed && info.SysManager != null)
                {
                    info.TargetNetId = ComHelpers.SafeStr(delegate { return info.SysManager.GetTargetNetId(); });
                    try
                    {
                        dynamic tipc = info.SysManager.LookupTreeItem("TIPC");
                        info.PlcProjectCount = ComHelpers.ChildCount(tipc);
                    }
                    catch { info.PlcProjectCount = 0; }
                }
                result.Add(info);
                return;
            }

            // Not a .tsproj: it may still be a solution folder holding some.
            dynamic items = null;
            try { items = project.ProjectItems; }
            catch { }
            if (items == null) return;
            int n = 0;
            try { n = (int)items.Count; }
            catch { n = 0; }
            for (int i = 1; i <= n; i++)
            {
                dynamic sub = null;
                try { sub = items.Item(i).SubProject; }
                catch { }
                if (sub != null) CollectProject(sub, result, detailed);
            }
        }

        // Which project of the solution to work on: by Name (the solution-explorer name,
        // or the .tsproj filename without extension) or by Path.
        public sealed class ProjectRequest
        {
            public string Name;
            public string Path;

            public bool NamesTarget
            {
                get { return !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Path); }
            }

            public string Describe()
            {
                if (!string.IsNullOrWhiteSpace(Path)) return "path '" + Path + "'";
                return "name '" + Name + "'";
            }
        }

        // One TwinCAT project of the solution, as reported to the caller.
        public sealed class ProjectInfo
        {
            public string Name;
            public string FullName;
            public string UniqueName;
            public string TargetNetId;
            public int PlcProjectCount;
            public bool IsCurrent;
            public dynamic SysManager;
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
