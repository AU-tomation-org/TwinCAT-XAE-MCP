using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Te1000Daemon
{
    // Append-only audit trail of everything that reaches the dispatcher: one JSON line when
    // a call begins and one when it ends, joined by callId. The begin line is what makes a
    // call that never returns visible at all: a wedged IDE leaves a begin with no end.
    //
    // It never throws and never decides anything: a full disk must not stop the IDE from
    // being driven. Whether a failed write should instead refuse the call is a policy
    // decision for the day the trail is relied on as evidence, and it belongs in
    // IAccessPolicy, not here.
    //
    // Default folder %LOCALAPPDATA%\AUT\xae-mcp\audit, one file per UTC day; TE1000_AUDIT_DIR
    // overrides it. Nothing is ever deleted here: retention is the operator's to set.
    public sealed class AuditLog
    {
        private const int MaxInlineString = 200;

        // The value is replaced, not summarised: a hash of a short secret can be brute forced.
        private static readonly Regex SecretKey =
            new Regex("pass|pwd|secret|credential|apikey", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object _gate = new object();
        private readonly string _folder;

        public AuditLog(string folder = null)
        {
            _folder = folder
                ?? Environment.GetEnvironmentVariable("TE1000_AUDIT_DIR")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AUT", "xae-mcp", "audit");
            try { Directory.CreateDirectory(_folder); }
            catch (Exception ex) { Log.Error("AuditLog.ctor", ex); }
        }

        public string Folder { get { return _folder; } }

        public void Append(Json.JObj entry)
        {
            try
            {
                string line = Json.Write(entry) + "\n";
                string path = Path.Combine(_folder, "audit-" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
                lock (_gate) File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("AuditLog.Append", ex); }
        }

        // The newest lines first, walking back across day files until max is reached.
        public Json.JArr Read(int max)
        {
            var rows = new Json.JArr();
            try
            {
                foreach (string file in Directory.GetFiles(_folder, "audit-*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal))
                {
                    string[] lines;
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        lines = sr.ReadToEnd().Split('\n');

                    for (int i = lines.Length - 1; i >= 0 && rows.Count < max; i--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        try { rows.Add(Json.ParseObject(lines[i])); }
                        catch (Exception) { /* a torn last line must not hide the rest */ }
                    }
                    if (rows.Count >= max) break;
                }
            }
            catch (Exception ex) { Log.Error("AuditLog.Read", ex); }
            return rows;
        }

        // What the audit keeps of a payload: small values as they are, long text as length
        // plus hash, so a write of a POU can be matched to a file without copying the source
        // into the log. Secrets by key name are masked at every depth.
        public static Json.JObj Summarise(Json.JObj payload)
        {
            var o = new Json.JObj();
            if (payload == null) return o;
            foreach (KeyValuePair<string, object> kv in payload)
                o[kv.Key] = SecretKey.IsMatch(kv.Key) ? "***" : SummariseValue(kv.Value);
            return o;
        }

        private static object SummariseValue(object value)
        {
            string s = value as string;
            if (s != null) return s.Length <= MaxInlineString ? (object)s : LongText(s);

            var obj = value as Json.JObj;
            if (obj != null) return Summarise(obj);

            var arr = value as Json.JArr;
            if (arr != null)
            {
                var copy = new Json.JArr();
                foreach (object item in arr) copy.Add(SummariseValue(item));
                return copy;
            }
            return value;
        }

        private static Json.JObj LongText(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var o = new Json.JObj();
                o["length"] = s.Length;
                o["sha256"] = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return o;
            }
        }
    }
}
