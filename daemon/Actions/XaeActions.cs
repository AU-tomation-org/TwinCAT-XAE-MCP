using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Te1000Daemon
{
    // XAE / DTE shell actions:
    //   xae_status, xae_open_solution, xae_list_commands,
    //   xae_execute_command, xae_get_active_document,
    //   xae_get_selected_items, xae_focus_tree_item,
    //   xae_get_error_list, xae_clear_error_list,
    //   xae_save_all, xae_solution_build.
    internal static class XaeActions
    {
        public static void Register(Dictionary<string, ActionHandler> h)
        {
            h["xae_status"] = XaeStatus;
            h["xae_open_solution"] = XaeOpenSolution;
            h["xae_list_commands"] = XaeListCommands;
            h["xae_execute_command"] = XaeExecuteCommand;
            h["xae_get_active_document"] = XaeGetActiveDocument;
            h["xae_get_selected_items"] = XaeGetSelectedItems;
            h["xae_focus_tree_item"] = XaeFocusTreeItem;
            h["xae_get_error_list"] = XaeGetErrorList;
            h["xae_clear_error_list"] = XaeClearErrorList;
            h["xae_save_all"] = XaeSaveAll;
            h["xae_solution_build"] = XaeSolutionBuild;
            h["xae_shutdown_ide"] = XaeShutdownIde;
            h["xae_list_instances"] = XaeListInstances;
            h["xae_attach"] = XaeAttach;
            h["xae_list_projects"] = XaeListProjects;
            h["xae_select_project"] = XaeSelectProject;
            h["xae_list_configurations"] = XaeListConfigurations;
            h["xae_set_configuration"] = XaeSetConfiguration;
            h["xae_get_output"] = XaeGetOutput;
            h["xae_find_project_template"] = XaeFindProjectTemplate;
            h["xae_create_solution"] = XaeCreateSolution;
            h["xae_add_project"] = XaeAddProject;
        }

        // ---- starting a new solution / project ------------------------------
        //
        // There was no way to start anything: plc_project create_from_template needs a
        // .tsproj that already exists, and handing a .tsproj to open_solution fails with
        // E_ABORT. So a new repo was made by copying an old one and rewriting names and
        // GUIDs by hand.
        //
        // The empty solution and the project inside it are two different jobs. The
        // solution is plain EnvDTE (Solution.Create + SaveAs). The project is
        // Solution.AddFromTemplate -- and the whole difficulty is the template PATH,
        // which differs between a TcXaeShell install and TE1000 integrated into VS2022,
        // exactly like the PIA paths. It is not on disk as a .vstemplate under either
        // install (searched, 2026-09-08), so it is not something to go looking for: ASK
        // the IDE, through Solution.GetProjectTemplate(name, language). That answers
        // correctly on whichever shell is actually running, which is the point.

        // GetProjectTemplate lives on Solution2, not on Solution, and late-bound dynamic
        // cannot see it: every call answers "'System.__ComObject' does not contain a
        // definition for 'GetProjectTemplate'", which reads like a missing method rather
        // than a missing interface. The RCW does QI to Solution2 -- it just has to be
        // asked in typed form, the same shape of fix as the error list and the tree items.
        // The hand-off out of `dynamic` has to be an ordinary assignment to `object`:
        // casting a dynamic expression inline keeps the whole thing on the DLR, so the
        // call is still looked up on __ComObject and fails with the same message about a
        // missing definition -- which is what made this look like a missing method twice.
        private static string GetProjectTemplate(dynamic dte, string name, string language)
        {
            object solObj = dte.Solution;
            EnvDTE80.Solution2 sol2 = solObj as EnvDTE80.Solution2;
            if (sol2 == null)
                throw new BridgeException("The solution object does not QI to EnvDTE80.Solution2 " +
                                          "(runtime type " + (solObj == null ? "null" : solObj.GetType().FullName) + ").");
            return sol2.GetProjectTemplate(name, language);
        }

        // The (name, language) pairs GetProjectTemplate is tried with when the caller
        // does not name one. Order matters only in that the first hit wins.
        private static readonly string[][] TemplateCandidates = new string[][]
        {
            new string[] { "TwinCAT XAE Project.zip", "TwinCAT Projects" },
            new string[] { "TwinCAT XAE Project", "TwinCAT Projects" },
            new string[] { "TwinCAT Project.zip", "TwinCAT Projects" },
            new string[] { "TwinCAT XAE Project (XML format).zip", "TwinCAT Projects" },
            new string[] { "TcXaeProject.zip", "TwinCAT Projects" },
            new string[] { "TwinCAT XAE Project.zip", "TwinCAT" },
            new string[] { "TwinCAT Project.zip", "TwinCAT" },
        };

        // Where the XAE project template actually is. It is NOT a .vstemplate and not a
        // zip: searched the whole VS extension tree, the template caches and both TwinCAT
        // install roots on 2026-09-08 and there is exactly one candidate on the machine --
        // a 67-byte stub .tsproj (<TcSmProject><Project/></TcSmProject>) next to an old
        // style tsmprojects.vsdir, which is why Solution2.GetProjectTemplate answers
        // "file not found" for every name one might guess. The project factory expands the
        // stub when the project opens. TWINCAT3DIR is asked first because it is what the
        // installer sets, so it follows a TwinCAT installed somewhere unusual.
        private static readonly string[] XaeTemplateRelPaths = new string[]
        {
            @"Components\Base\PrjTemplate\TwinCAT Project.tsproj",
        };

        private static string FindXaeProjectTemplateOnDisk()
        {
            var roots = new List<string>();
            string env = null;
            try { env = Environment.GetEnvironmentVariable("TWINCAT3DIR"); }
            catch { }
            if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
            roots.Add(@"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1");
            roots.Add(@"C:\TwinCAT\3.1");

            foreach (string root in roots)
            {
                foreach (string rel in XaeTemplateRelPaths)
                {
                    try
                    {
                        string p = System.IO.Path.Combine(root.TrimEnd('\\', '/'), rel);
                        if (System.IO.File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        // Read-only: what can this machine actually start a TwinCAT project from? Reports
        // both routes -- the template on disk and every (name, language) pair
        // GetProjectTemplate was asked about -- so the answer is a measurement, and a
        // caller on an install nobody here has seen can read the failures and pass its own
        // templatePath or pair to add_project.
        private static Json.JObj XaeFindProjectTemplate(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            Json.JArr given = ctx.Payload.Arr("candidates");

            var tried = new Json.JArr();
            string foundPath = null;
            string foundName = null;
            string foundLanguage = null;

            var pairs = new List<string[]>();
            if (given != null && given.Count > 0)
            {
                foreach (object o in given)
                {
                    Json.JObj c = o as Json.JObj;
                    if (c == null) continue;
                    pairs.Add(new string[] { c.Str("name"), c.Str("language") });
                }
            }
            else
            {
                pairs.AddRange(TemplateCandidates);
            }

            foreach (string[] pair in pairs)
            {
                var row = new Json.JObj();
                row["name"] = pair[0];
                row["language"] = pair[1];
                try
                {
                    string p = GetProjectTemplate(dte, pair[0], pair[1]);
                    row["path"] = p;
                    row["ok"] = !string.IsNullOrWhiteSpace(p);
                    if (!string.IsNullOrWhiteSpace(p) && foundPath == null)
                    {
                        foundPath = p; foundName = pair[0]; foundLanguage = pair[1];
                    }
                }
                catch (Exception ex)
                {
                    row["ok"] = false;
                    row["error"] = ex.GetType().Name + ": " + ex.Message;
                }
                tried.Add(row);
            }

            string onDisk = FindXaeProjectTemplateOnDisk();

            var data = new Json.JObj();
            data["found"] = onDisk != null || foundPath != null;
            data["diskTemplatePath"] = onDisk;
            if (foundPath != null)
            {
                data["templatePath"] = foundPath;
                data["templateName"] = foundName;
                data["templateLanguage"] = foundLanguage;
            }
            else if (onDisk != null)
            {
                data["templatePath"] = onDisk;
            }
            data["getProjectTemplateTried"] = tried;
            data["note"] = "The XAE project template is a stub .tsproj registered by an old-style " +
                ".vsdir, not a .vstemplate, so GetProjectTemplate does not resolve it under any name; " +
                "diskTemplatePath is the route that works.";
            return data;
        }

        // Create a blank solution. Creating one CLOSES whatever is open, so an open
        // solution is refused by name unless the caller says closeExisting -- the same
        // contract as everywhere else here: never quietly do the destructive half.
        private static Json.JObj XaeCreateSolution(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            string dir = ctx.Require("directory");
            string name = ctx.Require("name");
            bool closeExisting = ctx.Payload.Has("closeExisting") && ctx.Payload.Bool("closeExisting");

            Json.JObj open = GetSolutionInfo(dte);
            if (open.Bool("isOpen"))
            {
                if (!closeExisting)
                    throw new BridgeException("'" + open.Str("fullName") + "' is open, and creating a " +
                        "solution closes it. Re-run with closeExisting:true, or use another IDE instance " +
                        "(xae list_instances / attach).");
                try { dte.Solution.Close(true); }
                catch (Exception ex) { throw new BridgeException("Closing the open solution failed: " + ex.Message); }
            }

            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex) { throw new BridgeException("Cannot create '" + dir + "': " + ex.Message); }

            try { dte.Solution.Create(dir, name); }
            catch (Exception ex) { throw new BridgeException("Solution.Create failed: " + ex.Message); }

            // Create() builds the solution in memory; nothing is on disk until it is
            // saved, and a solution that is not on disk cannot be reopened or committed.
            string slnPath = System.IO.Path.Combine(dir, name + ".sln");
            string saveError = null;
            try { dte.Solution.SaveAs(slnPath); }
            catch (Exception ex) { saveError = ex.Message; }

            var data = new Json.JObj();
            data["directory"] = dir;
            data["name"] = name;
            data["solutionPath"] = slnPath;
            // Read it back from disk: Create + SaveAs returning is not proof of a file.
            try { data["solutionFileWritten"] = System.IO.File.Exists(slnPath); }
            catch { data["solutionFileWritten"] = null; }
            if (saveError != null) data["saveError"] = saveError;
            data["solution"] = GetSolutionInfo(dte);
            ctx.Cache.Clear();
            return data;
        }

        // Add a project from a template to the open solution. templatePath wins; failing
        // that templateName/templateLanguage; failing that the probed candidates.
        private static Json.JObj XaeAddProject(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            string name = ctx.Require("name");

            Json.JObj open = GetSolutionInfo(dte);
            if (!open.Bool("isOpen"))
                throw new BridgeException("No solution is open. Create one first (xae create_solution) " +
                                          "or open one (xae open_solution).");

            string slnFile = open.Str("fullName");
            string dir = ctx.Payload.Truthy("directory") ? ctx.Payload.Str("directory") : null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                string slnDir = null;
                try { slnDir = System.IO.Path.GetDirectoryName(slnFile); }
                catch { }
                if (string.IsNullOrWhiteSpace(slnDir))
                    throw new BridgeException("directory is required (the solution path could not be read).");
                dir = System.IO.Path.Combine(slnDir, name);
            }

            string templatePath = ctx.Payload.Truthy("templatePath") ? ctx.Payload.Str("templatePath") : null;
            var resolution = new Json.JObj();
            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                resolution["how"] = "templatePath";
            }
            else
            {
                string tName = ctx.Payload.Truthy("templateName") ? ctx.Payload.Str("templateName") : null;
                string tLang = ctx.Payload.Truthy("templateLanguage") ? ctx.Payload.Str("templateLanguage") : "TwinCAT Projects";

                // A named pair is what the caller asked for, so it is tried first and on its
                // own. With nothing named, the stub .tsproj on disk is the route that works
                // on a TwinCAT install (see FindXaeProjectTemplateOnDisk); the name probes
                // are kept after it for a shell that does register a real template.
                var tried = new Json.JArr();
                if (!string.IsNullOrWhiteSpace(tName))
                {
                    var row = new Json.JObj();
                    row["name"] = tName;
                    row["language"] = tLang;
                    try
                    {
                        string p = GetProjectTemplate(dte, tName, tLang);
                        row["ok"] = !string.IsNullOrWhiteSpace(p);
                        row["path"] = p;
                        if (!string.IsNullOrWhiteSpace(p)) templatePath = p;
                    }
                    catch (Exception ex) { row["ok"] = false; row["error"] = ex.GetType().Name + ": " + ex.Message; }
                    tried.Add(row);
                    resolution["how"] = "GetProjectTemplate";
                }
                else
                {
                    templatePath = FindXaeProjectTemplateOnDisk();
                    if (!string.IsNullOrWhiteSpace(templatePath)) resolution["how"] = "diskTemplate";
                    else
                    {
                        foreach (string[] pair in TemplateCandidates)
                        {
                            var row = new Json.JObj();
                            row["name"] = pair[0];
                            row["language"] = pair[1];
                            try
                            {
                                string p = GetProjectTemplate(dte, pair[0], pair[1]);
                                row["ok"] = !string.IsNullOrWhiteSpace(p);
                                row["path"] = p;
                                if (!string.IsNullOrWhiteSpace(p)) { templatePath = p; tried.Add(row); break; }
                            }
                            catch (Exception ex) { row["ok"] = false; row["error"] = ex.GetType().Name + ": " + ex.Message; }
                            tried.Add(row);
                        }
                        resolution["how"] = "GetProjectTemplate";
                    }
                }
                if (tried.Count > 0) resolution["tried"] = tried;
                if (string.IsNullOrWhiteSpace(templatePath))
                    throw new BridgeException("No project template resolved. Run xae find_project_template " +
                        "to see what this machine offers, then pass templatePath (or " +
                        "templateName/templateLanguage). Tried: " + Json.Write(tried));
            }
            resolution["templatePath"] = templatePath;

            try { dte.Solution.AddFromTemplate(templatePath, dir, name, false); }
            catch (Exception ex)
            {
                throw new BridgeException("AddFromTemplate('" + templatePath + "', '" + dir + "', '" +
                                          name + "') failed: " + ex.Message);
            }

            var data = new Json.JObj();
            data["name"] = name;
            data["directory"] = dir;
            data["template"] = resolution;
            data["projects"] = ListSolutionProjectNames(dte);
            // The point of the verb is a .tsproj on disk; say whether one arrived rather
            // than reporting the call that was made.
            string tsproj = FindFileByExtension(dir, "*.tsproj");
            data["tsProjectPath"] = tsproj;
            data["tsProjectWritten"] = tsproj != null;
            ctx.Cache.Clear();
            return data;
        }

        private static Json.JArr ListSolutionProjectNames(dynamic dte)
        {
            var arr = new Json.JArr();
            try
            {
                dynamic projects = dte.Solution.Projects;
                int n = ComHelpers.ToInt(projects.Count);
                for (int i = 1; i <= n; i++)
                {
                    dynamic p;
                    try { p = projects.Item(i); }
                    catch { continue; }
                    arr.Add(ComHelpers.SafeStr(delegate { return p.FullName; }));
                }
            }
            catch { }
            return arr;
        }

        private static string FindFileByExtension(string dir, string pattern)
        {
            try
            {
                if (!System.IO.Directory.Exists(dir)) return null;
                string[] hits = System.IO.Directory.GetFiles(dir, pattern, System.IO.SearchOption.AllDirectories);
                return hits.Length > 0 ? hits[0] : null;
            }
            catch { return null; }
        }

        // ---- shared helpers (port of bridge helper functions) ----------------

        // Get-SolutionInfo (bridge L581-601): {isOpen, fullName}.
        private static Json.JObj GetSolutionInfo(dynamic dte)
        {
            dynamic solution = dte.Solution;
            string fullName = null;
            bool isOpen = false;
            bool isOpenResolved = false;

            try { fullName = (string)solution.FullName; }
            catch { }

            try { isOpen = (bool)solution.IsOpen; isOpenResolved = true; }
            catch { isOpenResolved = false; }

            if (!isOpenResolved)
            {
                isOpen = !string.IsNullOrWhiteSpace(fullName);
            }

            var o = new Json.JObj();
            o["isOpen"] = isOpen;
            o["fullName"] = fullName;
            return o;
        }

        // Get-AutomationSettings (bridge L695-703). Retries TcAutomationSettings.
        private static dynamic GetAutomationSettings(dynamic dte)
        {
            return ComHelpers.WithRetry<dynamic>(delegate()
            {
                dynamic settings = dte.GetObject("TcAutomationSettings");
                if (settings == null) throw new BridgeException("TcAutomationSettings is null");
                return settings;
            }, 20, 250);
        }

        // Wait-ForSolutionOpen (bridge L610-621).
        private static Json.JObj WaitForSolutionOpen(dynamic dte, string expectedPath)
        {
            return ComHelpers.WithRetry<Json.JObj>(delegate()
            {
                Json.JObj info = GetSolutionInfo(dte);
                bool isOpen = info.Bool("isOpen");
                if (!isOpen) throw new BridgeException("Solution is not open yet");
                string fullName = info.Str("fullName");
                // Compare the paths as paths, not as strings -- see PathUtil.SamePath.
                if (!string.IsNullOrWhiteSpace(expectedPath) &&
                    !PathUtil.SamePath(fullName, expectedPath))
                {
                    throw new BridgeException("Different solution is active: " + fullName);
                }
                return info;
            }, 60, 500);
        }

        // Wait-ForBuildFinish (bridge L678-693): poll until BuildState != 2.
        internal static Json.JObj WaitForBuildFinish(dynamic solutionBuild, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                int state = (int)solutionBuild.BuildState;
                if (state != 2)
                {
                    var done = new Json.JObj();
                    done["buildState"] = state;
                    done["lastBuildInfo"] = (int)solutionBuild.LastBuildInfo;
                    return done;
                }
                System.Threading.Thread.Sleep(500);
            }
            throw new BridgeException("Timed out waiting for build completion after " + timeoutMs + " ms");
        }

        // Invoke-DteCommand (bridge L705-750): {commandName, isAvailable, executed}.
        private static Json.JObj InvokeDteCommand(dynamic dte, string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName)) throw new BridgeException("CommandName is required");

            GetAutomationSettings(dte);
            dynamic cmd;
            try { cmd = dte.Commands.Item(commandName, 0); }
            catch (Exception ex) { throw new BridgeException("Command lookup failed for '" + commandName + "': " + ex.Message); }

            if (cmd == null) throw new BridgeException("Command not found: " + commandName);

            bool isAvailable = true;
            try { isAvailable = (bool)cmd.IsAvailable; }
            catch { }

            if (!isAvailable) throw new BridgeException("Command is not available in the current XAE context: " + commandName);

            try { dte.ExecuteCommand(commandName); }
            catch (Exception ex) { throw new BridgeException("ExecuteCommand failed for '" + commandName + "': " + ex.Message); }

            var o = new Json.JObj();
            o["commandName"] = commandName;
            o["isAvailable"] = isAvailable;
            o["executed"] = true;
            return o;
        }

        // Convert-SelectedItem (bridge L3002-3021).
        private static Json.JObj ConvertSelectedItem(dynamic selectedItem)
        {
            dynamic projectItem = ComHelpers.Safe<dynamic>(delegate() { return selectedItem.ProjectItem; });
            dynamic projectItemObject = null;
            if (projectItem != null)
            {
                projectItemObject = ComHelpers.Safe<dynamic>(delegate() { return projectItem.Object; });
            }

            dynamic pi = projectItem;
            dynamic pio = projectItemObject;

            var o = new Json.JObj();
            o["name"] = ComHelpers.SafeStr(delegate() { return selectedItem.Name; });
            o["projectName"] = ComHelpers.SafeStr(delegate() { return selectedItem.Project.Name; });
            o["projectItemName"] = ComHelpers.SafeStr(delegate() { return pi.Name; });
            o["projectItemKind"] = ComHelpers.SafeStr(delegate() { return pi.Kind; });
            o["treePath"] = ComHelpers.SafeStr(delegate() { return pio.PathName; });
            return o;
        }

        // ---- actions ---------------------------------------------------------

        // xae_status (L3689-3718).
        private static Json.JObj XaeStatus(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);

            bool automationAvailable = false;
            try { dynamic s = GetAutomationSettings(dte); automationAvailable = (s != null); }
            catch { automationAvailable = false; }

            bool sysManagerAvailable = false;
            try { ctx.SysManager(); sysManagerAvailable = true; }
            catch { sysManagerAvailable = false; }

            var data = new Json.JObj();
            data["progId"] = ctx.ProgId;
            data["mode"] = ctx.Mode;
            data["pid"] = ctx.Session.CurrentPid;
            data["solution"] = solution;
            data["automationSettingsAvailable"] = automationAvailable;
            data["sysManagerAvailable"] = sysManagerAvailable;
            // Which project and which configuration the next call would work on. Both
            // decide the outcome of a build or an activation and neither was visible.
            data["activeConfiguration"] = ComHelpers.SafeStr(delegate { return ActiveConfigurationName(dte.Solution.SolutionBuild); });
            data["tsProjectSelected"] = ctx.Session.HasProjectSelection;
            return data;
        }

        // xae_open_solution (L3720-3767).
        private static Json.JObj XaeOpenSolution(ActionContext ctx)
        {
            string solutionPath = ctx.Payload.Str("solutionPath");
            if (string.IsNullOrWhiteSpace(solutionPath)) throw new BridgeException("solutionPath is required");
            if (!System.IO.File.Exists(solutionPath)) throw new BridgeException("Solution file not found: " + solutionPath);

            bool visible = true;
            if (ctx.Payload.Has("visible")) visible = ctx.Payload.Bool("visible");

            bool closeExisting = false;
            if (ctx.Payload.Has("closeExisting")) closeExisting = ctx.Payload.Bool("closeExisting");

            bool discardChanges = false;
            if (ctx.Payload.Has("discardChanges")) discardChanges = ctx.Payload.Bool("discardChanges");

            // open_solution may pass its own mode (read by ActionContext into ctx.Mode).
            dynamic dte = ctx.Dte(visible);
            try { dte.MainWindow.Visible = visible; }
            catch { }

            Json.JObj current = GetSolutionInfo(dte);
            if (current.Bool("isOpen") && closeExisting)
            {
                dte.Solution.Close(!discardChanges);
            }

            dte.Solution.Open(solutionPath);
            Json.JObj solution = WaitForSolutionOpen(dte, solutionPath);
            GetAutomationSettings(dte);
            // A project chosen in the previous solution names nothing in this one, and a
            // stale selection would turn every later call into an error about a project
            // the caller never mentioned.
            ctx.Session.ClearProjectSelection();

            var data = new Json.JObj();
            data["progId"] = ctx.ProgId;
            data["solution"] = solution;
            return data;
        }

        // xae_list_commands (L3769-3803).
        private static Json.JObj XaeListCommands(ActionContext ctx)
        {
            string filter = ctx.Payload.Truthy("filter") ? ctx.Payload.Str("filter") : null;
            int limit = 250;
            if (ctx.Payload.Has("limit")) limit = ctx.Payload.Int("limit", 250);

            dynamic dte = ctx.Dte(true);
            var names = new List<string>();

            System.Text.RegularExpressions.Regex rx = null;
            if (!string.IsNullOrEmpty(filter))
            {
                rx = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            foreach (dynamic cmd in dte.Commands)
            {
                try
                {
                    string name = (string)cmd.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (rx != null && !rx.IsMatch(name)) continue;
                    names.Add(name);
                }
                catch { }
            }

            // Sort -Unique then Select -First $limit.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            names.Sort(StringComparer.Ordinal);
            var commands = new Json.JArr();
            foreach (string n in names)
            {
                if (commands.Count >= limit) break;
                if (seen.Add(n)) commands.Add(n);
            }

            var data = new Json.JObj();
            data["filter"] = filter;
            data["count"] = commands.Count;
            data["commands"] = commands;
            return data;
        }

        // xae_execute_command (L3805-3843).
        private static Json.JObj XaeExecuteCommand(ActionContext ctx)
        {
            string commandName = ctx.Payload.Str("commandName");
            if (string.IsNullOrWhiteSpace(commandName)) throw new BridgeException("commandName is required");

            string args = ctx.Payload.Has("args") ? ctx.Payload.Str("args") : "";
            if (args == null) args = "";

            dynamic dte = ctx.Dte(true);
            GetAutomationSettings(dte);
            dynamic cmd = dte.Commands.Item(commandName, 0);
            if (cmd == null) throw new BridgeException("Command not found: " + commandName);

            bool isAvailable = true;
            try { isAvailable = (bool)cmd.IsAvailable; }
            catch { }

            if (!isAvailable) throw new BridgeException("Command is not available in the current XAE context: " + commandName);

            if (string.IsNullOrWhiteSpace(args)) dte.ExecuteCommand(commandName);
            else dte.ExecuteCommand(commandName, args);

            var data = new Json.JObj();
            data["commandName"] = commandName;
            data["args"] = args;
            data["isAvailable"] = isAvailable;
            data["executed"] = true;
            return data;
        }

        // xae_get_active_document (L3845-3860).
        private static Json.JObj XaeGetActiveDocument(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            dynamic doc = ComHelpers.Safe<dynamic>(delegate() { return dte.ActiveDocument; });
            dynamic d = doc;

            var data = new Json.JObj();
            data["hasActiveDocument"] = (doc != null);
            data["name"] = ComHelpers.SafeStr(delegate() { return d.Name; });
            data["fullName"] = ComHelpers.SafeStr(delegate() { return d.FullName; });
            data["kind"] = ComHelpers.SafeStr(delegate() { return d.Kind; });
            data["projectItemName"] = ComHelpers.SafeStr(delegate() { return d.ProjectItem.Name; });
            return data;
        }

        // xae_get_selected_items (L3862-3883).
        private static Json.JObj XaeGetSelectedItems(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            var items = new Json.JArr();
            int count = 0;

            try { count = (int)dte.SelectedItems.Count; }
            catch { }

            for (int i = 1; i <= count; i++)
            {
                items.Add(ConvertSelectedItem(dte.SelectedItems.Item(i)));
            }

            var data = new Json.JObj();
            data["count"] = count;
            data["items"] = items;
            return data;
        }

        // xae_focus_tree_item (L3886-3911).
        private static Json.JObj XaeFocusTreeItem(ActionContext ctx)
        {
            string treePath = ctx.Payload.Str("treePath");
            if (string.IsNullOrWhiteSpace(treePath)) throw new BridgeException("treePath is required");

            dynamic dte = ctx.Dte(true);
            dynamic sysManager = ctx.SysManager();
            dynamic item = ComHelpers.GetTreeItem(sysManager, treePath);
            dynamic vsProjectItem = ComHelpers.Safe<dynamic>(delegate() { return item.VSProjectItem; });
            if (vsProjectItem == null) throw new BridgeException("No VSProjectItem is available for tree item: " + treePath);

            dynamic vp = vsProjectItem;
            ComHelpers.Safe<object>(delegate() { dte.ExecuteCommand("View.SolutionExplorer"); return null; });
            ComHelpers.Safe<object>(delegate() { vp.ExpandView(); return null; });

            var data = new Json.JObj();
            data["treePath"] = treePath;
            data["expanded"] = true;
            data["note"] = "Best effort only. XAE did not expose a reliable programmatic selection method in this environment.";
            return data;
        }

        // xae_get_error_list (L3914-3954) + XaeErrorListProbe (L205-260).
        private static Json.JObj XaeGetErrorList(ActionContext ctx)
        {
            // R6: error_list-specific default lowered 200 -> 50 (build-error floods
            // collapse; count still reports the true total). severityFilter trims to
            // errors/warnings BEFORE the cap so an errors-only query is never starved
            // by a leading warning flood.
            int limit = 50;
            if (ctx.Payload.Has("limit")) limit = ctx.Payload.Int("limit", 50);
            string severityFilter = ctx.Payload.Has("severityFilter") ? ctx.Payload.Str("severityFilter") : "all";

            string error;
            ErrorListResult result = ReadErrorList(ctx, limit, severityFilter, out error);
            if (result == null)
            {
                var unavailable = new Json.JObj();
                unavailable["available"] = false;
                unavailable["count"] = 0;
                unavailable["items"] = new Json.JArr();
                if (!string.IsNullOrEmpty(error)) unavailable["error"] = error;
                return unavailable;
            }

            var data = new Json.JObj();
            data["available"] = true;
            data["count"] = result.TotalCount;
            data["returned"] = result.Items.Count;
            // Say it out loud when the severity filter could not do its job: these rows
            // are TwinCAT PLC rows, which report every severity at the same ErrorLevel
            // (see SeverityMatches). They are kept, not dropped, so an errors-only query
            // never comes back empty on a PLC project -- but the caller must read the
            // descriptions to tell an error from a warning.
            if (result.AmbiguousCount > 0)
            {
                data["severityUndecidableCount"] = result.AmbiguousCount;
                data["severityNote"] = "TwinCAT PLC rows report errors, warnings and info at the same ErrorLevel; rows kept regardless of severityFilter.";
            }
            data["items"] = result.Items;
            return data;
        }

        // xae_list_instances -- which IDEs are running, and what each has open.
        //
        // Read-only, and deliberately does NOT attach: it must be callable to decide
        // WHICH instance to work with, so it cannot itself pick one as a side effect.
        // For that reason it never starts an IDE either -- with none running it returns
        // an empty list, not a freshly created shell.
        private static Json.JObj XaeListInstances(ActionContext ctx)
        {
            var instances = ctx.Session.ListInstances(ctx.ProgId);

            var arr = new Json.JArr();
            foreach (var i in instances)
            {
                var o = new Json.JObj();
                o["pid"] = i.Pid;
                o["displayName"] = i.DisplayName;
                o["solution"] = string.IsNullOrWhiteSpace(i.Solution) ? null : i.Solution;
                o["hasSolution"] = !string.IsNullOrWhiteSpace(i.Solution);
                o["isCurrent"] = i.IsCurrent;
                o["startedByUs"] = i.OwnedByUs;
                arr.Add(o);
            }

            var data = new Json.JObj();
            data["progId"] = ctx.ProgId;
            data["count"] = arr.Count;
            data["instances"] = arr;
            return data;
        }

        // xae_attach -- bind this session to ONE named instance, by pid or by the
        // solution it has open.
        //
        // Without this the only lever was `mode`, and mode:"active" means "whichever
        // instance the ROT lists first that has any solution open" -- an ordering the
        // caller does not control and cannot see. Pair it with list_instances: look,
        // then choose. The binding sticks, because the COM session is cached, so every
        // later call -- including the tools that expose no mode of their own, xae_build
        // among them -- runs against the instance chosen here.
        private static Json.JObj XaeAttach(ActionContext ctx)
        {
            int pid = ctx.Payload.Has("pid") ? ctx.Payload.Int("pid", 0) : 0;
            string solutionPath = ctx.Payload.Truthy("solutionPath") ? ctx.Payload.Str("solutionPath") : null;
            if (pid <= 0 && string.IsNullOrWhiteSpace(solutionPath))
                throw new BridgeException("attach requires pid or solutionPath");

            var target = new ComSession.InstanceRequest();
            target.Pid = pid;
            target.SolutionPath = solutionPath;

            // Mode is irrelevant with an explicit target -- AcquireDte rejects a miss
            // rather than falling back -- but pass "active" so nothing can create one.
            dynamic dte = ctx.Session.GetDte(ctx.ProgId, "active", true, target);

            var data = new Json.JObj();
            data["progId"] = ctx.ProgId;
            data["attached"] = true;
            data["pid"] = ctx.Session.CurrentPid;
            data["startedByUs"] = ctx.Session.OwnedByUs;
            data["solution"] = GetSolutionInfo(dte);
            return data;
        }

        // xae_list_projects -- the TwinCAT projects of the open solution.
        //
        // The counterpart of list_instances one level down: attach chooses WHICH IDE,
        // this chooses WHICH .tsproj inside its solution. Every AU-tomation repo holds
        // two (the library and its TcUnit suite) and only one of them carries a
        // TargetNetId, so "the first project" is a coin toss that decides which runtime
        // gets activated. Read-only: it resolves nothing and changes no selection.
        private static Json.JObj XaeListProjects(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            var projects = ctx.Session.ListProjects(true);

            var arr = new Json.JArr();
            foreach (var p in projects)
            {
                var o = new Json.JObj();
                o["name"] = p.Name;
                o["path"] = p.FullName;
                o["uniqueName"] = p.UniqueName;
                o["targetNetId"] = p.TargetNetId;
                o["plcProjectCount"] = p.PlcProjectCount;
                o["hasPlcProject"] = p.PlcProjectCount > 0;
                o["isCurrent"] = p.IsCurrent;
                arr.Add(o);
            }

            var data = new Json.JObj();
            data["solution"] = solution;
            data["count"] = arr.Count;
            data["projects"] = arr;
            data["selected"] = ctx.Session.HasProjectSelection ? ctx.Session.CurrentProjectName : null;
            if (arr.Count > 1 && !ctx.Session.HasProjectSelection)
            {
                data["note"] = "No project chosen: reads fall back to the first one, and the actions that change the target (activate, restart, boot flags, download) refuse until one is named. Use select_project, or pass tsProject per call.";
            }
            return data;
        }

        // xae_select_project -- bind this session to ONE TwinCAT project of the solution.
        //
        // Same contract as attach: a miss is an error that lists what IS there, never a
        // silent fallback, and the binding sticks for later calls -- including the tools
        // that take no project parameter of their own. Dropped when the ground moves:
        // opening another solution, or attaching to another IDE.
        private static Json.JObj XaeSelectProject(ActionContext ctx)
        {
            string name = ctx.Payload.Truthy("name") ? ctx.Payload.Str("name") : null;
            string path = ctx.Payload.Truthy("path") ? ctx.Payload.Str("path") : null;
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path))
                throw new BridgeException("select_project requires name or path (use list_projects to see them)");

            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            var req = new ComSession.ProjectRequest();
            req.Name = name;
            req.Path = path;
            var chosen = ctx.Session.SelectProject(req);

            // The tree cache is keyed by path, and the same path names a different item
            // in another project -- keeping it would answer from the previous project.
            if (ctx.Cache != null) ctx.Cache.Clear();

            var data = new Json.JObj();
            data["selected"] = true;
            data["solution"] = solution;
            data["tsProject"] = chosen.Name;
            data["tsProjectPath"] = chosen.FullName;
            data["uniqueName"] = chosen.UniqueName;
            data["targetNetId"] = chosen.TargetNetId;
            data["plcProjectCount"] = chosen.PlcProjectCount;
            return data;
        }

        // xae_list_configurations -- the solution configurations, and which is active.
        //
        // xae_build builds whatever configuration happens to be active and nothing could
        // read or set it: a solution left on TwinCAT RT (x64) builds RT while CI builds
        // TwinCAT OS (x64), and the two verdicts differ with nothing on the response to
        // say why.
        private static Json.JObj XaeListConfigurations(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            dynamic solutionBuild = dte.Solution.SolutionBuild;
            var arr = new Json.JArr();
            string activeName = ActiveConfigurationName(solutionBuild);

            dynamic configurations = null;
            try { configurations = solutionBuild.SolutionConfigurations; }
            catch { }
            if (configurations != null)
            {
                int count = ComHelpers.SafeInt(delegate { return configurations.Count; }, 0);
                for (int i = 1; i <= count; i++)
                {
                    dynamic cfg = null;
                    try { cfg = configurations.Item(i); }
                    catch { }
                    if (cfg == null) continue;
                    string full = ConfigurationFullName(cfg);
                    var o = new Json.JObj();
                    o["name"] = ComHelpers.SafeStr(delegate { return cfg.Name; });
                    o["platform"] = PlatformName(cfg);
                    o["fullName"] = full;
                    o["isActive"] = !string.IsNullOrWhiteSpace(full) &&
                                    string.Equals(full, activeName, StringComparison.OrdinalIgnoreCase);
                    arr.Add(o);
                }
            }

            var data = new Json.JObj();
            data["solution"] = solution;
            data["active"] = activeName;
            data["count"] = arr.Count;
            data["configurations"] = arr;
            return data;
        }

        // xae_set_configuration -- activate one solution configuration by name.
        //
        // `name` may be the bare configuration ("Release") or the full form
        // ("Release|TwinCAT OS (x64)"); `platform` names the platform separately. A miss
        // lists what exists, like every other choice in this server.
        private static Json.JObj XaeSetConfiguration(ActionContext ctx)
        {
            string wanted = ctx.Require("name");
            string platform = ctx.Payload.Truthy("platform") ? ctx.Payload.Str("platform") : null;

            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            dynamic solutionBuild = dte.Solution.SolutionBuild;
            dynamic configurations = null;
            try { configurations = solutionBuild.SolutionConfigurations; }
            catch { }
            if (configurations == null) throw new BridgeException("SolutionConfigurations is not available on this solution");

            // Split a full "Config|Platform" so both spellings reach the same match.
            if (string.IsNullOrWhiteSpace(platform))
            {
                int bar = wanted.IndexOf('|');
                if (bar > 0)
                {
                    platform = wanted.Substring(bar + 1).Trim();
                    wanted = wanted.Substring(0, bar).Trim();
                }
            }

            int count = ComHelpers.SafeInt(delegate { return configurations.Count; }, 0);
            dynamic match = null;
            var have = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                dynamic cfg = null;
                try { cfg = configurations.Item(i); }
                catch { }
                if (cfg == null) continue;
                string cfgName = ComHelpers.SafeStr(delegate { return cfg.Name; });
                string cfgPlatform = PlatformName(cfg);
                have.Add(ConfigurationFullName(cfg));
                if (!string.Equals(cfgName, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(platform) &&
                    !string.Equals(cfgPlatform, platform, StringComparison.OrdinalIgnoreCase)) continue;
                match = cfg;
                break;
            }

            if (match == null)
            {
                string want = wanted + (string.IsNullOrWhiteSpace(platform) ? "" : "|" + platform);
                throw new BridgeException("No solution configuration matches '" + want + "'. Configurations: " +
                    (have.Count == 0 ? "none" : string.Join("; ", have.ToArray())) + ".");
            }

            match.Activate();

            var data = new Json.JObj();
            data["solution"] = solution;
            data["activated"] = ConfigurationFullName(match);
            data["active"] = ActiveConfigurationName(solutionBuild);
            return data;
        }

        // "Config|Platform" for one SolutionConfiguration, or just the name when the
        // platform is unreadable (SolutionConfiguration2.PlatformName is EnvDTE80; a
        // plain SolutionConfiguration has no platform at all).
        private static string ConfigurationFullName(dynamic cfg)
        {
            string name = ComHelpers.SafeStr(delegate { return cfg.Name; });
            string platform = PlatformName(cfg);
            if (string.IsNullOrWhiteSpace(name)) return null;
            return string.IsNullOrWhiteSpace(platform) ? name : name + "|" + platform;
        }

        // EnvDTE80.SolutionConfiguration2.PlatformName is NOT reachable through raw
        // IDispatch -- the same blind spot as ToolWindows.ErrorList below. Measured on
        // an AUT_Core solution: late binding returned nothing for every entry, so the
        // list came back as seven identical "Debug" rows and the platform, the whole
        // reason to read configurations at all, was invisible. The typed cast reads it.
        private static string PlatformName(dynamic cfg)
        {
            string viaDispatch = ComHelpers.SafeStr(delegate { return cfg.PlatformName; });
            if (!string.IsNullOrWhiteSpace(viaDispatch)) return viaDispatch;

            IntPtr pUnk = IntPtr.Zero;
            try
            {
                VsInterop.EnsureResolver();
                pUnk = Marshal.GetIUnknownForObject((object)cfg);
                var typed = (EnvDTE80.SolutionConfiguration2)Marshal.GetTypedObjectForIUnknown(pUnk, typeof(EnvDTE80.SolutionConfiguration2));
                return typed == null ? null : typed.PlatformName;
            }
            catch { return null; }
            finally { if (pUnk != IntPtr.Zero) Marshal.Release(pUnk); }
        }

        internal static string ActiveConfigurationName(dynamic solutionBuild)
        {
            try
            {
                dynamic active = solutionBuild.ActiveConfiguration;
                if (active == null) return null;
                return ConfigurationFullName(active);
            }
            catch { return null; }
        }

        // xae_shutdown_ide -- close the IDE this session is driving.
        //
        // Without this the daemon had no way to end an IDE it had started: killing the
        // daemon leaves devenv running, holding the solution open, invisible to the next
        // run (verified). A long-lived automation service that cannot put back what it
        // started leaks one IDE process per session.
        //
        // Quit() alone is not enough: with dirty documents it raises the modal "Save
        // changes?" prompt, which is exactly the thing that wedges a headless daemon. So
        // the dirty state is settled FIRST -- saved when save is true (the default),
        // discarded when it is false -- the solution is closed with saveFirst:false, and
        // only then does Quit() run, with nothing left to prompt about.
        //
        // Never creates an IDE just to close it: mode is forced to "active", so with no
        // IDE running this reports alreadyClosed instead of starting one.
        private static Json.JObj XaeShutdownIde(ActionContext ctx)
        {
            bool save = true;
            if (ctx.Payload.Has("save")) save = ctx.Payload.Bool("save");

            dynamic dte;
            try
            {
                dte = ctx.DteActiveOnly();
            }
            catch (Exception)
            {
                dte = null;
            }
            if (dte == null)
            {
                var none = new Json.JObj();
                none["alreadyClosed"] = true;
                none["quit"] = false;
                return none;
            }

            string closedSolution = null;
            try { closedSolution = GetSolutionInfo(dte).Str("fullName"); }
            catch { }

            bool saved = false;
            if (save)
            {
                try { dte.ExecuteCommand("File.SaveAll"); saved = true; }
                catch (Exception ex) { Log.Error("shutdown: SaveAll failed", ex); }
            }

            // saveFirst:false either way -- the save above already happened, or the
            // caller asked to discard. Passing true here is what summons the prompt.
            bool solutionClosed = false;
            try { dte.Solution.Close(false); solutionClosed = true; }
            catch (Exception ex) { Log.Error("shutdown: Solution.Close failed", ex); }

            bool quit = false;
            string quitError = null;
            try { dte.Quit(); quit = true; }
            catch (Exception ex)
            {
                // A Quit() that races the IDE tearing down its COM server reports an
                // RPC failure although the IDE is on its way out; report it, do not throw.
                quitError = ex.GetType().Name + ": " + ex.Message;
                Log.Error("shutdown: Quit failed", ex);
            }

            // The cached DTE points at an IDE that is gone; the next call must reconnect.
            ctx.InvalidateSession();

            var data = new Json.JObj();
            data["quit"] = quit;
            data["saved"] = saved;
            data["solutionClosed"] = solutionClosed;
            if (!string.IsNullOrEmpty(closedSolution)) data["solution"] = closedSolution;
            if (!string.IsNullOrEmpty(quitError)) data["quitError"] = quitError;
            return data;
        }

        // xae_clear_error_list (L3957-3970).
        private static Json.JObj XaeClearErrorList(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            Json.JObj showResult = InvokeDteCommand(dte, "View.ErrorList");
            Json.JObj clearResult = InvokeDteCommand(dte, "OtherContextMenus.ErrorList.Clear");

            var data = new Json.JObj();
            data["cleared"] = true;
            data["showCommand"] = showResult;
            data["clearCommand"] = clearResult;
            return data;
        }

        // vsWindowKindOutput. The Output window is reached by its kind GUID when
        // dte.ToolWindows.OutputWindow is not available.
        private const string VsWindowKindOutput = "{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}";

        private static dynamic GetOutputWindowPanes(dynamic dte)
        {
            string firstError = null;
            try
            {
                dynamic ow = dte.ToolWindows.OutputWindow;
                if (ow != null) return ow.OutputWindowPanes;
            }
            catch (Exception ex) { firstError = ex.Message; }

            // An IDE that has never shown the Output window can fail the ToolWindows
            // route; the window itself still exists and answers through its kind GUID.
            try
            {
                dynamic win = dte.Windows.Item(VsWindowKindOutput);
                dynamic ow = win.Object;
                if (ow != null) return ow.OutputWindowPanes;
            }
            catch (Exception ex)
            {
                throw new BridgeException("Output window unreachable: " +
                    (firstError == null ? "" : firstError + " / ") + ex.Message);
            }
            throw new BridgeException("Output window unreachable" +
                (firstError == null ? "." : ": " + firstError));
        }

        // xae_get_output -- the text of an Output window pane, newest lines last.
        //
        // The cure for the build that fails without saying why: a solution build that
        // breaks inside one PLC project can leave a SINGLE Error List row -- "'TwinCAT
        // XAE': Project 'X' build for platform 'Y' failed." -- with no file, no code and
        // no compiler row. The reason is in the Output window, which nothing here could
        // read, so the only way to a diagnosis was opening that project's own solution
        // and building it there by hand.
        //
        // The pane list is always returned, and a pane that is not there is an error
        // that names the ones that ARE -- the attach / select_project contract one level
        // down, never a silent fallback onto some other pane. Matching is exact first,
        // then case-insensitive substring, because pane names are localized.
        private static Json.JObj XaeGetOutput(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            string wanted = ctx.Payload.Truthy("pane") ? ctx.Payload.Str("pane") : "Build";
            int tail = ctx.Payload.Has("tail") ? ctx.Payload.Int("tail", 200) : 200;
            if (tail <= 0) tail = 200;
            if (tail > 20000) tail = 20000;

            dynamic panes = GetOutputWindowPanes(dte);

            int count;
            try { count = ComHelpers.ToInt(panes.Count); }
            catch (Exception ex) { throw new BridgeException("OutputWindowPanes.Count failed: " + ex.Message); }

            var names = new Json.JArr();
            var exact = new List<dynamic>();
            var exactNames = new List<string>();
            var partial = new List<dynamic>();
            var partialNames = new List<string>();

            for (int i = 1; i <= count; i++)
            {
                dynamic p;
                try { p = panes.Item(i); }
                catch { continue; }

                string n = null;
                try { n = (string)p.Name; }
                catch { }
                names.Add(n);
                if (string.IsNullOrEmpty(n)) continue;

                if (string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    exact.Add(p); exactNames.Add(n);
                }
                else if (n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    partial.Add(p); partialNames.Add(n);
                }
            }

            List<dynamic> hits = exact.Count > 0 ? exact : partial;
            List<string> hitNames = exact.Count > 0 ? exactNames : partialNames;

            if (hits.Count == 0)
                throw new BridgeException("No Output pane matches '" + wanted + "'. Panes: " +
                                          string.Join(", ", NamesOf(names)) + ".");
            if (hits.Count > 1)
                throw new BridgeException("'" + wanted + "' matches several Output panes: " +
                                          string.Join(", ", hitNames.ToArray()) +
                                          ". Name one of them exactly.");

            dynamic pane = hits[0];
            string paneName = hitNames[0];

            string text;
            try
            {
                dynamic doc = pane.TextDocument;
                dynamic ep = doc.StartPoint.CreateEditPoint();
                text = (string)ep.GetText(doc.EndPoint);
            }
            catch (Exception ex)
            {
                // Not every pane backs its content with a TextDocument -- the Source Control
                // panes answer E_FAIL. That is a property of the pane, not a fault of the
                // call, so say which pane and that another one may well work.
                throw new BridgeException("Pane '" + paneName + "' has no readable text (" +
                    ex.Message + "). Not every Output pane exposes a TextDocument; try another. Panes: " +
                    string.Join(", ", NamesOf(names)) + ".");
            }
            if (text == null) text = "";

            string[] all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // The pane text ends with a newline, so the split leaves empty tail entries.
            int total = all.Length;
            while (total > 0 && string.IsNullOrEmpty(all[total - 1])) total--;
            int start = total > tail ? total - tail : 0;

            var lines = new Json.JArr();
            for (int i = start; i < total; i++) lines.Add(all[i]);

            var data = new Json.JObj();
            data["pane"] = paneName;
            data["panes"] = names;
            data["lineCount"] = total;
            data["returned"] = lines.Count;
            data["truncated"] = start > 0;
            data["lines"] = lines;
            return data;
        }

        private static string[] NamesOf(Json.JArr names)
        {
            var list = new List<string>();
            foreach (object o in names)
            {
                string s = o as string;
                list.Add(string.IsNullOrEmpty(s) ? "(unnamed)" : s);
            }
            return list.ToArray();
        }

        // xae_save_all (L5306-5317): Save-Solution (File.SaveAll) then SolutionInfo.
        private static Json.JObj XaeSaveAll(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            int budget = ctx.Payload.Has("timeoutMs") ? ctx.Payload.Int("timeoutMs", SaveSettleMs) : SaveSettleMs;

            var data = SaveAllAndSettle(dte, budget);
            data["solution"] = GetSolutionInfo(dte);
            return data;
        }

        // How long a save is given to settle before it is reported unsettled.
        internal const int SaveSettleMs = 15000;

        // File.SaveAll is a shell COMMAND, not a method call: ExecuteCommand QUEUES it and
        // returns before the save has landed. That is the whole of the "save:true does not
        // put the .plcproj on disk" defect -- a create with save:true followed straight away
        // by open_solution discardChanges:true discarded a project registration that had not
        // been written yet, and the type then failed to resolve everywhere with "Unknown
        // type: 'X'", a symptom that reads like a broken library reference rather than a lost
        // <Compile Include>. The same sequence with a SEPARATE xae save_all call worked for
        // one reason only: two pipe round trips let time pass.
        //
        // So: issue the command, then WAIT for the IDE to report itself clean, and say what
        // was observed. A save still dirty at the end of its budget is reported as unsettled
        // rather than claimed as done -- the caller can act on that instead of discovering it
        // three calls later. Projects that stay dirty are asked to save themselves directly,
        // since Project.Save() is a call and not a queued command.
        internal static Json.JObj SaveAllAndSettle(dynamic dte, int timeoutMs)
        {
            if (timeoutMs <= 0) timeoutMs = SaveSettleMs;
            if (timeoutMs > 120000) timeoutMs = 120000;

            var data = new Json.JObj();
            try { dte.ExecuteCommand("File.SaveAll"); }
            catch (Exception ex)
            {
                data["saved"] = false;
                data["saveError"] = ex.GetType().Name + ": " + ex.Message;
                return data;
            }

            var sw = Stopwatch.StartNew();
            Json.JArr dirty = UnsavedItems(dte);
            bool nudged = false;
            while (dirty.Count > 0 && sw.ElapsedMilliseconds < timeoutMs)
            {
                // Halfway through the budget, stop waiting on the queued command and ask the
                // dirty projects directly. Project.Save() is a call, so it has finished when
                // it returns; the command may still be behind other work in the shell queue.
                if (!nudged && sw.ElapsedMilliseconds > timeoutMs / 2)
                {
                    nudged = true;
                    SaveDirtyProjects(dte);
                }
                Thread.Sleep(100);
                dirty = UnsavedItems(dte);
            }
            sw.Stop();

            bool settled = dirty.Count == 0;
            data["saved"] = settled;
            data["settled"] = settled;
            data["waitedMs"] = (int)sw.ElapsedMilliseconds;
            if (nudged) data["savedProjectsDirectly"] = true;
            if (!settled)
            {
                data["stillDirty"] = dirty;
                data["warning"] = "File.SaveAll was issued but the IDE still reports unsaved items after " +
                    ((int)sw.ElapsedMilliseconds) + " ms. Do NOT reopen the solution with discardChanges " +
                    "until this settles: what is still dirty would be thrown away.";
            }
            return data;
        }

        // Everything the IDE still considers unsaved: open documents, the solution file, and
        // the projects. Anything whose Saved cannot be read is skipped rather than guessed at
        // -- a property that fails to answer is not evidence of a dirty file.
        private static Json.JArr UnsavedItems(dynamic dte)
        {
            var dirty = new Json.JArr();

            try
            {
                dynamic docs = dte.Documents;
                int n = ComHelpers.ToInt(docs.Count);
                for (int i = 1; i <= n; i++)
                {
                    dynamic d;
                    try { d = docs.Item(i); }
                    catch { continue; }
                    bool saved;
                    try { saved = (bool)d.Saved; }
                    catch { continue; }
                    if (!saved) dirty.Add(ComHelpers.SafeStr(delegate { return d.FullName; }));
                }
            }
            catch { }

            try
            {
                dynamic sol = dte.Solution;
                bool solSaved;
                try { solSaved = (bool)sol.Saved; }
                catch { solSaved = true; }
                if (!solSaved) dirty.Add(ComHelpers.SafeStr(delegate { return sol.FullName; }));

                dynamic projects = sol.Projects;
                int pn = ComHelpers.ToInt(projects.Count);
                for (int i = 1; i <= pn; i++)
                {
                    dynamic p;
                    try { p = projects.Item(i); }
                    catch { continue; }
                    bool saved;
                    try { saved = (bool)p.Saved; }
                    catch { continue; }
                    if (!saved) dirty.Add(ComHelpers.SafeStr(delegate { return p.FullName; }));
                }
            }
            catch { }

            return dirty;
        }

        private static void SaveDirtyProjects(dynamic dte)
        {
            try
            {
                dynamic projects = dte.Solution.Projects;
                int pn = ComHelpers.ToInt(projects.Count);
                for (int i = 1; i <= pn; i++)
                {
                    dynamic p;
                    try { p = projects.Item(i); }
                    catch { continue; }
                    bool saved;
                    try { saved = (bool)p.Saved; }
                    catch { continue; }
                    if (saved) continue;
                    try { p.Save(""); }
                    catch (Exception ex) { Log.Error("SaveAllAndSettle: Project.Save failed", ex); }
                }
            }
            catch (Exception ex) { Log.Error("SaveAllAndSettle: enumerating projects failed", ex); }
        }

        // xae_solution_build (L5436-5502).
        private static Json.JObj XaeSolutionBuild(ActionContext ctx)
        {
            string actionName = ctx.Payload.Str("action");
            if (string.IsNullOrWhiteSpace(actionName)) throw new BridgeException("action is required");

            bool waitForFinish = true;
            if (ctx.Payload.Has("waitForFinish")) waitForFinish = ctx.Payload.Bool("waitForFinish");

            int timeoutMs = 1800000;
            if (ctx.Payload.Has("timeoutMs")) timeoutMs = ctx.Payload.Int("timeoutMs", 1800000);

            string project = ctx.Payload.Truthy("project") ? ctx.Payload.Str("project") : null;
            string configuration = ctx.Payload.Truthy("configuration") ? ctx.Payload.Str("configuration") : null;

            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            dynamic solutionBuild = dte.Solution.SolutionBuild;

            // ONE project of the solution instead of all of it. A repo whose library and
            // TcUnit suite sit in the same solution rebuilds both on every edit, and a
            // failure in the one you are not working on stops the build you asked for.
            if (!string.IsNullOrWhiteSpace(project))
            {
                if (actionName != "build")
                    throw new BridgeException("Only action 'build' takes a project: EnvDTE has no per-project clean or rebuild (SolutionBuild.Clean is solution-wide). Run clean/rebuild without 'project', or build the project after a solution clean.");

                string uniqueName = ResolveProjectUniqueName(dte, project);
                string configName = string.IsNullOrWhiteSpace(configuration)
                    ? ActiveConfigurationName(solutionBuild)
                    : configuration;
                if (string.IsNullOrWhiteSpace(configName))
                    throw new BridgeException("No active solution configuration to build with; pass configuration (see xae list_configurations)");

                solutionBuild.BuildProject(configName, uniqueName, waitForFinish);

                Json.JObj projectBuild = new Json.JObj();
                projectBuild["buildState"] = ComHelpers.SafeInt(delegate { return solutionBuild.BuildState; }, 0);
                projectBuild["lastBuildInfo"] = null;
                if (waitForFinish) projectBuild = WaitForBuildFinish(solutionBuild, timeoutMs);
                else { try { projectBuild["lastBuildInfo"] = (int)solutionBuild.LastBuildInfo; } catch { } }

                var projectData = new Json.JObj();
                projectData["action"] = actionName;
                projectData["waited"] = waitForFinish;
                projectData["solution"] = solution;
                projectData["project"] = uniqueName;
                projectData["configuration"] = configName;
                projectData["build"] = projectBuild;
                return projectData;
            }

            if (!string.IsNullOrWhiteSpace(configuration))
                throw new BridgeException("configuration only applies with 'project'; to build the whole solution in another configuration activate it first with xae set_configuration");

            switch (actionName)
            {
                case "clean":
                    solutionBuild.Clean(waitForFinish);
                    break;
                case "build":
                    solutionBuild.Build(waitForFinish);
                    break;
                case "rebuild":
                    solutionBuild.Clean(waitForFinish);
                    if (waitForFinish)
                    {
                        WaitForBuildFinish(solutionBuild, timeoutMs);
                    }
                    solutionBuild.Build(waitForFinish);
                    break;
                default:
                    throw new BridgeException("Unsupported build action: " + actionName);
            }

            Json.JObj buildResult = new Json.JObj();
            buildResult["buildState"] = (int)solutionBuild.BuildState;
            buildResult["lastBuildInfo"] = null;

            if (waitForFinish)
            {
                buildResult = WaitForBuildFinish(solutionBuild, timeoutMs);
            }
            else
            {
                try { buildResult["lastBuildInfo"] = (int)solutionBuild.LastBuildInfo; }
                catch { }
            }

            var data = new Json.JObj();
            data["action"] = actionName;
            data["waited"] = waitForFinish;
            data["solution"] = solution;
            data["configuration"] = ActiveConfigurationName(solutionBuild);
            data["build"] = buildResult;
            return data;
        }

        // The UniqueName EnvDTE wants for SolutionBuild.BuildProject, from whatever the
        // caller had at hand: the project name, its UniqueName, or its file path. Walks
        // solution folders too, and covers every project kind (a solution can hold C++
        // and HMI projects next to the .tsproj), which is why it does not reuse
        // ComSession.ListProjects -- that one is deliberately .tsproj only.
        private static string ResolveProjectUniqueName(dynamic dte, string wanted)
        {
            var candidates = new List<Json.JObj>();
            CollectSolutionProjects(dte.Solution.Projects, candidates);

            foreach (var p in candidates)
            {
                if (string.Equals(p.Str("uniqueName"), wanted, StringComparison.OrdinalIgnoreCase)) return p.Str("uniqueName");
            }
            foreach (var p in candidates)
            {
                if (string.Equals(p.Str("name"), wanted, StringComparison.OrdinalIgnoreCase)) return p.Str("uniqueName");
            }
            foreach (var p in candidates)
            {
                if (PathUtil.SamePath(p.Str("fullName"), wanted)) return p.Str("uniqueName");
            }

            var have = new List<string>();
            foreach (var p in candidates) have.Add(p.Str("name"));
            throw new BridgeException("No project named '" + wanted + "' in this solution. Projects: " +
                (have.Count == 0 ? "none" : string.Join("; ", have.ToArray())) + ".");
        }

        private static void CollectSolutionProjects(dynamic projects, List<Json.JObj> into)
        {
            if (projects == null) return;
            int count = ComHelpers.SafeInt(delegate { return projects.Count; }, 0);
            for (int i = 1; i <= count; i++)
            {
                dynamic project = null;
                try { project = projects.Item(i); }
                catch { }
                if (project != null) CollectOneProject(project, into);
            }
        }

        // One project, plus anything nested under it: a solution folder holds its
        // projects as ProjectItems[].SubProject, so the recursion is what finds them.
        private static void CollectOneProject(dynamic project, List<Json.JObj> into)
        {
            string unique = ComHelpers.SafeStr(delegate { return project.UniqueName; });
            string fullName = ComHelpers.SafeStr(delegate { return project.FullName; });
            if (!string.IsNullOrWhiteSpace(unique) && !string.IsNullOrWhiteSpace(fullName))
            {
                var o = new Json.JObj();
                o["name"] = ComHelpers.SafeStr(delegate { return project.Name; });
                o["uniqueName"] = unique;
                o["fullName"] = fullName;
                into.Add(o);
            }
            dynamic items = null;
            try { items = project.ProjectItems; }
            catch { }
            if (items == null) return;
            int n = ComHelpers.SafeInt(delegate { return items.Count; }, 0);
            for (int j = 1; j <= n; j++)
            {
                dynamic sub = null;
                try { sub = items.Item(j).SubProject; }
                catch { }
                if (sub != null) CollectOneProject(sub, into);
            }
        }

        // ---- error-list reading (port of XaeErrorListProbe, bridge L205-260) -
        // Reads the DTE ToolWindows ErrorList through the STRONGLY-TYPED DTE2 cast,
        // exactly like the PS bridge's XaeErrorListProbe. Raw IDispatch late
        // binding (`dynamic dte.ToolWindows.ErrorList`) returns NULL on TcXaeShell
        // — EnvDTE80.ToolWindows.get_ErrorList is not reachable that way — so the
        // earlier dynamic port always reported the list "unavailable". The typed
        // cast (GetTypedObjectForIUnknown → DTE2) returns a live ErrorList. The
        // EnvDTE PIAs are loaded at runtime by VsInterop. Returns null + an error
        // string on failure (matches the PS {available:false} path). Item key order
        // matches the PS handler at L3935-3942: description, fileName, line, column,
        // project, errorLevel.
        private sealed class ErrorListResult
        {
            public int TotalCount;
            public Json.JArr Items;
            // How many matched rows matched only because their severity was
            // undecidable (TwinCAT PLC rows). Zero when no filter is active.
            public int AmbiguousCount;
        }

        // A row that came from a TwinCAT PLC project: its Project ends in .plcproj.
        // These are the rows whose ErrorLevel carries no severity -- see SeverityMatches.
        private static bool IsPlcProjectRow(string project)
        {
            if (string.IsNullOrEmpty(project)) return false;
            return project.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase);
        }

        // R6: ErrorLevel serializes as the strings "vsBuildErrorLevelHigh"/"Medium"/
        // "Low" (High = error, Medium = warning, Low = message) — NOT "Error". Match
        // on the substring so each filter maps to exactly one documented severity:
        // errors -> High, warnings -> Medium. Low (message) rows show only under 'all'
        // (folding them into 'warnings' would inflate the warning count).
        //
        // That mapping holds for C++/C#/HMI projects. It does NOT hold for TwinCAT PLC
        // projects, measured on TC 3.1.4026: the PLC compiler reports EVERY row at
        // vsBuildErrorLevelMedium -- a real error ("Identifier 'x' not defined"), an
        // explicit {warning '...'} pragma and an explicit {info '...'} pragma all come
        // back Medium. High never appears on a .plcproj row at all, and Low is what the
        // XAE shell uses for its own progress messages. So on those rows the level says
        // nothing about severity, and dropping them under filter "errors" made
        // severityFilter:"errors" return ZERO on every TwinCAT project -- the bug this
        // replaces. A .plcproj row therefore matches BOTH "errors" and "warnings", and
        // reports itself as ambiguous so the caller is told the filter could not
        // discriminate instead of silently trusting a filtered list.
        //
        // VS itself DOES know the real severity -- its Error List shows "Warning C0373"
        // and "Error C0046" on the very rows that come back Medium here. That severity
        // lives on IVsErrorItem.GetCategory, behind SVsErrorList, and is NOT reachable
        // from here: the IVs* shell interfaces have no cross-process marshalling, and a
        // QI for OLE IServiceProvider on an out-of-process DTE fails outright (probed
        // 2026-08-27). Only in-process code -- a VSIX -- can read it. EnvDTE's ErrorItem
        // exposes no Code and no Severity, so for an external automation client the
        // ambiguity below is not a shortcut, it is the whole of what is knowable.
        private static bool SeverityMatches(string level, string filter, string project, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrEmpty(filter) || filter == "all") return true;
            if (IsPlcProjectRow(project))
            {
                ambiguous = true;
                return true;
            }
            if (level == null) return false;
            if (filter == "errors") return level.IndexOf("High", StringComparison.OrdinalIgnoreCase) >= 0;
            if (filter == "warnings") return level.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0;
            return true;
        }

        private static ErrorListResult ReadErrorList(ActionContext ctx, int limit, string severityFilter, out string error)
        {
            error = null;
            IntPtr pUnk = IntPtr.Zero;
            try
            {
                // Acquire the DTE inside the try so a dead/absent XAE (ctx.Dte
                // throws) takes the graceful {available:false, error:...} path
                // instead of escaping as a hard com_error.
                object rawDte = ctx.Dte(true);
                pUnk = Marshal.GetIUnknownForObject(rawDte);
                EnvDTE80.DTE2 dte = (EnvDTE80.DTE2)Marshal.GetTypedObjectForIUnknown(pUnk, typeof(EnvDTE80.DTE2));

                try { dte.ExecuteCommand("View.ErrorList", " "); }
                catch { }
                System.Threading.Thread.Sleep(1000);

                EnvDTE80.ErrorList errorList = dte.ToolWindows.ErrorList;
                if (errorList == null) { error = "ToolWindows.ErrorList returned null"; return null; }

                try { errorList.ShowErrors = true; } catch { }
                try { errorList.ShowWarnings = true; } catch { }
                try { errorList.ShowMessages = true; } catch { }

                EnvDTE80.ErrorItems errorItems = errorList.ErrorItems;
                int rawCount = errorItems.Count;

                // R6: with a severity filter active, walk ALL items so `count` reflects
                // the true matching total (one COM ErrorLevel read per item). Without a
                // filter the total IS rawCount, so stop collecting at `limit` and skip
                // the full walk — keeps the common build-flood path bounded (R7).
                bool filtering = !(string.IsNullOrEmpty(severityFilter) || severityFilter == "all");
                int matchedTotal = 0;
                int ambiguousTotal = 0;
                var items = new Json.JArr();
                for (int i = 1; i <= rawCount; i++)
                {
                    if (!filtering && items.Count >= limit) break; // total is rawCount; no need to walk on
                    EnvDTE80.ErrorItem item = errorItems.Item(i);
                    string level = ComHelpers.SafeStr(delegate() { return item.ErrorLevel; });
                    // Project is read before the filter now: on a TwinCAT PLC row it is
                    // what decides the match, because the level cannot.
                    string project = ComHelpers.SafeStr(delegate() { return item.Project; });
                    bool ambiguous;
                    if (!SeverityMatches(level, severityFilter, project, out ambiguous)) continue;
                    matchedTotal++;
                    if (ambiguous) ambiguousTotal++;
                    if (items.Count >= limit) continue; // keep counting, stop collecting
                    var o = new Json.JObj();
                    o["description"] = ComHelpers.SafeStr(delegate() { return item.Description; });
                    o["fileName"] = ComHelpers.SafeStr(delegate() { return item.FileName; });
                    o["line"] = NullableInt(delegate() { return item.Line; });
                    o["column"] = NullableInt(delegate() { return item.Column; });
                    o["project"] = project;
                    o["errorLevel"] = level;
                    if (ambiguous) o["severityUndecidable"] = true;
                    items.Add(o);
                }

                ErrorListResult result = new ErrorListResult();
                result.TotalCount = filtering ? matchedTotal : rawCount;
                result.Items = items;
                result.AmbiguousCount = ambiguousTotal;
                return result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Log.Error("ReadErrorList failed", ex);
                return null;
            }
            finally
            {
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
        }

        // Get-SafeValue { [int]$x } / Normalize-ScalarValue: a value that fails to
        // read becomes null (not 0), matching the PS shape.
        private static object NullableInt(Func<object> f)
        {
            try
            {
                object v = f();
                if (v == null) return null;
                return (object)ComHelpers.ToInt(v);
            }
            catch { return null; }
        }
    }
}
