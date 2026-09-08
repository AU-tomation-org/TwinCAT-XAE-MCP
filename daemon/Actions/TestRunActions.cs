using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Te1000Daemon
{
    // plc_run_tests -- run the TcUnit suite on the target and return the numbers.
    //
    // This is the piece that kept the CI shelling out to TcCIBuilder for the verdict: the
    // server could compile and read the Error List, but not say whether the tests passed.
    // The sequence is the one proven by hand:
    //
    //   1. set the PLC project's boot autostart flag;
    //   2. build, and stop on a non-zero lastBuildInfo -- a suite that did not compile
    //      cannot be run, and running it anyway would report the PREVIOUS results;
    //   3. DELETE <runtime>\Boot\tcunit_xunit_testresults.xml before activating. Without
    //      this you read the last run's file and have no way to tell: same shape, same
    //      numbers, no clue. This is the step that makes the verdict real;
    //   4. activate + restart the runtime;
    //   5. wait for the file to come back, and parse it.
    //
    // Two things the parser has to get right, both measured:
    //
    //   * the `tests` attribute on the root <testsuites> DOES NOT COUNT THE FAILURES. A run
    //     with 3 red wrote tests="65" failures="3" while its <testsuite> elements summed to
    //     68. So the total is summed from the suites, and the root attribute is reported
    //     separately as what the file claims -- when the two disagree, that IS the finding.
    //   * the same numbers arrive in the Error List as PlcTask messages ("Successful tests:
    //     N" / "Failed tests: N") at severity HIGH, which the PLC compiler never uses. They
    //     are read as an independent cross-check: two sources agreeing is a verdict, one
    //     source is a reading.
    //
    // ACTIVATION REPLACES WHAT IS RUNNING ON THE TARGET. That is said in the result, every
    // time, because putting back what was there is the caller's job and is easy to forget.
    //
    // C#5-clean (no interpolation, no out var, no expression-bodied members).
    internal static class TestRunActions
    {
        public const string TestRunConfirmation = "ALLOW_PLC_TESTS";

        public static void Register(Dictionary<string, ActionHandler> h)
        {
            h["plc_run_tests"] = RunTests;
            h["plc_test_results"] = ReadResults;
        }

        private const string ResultsFileName = "tcunit_xunit_testresults.xml";
        private const int DefaultWaitMs = 600000;

        // ---- boot directory ------------------------------------------------
        //
        // Which runtime's Boot directory belongs to this target. A user-mode runtime lives
        // in <ProgramData>\Beckhoff\TwinCAT\3.1\Runtimes\<name>\3.1\Boot, and there are
        // several on an engineering host (UmRT_Default, UmRT_Machine, UmRT_DT here), so
        // picking one by convention would silently read another machine's results.
        //
        // Each runtime's TcRegistry.xml carries its own AmsNetId, as BINARY hex:
        // C7042AFA0101 is 199.4.42.250.1.1. So the directory is MATCHED to the target
        // NetId the system manager reports, and when nothing matches that is an error
        // listing what was found -- never a fallback to the first one.
        private static readonly string[] RuntimeRoots = new string[]
        {
            @"C:\ProgramData\Beckhoff\TwinCAT\3.1\Runtimes",
        };

        private static string NetIdFromRegistry(string tcRegistryPath)
        {
            try
            {
                string text = File.ReadAllText(tcRegistryPath);
                int i = text.IndexOf("AmsNetId", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                int open = text.IndexOf('>', i);
                int close = open < 0 ? -1 : text.IndexOf('<', open);
                if (open < 0 || close < 0) return null;
                string hex = text.Substring(open + 1, close - open - 1).Trim();
                if (hex.Length != 12) return null;
                var parts = new List<string>();
                for (int k = 0; k < 12; k += 2)
                {
                    int b;
                    if (!int.TryParse(hex.Substring(k, 2), NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out b)) return null;
                    parts.Add(b.ToString(CultureInfo.InvariantCulture));
                }
                return string.Join(".", parts.ToArray());
            }
            catch { return null; }
        }

        private sealed class RuntimeDir
        {
            public string Name;
            public string NetId;
            public string BootDir;
        }

        private static List<RuntimeDir> FindRuntimes()
        {
            var found = new List<RuntimeDir>();
            foreach (string root in RuntimeRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string reg = Path.Combine(dir, @"3.1\TcRegistry.xml");
                    string boot = Path.Combine(dir, @"3.1\Boot");
                    if (!File.Exists(reg) || !Directory.Exists(boot)) continue;
                    var r = new RuntimeDir();
                    r.Name = Path.GetFileName(dir);
                    r.NetId = NetIdFromRegistry(reg);
                    r.BootDir = boot;
                    found.Add(r);
                }
            }
            return found;
        }

        private static string ResolveBootDir(ActionContext ctx, string targetNetId, Json.JObj report)
        {
            string given = ctx.Payload.Truthy("bootDir") ? ctx.Payload.Str("bootDir") : null;
            if (!string.IsNullOrWhiteSpace(given))
            {
                if (!Directory.Exists(given))
                    throw new BridgeException("bootDir '" + given + "' does not exist.");
                report["bootDirVia"] = "bootDir";
                return given;
            }

            List<RuntimeDir> runtimes = FindRuntimes();
            var listed = new Json.JArr();
            string hit = null;
            foreach (RuntimeDir r in runtimes)
            {
                var o = new Json.JObj();
                o["name"] = r.Name;
                o["netId"] = r.NetId;
                o["bootDir"] = r.BootDir;
                listed.Add(o);
                if (!string.IsNullOrWhiteSpace(targetNetId) &&
                    string.Equals(r.NetId, targetNetId, StringComparison.Ordinal)) hit = r.BootDir;
            }
            report["runtimesFound"] = listed;

            // The local (non user-mode) runtime, for a target that is this machine's own.
            const string localBoot = @"C:\ProgramData\Beckhoff\TwinCAT\3.1\Boot";
            if (hit == null && Directory.Exists(localBoot))
            {
                string localReg = @"C:\ProgramData\Beckhoff\TwinCAT\3.1\TcRegistry.xml";
                string localNetId = File.Exists(localReg) ? NetIdFromRegistry(localReg) : null;
                var o = new Json.JObj();
                o["name"] = "(local)";
                o["netId"] = localNetId;
                o["bootDir"] = localBoot;
                listed.Add(o);
                if (!string.IsNullOrWhiteSpace(targetNetId) &&
                    string.Equals(localNetId, targetNetId, StringComparison.Ordinal)) hit = localBoot;
            }

            if (hit == null)
                throw new BridgeException("No runtime on this machine reports the target NetId '" +
                    targetNetId + "', so there is no way to know which Boot directory holds the " +
                    "results -- and reading the wrong one would report another target's numbers as " +
                    "these. Pass bootDir explicitly. Found: " + Json.Write(listed) + ".");

            report["bootDirVia"] = "matched on target NetId";
            return hit;
        }

        // ---- the run -------------------------------------------------------

        private static Json.JObj RunTests(ActionContext ctx)
        {
            if (!string.Equals(ctx.Payload.Str("confirm"), TestRunConfirmation, StringComparison.Ordinal))
                throw new BridgeException("Blocked. Running the tests ACTIVATES the configuration and " +
                    "RESTARTS the runtime, which replaces whatever that target was running. Re-run with " +
                    "confirm=\"" + TestRunConfirmation + "\".");

            bool doBuild = !ctx.Payload.Has("build") || ctx.Payload.Bool("build");
            int waitMs = ctx.Payload.Has("waitMs") ? ctx.Payload.Int("waitMs", DefaultWaitMs) : DefaultWaitMs;
            if (waitMs <= 0) waitMs = DefaultWaitMs;
            int buildTimeoutMs = ctx.Payload.Has("buildTimeoutMs") ? ctx.Payload.Int("buildTimeoutMs", 1800000) : 1800000;

            // SysManagerForTargetAction: with several .tsproj open and none chosen this
            // refuses instead of activating a configuration on a machine nobody meant to
            // touch. That guard matters more here than anywhere else in this file.
            dynamic sm = ctx.SysManagerForTargetAction();
            dynamic dte = ctx.Dte(true);

            var data = new Json.JObj();
            string targetNetId = ComHelpers.SafeStr(delegate { return sm.GetTargetNetId(); });
            data["targetNetId"] = targetNetId;
            data["replaces"] = "Activating replaced whatever this target was running. Putting back what " +
                               "was there is the caller's job.";

            string bootDir = ResolveBootDir(ctx, targetNetId, data);
            data["bootDir"] = bootDir;
            string resultsPath = Path.Combine(bootDir, ResultsFileName);
            data["resultsPath"] = resultsPath;

            // ---- 1. boot autostart, remembering what it was ----------------
            string plcRoot = ResolvePlcRoot(ctx, sm);
            data["plcRoot"] = plcRoot;
            dynamic plcItem = ComHelpers.GetTreeItem(sm, plcRoot);

            var flagsBefore = new Json.JObj();
            object[] previous;
            try
            {
                previous = PlcProjectHelper.SetBootFlags((object)plcItem, true, true, false, false);
                flagsBefore["autostartWas"] = Convert.ToBoolean(previous[0]);
                flagsBefore["tmcFileCopy"] = Convert.ToBoolean(previous[1]);
            }
            catch (Exception ex)
            {
                throw new BridgeException("Setting the boot autostart flag on '" + plcRoot + "' failed: " +
                                          ex.Message + " (is that the PLC ROOT node?)");
            }
            data["bootFlags"] = flagsBefore;

            // ---- 2. build ---------------------------------------------------
            if (doBuild)
            {
                XaeActions.SaveAllAndSettle(dte, XaeActions.SaveSettleMs);
                dynamic solutionBuild = dte.Solution.SolutionBuild;
                solutionBuild.Build(true);
                Json.JObj build = XaeActions.WaitForBuildFinish(solutionBuild, buildTimeoutMs);
                build["configuration"] = XaeActions.ActiveConfigurationName(solutionBuild);
                data["build"] = build;

                object lbi = build["lastBuildInfo"];
                int failed = lbi == null ? -1 : ComHelpers.ToInt(lbi);
                if (failed != 0)
                {
                    data["ran"] = false;
                    data["stoppedBecause"] = "The build reported lastBuildInfo=" +
                        (lbi == null ? "null" : lbi.ToString()) + ", so nothing was activated. A suite " +
                        "that did not compile cannot be run, and activating anyway would have left the " +
                        "PREVIOUS results file in place to be read as this run's.";
                    return data;
                }
            }
            else data["build"] = "skipped (build:false)";

            // ---- 3. delete the previous results ----------------------------
            var previousFile = new Json.JObj();
            try
            {
                if (File.Exists(resultsPath))
                {
                    previousFile["existed"] = true;
                    previousFile["lastWriteUtc"] = File.GetLastWriteTimeUtc(resultsPath)
                        .ToString("o", CultureInfo.InvariantCulture);
                    previousFile["length"] = new FileInfo(resultsPath).Length;
                    File.Delete(resultsPath);
                    previousFile["deleted"] = !File.Exists(resultsPath);
                }
                else previousFile["existed"] = false;
            }
            catch (Exception ex)
            {
                throw new BridgeException("Could not delete the previous results file '" + resultsPath +
                    "': " + ex.Message + ". Refusing to run: with the old file still there, a run that " +
                    "produces nothing is indistinguishable from one that passed.");
            }
            if (previousFile.Has("deleted") && !previousFile.Bool("deleted"))
                throw new BridgeException("The previous results file is still at '" + resultsPath +
                    "' after Delete returned. Refusing to run for the same reason.");
            data["previousResults"] = previousFile;

            // ---- 4. activate + restart -------------------------------------
            try { sm.ActivateConfiguration(); data["activated"] = true; }
            catch (Exception ex) { throw new BridgeException("ActivateConfiguration failed: " + ex.Message); }

            try { sm.StartRestartTwinCAT(); data["restarted"] = true; }
            catch (Exception ex) { throw new BridgeException("StartRestartTwinCAT failed: " + ex.Message); }
            ctx.Cache.Invalidate(null);

            // ---- 5. wait for the file, then parse --------------------------
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastLen = -1;
            int stable = 0;
            bool appeared = false;
            while (sw.ElapsedMilliseconds < waitMs)
            {
                System.Threading.Thread.Sleep(500);
                if (!File.Exists(resultsPath)) continue;
                appeared = true;
                long len;
                try { len = new FileInfo(resultsPath).Length; }
                catch { continue; }
                // A file that is still being written is a truncated read, which looks like
                // corrupt XML or -- worse -- like a smaller, passing run.
                if (len > 0 && len == lastLen) { stable++; if (stable >= 2) break; }
                else stable = 0;
                lastLen = len;
            }
            sw.Stop();
            data["waitedMs"] = (int)sw.ElapsedMilliseconds;
            data["resultsAppeared"] = appeared;

            RestoreBootFlags(ctx, plcItem, flagsBefore, data);

            if (!appeared)
            {
                data["ran"] = false;
                data["stoppedBecause"] = "No results file appeared at '" + resultsPath + "' within " +
                    ((int)sw.ElapsedMilliseconds) + " ms. The configuration WAS activated and the " +
                    "runtime restarted, so the target is now running this project. Either the suite " +
                    "does not write xUnit results, the boot project did not start, or waitMs was short.";
                data["errorListTestRows"] = TestRowsFromErrorList(ctx, dte);
                return data;
            }

            data["ran"] = true;
            data["results"] = ParseResults(resultsPath);
            data["errorListTestRows"] = TestRowsFromErrorList(ctx, dte);
            return data;
        }

        private static void RestoreBootFlags(ActionContext ctx, dynamic plcItem, Json.JObj before, Json.JObj data)
        {
            // Put the project's own flag back. Flipping autostart was this action's doing,
            // not the caller's intent, and leaving it flipped changes what the NEXT
            // activation does -- a side effect nobody asked for and nobody would see.
            if (ctx.Payload.Has("keepAutostart") && ctx.Payload.Bool("keepAutostart"))
            {
                data["bootFlagsRestored"] = false;
                return;
            }
            if (!before.Has("autostartWas")) { data["bootFlagsRestored"] = false; return; }
            bool was = before.Bool("autostartWas");
            try
            {
                PlcProjectHelper.SetBootFlags((object)plcItem, true, was, false, false);
                data["bootFlagsRestored"] = true;
            }
            catch (Exception ex)
            {
                data["bootFlagsRestored"] = false;
                data["bootFlagsRestoreError"] = ex.Message;
            }
        }

        private static Json.JObj ReadResults(ActionContext ctx)
        {
            dynamic sm = ctx.SysManager();
            var data = new Json.JObj();
            string targetNetId = ComHelpers.SafeStr(delegate { return sm.GetTargetNetId(); });
            data["targetNetId"] = targetNetId;

            string bootDir = ResolveBootDir(ctx, targetNetId, data);
            string resultsPath = Path.Combine(bootDir, ResultsFileName);
            data["bootDir"] = bootDir;
            data["resultsPath"] = resultsPath;

            if (!File.Exists(resultsPath))
            {
                data["exists"] = false;
                data["note"] = "No results file. Nothing has run on this target, or plc_run_tests " +
                               "deleted it and the run produced none.";
                return data;
            }
            data["exists"] = true;
            data["lastWriteUtc"] = File.GetLastWriteTimeUtc(resultsPath).ToString("o", CultureInfo.InvariantCulture);
            data["warning"] = "This is whatever file is on disk. It says NOTHING about when it was " +
                              "written relative to the code you are asking about -- that is what " +
                              "plc_run_tests deletes it for. Check lastWriteUtc.";
            data["results"] = ParseResults(resultsPath);
            return data;
        }

        // ---- the parser ----------------------------------------------------

        private static Json.JObj ParseResults(string path)
        {
            var o = new Json.JObj();
            XmlDocument doc = new XmlDocument();
            try { doc.Load(path); }
            catch (Exception ex)
            {
                o["parsed"] = false;
                o["error"] = "Could not parse '" + path + "': " + ex.Message;
                return o;
            }
            o["parsed"] = true;

            XmlElement root = doc.DocumentElement;
            if (root == null) { o["parsed"] = false; o["error"] = "empty document"; return o; }

            var claimed = new Json.JObj();
            claimed["tests"] = AttrInt(root, "tests");
            claimed["failures"] = AttrInt(root, "failures");
            claimed["errors"] = AttrInt(root, "errors");
            claimed["skipped"] = AttrInt(root, "skipped");
            o["rootAttributes"] = claimed;

            int tests = 0, failures = 0, errors = 0, skipped = 0;
            var suites = new Json.JArr();
            var failing = new Json.JArr();

            foreach (XmlNode n in doc.GetElementsByTagName("testsuite"))
            {
                XmlElement s = n as XmlElement;
                if (s == null) continue;

                object st = AttrInt(s, "tests");
                object sf = AttrInt(s, "failures");
                object se = AttrInt(s, "errors");
                object sk = AttrInt(s, "skipped");
                tests += st == null ? 0 : (int)st;
                failures += sf == null ? 0 : (int)sf;
                errors += se == null ? 0 : (int)se;
                skipped += sk == null ? 0 : (int)sk;

                var so = new Json.JObj();
                so["name"] = s.GetAttribute("name");
                so["tests"] = st;
                so["failures"] = sf;
                so["errors"] = se;
                so["skipped"] = sk;
                suites.Add(so);

                foreach (XmlNode c in s.ChildNodes)
                {
                    XmlElement tc = c as XmlElement;
                    if (tc == null) continue;
                    if (!string.Equals(tc.LocalName, "testcase", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (XmlNode f in tc.ChildNodes)
                    {
                        XmlElement fe = f as XmlElement;
                        if (fe == null) continue;
                        if (!string.Equals(fe.LocalName, "failure", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(fe.LocalName, "error", StringComparison.OrdinalIgnoreCase)) continue;
                        var fo = new Json.JObj();
                        fo["suite"] = s.GetAttribute("name");
                        fo["test"] = tc.GetAttribute("name");
                        fo["kind"] = fe.LocalName;
                        fo["type"] = fe.GetAttribute("type");
                        string msg = fe.GetAttribute("message");
                        if (string.IsNullOrEmpty(msg)) msg = fe.InnerText;
                        if (msg != null && msg.Length > 500) msg = msg.Substring(0, 500) + "...";
                        fo["message"] = msg;
                        failing.Add(fo);
                    }
                }
            }

            var summed = new Json.JObj();
            summed["tests"] = tests;
            summed["failures"] = failures;
            summed["errors"] = errors;
            summed["skipped"] = skipped;
            o["summed"] = summed;
            o["suiteCount"] = suites.Count;
            o["suites"] = suites;
            o["failing"] = failing;

            // The verdict is the SUM, and the disagreement is worth saying out loud: the
            // root `tests` attribute has been measured not to count the failures (65 vs a
            // suite sum of 68 on a run with 3 red), so anyone reading the attribute alone
            // gets a total that is short by exactly the interesting cases.
            o["testsTotal"] = tests;
            o["failuresTotal"] = failures + errors;
            o["passed"] = (failures + errors) == 0 && tests > 0;
            object claimedTests = claimed["tests"];
            if (claimedTests != null && ComHelpers.ToInt(claimedTests) != tests)
            {
                o["rootAttributeDisagrees"] = true;
                o["note"] = "The root <testsuites tests=\"" + claimedTests + "\"> does not match the " +
                    tests + " summed from the " + suites.Count + " <testsuite> elements. That is the " +
                    "known defect in the file, not in this reading: the attribute does not count the " +
                    "failures. Use the summed numbers.";
            }
            if (tests == 0)
                o["warning"] = "Zero tests were counted. A file with no tests is not a pass.";
            return o;
        }

        private static object AttrInt(XmlElement e, string name)
        {
            string v = e.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(v)) return null;
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return null;
            return n;
        }

        // ---- the independent cross-check -----------------------------------
        //
        // The runtime also reports the tally into the Error List as PlcTask messages at
        // severity HIGH -- which the PLC compiler never uses, so these rows CAN be told
        // apart by severity even though compiler rows cannot. Two sources agreeing is a
        // verdict; one source is a reading.
        private static Json.JArr TestRowsFromErrorList(ActionContext ctx, dynamic dte)
        {
            var rows = new Json.JArr();
            Json.JObj list = null;
            try
            {
                var payload = new Json.JObj();
                payload["limit"] = 500;
                var sub = new ActionContext("xae_get_error_list", payload, ctx.Session, ctx.Cache, ctx.Edits);
                list = ErrorListForTests(sub);
            }
            catch (Exception ex)
            {
                var o = new Json.JObj();
                o["error"] = "Could not read the Error List: " + ex.Message;
                rows.Add(o);
                return rows;
            }
            if (list == null) return rows;

            Json.JArr items = list.Arr("items");
            if (items == null) return rows;
            foreach (object x in items)
            {
                Json.JObj it = x as Json.JObj;
                if (it == null) continue;
                string d = it.Str("description");
                if (string.IsNullOrEmpty(d)) continue;
                if (d.IndexOf("Successful tests", StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.IndexOf("Failed tests", StringComparison.OrdinalIgnoreCase) < 0 &&
                    d.IndexOf("Tests with", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var o = new Json.JObj();
                o["description"] = d;
                o["errorLevel"] = it["errorLevel"];
                rows.Add(o);
            }
            return rows;
        }

        private static Json.JObj ErrorListForTests(ActionContext ctx)
        {
            var handlers = new Dictionary<string, ActionHandler>(StringComparer.Ordinal);
            XaeActions.Register(handlers);
            ActionHandler h;
            if (!handlers.TryGetValue("xae_get_error_list", out h)) return null;
            return h(ctx);
        }

        private static string ResolvePlcRoot(ActionContext ctx, dynamic sm)
        {
            string given = ctx.Payload.Truthy("treePath") ? ctx.Payload.Str("treePath") : null;
            if (!string.IsNullOrWhiteSpace(given)) return given;

            dynamic tipc = ComHelpers.GetTreeItem(sm, "TIPC");
            int n = ComHelpers.ChildCount(tipc);
            if (n < 1) throw new BridgeException("No PLC project found under TIPC.");
            if (n > 1)
            {
                // Several PLC projects and no treePath: which one carries the suite is not
                // guessable, and setting the boot flag on the wrong one runs nothing.
                var names = new Json.JArr();
                for (int i = 1; i <= n; i++)
                {
                    dynamic c = ComHelpers.Child(tipc, i);
                    if (c == null) continue;
                    names.Add("TIPC^" + ComHelpers.SafeStr(delegate { return c.Name; }));
                }
                throw new BridgeException("This TwinCAT project has " + n + " PLC projects and none was " +
                    "named. Pass treePath -- the boot flag has to go on the one that holds the suite. " +
                    "Candidates: " + Json.Write(names) + ".");
            }
            dynamic first = ComHelpers.Child(tipc, 1);
            return "TIPC^" + ComHelpers.SafeStr(delegate { return first.Name; });
        }
    }
}
