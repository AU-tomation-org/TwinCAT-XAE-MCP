using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Te1000Daemon
{
    // TwinCAT HMI (TE2000) action group.
    //
    // Reached from the SAME DTE as everything else (see ComSession.GetHmiAutomation),
    // so it lives on this daemon's single STA thread and shares its message filter and
    // dialog watcher. That is the whole architectural point: a separate MCP server for
    // HMI would attach a second automation client to one IDE and bring back the
    // orphaned-devenv and ROT-ambiguity problems.
    //
    // Two things measured here that the API docs do not say:
    //
    //  * METHOD calls dispatch late-bound. ITcHmiAutomation and its children answer
    //    through `dynamic` (unlike IMeasurementScope, which is vtable-only and needs
    //    the reflection shim in MeasurementActions). What comes back empty late-bound
    //    is a property read of the WRONG name: ITcHmiProject has no `Name`, the name
    //    lives on GetProjectInformation().ProjectName.
    //
    //  * ConfigFields has only 8 members (ActiveTheme, Locale, ScaleMode, StartupView,
    //    the three websocket timings, LoginPage). It CANNOT register a Function: the
    //    dependencyFiles / userFunctions lists of tchmiconfig.json are not reachable
    //    from ChangeConfig at all. hmi_function therefore edits that file directly --
    //    which is safe, because it is a file the project already declares (a file the
    //    .hmiproj does NOT declare simply does not exist for build or publish).
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    internal static class HmiActions
    {
        public static void Register(Dictionary<string, ActionHandler> h)
        {
            h["hmi_list_projects"] = ListProjectsAction;
            h["hmi_project_info"] = ProjectInfoAction;
            h["hmi_project_save"] = SaveAction;
            h["hmi_add_item"] = AddItemAction;
            h["hmi_config_get"] = ConfigGetAction;
            h["hmi_config_set"] = ConfigSetAction;
            h["hmi_build"] = BuildAction;
            h["hmi_symbol_list"] = SymbolListAction;
            h["hmi_symbol_map"] = SymbolMapAction;
            h["hmi_symbol_unmap"] = SymbolUnmapAction;
            h["hmi_symbol_internal_add"] = InternalSymbolAddAction;
            h["hmi_symbol_internal_remove"] = InternalSymbolRemoveAction;
            h["hmi_function_list"] = FunctionListAction;
            h["hmi_function_create"] = FunctionCreateAction;
            h["hmi_function_rename"] = FunctionRenameAction;
            h["hmi_file_identifiers"] = FileIdentifiersAction;
            h["hmi_file_source"] = FileSourceAction;
            h["hmi_file_set_source"] = FileSetSourceAction;
            h["hmi_file_control"] = FileControlAction;
            h["hmi_file_add_control"] = FileAddControlAction;
            h["hmi_file_change_attributes"] = FileChangeAttributesAction;
            h["hmi_file_remove_control"] = FileRemoveControlAction;
            h["hmi_file_beautify"] = FileBeautifyAction;
            h["hmi_publish_profiles"] = PublishProfilesAction;
            h["hmi_publish"] = PublishAction;
            h["hmi_publish_result"] = PublishResultAction;
        }

        public const string PublishConfirmation = "ALLOW_HMI_PUBLISH";

        // How long a publish is waited on before its result is reported as partial. The
        // upload of a real project (2131 files) takes seconds, not minutes, but a slow
        // target and a cold server extension start-up are both in the budget.
        private const int PublishWaitMs = 600000;

        private static bool IsPublishRunning(HmiTarget t)
        {
            object v = ComHelpers.Safe<object>(delegate { return t.Project.IsPublishRunning(); });
            return v is bool && (bool)v;
        }

        // ================= target resolution =================================

        private sealed class HmiTarget
        {
            public ComSession.ProjectInfo Info;
            public dynamic Project;        // ITcHmiProject
            public dynamic Automation;     // ITcHmiAutomation
            public string ResolvedBy;      // which GetHmiProject overload answered
            public bool Ambiguous;
            public int Candidates;
            public string ProjectDir;      // the project directory on disk

            public string HmiProjPath { get { return Info == null ? null : Info.FullName; } }
            public string ConfigPath { get { return Path.Combine(Path.Combine(ProjectDir, "Properties"), "tchmiconfig.json"); } }
        }

        // Which HMI project this call works on.
        //
        //   requireUnambiguous: for the verbs that WRITE. With several .hmiproj in the
        //   solution and none named, a write refuses and lists them rather than picking
        //   one -- same rule the .tsproj verbs follow for the actions that change the
        //   target. Reads take the first and say so (`hmiProjectAmbiguous`).
        private static HmiTarget Resolve(ActionContext ctx, bool requireUnambiguous)
        {
            ctx.Dte(true);
            dynamic hmi = ctx.Session.GetHmiAutomation();

            List<ComSession.ProjectInfo> projects = ctx.Session.ListHmiProjects();
            if (projects.Count == 0)
                throw new BridgeException(
                    "This solution has no TwinCAT HMI project (.hmiproj). Open a solution that " +
                    "contains one, or add it in the IDE.");

            ComSession.ProjectRequest request = HmiProjectRequest(ctx);
            var t = new HmiTarget();
            t.Automation = hmi;
            t.Candidates = projects.Count;

            if (request != null)
            {
                ComSession.ProjectInfo hit = ctx.Session.MatchHmiProject(projects, request);
                if (hit == null) throw new BridgeException(DescribeMissing(projects, request));
                t.Info = hit;
            }
            else if (projects.Count == 1)
            {
                t.Info = projects[0];
            }
            else
            {
                if (requireUnambiguous)
                {
                    var names = new List<string>();
                    foreach (ComSession.ProjectInfo p in projects) names.Add(p.Name + " -> " + p.FullName);
                    throw new BridgeException(
                        "This solution has " + projects.Count.ToString(CultureInfo.InvariantCulture) +
                        " HMI projects and none was chosen: " + string.Join("; ", names.ToArray()) +
                        ". This action writes, so it will not guess. Pass hmiProject (name or path).");
                }
                t.Info = projects[0];
                t.Ambiguous = true;
            }

            t.Project = OpenHmiProject(hmi, t.Info, out t.ResolvedBy);
            t.ProjectDir = ResolveProjectDirectory(t.Project, t.Info);
            return t;
        }

        // ITcHmiAutomation exposes GetHmiProject twice -- once taking the EnvDTE.Project
        // node, once taking a name. Overloads do not survive IDispatch under one name,
        // so which spelling actually answers is an environment fact, not a documented
        // one: try them in order and report the one that worked. Passing the DTE node is
        // preferred because it is unambiguous identity; the name is a fallback.
        private static dynamic OpenHmiProject(dynamic hmi, ComSession.ProjectInfo info, out string resolvedBy)
        {
            Exception last = null;

            if (info.DteProject != null)
            {
                try
                {
                    dynamic p = hmi.GetHmiProject(info.DteProject);
                    if (p != null) { resolvedBy = "GetHmiProject(EnvDTE.Project)"; return p; }
                }
                catch (Exception ex) { last = ex; }

                try
                {
                    dynamic p = hmi.GetHmiProject_2(info.DteProject);
                    if (p != null) { resolvedBy = "GetHmiProject_2(EnvDTE.Project)"; return p; }
                }
                catch (Exception ex) { last = ex; }
            }

            if (!string.IsNullOrWhiteSpace(info.Name))
            {
                try
                {
                    dynamic p = hmi.GetHmiProject(info.Name);
                    if (p != null) { resolvedBy = "GetHmiProject(name)"; return p; }
                }
                catch (Exception ex) { last = ex; }

                try
                {
                    dynamic p = hmi.GetHmiProject_2(info.Name);
                    if (p != null) { resolvedBy = "GetHmiProject_2(name)"; return p; }
                }
                catch (Exception ex) { last = ex; }
            }

            // Last resort: the array. Only safe when there is exactly one project, since
            // its order carries no identity we can check.
            try
            {
                dynamic arr = hmi.GetHmiProjects();
                int n = (int)arr.Length;
                if (n == 1) { resolvedBy = "GetHmiProjects()[0]"; return arr[0]; }
            }
            catch (Exception ex) { last = ex; }

            throw new BridgeException(
                "Could not open the HMI project '" + info.Name + "' through the automation object" +
                (last == null ? "." : ": " + last.Message));
        }

        private static string ResolveProjectDirectory(dynamic project, ComSession.ProjectInfo info)
        {
            string dir = null;
            try
            {
                dynamic pi = project.GetProjectInformation();
                if (pi != null) dir = ComHelpers.SafeStr(delegate { return pi.ProjectDirectory; });
            }
            catch { }
            if (string.IsNullOrWhiteSpace(dir))
            {
                try { dir = Path.GetDirectoryName(info.FullName); }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(dir)) dir = dir.TrimEnd('\\');
            return dir;
        }

        private static ComSession.ProjectRequest HmiProjectRequest(ActionContext ctx)
        {
            string v = ctx.Payload.Truthy("hmiProject") ? ctx.Payload.Str("hmiProject") : null;
            if (string.IsNullOrWhiteSpace(v)) return null;
            var r = new ComSession.ProjectRequest();
            if (v.IndexOf('\\') >= 0 || v.IndexOf('/') >= 0 ||
                v.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase))
                r.Path = v;
            else
                r.Name = v;
            return r;
        }

        private static string DescribeMissing(List<ComSession.ProjectInfo> projects, ComSession.ProjectRequest target)
        {
            var have = new List<string>();
            foreach (ComSession.ProjectInfo p in projects) have.Add(p.Name + " -> " + p.FullName);
            return "No HMI project in this solution matches " + target.Describe() +
                   ". Projects: " + string.Join("; ", have.ToArray()) +
                   ". Use hmi_project list to see them.";
        }

        // Stamp every response with which project it worked on, and whether that was a
        // guess -- the same discipline the .tsproj side follows.
        private static Json.JObj Stamp(HmiTarget t, Json.JObj data)
        {
            data["hmiProject"] = t.Info.Name;
            data["hmiProjectPath"] = t.Info.FullName;
            data["hmiProjectResolvedBy"] = t.ResolvedBy;
            if (t.Ambiguous)
            {
                data["hmiProjectAmbiguous"] = true;
                data["hmiProjectCandidates"] = t.Candidates;
                data["note"] = "This solution has " + t.Candidates.ToString(CultureInfo.InvariantCulture) +
                               " HMI projects and none was named; the first was used. Pass hmiProject to choose.";
            }
            return data;
        }

        private static void SaveIfRequested(ActionContext ctx, HmiTarget t, Json.JObj data, bool defaultSave)
        {
            bool save = ctx.Payload.Has("save") ? ctx.Payload.Bool("save") : defaultSave;
            data["saved"] = save;
            if (!save) return;
            try { t.Automation.SaveAllFiles(); }
            catch (Exception ex)
            {
                data["saved"] = false;
                data["saveError"] = ex.Message;
            }
        }

        // ================= H1: read-only ====================================

        private static Json.JObj ListProjectsAction(ActionContext ctx)
        {
            ctx.Dte(true);
            dynamic hmi = ctx.Session.GetHmiAutomation();
            List<ComSession.ProjectInfo> projects = ctx.Session.ListHmiProjects();

            var arr = new Json.JArr();
            foreach (ComSession.ProjectInfo p in projects)
            {
                var o = new Json.JObj();
                o["name"] = p.Name;
                o["fullName"] = p.FullName;
                o["uniqueName"] = p.UniqueName;
                string how;
                try
                {
                    dynamic hp = OpenHmiProject(hmi, p, out how);
                    o["resolvedBy"] = how;
                    o["isReady"] = ComHelpers.Safe<object>(delegate { return hp.IsReady(); });
                }
                catch (Exception ex) { o["error"] = ex.Message; }
                arr.Add(o);
            }

            var data = new Json.JObj();
            data["projects"] = arr;
            data["count"] = arr.Count;
            data["progId"] = ctx.Session.HmiProgId;
            data["progIdResolvedBy"] = ctx.Session.HmiProgIdHow;
            data["idePid"] = ComHelpers.SafeInt(delegate { return hmi.GetProcessId(); });
            return data;
        }

        private static Json.JObj ProjectInfoAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            var data = new Json.JObj();

            var info = new Json.JObj();
            try
            {
                dynamic pi = t.Project.GetProjectInformation();
                if (pi != null)
                {
                    info["projectName"] = ComHelpers.SafeStr(delegate { return pi.ProjectName; });
                    info["projectDirectory"] = ComHelpers.SafeStr(delegate { return pi.ProjectDirectory; });
                    info["projectGuid"] = ComHelpers.SafeStr(delegate { return pi.ProjectGuid; });
                    info["hasSccFlag"] = ComHelpers.Safe<object>(delegate { return pi.HasSccFlag; });
                }
            }
            catch (Exception ex) { info["error"] = ex.Message; }
            data["information"] = info;

            data["isReady"] = ComHelpers.Safe<object>(delegate { return t.Project.IsReady(); });
            data["isPublishRunning"] = ComHelpers.Safe<object>(delegate { return t.Project.IsPublishRunning(); });
            data["config"] = ReadAllConfigFields(t);
            data["progId"] = ctx.Session.HmiProgId;

            Json.JObj counts = ConfigCounts(t);
            if (counts != null) data["contents"] = counts;

            return Stamp(t, data);
        }

        private static Json.JObj SaveAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            t.Automation.SaveAllFiles();
            var data = new Json.JObj();
            data["saved"] = true;
            return Stamp(t, data);
        }

        // ================= config ===========================================

        // ConfigFields, by name. Passed to the COM call as the integer the enum holds:
        // the parameter is typed on the enum, and IDispatch coerces the int.
        private static readonly string[] ConfigFieldNames =
        {
            "ActiveTheme", "Locale", "ScaleMode", "StartupView",
            "WebsocketIntervalTime", "WebsocketTimeout", "WebsocketSystemTimeout", "LoginPage",
        };

        private static int ConfigFieldValue(string name)
        {
            for (int i = 0; i < ConfigFieldNames.Length; i++)
                if (string.Equals(ConfigFieldNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new BridgeException(
                "Unknown config field '" + name + "'. ConfigFields are: " +
                string.Join(", ", ConfigFieldNames) + ".");
        }

        // ScaleMode is an enum of its own (ConfigScaleMode), so it accepts either the
        // name or the number, and refuses anything else by listing what it knows.
        private static readonly string[] ScaleModeNames =
        {
            "None", "ScaleToFit", "ScaleToFitWidth", "ScaleToFitHeight", "ScaleToFill",
        };

        private static string ApplyConfigField(HmiTarget t, int field, string name, object value)
        {
            switch (field)
            {
                case 0: t.Project.ChangeTheme(AsString(name, value)); return "ChangeTheme";
                case 1: t.Project.ChangeLocale(AsString(name, value)); return "ChangeLocale";
                case 2: t.Project.ChangeScaleMode(AsScaleMode(value)); return "ChangeScaleMode";
                case 3: t.Project.ChangeStartupView(AsString(name, value)); return "ChangeStartupView";
                case 4: t.Project.ChangeWebsocketIntervalTime(AsInt(name, value)); return "ChangeWebsocketIntervalTime";
                case 5: t.Project.ChangeWebsocketTimeout(AsInt(name, value)); return "ChangeWebsocketTimeout";
                case 6: t.Project.ChangeWebsocketSystemTimeout(AsInt(name, value)); return "ChangeWebsocketSystemTimeout";
                case 7: t.Project.ChangeLoginPage(AsString(name, value)); return "ChangeLoginPage";
            }
            throw new BridgeException("No setter for config field '" + name + "'.");
        }

        private static string AsString(string field, object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            throw new BridgeException("'" + field + "' takes a string (got " + v.GetType().Name + ").");
        }

        private static int AsInt(string field, object v)
        {
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double)
            {
                double d = (double)v;
                if (d == Math.Floor(d)) return (int)d;
            }
            throw new BridgeException("'" + field + "' takes a whole number of milliseconds (got " +
                                      (v == null ? "null" : v.GetType().Name) + ").");
        }

        private static int AsScaleMode(object v)
        {
            string s = v as string;
            if (s != null)
            {
                for (int i = 0; i < ScaleModeNames.Length; i++)
                    if (string.Equals(ScaleModeNames[i], s, StringComparison.OrdinalIgnoreCase)) return i;
                throw new BridgeException("Unknown ScaleMode '" + s + "'. Use one of: " +
                                          string.Join(", ", ScaleModeNames) + ".");
            }
            int n = AsInt("ScaleMode", v);
            if (n < 0 || n >= ScaleModeNames.Length)
                throw new BridgeException("ScaleMode " + n.ToString(CultureInfo.InvariantCulture) +
                                          " is out of range; 0-4 are " + string.Join(", ", ScaleModeNames) + ".");
            return n;
        }

        private static Json.JObj ReadAllConfigFields(HmiTarget t)
        {
            var o = new Json.JObj();
            for (int i = 0; i < ConfigFieldNames.Length; i++)
            {
                int field = i;
                o[ConfigFieldNames[i]] = ComHelpers.Safe<object>(delegate { return t.Project.GetConfigValue(field); });
            }
            return o;
        }

        private static Json.JObj ConfigGetAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            var data = new Json.JObj();
            if (ctx.Payload.Truthy("field"))
            {
                string name = ctx.Payload.Str("field");
                int field = ConfigFieldValue(name);
                data["field"] = name;
                data["value"] = t.Project.GetConfigValue(field);
            }
            else
            {
                data["config"] = ReadAllConfigFields(t);
            }
            return Stamp(t, data);
        }

        private static Json.JObj ConfigSetAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string name = ctx.Require("field");
            int field = ConfigFieldValue(name);
            if (!ctx.Payload.Has("value")) throw new BridgeException("value is required");
            object value = ctx.Payload["value"];

            // NOT ChangeConfig(field, object): the generic setter takes the value as a
            // VARIANT and casts it to the field's own type inside, so a JSON number
            // arriving as a double dies with a bare "Specified cast is not valid" that
            // names neither the field nor the type. Each field has a typed setter of its
            // own; those take exactly what they need and say so.
            string how = ApplyConfigField(t, field, name, value);

            var data = new Json.JObj();
            data["field"] = name;
            data["requested"] = value;
            data["setVia"] = how;
            data["readback"] = ComHelpers.Safe<object>(delegate { return t.Project.GetConfigValue(field); });
            SaveIfRequested(ctx, t, data, true);
            return Stamp(t, data);
        }

        // ================= H2: items ========================================

        private static Json.JObj AddItemAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string kind = ctx.Require("kind").ToLowerInvariant();

            var data = new Json.JObj();
            data["kind"] = kind;

            string relPath = null;

            switch (kind)
            {
                case "view":
                case "usercontrol":
                case "content":
                    {
                        relPath = NormalizeRelative(ctx.Require("path"));
                        RequireExtension(kind, relPath);
                        dynamic file;
                        if (kind == "view") file = t.Project.AddView(relPath);
                        else if (kind == "usercontrol") file = t.Project.AddUserControl(relPath);
                        else file = t.Project.AddContent(relPath);
                        if (file == null)
                            throw new BridgeException(
                                "Add" + kind + " returned nothing for '" + relPath + "'. The path is " +
                                "project-relative and must carry the extension, e.g. " +
                                "UserControls\\Foo.usercontrol.");
                        data["path"] = relPath;
                        break;
                    }
                case "theme":
                    {
                        string name = ctx.Require("name");
                        t.Project.AddTheme(name);
                        data["name"] = name;
                        break;
                    }
                case "localization":
                    {
                        string name = ctx.Require("name");
                        string iso = ctx.Require("isoLanguage");
                        t.Project.AddLocalization(name, iso);
                        data["name"] = name;
                        data["isoLanguage"] = iso;
                        break;
                    }
                default:
                    throw new BridgeException(
                        "Unknown kind '" + kind + "'. Use view | usercontrol | content | theme | localization.");
            }

            SaveIfRequested(ctx, t, data, true);

            // The point of going through the automation object rather than writing the
            // file is that ONE add writes several registrations, and a hand-made file
            // gets one of them. Read them back and say which landed: a create that
            // reports success while the project entry is missing is the failure that
            // looks like a working file.
            if (relPath != null) data["registrations"] = VerifyRegistrations(t, relPath, kind);

            return Stamp(t, data);
        }

        private static void RequireExtension(string kind, string relPath)
        {
            string want = kind == "view" ? ".view" : (kind == "usercontrol" ? ".usercontrol" : ".content");
            if (!relPath.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException(
                    "A " + kind + " path must end in '" + want + "' (got '" + relPath + "').");
        }

        private static string NormalizeRelative(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) throw new BridgeException("path is required");
            string s = p.Trim().Replace('/', '\\').TrimStart('\\');
            if (Path.IsPathRooted(s))
                throw new BridgeException("path must be project-relative (got the absolute path '" + p + "').");
            return s;
        }

        private static Json.JObj VerifyRegistrations(HmiTarget t, string relPath, string kind)
        {
            var o = new Json.JObj();
            string url = relPath.Replace('\\', '/');

            o["hmiproj"] = FileContains(t.HmiProjPath, "Include=\"" + relPath + "\"");

            string cfg = ReadTextOrNull(t.ConfigPath);
            if (cfg == null) o["tchmiconfig"] = null;
            else o["tchmiconfig"] = cfg.IndexOf("\"" + url + "\"", StringComparison.OrdinalIgnoreCase) >= 0;

            if (kind == "usercontrol")
            {
                string schema = ReadTextOrNull(Path.Combine(
                    Path.Combine(t.ProjectDir, "Properties"), "tchmi.project.Schema.json"));
                string stem = Path.GetFileNameWithoutExtension(relPath);
                o["projectSchema"] = schema != null &&
                    schema.IndexOf("\"" + stem + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            bool complete = true;
            foreach (string k in o.Keys)
            {
                object v = o[k];
                if (!(v is bool) || !((bool)v)) complete = false;
            }
            o["complete"] = complete;
            if (!complete)
                o["warning"] = "At least one registration is missing on disk. Registrations land only " +
                               "after a save; if save was false, save and re-check before trusting this.";
            return o;
        }

        // ================= H5: build ========================================

        private static Json.JObj BuildAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            bool clean = ctx.Payload.Has("clean") && ctx.Payload.Bool("clean");
            bool updateUi = ctx.Payload.Has("updateUi") ? ctx.Payload.Bool("updateUi") : false;

            var data = new Json.JObj();
            if (clean)
            {
                data["action"] = "clean";
                data["ok"] = t.Project.Clean(updateUi);
            }
            else
            {
                string config = ctx.Payload.Truthy("configuration") ? ctx.Payload.Str("configuration") : "";
                data["action"] = "build";
                data["configuration"] = config;
                data["ok"] = t.Project.Build(config, updateUi);
            }
            // The measured caveat, carried on every response so nobody has to remember it:
            // an HMI build stays green with a misspelled binding attribute (measured
            // 2026-09-08: data-tchmi-ctrljsondata renamed to ...BROKEN, build green, Error
            // List empty). Same shape of false green as xae_build on a PLC library.
            data["verdictMeans"] = "The project packages. It does NOT mean the bindings are wired: " +
                                   "a misspelled binding attribute builds green with an empty Error List. " +
                                   "Use xae error_list for compiler rows, and the running HMI for bindings.";
            return Stamp(t, data);
        }

        // ================= H3: symbols ======================================

        private static dynamic ServerInterface(HmiTarget t)
        {
            dynamic srv = null;
            try { srv = t.Project.GetServerInterface(); }
            catch (Exception ex)
            {
                throw new BridgeException("GetServerInterface failed: " + ex.Message);
            }
            if (srv == null) throw new BridgeException("The HMI project returned no server interface.");
            return srv;
        }

        private static Json.JObj SymbolListAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            dynamic srv = ServerInterface(t);
            bool refresh = ctx.Payload.Has("refresh") && ctx.Payload.Bool("refresh");
            string filter = ctx.Payload.Truthy("filter") ? ctx.Payload.Str("filter") : null;
            int max = ctx.Payload.Has("maxResults") ? ctx.Payload.Int("maxResults", 500) : 500;
            if (max <= 0) max = 500;

            var arr = new Json.JArr();
            int total = 0;
            bool truncated = false;
            try
            {
                dynamic symbols = srv.GetMappedSymbols(refresh);
                int n = symbols == null ? 0 : (int)symbols.Length;
                for (int i = 0; i < n; i++)
                {
                    dynamic s = symbols[i];
                    string mapped = ComHelpers.SafeStr(delegate { return s.MappedName; });
                    if (filter != null && (mapped == null ||
                        mapped.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    total++;
                    if (arr.Count >= max) { truncated = true; continue; }
                    var o = new Json.JObj();
                    o["mappedName"] = mapped;
                    o["domain"] = ComHelpers.SafeStr(delegate { return s.Domain; });
                    o["type"] = ComHelpers.SafeStr(delegate { return s.Type; });
                    o["dataTypeDisplayName"] = ComHelpers.SafeStr(delegate { return s.DataTypeDisplayName; });
                    o["readOnly"] = ComHelpers.Safe<object>(delegate { return s.ReadOnly; });
                    o["hidden"] = ComHelpers.Safe<object>(delegate { return s.Hidden; });
                    arr.Add(o);
                }
            }
            catch (Exception ex)
            {
                throw new BridgeException("GetMappedSymbols failed: " + ex.Message);
            }

            var data = new Json.JObj();
            data["symbols"] = arr;
            data["returned"] = arr.Count;
            data["matched"] = total;
            if (truncated) data["truncated"] = true;
            data["note"] = "Mapped symbols are an EXPLICIT list (Server\\TcHmiSrv\\TcHmiSrv.Config.default.json), " +
                           "not a live discovery: a binding onto a symbol nobody mapped is null at run time and " +
                           "silent at build time.";
            return Stamp(t, data);
        }

        private static Json.JObj SymbolMapAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            dynamic srv = ServerInterface(t);
            string mapName = ctx.Require("mapName");
            string internalName = ctx.Require("internalName");
            string domain = ctx.Payload.Truthy("domain") ? ctx.Payload.Str("domain") : "ADS";
            string sub = ctx.Payload.Truthy("subSymbolName") ? ctx.Payload.Str("subSymbolName") : null;

            var data = new Json.JObj();
            data["mapName"] = mapName;
            data["internalName"] = internalName;
            data["domain"] = domain;

            bool ok;
            if (sub != null)
            {
                // ITcHmiServer3 adds the 4-argument form. Only that interface has it, so
                // a server that predates it fails here rather than silently ignoring the
                // sub-symbol.
                data["subSymbolName"] = sub;
                try { ok = (bool)srv.MapSymbol(mapName, internalName, sub, domain); }
                catch (Exception ex)
                {
                    throw new BridgeException(
                        "MapSymbol with a sub-symbol needs ITcHmiServer3: " + ex.Message);
                }
            }
            else
            {
                ok = (bool)srv.MapSymbol(mapName, internalName, domain);
            }
            data["ok"] = ok;
            SaveIfRequested(ctx, t, data, true);
            return Stamp(t, data);
        }

        private static Json.JObj SymbolUnmapAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            dynamic srv = ServerInterface(t);
            string mapName = ctx.Require("mapName");
            string domain = ctx.Payload.Truthy("domain") ? ctx.Payload.Str("domain") : "ADS";
            bool removeData = ctx.Payload.Has("removeHistorizedData") && ctx.Payload.Bool("removeHistorizedData");

            var data = new Json.JObj();
            data["mapName"] = mapName;
            data["domain"] = domain;
            data["removeHistorizedData"] = removeData;
            data["ok"] = (bool)srv.UnMapSymbol(mapName, domain, removeData);
            SaveIfRequested(ctx, t, data, true);
            return Stamp(t, data);
        }

        private static Json.JObj InternalSymbolAddAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string name = ctx.Require("name");
            string type = ctx.Require("type");
            object value = ctx.Payload.Has("value") ? ctx.Payload["value"] : null;
            bool persist = ctx.Payload.Has("persist") && ctx.Payload.Bool("persist");
            bool readOnly = ctx.Payload.Has("readOnly") && ctx.Payload.Bool("readOnly");

            // The typed route first: ITcHmiInternalSymbol does not marshal through
            // IDispatch (all four late-bound routes fail -- see below, and HmiTyped for
            // the measurement), so it is asked for by interface. The late-bound attempts
            // are kept underneath rather than deleted: they cost nothing when the typed
            // cast works, and if a future TE2000 changes the assembly identity under us
            // this verb degrades instead of disappearing.
            object typedSym = null;
            string typedError = null;
            try
            {
                typedSym = HmiTyped.NewInternalSymbol((object)t.Project, name, value, type, persist, readOnly);
            }
            catch (Exception ex) { typedError = ex.GetType().Name + ": " + ex.Message; }

            if (typedSym != null)
            {
                HmiTyped.AddInternalSymbol((object)t.Project, (TcHmiAutomation.ITcHmiInternalSymbol)typedSym);

                var typed = new Json.JObj();
                typed["name"] = name;
                typed["type"] = type;
                typed["persist"] = persist;
                typed["readOnly"] = readOnly;
                typed["instanceVia"] = "TcHmiAutomation.ITcHmiProject (early bound)";
                // The project is the authority, not the call that returned: read it back.
                Json.JObj typedBack = HmiTyped.ReadInternalSymbol((object)t.Project, name);
                typed["confirmed"] = typedBack.Bool("found");
                typed["readBack"] = typedBack;
                SaveIfRequested(ctx, t, typed, true);
                return Stamp(t, typed);
            }

            // GetInternalSymbolInstance is overloaded (0-arg and 5-arg). Only one of the
            // two keeps the plain name through IDispatch, and which one is not knowable
            // from the metadata -- measured here, the 5-arg form fails outright. So: try
            // the convenient form, then its mangled sibling, then build the instance from
            // the empty one and set its properties.
            dynamic sym = null;
            string how = null;
            try { sym = t.Project.GetInternalSymbolInstance(name, value, type, persist, readOnly); how = "GetInternalSymbolInstance(5)"; }
            catch { sym = null; }
            if (sym == null)
            {
                try { sym = t.Project.GetInternalSymbolInstance_2(name, value, type, persist, readOnly); how = "GetInternalSymbolInstance_2(5)"; }
                catch { sym = null; }
            }
            if (sym == null)
            {
                dynamic empty = null;
                try { empty = t.Project.GetInternalSymbolInstance(); }
                catch { empty = null; }
                if (empty == null)
                {
                    try { empty = t.Project.GetInternalSymbolInstance_2(); }
                    catch { empty = null; }
                }
                if (empty != null)
                {
                    empty.Name = name;
                    empty.Datatype = type;
                    empty.DefaultValue = value;
                    empty.Persist = persist;
                    empty.IsReadonly = readOnly;
                    sym = empty;
                    how = "GetInternalSymbolInstance() + property writes";
                }
            }
            if (sym == null)
                throw new BridgeException(
                    "Creating an internal symbol failed on every route. The early-bound one, which is " +
                    "the one that normally works, said: " + (typedError == null ? "(not attempted)" : typedError) +
                    ". The late-bound fallbacks fail as they always have (measured 2026-09-08): " +
                    "GetInternalSymbolInstance() gives 'Specified cast is not valid', " +
                    "GetInternalSymbolInstance_2() gives 'Missing parameter does not have a default value' " +
                    "(so _2 is the 5-argument overload), and the 5-argument call on either name fails the " +
                    "cast -- ITcHmiInternalSymbol does not marshal through IDispatch the way " +
                    "ITcHmiMappedSymbol does. If the early-bound error mentions loading TcHmiAutomation, " +
                    "point TE2000_ASSEMBLY_DIR at the directory holding TcHmiAutomation.dll.");
            t.Project.AddInternalSymbol(sym);

            var data = new Json.JObj();
            data["name"] = name;
            data["type"] = type;
            data["persist"] = persist;
            data["readOnly"] = readOnly;
            data["instanceVia"] = how;
            // The tree is the authority, not the call that returned: read it back.
            dynamic back = null;
            try { back = t.Project.GetInternalSymbol(name); }
            catch { }
            data["confirmed"] = back != null;
            SaveIfRequested(ctx, t, data, true);
            return Stamp(t, data);
        }

        private static Json.JObj InternalSymbolRemoveAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string name = ctx.Require("name");

            var data = new Json.JObj();
            data["name"] = name;

            // RemoveInternalSymbol(name) answers FALSE even on a symbol that is there and
            // gets removed (measured 2026-09-08), so its return value is a fact about the
            // call, not the verdict. RemoveInternalSymbol is overloaded on the symbol
            // OBJECT too, so if the by-name call leaves it behind, fetch it and remove
            // that -- and either way the answer comes from reading the project back.
            bool existed = HmiTyped.ReadInternalSymbol((object)t.Project, name).Bool("found");
            data["existed"] = existed;
            if (!existed)
                throw new BridgeException("There is no internal symbol named '" + name + "' in this " +
                                          "project, so nothing was removed.");

            object byName = ComHelpers.Safe<object>(delegate { return t.Project.RemoveInternalSymbol(name); });
            data["removeByNameReturned"] = byName;

            if (HmiTyped.ReadInternalSymbol((object)t.Project, name).Bool("found"))
            {
                try
                {
                    TcHmiAutomation.ITcHmiInternalSymbol sym =
                        HmiTyped.AsProject((object)t.Project).GetInternalSymbol(name);
                    if (sym != null)
                        data["removeByObjectReturned"] =
                            HmiTyped.AsProject((object)t.Project).RemoveInternalSymbol(sym);
                }
                catch (Exception ex) { data["removeByObjectError"] = ex.Message; }
            }

            bool gone = !HmiTyped.ReadInternalSymbol((object)t.Project, name).Bool("found");
            data["ok"] = gone;
            data["confirmed"] = gone;
            if (!gone)
                data["warning"] = "The symbol is still in the project after both remove overloads.";
            SaveIfRequested(ctx, t, data, true);
            return Stamp(t, data);
        }

        // ================= H4: functions ====================================
        //
        // There is NO AddFunction on ITcHmiProject: views, user controls, themes,
        // localizations, content and NuGet packages have first-class verbs, Functions do
        // not. And ChangeConfig cannot help -- ConfigFields has 8 members, none of them
        // the dependencyFiles / userFunctions lists. So a Function is assembled here.
        //
        // Its name lives in NINE places, and the IDE's own rename aligns only some of
        // them (and deletes module-scope variables declared outside the function body):
        //
        //   1  Functions\<N>.ts                       file name
        //   2  Functions\<N>.function.json            file name
        //   3  function <N>(...)                      in the .ts
        //   4  registerFunctionEx('<N>', ...)         in the .ts
        //   5  function.name                          in the .function.json
        //   6  function.displayName                   in the .function.json
        //   7  dependencyFiles[0].name = <N>.js       in the .function.json
        //   8  dependencyFiles[].name = Functions/<N>.js  in tchmiconfig.json
        //   9  userFunctions[].url   = Functions/<N>.js   in tchmiconfig.json
        //
        // plus the two <Content Include> entries in the .hmiproj. That is the whole
        // argument for the verb: it writes all of them by construction.

        private const string FunctionsFolder = "Functions";

        private static Json.JObj FunctionListAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            var arr = new Json.JArr();
            string dir = Path.Combine(t.ProjectDir, FunctionsFolder);
            if (Directory.Exists(dir))
            {
                string[] files = Directory.GetFiles(dir, "*.function.json", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                string cfg = ReadTextOrNull(t.ConfigPath);
                foreach (string f in files)
                {
                    string stem = Path.GetFileName(f);
                    stem = stem.Substring(0, stem.Length - ".function.json".Length);
                    var o = new Json.JObj();
                    o["name"] = stem;
                    o["descriptor"] = "Functions/" + stem + ".function.json";
                    o["source"] = File.Exists(Path.Combine(dir, stem + ".ts")) ? "Functions/" + stem + ".ts" : null;
                    string url = "Functions/" + stem + ".js";
                    o["registeredInConfig"] = cfg != null &&
                        cfg.IndexOf("\"" + url + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    arr.Add(o);
                }
            }
            var data = new Json.JObj();
            data["functions"] = arr;
            data["count"] = arr.Count;
            return Stamp(t, data);
        }

        private static Json.JObj FunctionCreateAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string name = ctx.Require("name");
            ValidateFunctionName(name);

            string dir = Path.Combine(t.ProjectDir, FunctionsFolder);
            string tsPath = Path.Combine(dir, name + ".ts");
            string jsonPath = Path.Combine(dir, name + ".function.json");
            if (File.Exists(tsPath) || File.Exists(jsonPath))
                throw new BridgeException("A function named '" + name + "' already exists in this project.");

            string ns = ctx.Payload.Truthy("namespace")
                ? ctx.Payload.Str("namespace")
                : (SniffNamespace(dir) ?? "TcHmi.Functions.HMI");
            string returnType = ctx.Payload.Truthy("returnType")
                ? ctx.Payload.Str("returnType")
                : "tchmi:general#/definitions/Any";
            string description = ctx.Payload.Truthy("description") ? ctx.Payload.Str("description") : "";
            Json.JArr args = ctx.Payload.Arr("arguments");

            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string schemaRef = SniffSchemaRef(dir);
            string descriptor = ctx.Payload.Truthy("descriptorJson")
                ? ctx.Payload.Str("descriptorJson")
                : BuildDescriptor(name, ns, returnType, description, args, schemaRef);
            string source = ctx.Payload.Truthy("sourceText")
                ? ctx.Payload.Str("sourceText")
                : BuildTypeScript(name, ns, returnType, args);

            WriteProjectText(jsonPath, descriptor);
            WriteProjectText(tsPath, source);

            // Declare the SOURCE only, in the collection of the folder it lives in.
            //
            // Two measurements shape this:
            //
            //  * Not project.ProjectItems.AddFromFile: that collection is the project
            //    ROOT, and the HMI project system answers it by COPYING the file up to
            //    the root and declaring the copy -- and then raises a modal "a file with
            //    the same name already exists, overwrite?" on the second file.
            //  * Adding the .ts is enough. The project system sees a function source,
            //    pulls the sibling .function.json into the project ITSELF (nesting it
            //    with DependentUpon) and writes both tchmiconfig registrations. Adding
            //    the descriptor explicitly on top of that produces a
            //    'X - Copy.function.json' -- a second declared descriptor for one
            //    function, which is exactly the silent mess this verb exists to avoid.
            var added = new Json.JArr();
            dynamic folder = ResolveFolderItems(ctx, t, dir);
            added.Add(AddExistingFile(folder, tsPath));

            // Saving here, before reading anything back: the project system writes its
            // registrations as part of accepting the item, and they reach disk on save.
            t.Automation.SaveAllFiles();

            // ...but only trust that it happened after looking. If the descriptor was not
            // picked up, declare it -- the fallback, not the normal path.
            var descriptorEntry = new Json.JObj();
            bool descriptorDeclared = FileContains(t.HmiProjPath,
                "Include=\"" + FunctionsFolder + "\\" + name + ".function.json\"");
            descriptorEntry["declaredByProjectSystem"] = descriptorDeclared;
            if (!descriptorDeclared)
            {
                descriptorEntry["addedExplicitly"] = AddExistingFile(folder, jsonPath);
                t.Automation.SaveAllFiles();
            }
            descriptorEntry["declared"] = FileContains(t.HmiProjPath,
                "Include=\"" + FunctionsFolder + "\\" + name + ".function.json\"");
            added.Add(descriptorEntry);

            // The project system registers a .function.json ITSELF, in both
            // dependencyFiles and userFunctions, with the url relative to the project.
            // So the job here is to CHECK, not to write -- writing unconditionally is how
            // the first version produced two entries per function. The manual append
            // stays as the fallback for the case where it did not.
            string url = FunctionsFolder + "/" + name + ".js";
            var registered = new Json.JObj();
            bool byProjectSystem = ConfigHasUrl(t.ConfigPath, url);
            registered["writtenByProjectSystem"] = byProjectSystem;
            if (!byProjectSystem)
            {
                registered["dependencyFiles"] = ConfigAppendDependencyFile(t.ConfigPath, url);
                registered["userFunctions"] = ConfigAppendUserFunction(t.ConfigPath, url);
            }
            registered["present"] = ConfigHasUrl(t.ConfigPath, url);

            var data = new Json.JObj();
            data["name"] = name;
            data["namespace"] = ns;
            data["files"] = added;
            data["configRegistrations"] = registered;
            data["url"] = url;
            data["saved"] = true;
            data["note"] = "dependencyFiles[].type is EsModule, which is what an ES-module .ts compiles to. " +
                           "A namespace-style .js needs JavaScript instead: the wizard writes EsModule either " +
                           "way, which compiles clean and throws ReferenceError at run time.";
            return Stamp(t, data);
        }

        private static Json.JObj FunctionRenameAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string oldName = ctx.Require("name");
            string newName = ctx.Require("newName");
            ValidateFunctionName(newName);
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                throw new BridgeException("newName is the same as name.");

            string dir = Path.Combine(t.ProjectDir, FunctionsFolder);
            string oldTs = Path.Combine(dir, oldName + ".ts");
            string oldJson = Path.Combine(dir, oldName + ".function.json");
            if (!File.Exists(oldJson))
                throw new BridgeException("No function '" + oldName + "' in this project (looked for " + oldJson + ").");
            if (File.Exists(Path.Combine(dir, newName + ".function.json")))
                throw new BridgeException("A function named '" + newName + "' already exists.");

            // Rename through the project item, not through File.Move: that is what keeps
            // the .hmiproj entry in step. Renaming the file behind the IDE's back leaves
            // a <Content Include> pointing at a file that is gone.
            //
            // ORDER MATTERS, and getting it wrong is silent. The .function.json is
            // declared <DependentUpon> the .ts, and the project system cascades a rename
            // from the PARENT to its dependent. Rename the .ts and the descriptor follows
            // correctly; rename the descriptor first and the cascade renames the source
            // to '<new>.function.json.ts' -- measured -- and writes that stem into
            // tchmiconfig too.
            var renamed = new Json.JArr();
            if (File.Exists(oldTs)) renamed.Add(RenameProjectItem(ctx, oldTs, newName + ".ts"));
            t.Automation.SaveAllFiles();

            // Only rename the descriptor if the cascade did not already do it.
            string cascaded = Path.Combine(dir, newName + ".function.json");
            if (!File.Exists(cascaded) && File.Exists(oldJson))
                renamed.Add(RenameProjectItem(ctx, oldJson, newName + ".function.json"));
            else
            {
                var o = new Json.JObj();
                o["from"] = oldJson;
                o["to"] = newName + ".function.json";
                o["renamed"] = File.Exists(cascaded);
                o["byCascade"] = true;
                renamed.Add(o);
            }

            t.Automation.SaveAllFiles();

            string newTs = Path.Combine(dir, newName + ".ts");
            string newJson = Path.Combine(dir, newName + ".function.json");

            // The IDE's own rename aligns only part of a function's identity, and it
            // deletes module-scope variables declared outside the function body. Renaming
            // the FILE through the project (above) is the half that keeps the .hmiproj
            // straight; the identity inside the two files is rewritten here, as whole
            // identifiers only, so renaming Foo does not turn FooBar into BarBar.
            var rewritten = new Json.JObj();
            rewritten["descriptor"] = RewriteIdentity(newJson, oldName, newName);
            if (File.Exists(newTs)) rewritten["source"] = RewriteIdentity(newTs, oldName, newName);

            string oldUrl = FunctionsFolder + "/" + oldName + ".js";
            string newUrl = FunctionsFolder + "/" + newName + ".js";
            var cfg = new Json.JObj();
            cfg["newUrlPresent"] = ConfigHasUrl(t.ConfigPath, newUrl);
            cfg["oldUrlStillThere"] = ConfigHasUrl(t.ConfigPath, oldUrl);
            // Same rule as create: check first, write only what the project system left
            // behind. It is the config that decides whether the function loads at all.
            if (ConfigHasUrl(t.ConfigPath, oldUrl))
                cfg["replacements"] = ReplaceInFile(t.ConfigPath, "\"" + oldUrl + "\"", "\"" + newUrl + "\"");
            cfg["present"] = ConfigHasUrl(t.ConfigPath, newUrl);
            // The two arrays are written by two different hands (the project system and,
            // when it fell short, this verb), so the honest check is that NO trace of the
            // old name is left anywhere in the config.
            // The trailing dot matters: without it, renaming Fn to FnTwo reports the new
            // url 'Functions/FnTwo.js' as a leftover of the old one, and a false alarm on
            // a correct rename is worse than no check at all.
            string cfgText = ReadTextOrNull(t.ConfigPath);
            bool residue = cfgText != null &&
                cfgText.IndexOf(FunctionsFolder + "/" + oldName + ".", StringComparison.OrdinalIgnoreCase) >= 0;
            cfg["oldNameResidue"] = residue;
            if (residue)
                cfg["warning"] = "tchmiconfig.json still mentions '" + FunctionsFolder + "/" + oldName +
                                 "'. Check dependencyFiles and userFunctions by hand before building.";

            var data = new Json.JObj();
            data["name"] = oldName;
            data["newName"] = newName;
            data["renamedFiles"] = renamed;
            data["rewritten"] = rewritten;
            data["tchmiconfig"] = cfg;
            data["saved"] = true;
            data["note"] = "Every binding and event that CALLS this function still names the old one: " +
                           "this verb renames the definition, not its call sites.";
            t.Automation.SaveAllFiles();
            return Stamp(t, data);
        }

        private static void ValidateFunctionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("name is required");
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = char.IsLetterOrDigit(c) || c == '_';
                if (i == 0) ok = char.IsLetter(c) || c == '_';
                if (!ok)
                    throw new BridgeException(
                        "'" + name + "' is not usable as a function name: it becomes a JavaScript identifier " +
                        "and a file name, so it must start with a letter or '_' and hold only letters, " +
                        "digits and '_'.");
            }
        }

        // The namespace and the $schema of an existing descriptor, so a new function
        // matches the project it lands in instead of a guessed default.
        private static string SniffNamespace(string functionsDir)
        {
            return SniffDescriptorString(functionsDir, "\"namespace\"");
        }

        private static string SniffSchemaRef(string functionsDir)
        {
            return SniffDescriptorString(functionsDir, "\"$schema\"");
        }

        private static string SniffDescriptorString(string functionsDir, string key)
        {
            if (!Directory.Exists(functionsDir)) return null;
            string[] files;
            try { files = Directory.GetFiles(functionsDir, "*.function.json", SearchOption.TopDirectoryOnly); }
            catch { return null; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string text = ReadTextOrNull(f);
                if (text == null) continue;
                int k = text.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) continue;
                int colon = text.IndexOf(':', k);
                if (colon < 0) continue;
                int q1 = text.IndexOf('"', colon);
                if (q1 < 0) continue;
                int q2 = text.IndexOf('"', q1 + 1);
                if (q2 < 0) continue;
                string v = text.Substring(q1 + 1, q2 - q1 - 1);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static string BuildDescriptor(string name, string ns, string returnType,
                                              string description, Json.JArr args, string schemaRef)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            if (!string.IsNullOrWhiteSpace(schemaRef))
                sb.Append("  \"$schema\": ").Append(Json.Write(schemaRef)).Append(",\r\n");
            sb.Append("  \"apiVersion\": 1,\r\n");
            sb.Append("  \"version\": {\r\n");
            sb.Append("    \"full\": \"0.0.0.0\",\r\n");
            sb.Append("    \"major\": 0,\r\n");
            sb.Append("    \"minor\": 0,\r\n");
            sb.Append("    \"revision\": 0,\r\n");
            sb.Append("    \"build\": 0\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"dependencyFiles\": [\r\n");
            sb.Append("    {\r\n");
            sb.Append("      \"name\": ").Append(Json.Write(name + ".js")).Append(",\r\n");
            sb.Append("      \"type\": \"EsModule\",\r\n");
            sb.Append("      \"description\": \"\"\r\n");
            sb.Append("    }\r\n");
            sb.Append("  ],\r\n");
            sb.Append("  \"function\": {\r\n");
            sb.Append("    \"name\": ").Append(Json.Write(name)).Append(",\r\n");
            sb.Append("    \"namespace\": ").Append(Json.Write(ns)).Append(",\r\n");
            sb.Append("    \"displayName\": ").Append(Json.Write(name)).Append(",\r\n");
            sb.Append("    \"description\": ").Append(Json.Write(description ?? "")).Append(",\r\n");
            sb.Append("    \"waitMode\": \"Synchronous\",\r\n");
            sb.Append("    \"category\": \"\",\r\n");
            sb.Append("    \"returnValue\": {\r\n");
            sb.Append("      \"type\": ").Append(Json.Write(returnType)).Append("\r\n");
            sb.Append("    },\r\n");
            sb.Append("    \"arguments\": [");
            if (args != null && args.Count > 0)
            {
                sb.Append("\r\n");
                for (int i = 0; i < args.Count; i++)
                {
                    var a = args[i] as Json.JObj;
                    if (a == null) throw new BridgeException("arguments[] entries must be objects.");
                    string an = a.Str("name");
                    if (string.IsNullOrWhiteSpace(an)) throw new BridgeException("every argument needs a name.");
                    string at = a.Truthy("type") ? a.Str("type") : "tchmi:general#/definitions/Any";
                    string ad = a.Truthy("description") ? a.Str("description") : "";
                    bool req = a.Has("required") ? a.Bool("required", true) : true;
                    bool bind = a.Has("bindable") ? a.Bool("bindable", true) : true;
                    sb.Append("      {\r\n");
                    sb.Append("        \"name\": ").Append(Json.Write(an)).Append(",\r\n");
                    sb.Append("        \"displayName\": ").Append(Json.Write(
                        a.Truthy("displayName") ? a.Str("displayName") : an)).Append(",\r\n");
                    sb.Append("        \"type\": ").Append(Json.Write(at)).Append(",\r\n");
                    sb.Append("        \"description\": ").Append(Json.Write(ad)).Append(",\r\n");
                    sb.Append("        \"defaultValue\": null,\r\n");
                    sb.Append("        \"required\": ").Append(req ? "true" : "false").Append(",\r\n");
                    sb.Append("        \"bindable\": ").Append(bind ? "true" : "false").Append("\r\n");
                    sb.Append("      }").Append(i == args.Count - 1 ? "\r\n" : ",\r\n");
                }
                sb.Append("    ");
            }
            sb.Append("]\r\n");
            sb.Append("  }\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static string BuildTypeScript(string name, string ns, string returnType, Json.JArr args)
        {
            var sb = new StringBuilder();
            sb.Append("import { Functions } from 'Beckhoff.TwinCAT.HMI.Framework/index.esm.js';\r\n");
            sb.Append("\r\n");
            sb.Append("function ").Append(name).Append("(");
            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    var a = args[i] as Json.JObj;
                    if (a == null) continue;
                    if (i > 0) sb.Append(", ");
                    sb.Append(a.Str("name")).Append(": ").Append(TsTypeOf(a.Truthy("type") ? a.Str("type") : null));
                }
            }
            sb.Append("): ").Append(TsTypeOf(returnType)).Append(" {\r\n");
            sb.Append("\t// TODO: implement\r\n");
            sb.Append("\treturn ").Append(TsDefaultOf(returnType)).Append(";\r\n");
            sb.Append("}\r\n");
            sb.Append("\r\n");
            sb.Append("Functions.registerFunctionEx('").Append(name).Append("', '").Append(ns)
              .Append("', ").Append(name).Append(");\r\n");
            return sb.ToString();
        }

        // The tchmi schema $refs map onto a handful of TS types. Anything unrecognised
        // becomes `any`, which compiles and leaves the author to tighten it.
        private static string TsTypeOf(string schemaRef)
        {
            if (string.IsNullOrWhiteSpace(schemaRef)) return "any";
            string s = schemaRef.ToLowerInvariant();
            if (s.EndsWith("/boolean")) return "boolean";
            if (s.EndsWith("/string")) return "string";
            if (s.EndsWith("/number") || s.EndsWith("/dint") || s.EndsWith("/int") ||
                s.EndsWith("/udint") || s.EndsWith("/uint") || s.EndsWith("/lreal") ||
                s.EndsWith("/real")) return "number";
            return "any";
        }

        private static string TsDefaultOf(string schemaRef)
        {
            string t = TsTypeOf(schemaRef);
            if (t == "boolean") return "false";
            if (t == "string") return "''";
            if (t == "number") return "0";
            return "null";
        }

        // ================= H6: publish ======================================

        private static string PublishConfigPath(HmiTarget t)
        {
            return Path.Combine(Path.Combine(t.ProjectDir, "Properties"), "tchmipublish.config.json");
        }

        private static Json.JObj PublishProfilesAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            var data = new Json.JObj();
            data["configPath"] = PublishConfigPath(t);
            data["profiles"] = ReadPublishProfiles(t);
            return Stamp(t, data);
        }

        private static Json.JArr ReadPublishProfiles(HmiTarget t)
        {
            var arr = new Json.JArr();
            string text = ReadTextOrNull(PublishConfigPath(t));
            if (text == null) return arr;
            object parsed;
            try { parsed = Json.Parse(text); }
            catch (Exception ex) { throw new BridgeException("Cannot parse the publish profiles: " + ex.Message); }
            var list = parsed as Json.JArr;
            if (list == null) { var one = parsed as Json.JObj; if (one != null) { list = new Json.JArr(); list.Add(one); } }
            if (list == null) return arr;

            foreach (object o in list)
            {
                var p = o as Json.JObj;
                if (p == null) continue;
                var e = new Json.JObj();
                e["profileName"] = p.Str("profileName");
                e["publishMode"] = p.Str("publishMode");
                e["destinationUrl"] = p.Str("tcHmiDestinationUrl");
                e["publishConfiguration"] = p.Str("publishConfiguration");
                Json.JArr ext = p.Arr("serverExtensions");
                int extCount = ext == null ? 0 : ext.Count;
                e["serverExtensionCount"] = extCount;
                e["pushesServerExtensionConfig"] = extCount == 0;
                if (extCount != 0)
                    e["warning"] = ServerExtensionsWarning;
                arr.Add(e);
            }
            return arr;
        }

        // The trap that costs a whole afternoon: with the extensions listed (which is
        // what the IDE's publish dialog writes), the publish SUCCEEDS, uploads the
        // project, and does not push the server-extension configuration -- the ADS
        // runtimes block stays at the server's own default and every symbol is dead.
        // With [] the same publish writes runtimes and symbols. Measured on the server
        // storage: PROJECTNAME written at 18:33 with the ADS rows still stamped 18:23
        // (instance creation), and one second apart after the fix.
        private const string ServerExtensionsWarning =
            "serverExtensions is NOT empty in this profile. A publish with it populated succeeds, " +
            "uploads the project, and silently does NOT push the server extension configuration: the " +
            "ADS runtimes block stays at the server default and every symbol is null. Set " +
            "\"serverExtensions\": [] in Properties\\tchmipublish.config.json.";

        private static Json.JObj PublishAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string profile = ctx.Require("profile");
            bool updateUi = ctx.Payload.Has("updateUi") ? ctx.Payload.Bool("updateUi") : false;
            bool force = ctx.Payload.Has("force") && ctx.Payload.Bool("force");

            var data = new Json.JObj();
            data["profile"] = profile;

            bool valid;
            try { valid = (bool)t.Project.IsValidPublishProfile(profile); }
            catch (Exception ex) { throw new BridgeException("IsValidPublishProfile failed: " + ex.Message); }
            data["profileValid"] = valid;
            if (!valid)
                throw new BridgeException(
                    "'" + profile + "' is not a valid publish profile for this project. " +
                    "Use hmi_publish profiles to list them.");

            // Pre-flight rather than post-mortem. The publish result cannot tell you the
            // extension config was skipped (ITcHmiPublishResult carries an int Result, an
            // IsCompleted and a SubmissionId -- measured by reflection, no per-extension
            // verdict), so the only honest place to catch this is before the call.
            Json.JArr profiles = ReadPublishProfiles(t);
            foreach (object o in profiles)
            {
                var p = o as Json.JObj;
                if (p == null) continue;
                if (!string.Equals(p.Str("profileName"), profile, StringComparison.OrdinalIgnoreCase)) continue;
                data["profileCheck"] = p;
                if (p.Has("warning") && !force)
                    throw new BridgeException(
                        ServerExtensionsWarning + " Re-run with force:true to publish anyway.");
            }

            if (IsPublishRunning(t))
                throw new BridgeException("A publish is already running on this project.");

            // Publish(profileName, updateUi = opt, progressCallback = opt). The callback is
            // OPTIONAL AND TYPED TO AN INTERFACE, and a C# null reaches IDispatch as
            // VT_EMPTY, which the callee cannot convert to ITcHmiPublishCallback: passing
            // it fails the whole call with "Specified OLE variant is invalid"
            // (DISP_E_BADVARTYPE). Measured 2026-09-08, on the first publish ever run from
            // here -- the pre-flight was tested, this line never was. An omitted optional
            // is passed as DISP_E_PARAMNOTFOUND, which is what the callee expects.
            bool ok;
            try { ok = (bool)t.Project.Publish(profile, updateUi); }
            catch (Exception ex) { throw new BridgeException("Publish failed: " + ex.Message); }

            data["started"] = ok;

            // Publish is ASYNCHRONOUS: it returns as soon as the upload is under way, so
            // reading the result straight after gives an object with nothing in it. Wait
            // for IsPublishRunning to go false, then read. Measured on the first real
            // publish: 2114 files replaced by 2131, and the call had already returned.
            int waitMs = ctx.Payload.Has("waitMs") ? ctx.Payload.Int("waitMs", PublishWaitMs) : PublishWaitMs;
            if (waitMs < 0) waitMs = 0;
            if (waitMs > 3600000) waitMs = 3600000;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool running = IsPublishRunning(t);
            while (running && sw.ElapsedMilliseconds < waitMs)
            {
                System.Threading.Thread.Sleep(250);
                running = IsPublishRunning(t);
            }
            sw.Stop();
            data["waitedMs"] = (int)sw.ElapsedMilliseconds;
            data["completed"] = !running;
            if (running)
                data["warning"] = "The publish was still running after " + ((int)sw.ElapsedMilliseconds) +
                    " ms; the result below is whatever was available at that moment. Poll " +
                    "hmi_publish result, or raise waitMs.";
            data["result"] = ReadPublishResult(t);
            data["verdictMeans"] =
                "ITcHmiPublishResult carries only Result / IsCompleted / SubmissionId -- there is no " +
                "per-extension verdict. A green publish means the upload ran, not that the server " +
                "configuration landed. Confirm on the server storage (RUNTIMES::*::NETID rows must be " +
                "no older than PROJECTNAME) before trusting a fresh runtime configuration.";
            data["replaces"] = "One TcHmiSrv instance hosts ONE project: publishing here replaces " +
                               "whatever that instance was serving.";
            return Stamp(t, data);
        }

        private static Json.JObj PublishResultAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            var data = new Json.JObj();
            data["isPublishRunning"] = ComHelpers.Safe<object>(delegate { return t.Project.IsPublishRunning(); });
            data["result"] = ReadPublishResult(t);
            return Stamp(t, data);
        }

        // GetPublishResult() is declared to return System.Object, so late binding hands
        // back an RCW on which Result / IsCompleted / SubmissionId all read as null --
        // which is how the first real publish reported {available:true} and no numbers at
        // all. The result has to be asked for by interface (HmiTyped.PublishResult).
        private static Json.JObj ReadPublishResult(HmiTarget t)
        {
            object raw;
            try { raw = t.Project.GetPublishResult(); }
            catch (Exception ex)
            {
                var err = new Json.JObj();
                err["available"] = false;
                err["error"] = ex.Message;
                return err;
            }
            return HmiTyped.PublishResult(raw);
        }

        // ================= H5: controls inside a view =======================
        //
        // Before this, changing what is IN a view meant editing its HTML as text, with
        // the .hmiproj and tchmiconfig.json registrations to keep in step by hand -- the
        // exact shortcut that loses one of the three and reports nothing. ITcHmiFile is
        // the supported way in, and it turns out to be a full surface: GetSource /
        // SetSource, GetAllIdentifiers / GetChildIdentifiers, GetControl, AddControl and
        // its Before/After siblings, RemoveControl, Beautify, and ChangeAttribute(s) on
        // the control. It does not marshal through IDispatch, so it is reached early
        // bound (HmiTyped).
        //
        // Reminder that applies to every verb here: a green HMI build does NOT mean a
        // binding is wired. Writing an attribute through this family is no different --
        // it puts the text in the file, and only the running HMI can say it delivers.

        // The ITcHmiFile for a project-RELATIVE path (e.g. "Desktop.view",
        // "UserControls\Foo.usercontrol"). Absolute paths are refused, like the add_*
        // verbs, so a caller cannot reach outside the project by accident.
        private static TcHmiAutomation.ITcHmiFile ResolveFile(ActionContext ctx, HmiTarget t, out string fullPath)
        {
            string rel = ctx.Require("path");
            if (Path.IsPathRooted(rel))
                throw new BridgeException("path must be project-relative (e.g. \"Desktop.view\" or " +
                                          "\"UserControls\\\\Foo.usercontrol\"), not absolute.");

            fullPath = Path.Combine(t.ProjectDir, rel);
            if (!File.Exists(fullPath))
                throw new BridgeException("'" + fullPath + "' does not exist. A file the .hmiproj does " +
                                          "not declare does not exist for build or publish either -- " +
                                          "create it with hmi_project add_view / add_usercontrol / " +
                                          "add_content, never by hand.");

            // Walk the project's own ProjectItems, segment by segment. NOT
            // Solution.FindProjectItem: on an HMI project it answers null even for a file
            // the .hmiproj plainly declares (measured on Desktop.view, whose <Content
            // Include> is right there in the file), so trusting it produced an error
            // message that stated something false about the project.
            dynamic dte = ctx.Dte(true);
            dynamic item = FindHmiProjectItem(t, rel);
            if (item == null)
            {
                try { item = dte.Solution.FindProjectItem(fullPath); }
                catch { item = null; }
            }
            if (item == null)
                throw new BridgeException("'" + rel + "' is on disk but neither the project's ProjectItems " +
                                          "walk nor Solution.FindProjectItem found it. If the .hmiproj has " +
                                          "no <Content Include> for it, add it with hmi_project add_content " +
                                          "(or add_view / add_usercontrol) -- a file the .hmiproj does not " +
                                          "declare does not exist for build or publish. If it IS declared, " +
                                          "the solution has not picked it up: reload it.");

            TcHmiAutomation.ITcHmiFile file = HmiTyped.GetHmiFile((object)t.Automation, (object)item);

            // Editing wants the file open in the designer's model. Open() is a no-op on
            // one that already is; nothing is closed afterwards, because closing a file
            // the user has open in the editor is not this verb's business.
            LastOpenState = new Json.JObj();
            bool wasOpen = false;
            try { wasOpen = file.IsOpen(); LastOpenState["wasOpen"] = wasOpen; }
            catch (Exception ex) { LastOpenState["isOpenError"] = ex.Message; }
            if (!wasOpen)
            {
                try { file.Open(); LastOpenState["opened"] = true; }
                catch (Exception ex) { throw new BridgeException("Opening '" + rel + "' failed: " + ex.Message); }
            }
            try { LastOpenState["openAndReady"] = file.IsOpenAndReady(15000); }
            catch (Exception ex) { LastOpenState["openAndReadyError"] = ex.Message; }
            try { LastOpenState["isOpen"] = file.IsOpen(); }
            catch { }
            return file;
        }

        // What ResolveFile observed about the file's open state on this call. Reported on
        // every response of this family, because whether the designer model is loaded is
        // the first thing to know when a control-level call answers nothing.
        [ThreadStatic]
        private static Json.JObj LastOpenState;

        // Descend the project tree one path segment at a time. Item() takes a name, so a
        // relative path has to be split; a segment that is not there ends the walk.
        private static dynamic FindHmiProjectItem(HmiTarget t, string rel)
        {
            if (t == null || t.Info == null || t.Info.DteProject == null) return null;
            string[] parts = rel.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            dynamic items;
            try { items = t.Info.DteProject.ProjectItems; }
            catch { return null; }

            dynamic current = null;
            for (int i = 0; i < parts.Length; i++)
            {
                if (items == null) return null;
                current = null;
                try { current = items.Item(parts[i]); }
                catch { current = null; }
                if (current == null) return null;
                if (i == parts.Length - 1) return current;
                try { items = current.ProjectItems; }
                catch { return null; }
            }
            return current;
        }

        private static Json.JObj FileStamp(HmiTarget t, string fullPath, Json.JObj data)
        {
            data["file"] = fullPath;
            if (LastOpenState != null) data["openState"] = LastOpenState;
            return Stamp(t, data);
        }

        private static Json.JObj FileIdentifiersAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);
            string parent = ctx.Payload.Truthy("parent") ? ctx.Payload.Str("parent") : null;

            var data = new Json.JObj();
            Json.JArr ids = HmiTyped.Identifiers(file, parent);
            data["parent"] = parent;
            data["count"] = ids.Count;
            data["identifiers"] = ids;
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileSourceAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);

            string src;
            try { src = file.GetSource(); }
            catch (Exception ex) { throw new BridgeException("GetSource failed: " + ex.Message); }
            if (src == null) src = "";

            var data = new Json.JObj();
            data["length"] = src.Length;
            data["source"] = src;
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileSetSourceAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);
            string source = ctx.Require("source");

            bool ok;
            try { ok = file.SetSource(source); }
            catch (Exception ex) { throw new BridgeException("SetSource failed: " + ex.Message); }

            var data = new Json.JObj();
            data["set"] = ok;
            data["identifiers"] = HmiTyped.Identifiers(file, null);
            SaveIfRequested(ctx, t, data, true);
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileControlAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, false);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);
            string id = ctx.Require("identifier");

            var how = new Json.JObj();
            TcHmiAutomation.ITcHmiControl c = GetControlOrThrow(t, file, id, how);

            var data = new Json.JObj();
            data["identifier"] = id;
            data["resolvedVia"] = how;
            data["attributes"] = HmiTyped.Attributes(c);
            data["children"] = HmiTyped.Identifiers(file, id);
            return FileStamp(t, full, data);
        }

        private static TcHmiAutomation.ITcHmiControl GetControlOrThrow(
            HmiTarget t, TcHmiAutomation.ITcHmiFile file, string id, Json.JObj how)
        {
            TcHmiAutomation.ITcHmiControl c = HmiTyped.FindControl((object)t.Project, file, id, how);
            if (c != null) return c;

            // A miss lists what IS there rather than answering nothing -- the same
            // contract as attach / select_project, one level further down. And when the
            // id IS in that list, say so: the failure is then the interface's, not the
            // caller's, and telling the two apart is the whole point of the message.
            Json.JArr ids = HmiTyped.Identifiers(file, null);
            bool listed = false;
            foreach (object o in ids) if (string.Equals(o as string, id, StringComparison.Ordinal)) { listed = true; break; }

            if (!listed)
                throw new BridgeException("No control with id '" + id + "' in this file. Ids: " +
                                          Json.Write(ids) + ".");

            throw new BridgeException("'" + id + "' IS in this file (GetAllIdentifiers lists it) but no " +
                "control object came back for it. Measured 2026-09-08 on TE2000: ITcHmiFile.GetControl " +
                "answers null for every identifier, freshly added ones included, with the file open and " +
                "IsOpenAndReady true, and ITcHmiProject.GetControlInstance does not take an identifier " +
                "either. So reading and changing the ATTRIBUTES of an existing control is not reachable " +
                "on this build; what works is hmi_file add_control (attributes are set at creation), " +
                "remove_control, and source / set_source. Routes tried: " + Json.Write(how) + ".");
        }

        private static Json.JObj FileAddControlAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);

            string id = ctx.Require("identifier");
            string type = ctx.Require("type");
            string parent = ctx.Payload.Truthy("parent") ? ctx.Payload.Str("parent") : null;
            string before = ctx.Payload.Truthy("before") ? ctx.Payload.Str("before") : null;
            string after = ctx.Payload.Truthy("after") ? ctx.Payload.Str("after") : null;

            int placements = (parent != null ? 1 : 0) + (before != null ? 1 : 0) + (after != null ? 1 : 0);
            if (placements == 0)
                throw new BridgeException("say where it goes: parent (append inside it), before or after " +
                                          "(next to that sibling).");
            if (placements > 1)
                throw new BridgeException("parent, before and after are three different placements; " +
                                          "name exactly one.");

            TcHmiAutomation.ITcHmiControlAttribute[] attrs =
                HmiTyped.BuildAttributes((object)t.Project, ctx.Payload.Arr("attributes"));

            TcHmiAutomation.ITcHmiControl created;
            string how;
            try
            {
                if (parent != null) { created = file.AddControl(parent, id, type, attrs); how = "AddControl"; }
                else if (before != null) { created = file.AddControlBefore(before, id, type, attrs); how = "AddControlBefore"; }
                else { created = file.AddControlAfter(after, id, type, attrs); how = "AddControlAfter"; }
            }
            catch (Exception ex) { throw new BridgeException("Adding '" + id + "' failed: " + ex.Message); }

            var data = new Json.JObj();
            data["identifier"] = id;
            data["type"] = type;
            data["via"] = how;
            // AddControl returns NULL even when it has added the control (measured
            // 2026-09-08: the identifier list went from 6 entries to 7 while the call
            // answered null). So its return value is reported as what it is -- a fact
            // about the call -- and the VERDICT comes from reading the file back.
            data["controlReturned"] = created != null;
            Json.JArr ids = HmiTyped.Identifiers(file, null);
            bool present = false;
            foreach (object o in ids) if (string.Equals(o as string, id, StringComparison.Ordinal)) { present = true; break; }
            data["added"] = present;
            data["confirmed"] = present;
            if (created != null) data["attributes"] = HmiTyped.Attributes(created);
            data["verdictMeans"] = "The control is in the file. Whether its bindings DELIVER is a " +
                                   "different question -- a misspelled binding attribute builds green " +
                                   "with an empty Error List; only the running HMI can answer it.";
            SaveIfRequested(ctx, t, data, true);
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileChangeAttributesAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);
            string id = ctx.Require("identifier");

            Json.JArr spec = ctx.Payload.Arr("attributes");
            if (spec == null || spec.Count == 0) throw new BridgeException("attributes is required");

            var how = new Json.JObj();
            TcHmiAutomation.ITcHmiControl c = GetControlOrThrow(t, file, id, how);
            TcHmiAutomation.ITcHmiControlAttribute[] attrs = HmiTyped.BuildAttributes((object)t.Project, spec);

            bool ok;
            try { ok = c.ChangeAttributes(attrs); }
            catch (Exception ex) { throw new BridgeException("ChangeAttributes on '" + id + "': " + ex.Message); }

            var data = new Json.JObj();
            data["identifier"] = id;
            data["changed"] = ok;
            data["resolvedVia"] = how;
            data["attributes"] = HmiTyped.Attributes(c);
            data["verdictMeans"] = "The attribute is written. A binding attribute that is written is not " +
                                   "a binding that delivers -- check it on the running HMI.";
            SaveIfRequested(ctx, t, data, true);
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileRemoveControlAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);
            string id = ctx.Require("identifier");

            // Refuse an id that is not in the file instead of reporting a cheerful false:
            // a remove that "succeeded" on nothing is how a typo passes for a change. The
            // check is the identifier LIST, not GetControl, which answers null for
            // everything on this build (see GetControlOrThrow).
            Json.JArr before = HmiTyped.Identifiers(file, null);
            bool known = false;
            foreach (object o in before) if (string.Equals(o as string, id, StringComparison.Ordinal)) { known = true; break; }
            if (!known)
                throw new BridgeException("No control with id '" + id + "' in this file. Ids: " +
                                          Json.Write(before) + ".");

            bool ok;
            try { ok = file.RemoveControl(id); }
            catch (Exception ex) { throw new BridgeException("RemoveControl('" + id + "'): " + ex.Message); }

            var data = new Json.JObj();
            data["identifier"] = id;
            data["removed"] = ok;
            Json.JArr ids = HmiTyped.Identifiers(file, null);
            bool stillThere = false;
            foreach (object o in ids) if (string.Equals(o as string, id, StringComparison.Ordinal)) { stillThere = true; break; }
            data["confirmed"] = !stillThere;
            SaveIfRequested(ctx, t, data, true);
            return FileStamp(t, full, data);
        }

        private static Json.JObj FileBeautifyAction(ActionContext ctx)
        {
            HmiTarget t = Resolve(ctx, true);
            string full;
            TcHmiAutomation.ITcHmiFile file = ResolveFile(ctx, t, out full);

            try { file.Beautify(); }
            catch (Exception ex) { throw new BridgeException("Beautify failed: " + ex.Message); }

            var data = new Json.JObj();
            data["beautified"] = true;
            SaveIfRequested(ctx, t, data, true);
            return FileStamp(t, full, data);
        }

        // ================= file / project plumbing ==========================

        private static string ReadTextOrNull(string path)
        {
            try { if (!File.Exists(path)) return null; return File.ReadAllText(path); }
            catch (Exception ex) { Log.Write("HmiActions: cannot read " + path + ": " + ex.Message); return null; }
        }

        private static bool FileContains(string path, string needle)
        {
            string text = ReadTextOrNull(path);
            return text != null && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // UTF-8 with a BOM and CRLF, which is what the IDE writes for these files. A
        // BOM-less file makes the next IDE save rewrite the whole thing and turns a
        // one-line change into a whole-file diff.
        private static void WriteProjectText(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }

        // The ProjectItems collection that OWNS a folder on disk, so a file added to it
        // is declared where it lies instead of being copied to the project root.
        //
        //  1. the folder's own project item, when the project declares folders;
        //  2. otherwise the collection of a file already declared in that folder --
        //     whatever collection a sibling belongs to is by definition the right one;
        //  3. otherwise the project root, which is correct only for a file that really
        //     is at the root.
        private static dynamic ResolveFolderItems(ActionContext ctx, HmiTarget t, string folderPath)
        {
            string folderName = Path.GetFileName(folderPath.TrimEnd('\\'));

            try
            {
                dynamic item = t.Info.DteProject.ProjectItems.Item(folderName);
                if (item != null && item.ProjectItems != null) return item.ProjectItems;
            }
            catch { }

            try
            {
                dynamic dte = ctx.Dte(true);
                string[] siblings = Directory.Exists(folderPath)
                    ? Directory.GetFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                    : new string[0];
                Array.Sort(siblings, StringComparer.OrdinalIgnoreCase);
                foreach (string s in siblings)
                {
                    dynamic si = null;
                    try { si = dte.Solution.FindProjectItem(s); }
                    catch { }
                    if (si == null) continue;
                    try { if (si.Collection != null) return si.Collection; }
                    catch { }
                }
            }
            catch { }

            return t.Info.DteProject.ProjectItems;
        }

        private static Json.JObj AddExistingFile(dynamic items, string fullPath)
        {
            var o = new Json.JObj();
            o["path"] = fullPath;
            try
            {
                dynamic item = items.AddFromFile(fullPath);
                o["declared"] = item != null;
                // AddFromFile can COPY rather than declare in place. The authority on
                // what happened is where the declared item's file actually is.
                if (item != null)
                {
                    // FileNames is a parameterized property and does not survive late
                    // binding here (it answers null), so the Properties collection is the
                    // route that actually reports where the item landed.
                    string landed = ComHelpers.SafeStr(delegate { return item.Properties.Item("FullPath").Value; });
                    if (string.IsNullOrWhiteSpace(landed))
                        landed = ComHelpers.SafeStr(delegate { return item.get_FileNames(1); });
                    o["declaredAs"] = landed;
                    if (!string.IsNullOrWhiteSpace(landed) && !PathUtil.SamePath(landed, fullPath))
                    {
                        o["declared"] = false;
                        o["error"] = "The project declared a COPY at '" + landed + "' instead of the " +
                                     "file itself. The item was added to the wrong collection.";
                    }
                }
            }
            catch (Exception ex)
            {
                o["declared"] = false;
                o["error"] = ex.Message;
            }
            return o;
        }

        private static bool ConfigHasUrl(string configPath, string url)
        {
            string text = ReadTextOrNull(configPath);
            return text != null && text.IndexOf("\"" + url + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Json.JObj RenameProjectItem(ActionContext ctx, string fullPath, string newFileName)
        {
            var o = new Json.JObj();
            o["from"] = fullPath;
            o["to"] = newFileName;
            dynamic dte = ctx.Dte(true);
            dynamic item = null;
            try { item = dte.Solution.FindProjectItem(fullPath); }
            catch (Exception ex) { o["error"] = "FindProjectItem: " + ex.Message; }
            if (item == null)
            {
                o["renamed"] = false;
                o["error"] = "The file is not declared in any project, so renaming it through the " +
                             "project would leave the .hmiproj untouched. Declare it first.";
                return o;
            }
            try
            {
                item.Name = newFileName;
                o["renamed"] = true;
            }
            catch (Exception ex)
            {
                o["renamed"] = false;
                o["error"] = ex.Message;
            }
            return o;
        }

        // Replace every occurrence of the old identity with the new one, and say how
        // many landed -- a rename that silently changed nothing is the failure mode.
        private static int RewriteIdentity(string path, string oldName, string newName)
        {
            string text = ReadTextOrNull(path);
            if (text == null) return 0;
            int count = 0;
            int i = 0;
            var sb = new StringBuilder(text.Length);
            while (true)
            {
                int k = text.IndexOf(oldName, i, StringComparison.Ordinal);
                if (k < 0) { sb.Append(text, i, text.Length - i); break; }
                // Only whole identifiers: FooBar must not become NewBar when renaming Foo.
                bool leftOk = k == 0 || !IsIdentChar(text[k - 1]);
                int end = k + oldName.Length;
                bool rightOk = end >= text.Length || !IsIdentChar(text[end]);
                sb.Append(text, i, k - i);
                if (leftOk && rightOk) { sb.Append(newName); count++; }
                else sb.Append(oldName);
                i = end;
            }
            if (count > 0) File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return count;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static int ReplaceInFile(string path, string find, string replaceWith)
        {
            string text = ReadTextOrNull(path);
            if (text == null) return 0;
            int count = 0;
            int i = 0;
            var sb = new StringBuilder(text.Length);
            while (true)
            {
                int k = text.IndexOf(find, i, StringComparison.Ordinal);
                if (k < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, k - i).Append(replaceWith);
                i = k + find.Length;
                count++;
            }
            if (count > 0) File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return count;
        }

        // ---- surgical edits to tchmiconfig.json ----------------------------
        //
        // The file is a project file under version control. Parsing and re-serialising it
        // would reformat 300 lines to register one function, so the two arrays are edited
        // in place: the diff is the two lines that were actually added.

        private static Json.JObj ConfigAppendDependencyFile(string configPath, string url)
        {
            string element = "{ \"name\": " + Json.Write(url) + ", \"description\": \"\", \"type\": \"EsModule\" }";
            return ConfigAppendToArray(configPath, "dependencyFiles", url, element);
        }

        private static Json.JObj ConfigAppendUserFunction(string configPath, string url)
        {
            string element = "{ \"url\": " + Json.Write(url) + " }";
            return ConfigAppendToArray(configPath, "userFunctions", url, element);
        }

        private static Json.JObj ConfigAppendToArray(string configPath, string key, string url, string element)
        {
            var o = new Json.JObj();
            o["array"] = key;
            string text = ReadTextOrNull(configPath);
            if (text == null)
            {
                o["ok"] = false;
                o["error"] = "tchmiconfig.json not found at " + configPath;
                return o;
            }

            int open, close, indent;
            if (!LocateArray(text, key, out open, out close, out indent))
            {
                o["ok"] = false;
                o["error"] = "No top-level \"" + key + "\" array in tchmiconfig.json.";
                return o;
            }

            string body = text.Substring(open + 1, close - open - 1);
            if (body.IndexOf("\"" + url + "\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                o["ok"] = true;
                o["alreadyPresent"] = true;
                return o;
            }

            string itemIndent = new string(' ', indent + 2);
            string closeIndent = new string(' ', indent);
            string inserted;
            if (body.Trim().Length == 0)
            {
                inserted = "[\r\n" + itemIndent + element + "\r\n" + closeIndent + "]";
            }
            else
            {
                string trimmedEnd = body.TrimEnd(' ', '\t', '\r', '\n');
                inserted = "[" + trimmedEnd + ",\r\n" + itemIndent + element + "\r\n" + closeIndent + "]";
            }

            string updated = text.Substring(0, open) + inserted + text.Substring(close + 1);
            try { File.WriteAllText(configPath, updated, new UTF8Encoding(true)); }
            catch (Exception ex)
            {
                o["ok"] = false;
                o["error"] = ex.Message;
                return o;
            }
            o["ok"] = true;
            o["appended"] = element;
            return o;
        }

        // Find a TOP-LEVEL "key": [ ... ] and return the bracket offsets plus the column
        // the key sits at. Depth-aware, so a nested array of the same name is not hit.
        private static bool LocateArray(string text, string key, out int open, out int close, out int indent)
        {
            open = close = indent = -1;
            string needle = "\"" + key + "\"";
            int depthObj = 0, depthArr = 0;
            bool inString = false, escape = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    if (depthObj == 1 && depthArr == 0 &&
                        string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                    {
                        int j = i + needle.Length;
                        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                        if (j < text.Length && text[j] == ':')
                        {
                            j++;
                            while (j < text.Length && (text[j] == ' ' || text[j] == '\t' ||
                                                       text[j] == '\r' || text[j] == '\n')) j++;
                            if (j < text.Length && text[j] == '[')
                            {
                                open = j;
                                indent = ColumnOf(text, i);
                                close = MatchBracket(text, j);
                                return close > open;
                            }
                        }
                    }
                    inString = true;
                    continue;
                }
                if (c == '{') depthObj++;
                else if (c == '}') depthObj--;
                else if (c == '[') depthArr++;
                else if (c == ']') depthArr--;
            }
            return false;
        }

        private static int ColumnOf(string text, int index)
        {
            int start = index;
            while (start > 0 && text[start - 1] != '\n') start--;
            return index - start;
        }

        private static int MatchBracket(string text, int openIndex)
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static Json.JObj ConfigCounts(HmiTarget t)
        {
            string text = ReadTextOrNull(t.ConfigPath);
            if (text == null) return null;
            object parsed;
            try { parsed = Json.Parse(text); }
            catch { return null; }
            var cfg = parsed as Json.JObj;
            if (cfg == null) return null;

            var o = new Json.JObj();
            o["views"] = CountOf(cfg, "views");
            o["content"] = CountOf(cfg, "content");
            o["userControls"] = CountOf(cfg, "userControls");
            o["userFunctions"] = CountOf(cfg, "userFunctions");
            o["dependencyFiles"] = CountOf(cfg, "dependencyFiles");
            o["startupView"] = cfg.Str("startupView");
            o["activeTheme"] = cfg.Str("activeTheme");
            return o;
        }

        private static int CountOf(Json.JObj cfg, string key)
        {
            Json.JArr a = cfg.Arr(key);
            return a == null ? 0 : a.Count;
        }
    }
}
