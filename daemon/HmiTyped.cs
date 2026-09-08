using System;
using System.Collections.Generic;

namespace Te1000Daemon
{
    // Early-bound access to the TE2000 automation interfaces that IDispatch does not
    // deliver.
    //
    // Everything else on the HMI side is reached late-bound through `dynamic` on the
    // object dte.GetObject(<TE2000 ProgId>) returns, and that works for most of the
    // surface -- ITcHmiProject's own methods, ITcHmiMappedSymbol, the config fields.
    // Three interfaces do not come through:
    //
    //   ITcHmiInternalSymbol   creating one failed on all four late-bound routes
    //                          (measured 2026-09-08): the 0-arg factory gives
    //                          "Specified cast is not valid", the 5-arg one
    //                          "Missing parameter does not have a default value".
    //   ITcHmiPublishResult    GetPublishResult() is declared to return System.Object,
    //                          so late binding hands back an RCW whose Result /
    //                          IsCompleted / SubmissionId all read as null -- which is
    //                          how the first publish ever run from here reported
    //                          {available:true} and nothing else.
    //   ITcHmiFile             not reachable at all, so placing a control meant editing
    //                          HTML as text.
    //
    // The assembly is [ComVisible(true)] with a Guid per interface, so the fix is not a
    // workaround: cast the late-bound RCW to the typed interface and QueryInterface does
    // the rest. Same shape as the typed DTE2 cast for the error list and the typed
    // TCatSysManagerLib casts for the tree items -- when late binding cannot see a
    // member, ask for the interface by IID instead of hunting for another spelling.
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    internal static class HmiTyped
    {
        // A cast that fails says WHAT it was handed. A silent null here turns into
        // "creating an internal symbol is not supported", which would be a lie about the
        // product rather than a report about this call.
        private static string Describe(object o)
        {
            if (o == null) return "null";
            try { return o.GetType().FullName; }
            catch { return "<untypeable>"; }
        }

        public static TcHmiAutomation.ITcHmiProject AsProject(object o)
        {
            VsInterop.EnsureResolver();
            TcHmiAutomation.ITcHmiProject p = o as TcHmiAutomation.ITcHmiProject;
            if (p == null)
                throw new BridgeException("This object does not QI to TcHmiAutomation.ITcHmiProject " +
                                          "(runtime type " + Describe(o) + ").");
            return p;
        }

        public static TcHmiAutomation.ITcHmiAutomation AsAutomation(object o)
        {
            VsInterop.EnsureResolver();
            TcHmiAutomation.ITcHmiAutomation a = o as TcHmiAutomation.ITcHmiAutomation;
            if (a == null)
                throw new BridgeException("This object does not QI to TcHmiAutomation.ITcHmiAutomation " +
                                          "(runtime type " + Describe(o) + ").");
            return a;
        }

        // ---- publish result ------------------------------------------------

        // Result / IsCompleted / SubmissionId, or an explanation. Result is TE2000's own
        // code and 0 is its success value; it is reported raw rather than translated,
        // because inventing a verdict here would be exactly the kind of green that
        // hmi_publish already warns about.
        public static Json.JObj PublishResult(object raw)
        {
            var o = new Json.JObj();
            if (raw == null) { o["available"] = false; return o; }

            VsInterop.EnsureResolver();

            // Measured 2026-09-08: GetPublishResult is DECLARED to return System.Object
            // and on this TE2000 build it hands back a plain Boolean, not an
            // ITcHmiPublishResult at all -- which is the real reason the late-bound
            // Result / IsCompleted / SubmissionId reads all came back null. So the honest
            // answer is the bool, said plainly, rather than three fields the object does
            // not have. The typed path below stays for a build that does return the
            // interface.
            if (raw is bool)
            {
                o["available"] = true;
                o["shape"] = "boolean";
                o["ok"] = (bool)raw;
                o["note"] = "GetPublishResult answered a plain Boolean on this TE2000 build, not an " +
                            "ITcHmiPublishResult, so there is no Result code, IsCompleted or " +
                            "SubmissionId to report. It says the last publish call succeeded -- NOT " +
                            "that the server-extension configuration landed. Confirm that on the " +
                            "server storage: the RUNTIMES::*::NETID rows must be no older than " +
                            "PROJECTNAME.";
                return o;
            }

            TcHmiAutomation.Publish.ITcHmiPublishResult r =
                raw as TcHmiAutomation.Publish.ITcHmiPublishResult;
            if (r == null)
            {
                o["available"] = false;
                o["error"] = "The publish result is neither a Boolean nor something that QIs to " +
                             "TcHmiAutomation.Publish.ITcHmiPublishResult (runtime type " +
                             Describe(raw) + ").";
                return o;
            }
            o["shape"] = "ITcHmiPublishResult";

            o["available"] = true;
            try { o["result"] = r.Result; } catch (Exception ex) { o["resultError"] = ex.Message; }
            try { o["isCompleted"] = r.IsCompleted; } catch (Exception ex) { o["isCompletedError"] = ex.Message; }
            try { o["submissionId"] = r.SubmissionId; } catch (Exception ex) { o["submissionIdError"] = ex.Message; }
            return o;
        }

        // ---- internal symbols ----------------------------------------------

        public static TcHmiAutomation.ITcHmiInternalSymbol NewInternalSymbol(
            object project, string name, object value, string type, bool persist, bool readOnly)
        {
            TcHmiAutomation.ITcHmiProject p = AsProject(project);
            return p.GetInternalSymbolInstance(name, value, type, persist, readOnly);
        }

        public static void AddInternalSymbol(object project, TcHmiAutomation.ITcHmiInternalSymbol sym)
        {
            AsProject(project).AddInternalSymbol(sym);
        }

        public static Json.JObj ReadInternalSymbol(object project, string name)
        {
            TcHmiAutomation.ITcHmiInternalSymbol s = null;
            try { s = AsProject(project).GetInternalSymbol(name); }
            catch { }
            var o = new Json.JObj();
            o["found"] = s != null;
            if (s == null) return o;
            try { o["name"] = s.Name; } catch { }
            try { o["datatype"] = s.Datatype; } catch { }
            try { o["defaultValue"] = s.DefaultValue; } catch { }
            try { o["persist"] = s.Persist; } catch { }
            try { o["readOnly"] = s.IsReadonly; } catch { }
            return o;
        }

        // ---- files and controls --------------------------------------------

        // ITcHmiAutomation.GetHmiFile takes an EnvDTE.ProjectItem, which the existing
        // solution walk already has in hand -- but NOT the EnvDTE this daemon compiles
        // against. TcHmiAutomation was built against envdte 8.0.0.0 while we reference
        // the 17.0.0.0 facade from the VS2022 PublicAssemblies (MSB3247 says so at build
        // time), and both are present on the machine. Naming EnvDTE.ProjectItem in our
        // own code would pin the parameter to OUR version and hand the call a type the
        // callee does not recognise. So the argument is passed as a raw object through
        // reflection: the conversion from the RCW then happens in TcHmiAutomation's own
        // binding context, against whichever EnvDTE its signature actually means.
        public static TcHmiAutomation.ITcHmiFile GetHmiFile(object automation, object projectItem)
        {
            VsInterop.EnsureResolver();
            object a = AsAutomation(automation);

            System.Reflection.MethodInfo mi = null;
            foreach (System.Reflection.MethodInfo m in typeof(TcHmiAutomation.ITcHmiAutomation).GetMethods())
            {
                if (!string.Equals(m.Name, "GetHmiFile", StringComparison.Ordinal)) continue;
                if (m.GetParameters().Length != 1) continue;
                mi = m; break;
            }
            if (mi == null)
                throw new BridgeException("TcHmiAutomation.ITcHmiAutomation has no one-argument GetHmiFile " +
                                          "on this TE2000 build.");

            object raw;
            try { raw = mi.Invoke(a, new object[] { projectItem }); }
            catch (System.Reflection.TargetInvocationException ex)
            {
                Exception inner = ex.InnerException == null ? ex : ex.InnerException;
                throw new BridgeException("GetHmiFile failed: " + inner.Message);
            }

            TcHmiAutomation.ITcHmiFile f = raw as TcHmiAutomation.ITcHmiFile;
            if (raw != null && f == null)
                throw new BridgeException("GetHmiFile answered something that does not QI to " +
                                          "TcHmiAutomation.ITcHmiFile (runtime type " + Describe(raw) + ").");
            if (f == null)
                throw new BridgeException("GetHmiFile returned null for this project item. Only the " +
                                          "HMI files of an HMI project have one (a .view, .content or " +
                                          ".usercontrol).");
            return f;
        }

        public static Json.JArr Identifiers(TcHmiAutomation.ITcHmiFile file, string parent)
        {
            string[] ids;
            if (string.IsNullOrEmpty(parent)) ids = file.GetAllIdentifiers();
            else ids = file.GetChildIdentifiers(parent);

            var arr = new Json.JArr();
            if (ids != null) foreach (string s in ids) arr.Add(s);
            return arr;
        }

        // ITcHmiFile.GetControl answers null for every identifier on this TE2000 build,
        // including ones GetAllIdentifiers has just listed and ones AddControl has just
        // created, with the file open and IsOpenAndReady true (measured 2026-09-08). So
        // the project's own factory is tried as well: GetControlInstance(node, ctx) takes
        // the node as an Object, and the identifier is the only thing a caller out of
        // process has to offer. Both routes are reported, because which one answered is
        // the useful half of the result.
        public static TcHmiAutomation.ITcHmiControl FindControl(
            object project, TcHmiAutomation.ITcHmiFile file, string id, Json.JObj how)
        {
            TcHmiAutomation.ITcHmiControl c = null;
            try { c = file.GetControl(id); if (how != null) how["getControl"] = c != null; }
            catch (Exception ex) { if (how != null) how["getControlError"] = ex.Message; }
            if (c != null) { if (how != null) how["via"] = "ITcHmiFile.GetControl"; return c; }

            try
            {
                c = AsProject(project).GetControlInstance(id, file);
                if (how != null) how["getControlInstance"] = c != null;
            }
            catch (Exception ex) { if (how != null) how["getControlInstanceError"] = ex.Message; }
            if (c != null && how != null) how["via"] = "ITcHmiProject.GetControlInstance";
            return c;
        }

        public static Json.JArr Attributes(TcHmiAutomation.ITcHmiControl control)
        {
            var arr = new Json.JArr();
            TcHmiAutomation.ITcHmiControlAttribute[] attrs = null;
            try { attrs = control.Attributes; }
            catch { }
            if (attrs == null) return arr;
            foreach (TcHmiAutomation.ITcHmiControlAttribute a in attrs)
            {
                if (a == null) continue;
                var o = new Json.JObj();
                try { o["name"] = a.Name; } catch { }
                try { o["value"] = a.Value; } catch { }
                try { o["isComplex"] = a.IsComplex; } catch { }
                arr.Add(o);
            }
            return arr;
        }

        // The attribute objects have no public concrete class: they come from the
        // project's own factory, so a caller cannot hand us one and we build them here.
        public static TcHmiAutomation.ITcHmiControlAttribute[] BuildAttributes(object project, Json.JArr spec)
        {
            var list = new List<TcHmiAutomation.ITcHmiControlAttribute>();
            if (spec == null) return list.ToArray();
            TcHmiAutomation.ITcHmiProject p = AsProject(project);
            foreach (object o in spec)
            {
                Json.JObj row = o as Json.JObj;
                if (row == null) continue;
                string name = row.Str("name");
                if (string.IsNullOrWhiteSpace(name))
                    throw new BridgeException("every attribute needs a name");
                string value = row.Str("value");
                list.Add(p.GetControlAttributeInstance(name, value));
            }
            return list.ToArray();
        }
    }
}
