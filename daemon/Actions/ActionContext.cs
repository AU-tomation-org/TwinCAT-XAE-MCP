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

        public dynamic Dte(bool visible = true) { return Session.GetDte(ProgId, Mode, visible); }

        // Attach to an IDE only if one is already running, whatever Mode says. For the
        // handlers whose whole job is to act on an existing IDE (shutdown), where the
        // "create" half of activeOrCreate would start the very thing being ended.
        // Returns null when no IDE is running.
        public dynamic DteActiveOnly()
        {
            return Session.GetDte(ProgId, "active", true);
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
            Session.GetDte(ProgId, Mode, true);
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
