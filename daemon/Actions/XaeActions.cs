using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
        private static Json.JObj WaitForBuildFinish(dynamic solutionBuild, int timeoutMs)
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
            data["solution"] = solution;
            data["automationSettingsAvailable"] = automationAvailable;
            data["sysManagerAvailable"] = sysManagerAvailable;
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

        // xae_save_all (L5306-5317): Save-Solution (File.SaveAll) then SolutionInfo.
        private static Json.JObj XaeSaveAll(ActionContext ctx)
        {
            dynamic dte = ctx.Dte(true);
            dte.ExecuteCommand("File.SaveAll");

            var data = new Json.JObj();
            data["saved"] = true;
            data["solution"] = GetSolutionInfo(dte);
            return data;
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

            dynamic dte = ctx.Dte(true);
            Json.JObj solution = GetSolutionInfo(dte);
            if (!solution.Bool("isOpen")) throw new BridgeException("No solution is open in XAE");

            dynamic solutionBuild = dte.Solution.SolutionBuild;

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
            data["build"] = buildResult;
            return data;
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
