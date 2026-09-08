# Backlog — rough edges and missing capabilities

Companion to [FORK-NOTES.md](FORK-NOTES.md). That file records what this fork **has
changed** and why, each item written to be sent upstream. This one is the working list:
what got in the way while using the server for real work, what is still missing, and what
is deliberately out of scope.

Everything below was observed while driving actual AU-tomation projects, and each entry
says what was measured rather than what is suspected. Where the cause was not isolated,
it says so.

---

## Rough edges

### 1. `save: true` does not put the `.plcproj` on disk

`SaveIfRequested` runs `File.SaveAll`, so on paper `save:true` is enough. In practice a
`plc_pou create` / `create_batch` with `save:true`, followed straight away by
`open_solution` with `discardChanges:true`, loses the object: the file is on disk but the
`.plcproj` never got its `<Compile Include>`, and the type then fails to resolve
everywhere with `Unknown type: 'X'` — a symptom that reads like a broken library
reference, not like a lost project entry.

The same sequence with a **separate `xae save_all` call** in between writes the
`.plcproj` correctly. Reproduced twice, once on a library project and once on a test
project.

Cause not isolated. The likely one is timing: the TwinCAT project system appears to mark
the `.plcproj` dirty after the tree operation returns, so the `File.SaveAll` issued inside
the same call runs too early. If that is it, the fix is to save after the project system
has settled (or to save the containing project explicitly) rather than to document a
second call.

**Until then**: after any `create` / `create_batch` / `delete` of objects, call
`xae save_all` and verify the `<Compile Include>` on disk before reopening the solution.

### 2. `save_as_library` reports the wrong reason when the target file exists

Exporting onto an existing `.library` fails with:

```
node 'TIPC^X^X Project' does not implement ITcPlcIECProject (use the nested project
instance node): File '...\X.library' already exist. Cannot SaveAsLibrary!
```

The real cause is the tail of the message; the head is the candidate-node walk reporting
its last failure. Two things to fix: surface the underlying error as the error, and take
an `overwrite` flag (`install_library` already has one).

### 3. `open_solution` on a cold IDE times out, and takes the daemon with it

Starting VS2022 + the XAE extension from scratch does not fit in the 180 s bridge
timeout. That alone would be acceptable — but the pipe is serial, so every later call
queues behind the one that timed out: `xae status` and `list_instances`, which touch
nothing, also time out, and the server looks wedged rather than busy.

Two independent fixes: a longer (or configurable) timeout for `open_solution`
specifically, and letting the read-only actions answer while a long call is in flight —
`list_instances` and `status` exist precisely to be safe to call *in order to decide*.

**Workaround that works**: start `devenv.exe <sln>` with the OS, wait for the window
title, then `xae attach pid:<pid>`. Once the IDE is warm, `open_solution` with
`closeExisting:true` is effectively instant, so the cost is paid once per session.

### 4. `plc_pou rename` trips a CoDeSys assertion

Renaming a DUT raises a modal `Assertion Failed: Abort=Quit, Retry=Debug,
Ignore=Continue` with a stack through `_3S.CoDeSys.UML.DiagramController` —
`RefactoringPerformer.PerformRefactoring` loading every object, on a project that has no
UML diagrams at all. **Ignore** completes the rename correctly, references included, and
several arrive in a row.

Not the server's bug, but the server is where it hurts: each one blocks the daemon and
has to be answered. Worth considering an opt-in allowlist entry — with the caveat that
auto-dismissing an assertion is exactly the kind of rule that hides a real failure later,
so it should be opt-in and never the default.

### 5. A build failure in a sub-project reports no reason

A solution build that fails inside one PLC project can produce a single Error List row:

```
'TwinCAT XAE': Project 'X' build for platform 'TwinCAT OS (x64)' failed.
```

with nothing else — no compiler row, no file, no code. The detail is in the **Output**
window, which no tool exposes. In the case measured, the true cause (a library compiled
against a stale base) was found only by opening that project's own solution and building
it there. See missing capability 3.

### 6. `BuildVInfo` carries two vInfo shapes

Creating the members of a POU needs a `string[]` with the IEC language spelled out
(`"ST"`), while the top-level POU types and the Property have always been created with an
`object[]` holding the language as a number (FORK-NOTES §10). Both work, and only the
first shape is what Beckhoff's own samples use everywhere.

The `object[]` cases were left alone on purpose: they are the ones proven in daily use,
and a preventive sweep would put the whole authoring path at risk to gain consistency and
nothing else. Worth aligning the day something else already touches those cases — with
602, 603, 604 and 611 all re-tested, not assumed.

### 7. Two identical builds of the daemon produce different bytes

`Te1000Daemon.csproj` sets `<Deterministic>true</Deterministic>`, so an install has been
checked by hashing a rebuild and comparing. That check does not hold: the in-box
compiler MSBuild v4 drives (`Framework64\v4.0.30319`) ignores the property. Measured
2026-09-08 -- same source, same configuration, same output directory, `obj` output
deleted between passes:

```
pass 1 : 48BA551445B1CD6D9C86CDB9EEA48C58D670CEB9FEEA34D61C7D1C10E425B0ED
pass 2 : 7186BA2965550796F774B752DD0F3B438A5F51274CFFCEDB56A593401E6F37EA
```

So a hash comparison between a probe build and a release build proves nothing, and a
mismatch is not evidence of a source difference -- which is the worse half, because it
sends you looking for a change that is not there.

**Until then**: install by COPYING the binary that passed the tests into `bin\Release`,
not by rebuilding it there. The copy makes the hashes equal by construction, and that is
then a real check rather than a hopeful one. (Whether `-p:Deterministic=true` reaches a
csc that honours it, or whether the build should move to a Roslyn compiler, is worth one
experiment before this is written up as a fix.)

---

### 8. Finish the TwinCAT HMI side

FORK-NOTES §11 landed `hmi_project`, `hmi_symbol`, `hmi_function` and `hmi_publish`.
Three pieces are deliberately not in it, in the order they are worth doing:

- **Bind `TcHmiAutomation.dll` early.** `ITcHmiInternalSymbol` does not marshal through
  IDispatch (all four call routes fail; the measurement is in §11), so creating an
  internal symbol is the one authoring job still left to the IDE. The assembly is
  `[ComVisible(true)]` with a GUID per interface and ships in
  `TE2000-HMI-Engineering\MSBuild\` and in its `bin\`, so this is the same shape of fix as the
  reflection shim in `MeasurementActions` -- and it would also make the property reads on
  the child interfaces typed rather than best-effort.
- **`ITcHmiFile`: place controls instead of editing HTML as text.** Reflection shows a
  richer surface than the notes assumed: `GetSource` / `SetSource`, `GetAllIdentifiers`,
  `GetChildIdentifiers`, `GetControl`, `AddControl` / `AddControlBefore` /
  `AddControlAfter`, `RemoveControl`, `Beautify`, and `ITcHmiControl.ChangeAttribute(s)`
  plus per-group permissions. `ITcHmiAutomation.GetHmiFile(EnvDTE.ProjectItem)` is the way
  in, which the existing solution walk already reaches. That answers the old open question
  "how do you enumerate an existing project's views" -- it is not open any more, just
  unbuilt.
- **Run `hmi_publish publish` end to end once.** It is written and its pre-flight is
  tested, but the publish itself has never been executed: doing so replaces whatever the
  target `TcHmiSrv` instance was serving, and there was no instance free to take it. It
  needs a throwaway server instance (they are cheap: `TcHmiSrv.exe --serviceAddInstance`,
  no admin rights and no service restart) before the verb can be called proven.

---

## Missing capabilities, most valuable first

### 1. ~~Choose WHICH TwinCAT project in the solution~~ — DONE

Shipped as `xae list_projects` / `xae select_project`, the per-call `tsProject`, and
`TE1000_DEFAULT_TSPROJECT`; every response that reached a system manager now names its
project, an unchosen pick among several is flagged `tsProjectAmbiguous`, and the verbs
that change the target refuse it outright. See FORK-NOTES §8 for the design and why the
refusal is limited to those verbs. The throwaway `.sln` with the test project first is no
longer needed.

### 2. ~~Set the active solution configuration / platform~~ — DONE

Shipped as `xae list_configurations` / `xae set_configuration`; `xae_build` reports the
configuration it built and takes a `project` to build one project of the solution. See
FORK-NOTES §9. Reading `PlatformName` needed the typed cast, like the error list.

### 3. Read the Output window

`dte.ToolWindows.OutputWindow.OutputWindowPanes.Item("Build").TextDocument` is where the
reason for rough edge 5 lives. A `xae output(pane, tail)` returning the last N lines would
turn "failed, no idea why" into a diagnosis without opening another solution. Cheap, and
it closes the biggest blind spot in the compile-read-fix loop this server exists for.

### 4. Create a solution, and a TwinCAT project inside it

There is no way to start a new project. `plc_project create_from_template` needs a
`.tsproj` that already exists, and passing a `.tsproj` to `open_solution` fails with
`E_ABORT`.

The two halves are different jobs. The empty solution is plain EnvDTE
(`Solution.Create(dir, name)`), and so is adding a project from a template
(`Solution.AddFromTemplate(templatePath, dir, name)`) — the interesting part is locating
the TwinCAT XAE project template, which differs between a TcXaeShell install and TE1000
integrated into VS2022, exactly like the PIA paths in FORK-NOTES §1. Adding the PLC
project inside it is already covered by `plc_project create_from_template`.

Until this exists, a new repo is made by copying an existing one and rewriting names and
GUIDs. That works — the way to keep it safe is to regenerate only the GUIDs that are
project identity and to leave the system ones alone, and the cheap way to tell them apart
is to intersect the GUIDs of the copy with those of an unrelated TwinCAT repo: what
appears in both is a type/CLSID GUID, what appears only in one is identity. (One
exception to add by hand: `{2150E333-8FDC-42A3-9474-1A3956D46DE8}`, the VS Solution
Folder type, shows up only where a solution folder exists.)

### 5. Run the TcUnit suite and return the results

Already in FORK-NOTES "Still to do"; here is the shape that was proven by hand, so the
action does not have to be designed from scratch:

1. `set_boot_flags` with `autostart:true`;
2. build, and stop on a non-zero `lastBuildInfo`;
3. **delete** `<runtime>\Boot\tcunit_xunit_testresults.xml` before activating — without
   this you read the previous run's results and cannot tell;
4. activate + restart the runtime;
5. wait for the file to reappear, then parse it.

Two things the parser must get right. The `tests` attribute on the root `<testsuites>`
**does not count the failures** — a run with 3 red wrote `tests="65" failures="3"` while
its three `<testsuite>` elements summed to 68 — so the total has to be summed from the
suites. And the same numbers arrive in the Error List as PlcTask messages
(`Successful tests: N` / `Failed tests: N`) at severity **High**, which the PLC compiler
never uses, so runtime messages *can* be told apart by severity even though compiler rows
cannot.

Measured on four suites: 373 tests, 1-2 minutes per full round trip including build and
activation, against roughly 4 minutes for the CI job.

Worth noting for the design: **activation replaces whatever is running on the target**.
The action should say so plainly in its result, because putting back what was there is
the caller's job and is easy to forget.

### 6. Static analysis and documentation

`RunStaticAnalysis()` + `ExportToSarif()` and doc generation, as listed in FORK-NOTES.
These are what still forces the CI to shell out to TcCIBuilder.

### 7. Get the data out of a Scope recording

`tc_measurement` can create a Scope project, add children, rename them and start/stop a
recording — but there is no way to **retrieve what was recorded**. `IMeasurementScope`
exposes `SaveSVD`, `ExportCSV` and `LookUpChild`, and `MeasurementActions.cs` deliberately
leaves all three unexposed, marked UNVERIFIED.

That gap is what stops the obvious workflow: record a machine cycle, export it, compare it
against the previous run. Start/stop without export is a button, not a measurement.

Verification is cheap — add a Scope project to a throwaway solution, record a few seconds
of one channel, and call the three by reflection the way `ScopeHelper` already calls
`CreateChild` / `ChangeName` / `StartRecord` / `StopRecord`. What has to be pinned down
before exposing them:

- the **signatures** (return code plus out-parameters, or a plain return?) — the four
  verified ones return an `int` rc, so assume the same shape until measured;
- whether `SaveSVD` / `ExportCSV` take an **absolute path**, and what they do when the
  file exists (silently overwrite is the likely answer, and the verb should say so);
- whether either call is **synchronous** — an export that returns before the file is
  complete would hand the caller a truncated read, which is the kind of failure that looks
  like corrupt data;
- whether they can be called **while recording**, or only after `StopRecord`.

Same note as `analytics_create`: the recording has to exist before any of this means
anything, so the test solution needs a live target and a real channel, not just the project
node.

---

## Not the server's problem

Recorded so they are not mistaken for bugs here.

- **A stale installed library.** After rebuilding a base library, a consumer can fail with
  errors pointing at a library nobody touched. The cure is to rebuild and reinstall that
  intermediate library — and only that one: the link that breaks is the one between the
  changed library and the consumer that failed, not the whole chain.
- **Incremental build after an out-of-band library change.** Once a library has been
  rebuilt elsewhere, the consumer needs `rebuild`, not `build`; the incremental one fails
  with the reasonless message of rough edge 5.
- **`{attribute 'hide'}` removes a member from the ADS symbol table**, so an HMI binding
  onto it goes quiet. A PLC-side decision, not an automation one.
