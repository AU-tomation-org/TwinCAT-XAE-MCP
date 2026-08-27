using System;

namespace Te1000Daemon
{
    // Per-call context handed to every action handler. Runs on the STA worker
    // thread. Provides the cached COM session, tree cache, payload accessors, and
    // the (progId, mode) resolved exactly as the PS bridge does (L3682-3683).
    public sealed class ActionContext
    {
        public readonly string Action;
        public readonly Json.JObj Payload;
        public readonly ComSession Session;
        public readonly TreeCache Cache;
        public readonly EditWatcher Edits;
        public readonly string ProgId;
        public readonly string Mode;

        public ActionContext(string action, Json.JObj payload, ComSession session, TreeCache cache, EditWatcher edits)
        {
            Action = action;
            Payload = payload ?? new Json.JObj();
            Session = session;
            Cache = cache;
            Edits = edits;
            ProgId = Payload.Truthy("progId") ? Payload.Str("progId") : "TcXaeShell.DTE.17.0";
            Mode = Payload.Truthy("mode") ? Payload.Str("mode") : "active";
        }

        // An explicit instance target from the payload, or null when the caller did not
        // name one. attachPid wins over attachSolution when both are present.
        public ComSession.InstanceRequest Attach
        {
            get
            {
                int pid = Payload.Has("attachPid") ? Payload.Int("attachPid", 0) : 0;
                string sln = Payload.Truthy("attachSolution") ? Payload.Str("attachSolution") : null;
                bool forceNew = Payload.Has("forceNew") && Payload.Bool("forceNew");
                if (pid <= 0 && string.IsNullOrWhiteSpace(sln) && !forceNew) return null;
                var t = new ComSession.InstanceRequest();
                t.Pid = pid;
                t.SolutionPath = sln;
                t.ForceNew = forceNew;
                return t;
            }
        }

        public dynamic Dte(bool visible = true) { return Session.GetDte(ProgId, Mode, visible, Attach); }

        // Attach to an IDE only if one is already running, whatever Mode says. For the
        // handlers whose whole job is to act on an existing IDE (shutdown), where the
        // "create" half of activeOrCreate would start the very thing being ended.
        // Returns null when no IDE is running.
        //
        // Still honours an explicit target: shutting down "the running IDE" and shutting
        // down "the IDE with pid N" are different requests, and dropping the target here
        // silently closed whichever instance the session happened to hold.
        public dynamic DteActiveOnly()
        {
            return Session.GetDte(ProgId, "active", true, Attach);
        }

        // Drop the cached DTE/sysmanager: the IDE they point at is gone or unusable, so
        // the next call has to reconnect instead of talking to a dead RCW.
        public void InvalidateSession()
        {
            Session.MarkStale();
            if (Cache != null) Cache.Clear();
        }

        public dynamic SysManager()
        {
            Session.GetDte(ProgId, Mode, true, Attach);
            return Session.GetSysManager();
        }

        // Standard success payload {ok:true, data:...} is assembled by the
        // dispatcher; handlers just return the `data` object.
        public static Json.JObj Ok(Json.JObj data) { return data; }

        // Require a payload key (mirrors PS `throw 'x is required'`).
        public string Require(string key)
        {
            var v = Payload.Str(key);
            if (string.IsNullOrWhiteSpace(v)) throw new BridgeException(key + " is required");
            return v;
        }

        public Json.JArr RequireArray(string key)
        {
            var a = Payload.Arr(key);
            if (a == null || a.Count == 0) throw new BridgeException(key + " is required");
            return a;
        }
    }
}
