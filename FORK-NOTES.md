# Fork notes — AU-tomation

This is a fork of [`Edge-JB/TwinCAT-XAE-MCP`](https://github.com/Edge-JB/TwinCAT-XAE-MCP).
Everything in [README.md](README.md) still applies; this file records only what **this
fork changes**, and why.

Work happens on the **`vs2022-support`** branch. `origin` is this fork,
`upstream` is Edge-JB.

None of the changes below are specific to our projects: they are bug fixes and
environment support that belong upstream, and the intent is to send each one there.
A patch that stays only on a fork is debt to be reapplied at every update.

---

## Why we forked

We were about to write an MCP server for VS2022 from scratch. This one already had
the hard parts right — a single long-lived STA thread with an `IOleMessageFilter`, a
modal-dialog watcher that matches on window class and owning process instead of on
title substrings, and a per-call attach mode. We measured the round trip that matters
to us, "compile, read the errors, fix":

| step | this server | our previous DTE runner |
|---|---|---|
| open solution, IDE already alive | 20.8 s | not possible |
| build | 7-18 s | ~28 s |
| read the error list | 1-4 s | never arrived |
| **compile → read errors → fix** | **~9-20 s** | **~6 min** |

The IDE staying alive between calls is what collapses the loop.

---

## What this fork changes

### 1. Resolve the EnvDTE PIAs when TE1000 is integrated in VS2022

`VsInterop.Roots` looked for `EnvDTE`, `EnvDTE80` and `Microsoft.VisualStudio.Interop`
only under `TcXaeShell\Common7\IDE\PublicAssemblies`. That directory does not exist
when TE1000 is installed **into a full Visual Studio 2022** rather than as the separate
TcXaeShell — and every call died with `Could not load file or assembly 'EnvDTE80'`.

Adds the VS2022 paths (Enterprise / Professional / Community / BuildTools) and a
`TE1000_PIA_DIR` override, both at runtime and in the `.csproj`.

### 2. The severity filter returned nothing on TwinCAT projects

`severityFilter:"errors"` mapped to `vsBuildErrorLevelHigh`. That is correct for
C++/C#/HMI projects. It is wrong for PLC ones, and it meant an errors-only query
returned **zero** on every TwinCAT project — the failure looked like "the build is
clean".

Measured on TC 3.1.4026, one build, one error list:

| row | VS Error List column | `ErrorItem.ErrorLevel` |
|---|---|---|
| `Identifier 'x' not defined` (C0046) | Error | `Medium` |
| `{warning '...'}` pragma (C0373) | Warning | `Medium` |
| `{info '...'}` pragma | Message | `Medium` |
| `'TwinCAT XAE': ... build failed` | Warning | `Medium` |
| `'TwinCAT XAE': Use existing project repository` | Message | `Low` |

Every row from the PLC compiler arrives at `Medium`. `High` never appears on a
`.plcproj` row at all. So on those rows the level carries no severity, and filtering on
it can only throw away real errors.

PLC rows now match **any** severity filter and are flagged `severityUndecidable`, with
`severityUndecidableCount` and a note on the response. The caller is told the filter
could not discriminate rather than being handed a list that quietly dropped the errors.

**Why not read the real severity:** VS knows it — the Error List shows *Warning C0373*
and *Error C0046*. It lives on `IVsErrorItem.GetCategory` behind `SVsErrorList`, and
the `IVs*` shell interfaces have no cross-process marshalling: a QI for OLE
`IServiceProvider` on an out-of-process DTE fails outright (probed 2026-08-27). Only
in-process code — a VSIX — can reach it. `EnvDTE.ErrorItem` exposes neither `Code` nor
`Severity`. For an external automation client, the ambiguity above is the whole of what
is knowable.

### 3. `xae shutdown_ide` — stop leaving an orphaned IDE behind

Nothing called `dte.Quit()`. Kill the daemon and `devenv` keeps running with the
solution open, invisible to the next session: one leaked IDE per run.

`Quit()` on its own is not enough — with dirty documents it raises the modal
"Save changes?" prompt, the exact thing that wedges a headless daemon. So the dirty
state is settled first (saved, or discarded with `save:false`), the solution is closed
with `saveFirst:false`, and only then does `Quit()` run with nothing left to prompt
about. It attaches only to a running IDE and never starts one in order to close it.

Guarded by `confirm="ALLOW_XAE_SHUTDOWN"`.

### 4. Solution paths compared as paths

`WaitForSolutionOpen` compared the requested path to the DTE's `Solution.FullName`
with an ordinal string compare. Pass a path with forward slashes — which is what a
JSON-speaking client naturally sends — and the solution opens and is then rejected
with `Different solution is active`. `PathUtil.SamePath` compares the canonical form.

### 5. The IEC project node is resolved, not assumed

`plcopen_export`, `plcopen_import` and `save_as_library` act through
`ITcPlcIECProject`, which lives on the nested project **instance** node. All three
defaulted to the PLC **root**, so with no explicit `treePath` they failed with a bare
`E_NOINTERFACE` — a default that could never work.

They now walk the same candidate order `plc_pou_check_objects` already used and
QI-probe each node, so the right one is chosen before the write instead of being
discovered through a failure mid-write.

### 6. `TE1000_DEFAULT_MODE` — an environment default for `mode`

`mode` is a **per-call** parameter defaulting to `active`, and `active` attaches to
whichever running IDE the ROT offers first that has any solution open
(`ComSession.GetPreferredDteFromRot`). On a machine where someone is working in Visual
Studio, an unqualified call therefore reaches **their** IDE and builds **their**
solution. Some tools — `xae_build` among them — expose no `mode` parameter at all, so
per-call discipline cannot cover every path.

`TE1000_DEFAULT_MODE` is the companion of the existing `TE1000_PROGID`. Set it to `create` to
keep a session on its own instance. An explicit per-call `mode` still wins.

### 7. Choosing which IDE to work with

`mode` decides *how* to get an IDE (`create` / `active` / `activeOrCreate`). It cannot say
*which* one. `active` means "whichever instance the ROT lists first that has any solution
open" — an ordering the caller neither controls nor sees. With two IDEs running, which
one a build lands on was luck.

Worse, `mode` was per-call only on paper: the session cache keyed on `progId` alone, so
once a DTE was attached every later call got that one back whatever it asked for. A
`mode:"create"` passed mid-session returned the existing instance instead of starting one.

Three changes, which together make the choice explicit:

- **`xae list_instances`** — every running IDE for this progId: `pid`, the `solution` it
  has open, whether it is the one this session is on (`isCurrent`) and whether this
  session started it (`startedByUs`). Read-only: it attaches to nothing and starts
  nothing, because it has to be safe to call *in order to decide*. The ROT walk already
  collected exactly this to run its heuristic and then discarded it.
- **`xae attach` (`pid` | `solutionPath`)** — bind the session to one named instance. A
  miss is an **error that lists what is running**, never a quiet fallback to a different
  IDE. Per-call `attachPid` / `attachSolution` do the same for a single call. The binding
  sticks, so the tools that expose no `mode` of their own — `xae_build` among them — run
  against the instance you chose.
- **The cache now honours the request.** A held instance is reused only when it actually
  satisfies what was asked (right pid, right solution, and for `mode:"create"`, an IDE
  this session started). `forceNew:true` starts an additional instance unconditionally —
  `create` alone deliberately reuses ours, so that passing it on every call does not spawn
  one IDE per call.

### 8. Choosing which TwinCAT project of the solution to work on

The same hole as §7, one level down. `ComSession.GetSysManager` took **the first
`.tsproj` of the solution** whose project object answered `GetTargetNetId()`, and cached
it. But a solution holds one system manager *per* TwinCAT project, and everything that
matters hangs off it: the `TIID`/`TIPC` tree, the target NetId, the boot flags, the
activation.

Every AU-tomation repo has two projects — the library and its TcUnit suite — and the
library comes first. So `tc_system get_netid` answered with the **local** NetId (only the
test project carries a `TargetNetId`), and `set_boot_flags` / `plc_download` /
`twincat_activate_configuration` all targeted the project with no task to run, reporting
success either way. The way around it was to generate a throwaway `.sln` listing the test
project first — which still had to include the library, because the suite resolves it
through a bracketed project reference and a single-project solution fails with hundreds
of `Unknown type`.

The design follows §7 deliberately, because the problem is the same shape:

- **`xae list_projects`** — every `.tsproj` of the open solution: `name`, `path`,
  `uniqueName`, `targetNetId`, how many PLC projects it holds, and which one the session
  is on. Read-only, and it changes no selection. Solution folders are walked, so grouped
  projects are listed too.
- **`xae select_project` (`name` | `path`)** — bind the session to one. A miss is an
  **error listing the projects that are there**. The binding sticks for later calls,
  including the tools that take no project parameter, and is held by name — so it
  survives a worker recycle. It is dropped when the ground moves: opening another
  solution, or attaching to another IDE.
- **`tsProject` on any project-scoped tool** — the per-call form, name or path. Declared
  once for every such tool and carried from the handler to the daemon through an
  `AsyncLocalStorage` scope, so it works everywhere without ~200 call sites forwarding
  it. `TE1000_DEFAULT_TSPROJECT` is the environment default, the sibling of
  `TE1000_PROGID` and `TE1000_DEFAULT_MODE`.
- **Every response that reached a system manager says which project it was**
  (`tsProject`, `tsProjectPath`). "The runtime restarted" is a different fact depending
  on the project, and the response never said which.
- **An ambiguous pick is declared, and the verbs that change the target refuse it.**
  With more than one `.tsproj` and no choice on record, reads still answer from the first
  in solution order and carry `tsProjectAmbiguous: true`. But `twincat_activate_configuration`,
  `twincat_restart_runtime`, `plc_download`, `plc_project boot_flags` / `generate_boot`,
  `tc_system set_netid` and `set_target_platform` fail with the list instead: guessing
  there activates a configuration on a machine nobody meant to touch, and the old
  behaviour gave no sign it had chosen at all. A single-project solution is unaffected —
  nothing to choose, nothing refused.

### 9. The solution configuration is readable, settable, and reported

`xae_build` built whatever configuration happened to be active, and nothing could read or
set it. A solution left on `TwinCAT RT (x64)` builds RT while CI builds
`TwinCAT OS (x64)`, and the two verdicts differ with nothing on the response to say why.

- **`xae list_configurations`** — the solution configurations and which is active;
  **`xae set_configuration` (`name`, `platform?`)** activates one, `name` accepting
  either `Release` or the full `Release|TwinCAT OS (x64)`.
- **`xae_build` reports the configuration it built.**
- **`xae_build` takes `project`** — build ONE project of the solution
  (`SolutionBuild.BuildProject`) instead of all of it, with an optional `configuration`.
  `build` only: EnvDTE has no per-project clean or rebuild, and saying so is better than
  quietly cleaning the whole solution.

`SolutionConfiguration2.PlatformName` needed the same typed cast as the error list (§2):
through raw IDispatch every configuration came back platform-less, which on a real
solution printed seven identical `Debug` rows.

### 10. The members of a POU — and of an interface — can be created

`plc_pou create` could author a Program, a Function, a Function Block, a DUT, a GVL or a
Property, but never a **Method**. Sub-type 609 failed on every project, with `language`
and `returnType` passed or not:

```
The specified vInfo (Type: Object[]) is not supported for creating TreeItem type
'TREEITEMTYPE_PLCMETHOD' on parent item type '604'
```

The message names the cause: the **type of the array**. `CreateChild` takes `vInfo` as a
VARIANT and each TreeItem type parses it its own way. A C# `object[]` reaches COM as
`SAFEARRAY(VARIANT)`, and the parser for the members of a POU wants `SAFEARRAY(BSTR)` —
a `string[]` — with the IEC language as its **name** (`"ST"`), not as its
`IECLANGUAGETYPES` number. Beckhoff's own scripting samples build a `string[]` for every
one of them (`GeneratePlcProject.cs`: `AddMethod`, `AddAction`, `AddTransition`,
`AddProperty`, `AddPropertyGet`, `AddPropertySet`).

The shapes now sent:

| sub-type | vInfo |
|---|---|
| 608 Action, 616 Transition | `string[]{ language, initial implementation }` |
| 609 Method | `string[]{ language, return type, accessor, initial implementation }` |
| 613 / 614 property Get / Set | `string[]{ language, accessor, initial implementation }` |
| 610 interface Method, 612 interface Property | the return type, as a bare string |
| 654 / 655 interface property Get / Set | nothing |

Two parameters carry the new elements: **`accessor`**
(`PUBLIC` \| `PRIVATE` \| `PROTECTED` \| `INTERNAL`, also honoured by 611) and
**`implSeed`**, an initial implementation in TwinCAT wrapper XML — normally left out,
since `set_impl` is the readable way to write a body. A method created with neither is
still complete: `set_decl` rewrites the whole `METHOD PUBLIC Foo : BOOL` header.

Sub-types **610, 612, 654 and 655 are new to the tool**. They are the only way to author
an interface here: `set_document` is typed on `ITcPlcPou`, which a
`TREEITEMTYPE_PLCITF` (618) does not implement, so the whole-document route that works
for a Function Block gives `E_NOINTERFACE` on a `.TcIO`. An interface takes 610/612
(+654/655), never 609/611.

The accessors 613/614/654/655 may be created **without a name** — the samples pass `""`
and the IDE names the child itself. The request asks for `Get`/`Set` explicitly, because
`AssertWellFormedChild` compares the name that comes back against the one that went in
and deletes the child when they differ.

**The guard now asks the tree.** `AssertWellFormedChild` exists because `CreateChild` can
report success while inserting a blank-named ghost, so it compared the name that came back
against the one that went in. A Method created **with** an initial implementation trips
that comparison while being perfectly created: declaration, accessor and body all land,
and the reference handed back has a blank `Name`. Reporting that as a failure is the worst
answer available — the object is in the tree, and a caller who believes the error and
retries ends up with two. Before failing, the guard now resolves `parent^name` and, when
the tree holds exactly what was asked for, reports that object instead. The tree is the
authority on what was created; the returned reference is only a convenience. Refusals
still refuse: a Method under a folder, or a Function Block under a Method, come back as
the Automation Interface's own *"Creating the child type ... is not possible on parent
node type ..."*.

Measured on a scratch solution with a separate daemon: 17 creations covering every
sub-type above, both accessors nameless, `implSeed` on 608/609/616, the two new
validations refusing, `allObjectsValid` true, and nothing left behind after the delete.

What was deliberately **not** touched: 602 Program, 603 Function, 604 Function Block and
611 Property keep the `object[]` form with the numeric language. That form is what this
bridge has always sent and what is proven in daily use; the two shapes coexisting is the
Automation Interface's asymmetry, not ours, and the comment on `BuildVInfo` says so.

---

### 11. TwinCAT HMI (TE2000) — a family of verbs for the HMI side

The server drove the PLC side of a solution and none of the HMI side, so anything
touching a view, a symbol mapping or a Function fell back to editing files by hand. That
is not a small gap: an HMI project is an MSBuild project with explicit `<Content Include>`
items, and the publish task is handed `@(Folder);@(Content)` — there is no directory scan
anywhere in the pipeline. **A file on disk that the `.hmiproj` does not declare does not
exist for build or publish, and nothing reports it.**

Adds the tools `hmi_project`, `hmi_symbol`, `hmi_function` and `hmi_publish`, the daemon
files `HmiInterop.cs` and `Actions/HmiActions.cs`, and teaches `ComSession` to cache the
HMI automation object beside the sysmanager and to walk the solution for `.hmiproj` nodes
as well as `.tsproj` ones.

**Why it belongs in this server rather than a second one.** The HMI automation object is
reached with `dte.GetObject(<ProgId>)` off the same `DTE` this daemon already holds, so it
lives in the same COM session on the same STA thread, behind the same message filter and
the same dialog watcher. A separate MCP server attaching to the same IDE for HMI work
would reintroduce exactly the orphaned-devenv and ROT-ambiguity problems that §3 and §7
exist to solve.

#### The ProgId is not guessable, and not the version you would guess

The object is registered by the TE2000 VS package under a versioned ProgId, in
`…\Common7\IDE\Extensions\…\TwinCAT HMI\TcHmiPackage.pkgdef`:

```
[$RootKey$\Packages\{16a09aeb-7616-4147-ab22-721f8fb090fc}\Automation]
"Beckhoff.TcHmi.1.12"=""
```

The suffix is the TcHmi **platform generation**, and it does **not** track the product.
Measured 2026-09-08 on one machine: HMI framework `14.4.70`, Controls `14.6.28`, VS
package stamped `14.3.995.1` — ProgId still `1.12`. The same `1.12` is what TE2000 writes
into the pkgdef it ships **for VS2026**, and the same `1.12` is the runtime moniker inside
the framework package (`runtimes\native1.12-tchmi\`). Probed against a live IDE, `1.10`,
`1.11`, `1.13`, `1.14`, `1.15` and `2.0` all answer `DISP_E_MEMBERNOTFOUND`; only `1.12`
hands out an object.

`HmiInterop` therefore reads the name out of the pkgdef of the IDE it is attached to (then
the copies in the TE2000 install, then a short probe list, with `TE2000_HMI_PROGID` as an
override) and reports which route answered. **Trap: those pkgdef files are UTF-16.** A
plain text read — or a `grep` — finds nothing in them, and the resolution then looks
exactly like "TE2000 is not installed".

#### Late binding works for methods; the empty property reads were a wrong name

`MeasurementActions` has to reach `IMeasurementScope` by reflection because it is a
vtable/IUnknown interface `dynamic` cannot touch, and an earlier HMI spike reported that
these children "came back empty" late-bound too. That was not a marshalling limit:
`ITcHmiProject` has no `Name` property at all, and the name lives on
`GetProjectInformation().ProjectName`. Bound late, that reads fine — as do `IsReady`,
every `Change*` setter, `GetMappedSymbols`, `Build` and `AddUserControl`. This family
needs no reflection shim.

One interface genuinely does not marshal: **`ITcHmiInternalSymbol`**. All four routes
fail — `GetInternalSymbolInstance()` gives *Specified cast is not valid*,
`GetInternalSymbolInstance_2()` gives *Missing parameter does not have a default value*
(so `_2` is the 5-argument overload), and the 5-argument call on either name fails the
cast. Creating an internal symbol is therefore **not exposed**. `ITcHmiMappedSymbol` reads
and maps perfectly well, and those verbs are.

Overloads in general do not survive IDispatch under one name, so where the API has them
the daemon tries the plain name, then the `_2` sibling, and reports which answered
(`hmiProjectResolvedBy` on every response). Here `GetHmiProject(EnvDTE.Project)` is the
one that answers.

#### What the verbs do, and the things that are not obvious

`hmi_project` — list / info / `add_view` / `add_usercontrol` / `add_content` /
`add_theme` / `add_localization` / config get+set / build / save. Project selection
mirrors the `.tsproj` machinery one kind over: HMI projects get their own `hmiProject`
parameter, a single project is used automatically, and with several and none named reads
take the first and flag `hmiProjectAmbiguous` while writes refuse and list them.

1. **One add writes several registrations.** Creating a user control touches the
   `.hmiproj` (two `<Content Include>` items), `Properties\tchmiconfig.json` (the
   user-control list) and `Properties\tchmi.project.Schema.json` (the control's own
   definition). A hand-written file plus a hand-written `.hmiproj` entry gets one of the
   three and silently loses the others, so the verb reads all of them back off disk and
   reports which landed.
2. **`ChangeConfig(field, object)` is a trap; the typed setters are not.** The generic
   setter casts the VARIANT to the field's own type inside itself, so a JSON number
   arriving as a double dies with a bare *"Specified cast is not valid"* naming neither
   the field nor the type. Each of the eight fields has a typed setter (`ChangeTheme`,
   `ChangeWebsocketTimeout`, …); those are what `config_set` calls, and every response
   carries a readback.
3. **A green HMI build means the project packages, not that it works.** Measured
   2026-09-08: `data-tchmi-ctrljsondata` renamed to `…BROKEN` on a
   `TcHmiUserControlHost`, build run, `lastBuildInfo` **0** and an empty Error List. The
   same false green as `xae_build` on a PLC library, so `hmi_project build` carries the
   caveat in its own response rather than leaving callers to remember it.

`hmi_symbol` — list / map / unmap over `ITcHmiServer(3)`. Worth a verb because mapped
symbols are an **explicit** list in `Server\TcHmiSrv\TcHmiSrv.Config.default.json`, not a
live discovery: a binding onto a symbol nobody mapped is null at run time and silent at
build time. In-process with the PLC side, the schema `$ref` can eventually be derived from
the PLC symbol table instead of guessed.

`hmi_function` — the one with no API behind it. There is **no `AddFunction`**: views, user
controls, themes, localizations and content have first-class verbs, Functions do not. And
`ChangeConfig` cannot stand in — `ConfigFields` holds eight members, none of them the
function lists. Meanwhile a Function's name lives in **nine** places (the two file names;
`function <N>` and `registerFunctionEx('<N>', …)` in the source; `function.name`,
`function.displayName` and `dependencyFiles[0].name` in the descriptor;
`dependencyFiles[].name` and `userFunctions[].url` in `tchmiconfig.json`) plus two
`<Content Include>` entries — and the IDE's own rename aligns only part of them *and*
deletes module-scope variables declared outside the function body. Two measurements shape
the implementation, and both were mistakes the first version made:

- **Add the source to the collection of the folder it lives in, and add only the source.**
  `project.ProjectItems.AddFromFile` targets the project ROOT: the HMI project system
  answers it by copying the file up to the root and declaring the copy, then raises a
  modal *"a file with the same name already exists, overwrite?"* on the second file. And
  adding the descriptor at all is wrong even in the right collection — the project system
  pulls it in itself when the `.ts` arrives, nesting it `DependentUpon` the source and
  writing **both** `tchmiconfig` registrations. Adding it explicitly on top of that leaves
  a second declared descriptor, `X - Copy.function.json`.
- **Rename the source, not the descriptor.** The descriptor is declared `DependentUpon`
  the `.ts`, and the project system cascades a rename from parent to dependent. Rename the
  `.ts` and everything follows correctly; rename the descriptor first and the cascade
  renames the source to `<new>.function.json.ts` and writes that stem into
  `tchmiconfig.json` — silently.

So the verb writes the files, adds the source, **checks** what the project system did, and
fills in only what it did not. Where a check could raise a false alarm it is bound to the
whole token: a leftover-name check on `Functions/<old>` reports the new
`Functions/<old>Two.js` as residue, and a false alarm on a correct rename is worse than no
check at all.

`hmi_publish` — profiles / publish / result, gated on `ALLOW_HMI_PUBLISH` because one
`TcHmiSrv` instance hosts **one** project and a publish replaces whatever it was serving.
The interesting part is the pre-flight. With `serverExtensions` populated — which is what
the IDE's publish dialog writes — a publish succeeds, uploads the project, and does **not**
push the server-extension configuration: the ADS runtimes block stays at the server
default and every symbol is null. That has to be caught before the call, because
`ITcHmiPublishResult` carries only `Result`, `IsCompleted` and `SubmissionId` (read by
reflection over the shipped assembly) — there is no per-extension verdict, so a green
publish means the upload ran, not that the configuration landed. The verb refuses such a
profile unless `force:true`, names the cure (`"serverExtensions": []`), and says in its own
response how to confirm on the server storage.

**What was exercised**, on a throwaway copy of a real HMI project driven by a separate
daemon on its own pipe: project list and info; item creation for all three kinds plus the
two refusals; function create and rename with all nine identity points checked on disk;
mapped-symbol listing (60 symbols); `config_set` with readback for a string, a number and
an enum, plus the two refusals; build; publish-profile reading; and the four tools end to
end through the MCP front, including the publish confirmation gate. **`hmi_publish
publish` itself was NOT run**: it would have replaced a live server instance, and there
was nothing safe to publish to. That is no longer true — see §16.

---

### 12. `xae output` — the window where a build explains itself

A solution build that breaks inside one PLC project can leave a **single** Error List row:

```
'TwinCAT XAE': Project 'X' build for platform 'TwinCAT OS (x64)' failed.
```

No file, no code, no compiler row. The reason is in the **Output** window, and nothing
exposed it, so "failed, no idea why" meant opening that project's own solution by hand and
building it there to see what the first build had already printed.

`xae output` returns the last N lines of a pane (`pane`, default `"Build"`; `tail`, default
200) together with the pane names. Three details, all of them measured rather than assumed:

- **Panes are matched exactly first, then as a case-insensitive substring**, because the
  names are localized. `"Source Control"` matches that pane and not
  `"Source Control - Team Foundation"`.
- **A pane that is not there is an error listing the ones that are** — the `attach` /
  `select_project` contract one level down, never a silent fallback onto another pane.
- **Not every pane has readable text.** The Source Control panes answer `E_FAIL` from
  `TextDocument`. That is a property of the pane, so the message says which pane, that
  another one may well work, and lists them.

The window is reached through `dte.ToolWindows.OutputWindow`, falling back to
`dte.Windows.Item(vsWindowKindOutput).Object` for an IDE that has never shown it.

### 13. `save: true` did not save — on the singular verbs it did nothing at all

The symptom was known and had a workaround in the notes: a `plc_pou create` with
`save:true`, followed by `open_solution` with `discardChanges:true`, lost the object — the
`.TcPOU` was on disk but the `.plcproj` never got its `<Compile Include>`, and the type then
failed to resolve everywhere with `Unknown type: 'X'`, which reads like a broken library
reference rather than a lost project entry. The same sequence with a **separate**
`xae save_all` call worked, so the cause was assumed to be timing.

It was not. Two defects, and the second is the larger:

1. **`File.SaveAll` is a shell COMMAND, not a method call.** `ExecuteCommand` QUEUES it and
   returns before the save has landed. Every save in the daemon went through that one line,
   so an in-call save was a request, not an event. A separate call worked for one reason
   only: two pipe round trips let time pass.
2. **The singular verbs ignored `save` outright.** `create`, `create_folder`, `delete`,
   `move`, `rename`, `set_decl`, `set_impl` and `set_document` never looked at the flag,
   while their `_batch` counterparts honoured it. So `set_impl` with `save:true` was a
   no-op that reported success — someone could believe their code was on disk when nothing
   had been written.

Both are fixed. `XaeActions.SaveAllAndSettle` issues the command and then **waits for the
IDE to report itself clean** — documents, solution and projects — asking any project that
stays dirty to `Save()` itself halfway through the budget, because `Project.Save()` is a
call and has finished when it returns. The report reaches the caller on every verb that
takes `save` (attached by the dispatcher, since a dozen handlers each build their own
result object):

```
"save": { "saved": true, "settled": true, "waitedMs": 152 }
```

and a save still dirty at the end of its budget is reported as **unsettled**, with what is
still dirty and a warning not to reopen the solution with `discardChanges` until it
settles. Measured before and after on the same call: the `<Compile Include>` was absent
from the `.plcproj` immediately after a `create save:true`, and present immediately after
the fix, with no second call in between.

### 14. `save_as_library` blamed the wrong thing, and could not overwrite

Exporting onto an existing `.library` failed with:

```
node 'TIPC^X^X Project' does not implement ITcPlcIECProject (use the nested project
instance node): File '...\X.library' already exist. Cannot SaveAsLibrary!
```

The head of that sentence contradicts the tail. The catch claimed a missing interface
**whatever** went wrong, so the real reason arrived as a suffix to a false diagnosis. Now
the node is asked whether it implements the interface, and only told so when it does not:

- wrong node (the PLC ROOT) → still the interface message, and now it names the shape of
  the right path;
- right node, anything else → `SaveAsLibrary failed on '<node>': <the actual error>`.
  Measured on a project with no library info: *"The specified library is not a managed
  library. (Reason: 'Title' not specified.)"* — which the old wrapper hid.

And `overwrite` (default false, like `install_library`'s) deletes the target first and
reports `replacedExisting`. Without it an existing file is refused **by name**, before the
COM call. The response also carries `fileWritten`, read back from disk: a call that returns
without throwing is not by itself proof that a file arrived.

### 15. Starting from nothing: a solution, and a TwinCAT project in it

There was no way to begin. `plc_project create_from_template` needs a `.tsproj` that
already exists, and handing a `.tsproj` to `open_solution` fails with `E_ABORT`, so a new
repo was made by copying an old one and rewriting names and GUIDs by hand.

- `xae create_solution` (`directory`, `name`) — `Solution.Create` then `SaveAs`, because
  `Create` builds the solution in memory and a solution that is not on disk cannot be
  reopened or committed. `solutionFileWritten` is read back from disk. Creating a solution
  **closes** whatever is open, so an open solution is refused unless `closeExisting:true`.
- `xae add_project` (`name`, `directory?`, `templatePath?` / `templateName?`) —
  `Solution.AddFromTemplate`. Reports `tsProjectWritten`, found on disk, rather than the
  call it made.
- `xae find_project_template` — read-only, and the reason the other two work.

**Where the XAE project template actually is.** `Solution2.GetProjectTemplate(name,
language)` is the documented way to locate a template, and it answers `FileNotFound` for
every name one might guess. Searched on 2026-09-08: the VS extension tree, both template
caches, and both TwinCAT install roots contain **no `.vstemplate` and no template zip** for
it. There is exactly one candidate on the machine:

```
C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PrjTemplate\
    TwinCAT Project.tsproj      67 bytes: <TcSmProject><Project/></TcSmProject>
    tsmprojects.vsdir           the old-style registration
    TwinCAT Project.ico
```

An old-style `.vsdir` template, not a `.vstemplate` — which is why `GetProjectTemplate`
cannot see it under any spelling. The project factory expands the stub when the project
opens. So the resolution order is: explicit `templatePath`, then a named
`templateName`/`templateLanguage` pair, then **the stub on disk** (probed under
`TWINCAT3DIR` first, since that is what the installer sets), then the name candidates for
a shell that does register a real template. `find_project_template` reports every route it
tried, so the answer is a measurement.

One more trap on the way in: `GetProjectTemplate` lives on `Solution2`, not `Solution`, and
late-bound `dynamic` cannot see it — every call answers *"'System.__ComObject' does not
contain a definition for 'GetProjectTemplate'"*, which reads like a missing method rather
than a missing interface. And casting inline (`(Solution2)dte.Solution`) does not help: the
operand is `dynamic`, so the whole expression stays on the DLR and the lookup happens on
`__ComObject` again, with the same message. The hand-off out of `dynamic` has to be an
ordinary assignment to `object` first.

**Exercised end to end** on a throwaway directory: empty solution created and saved, a
TwinCAT project added (`Mc4Tc.tsproj` on disk), a PLC project created inside it from the
Standard PLC Template (`Mc4Plc.plcproj` on disk), and POUs created in it — a whole project
tree from nothing, in one IDE that never had a solution open.

### 16. The HMI side, finished

The three pieces §11 deliberately left out.

**`hmi_publish publish` has now been run for real** — and it failed on the first attempt,
which is the whole argument for running a thing before calling it done:

```
Publish failed: Specified OLE variant is invalid.
```

`Publish(profileName, updateUi = opt, progressCallback = opt)`: the callback is **optional
and typed to an interface**, and a C# `null` reaches IDispatch as `VT_EMPTY`, which the
callee cannot convert to `ITcHmiPublishCallback` — `DISP_E_BADVARTYPE`, and the whole call
dies. An omitted optional is passed as `DISP_E_PARAMNOTFOUND`, which is what it expects, so
the argument had to go away rather than be nulled.

With that fixed the publish landed: 2114 files replaced by 2131 on the target instance, and
the check the tool's own `verdictMeans` prescribes came out right — `PROJECTNAME` updated at
`1787927547`, `RUNTIMES::AUT_Workbench::NETID` and `RUNTIMES::AUT_Workbench_DT::NETID` at
`1787927548`, one second newer. The server-extension configuration was pushed, not just the
upload. Two further findings:

- **Publish is asynchronous.** It returns while the upload is still running, so the result
  read straight afterwards is empty. The verb now waits for `IsPublishRunning` to go false
  (`waitMs`, default 10 min) and reports `waitedMs` and `completed`.
- **`GetPublishResult()` answers a plain `Boolean`** on this TE2000 build, not an
  `ITcHmiPublishResult` — which is the real reason the late-bound `Result` / `IsCompleted` /
  `SubmissionId` reads all came back null. §11 recorded those three fields from reflection
  on the interface; the object does not implement it. So the result is reported as the bool
  it is, with a note that it means the call succeeded and not that the configuration landed.
- Publishing **rewrites the profile file itself** (`tcHmiServerPort` becomes a number,
  `socketTimeout` appears) and touches `engineering.html`. Expect both at `git status`.

**Internal symbols work.** `TcHmiAutomation.dll` is now referenced (resolved at runtime by
`VsInterop` from the TE2000 directories, like the PIAs; `TE2000_ASSEMBLY_DIR` overrides),
and `HmiTyped` casts the late-bound RCW to the typed interfaces. The assembly is
`[ComVisible(true)]` with a Guid per interface, so this is not a workaround: when late
binding cannot see a member, ask for the interface by IID. `hmi_symbol internal_add` and
`internal_remove` are exposed and exercised — created, read back with all five fields
(`name`, `datatype`, `defaultValue`, `persist`, `readOnly`), removed, confirmed gone. Note
that **`RemoveInternalSymbol` returns FALSE on a symbol it has just removed**, so its
return value is reported as a fact about the call and the verdict comes from reading the
project back.

One trap worth its own line: `TcHmiAutomation` is built against **envdte 8.0.0.0** while
this daemon references the 17.0.0.0 facade, and both are on the machine (MSB3247 at build
time). Naming `EnvDTE.ProjectItem` in our own code would pin `GetHmiFile`'s parameter to
*our* version and hand the callee a type it does not recognise, so the argument is passed
as a raw `object` through reflection and the conversion happens in TcHmiAutomation's own
binding context.

**`ITcHmiFile` — controls instead of HTML as text.** `hmi_file` exposes `identifiers`,
`source` / `set_source`, `add_control` (with `parent` / `before` / `after`, exactly one),
`remove_control`, `beautify` and `control`. Exercised on a real `Desktop.view`: the six
control ids enumerated, a control added inside the view, confirmed by re-reading the file,
removed, and the file left **byte-identical** to before.

Two measured limits, both reported by the verbs themselves rather than left to be
discovered:

- **`AddControl` returns null even when it has added the control.** The identifier list
  went from 6 entries to 7 while the call answered null. So the return value is reported as
  `controlReturned` and `added`/`confirmed` come from reading the file.
- **`ITcHmiFile.GetControl` answers null for every identifier** — ids it has just listed,
  controls just created, with the file open and `IsOpenAndReady` true — and
  `ITcHmiProject.GetControlInstance` wants a DOM node, not an id (*"invalid argument in
  constructor: node is null"*). So reading or changing the attributes of an **existing**
  control is out of reach on this build. `control` and `change_attributes` exist and say
  exactly that, with the routes they tried; `add_control` (attributes set at creation),
  `remove_control` and `source`/`set_source` are what work.

The file itself is located by walking the project's own `ProjectItems`, **not**
`Solution.FindProjectItem`, which answers null on an HMI project even for a file the
`.hmiproj` plainly declares — measured on `Desktop.view`, whose `<Content Include>` is right
there in the file. Trusting it produced an error message that stated something false about
the project.

### 17. Busy is not wedged

Opening a solution on a **cold** IDE does not fit in the ordinary 180 s ceiling: VS2022 plus
the XAE extension starting from scratch runs past it, the call dies for no reason the caller
can see, and then — because the pipe was serial per connection — every later call queued
behind it. `xae status` and `list_instances`, which exist precisely to be safe to call *in
order to decide*, timed out too, and the server read as broken rather than busy.

Three changes, and the third is the one that matters:

- **`xae_open_solution` gets its own 10-minute budget.** Not an infinite wait: an IDE that
  never comes up is worse. On a warm IDE the same call is effectively instant, so this
  costs nothing in the normal case.
- **Each request on a connection is handled on its own thread.** The client keeps one
  connection and correlates replies by id, which is what the id was always for, so nothing
  about COM changes — the single STA worker still serializes every call that needs the IDE
  — but a reply that IS ready now gets out now. One lock guards a whole response line plus
  its flush; interleaved bytes would break the framing for everything after them.
- **The daemon says what it is doing.** `ComWorker` reports the action in flight, how long
  it has been running and how many calls are queued. That report is on every timeout error,
  and `xae status` / `list_instances` are answered **without the STA thread** while it is
  busy — from the running-object table plus each process's window title, with no call into
  any IDE, because asking a busy IDE what it has open would block on the very thing being
  reported. What is missing from that answer (`isCurrent`, `startedByUs`, the solution
  *path*) is named in it: a partial answer that says which part is partial beats a timeout,
  and beats a full answer that is quietly a guess.

Measured during an 82-second rebuild, all three on the same connection: `ping` answered in
54 ms, `list_instances` in 87 ms, `status` in 96 ms, each naming `xae_solution_build` and
its elapsed time. At rest the full answers come back unchanged.

### 18. `plc_tests` — the verdict the tests actually give

The one thing that still forced the CI to be the source of truth for whether the code works:
the server could compile and read the Error List, but not say whether the tests passed.

`plc_tests run` is guarded (`ALLOW_PLC_TESTS`) and does the sequence that was proven by
hand: set the PLC project's boot autostart flag → save and build, stopping on a non-zero
`lastBuildInfo` → **delete** `<runtime>\Boot\tcunit_xunit_testresults.xml` → activate →
restart the runtime → wait for the file to reappear *and settle* → parse. Then it puts the
boot flag back the way it found it, because flipping it was this action's doing and not the
caller's intent.

Four things it will not do quietly:

- **The delete is not optional.** Without it you read the previous run's file and have no
  way to tell — same shape, same numbers, no clue. If the delete fails, the run is refused
  rather than started, because a run that produces nothing would then be
  indistinguishable from one that passed.
- **The totals are SUMMED from the `<testsuite>` elements.** The root `<testsuites tests>`
  attribute does not count the failures: a run with 3 red wrote `tests="65"` while its
  suites summed to 68. The attribute is reported separately and a disagreement is flagged
  — anyone reading it alone gets a total short by exactly the interesting cases.
- **Which Boot directory is matched to the target NetId**, never picked by convention. An
  engineering host has several user-mode runtimes (here `UmRT_Default`, `UmRT_Machine`,
  `UmRT_DT`) and each one's `3.1\TcRegistry.xml` carries its own AmsNetId as **binary hex**
  — `C7042AFA0101` is `199.4.42.250.1.1`. No match is an error listing what was found:
  reading the wrong directory would report another target's numbers as these.
- **Activation replaces what is running on the target**, and the result says so every time.
  Putting back what was there is the caller's job and is easy to forget.

The same tally also arrives in the Error List as `PlcTask` rows at severity **High** —
which the PLC compiler never uses, so runtime messages *can* be told apart by severity even
though compiler rows cannot. Those rows are returned as an independent cross-check: two
sources agreeing is a verdict, one source is a reading.

**Exercised end to end** on `AUT_StandardDevices_Test`: both guards refused first (no
project chosen; no `confirm`), then build `lastBuildInfo: 0`, the previous 35068-byte file
deleted with its timestamp recorded, activate + restart, results back in **13.7 s**, parsed
to **238 tests / 0 failures across 9 suites**, boot flags restored — and the Error List rows
at `vsBuildErrorLevelHigh` said `Successful tests: 238` / `Failed tests: 0`, matching.
`plc_tests results` parses whatever file is on disk and warns that its age says nothing
about the code you are asking about.

### 19. `plc_pou move` destroyed what it moved

`move` is export / delete / import: `ExportChild` writes the object to a temporary `.zip`,
the original is deleted so the global namespace is free, and `ImportChild` puts it back
under the new parent. The archive is where it went wrong. **`ExportChild` writes the
object's whole project path into the entry name** —

```
POUs\ContactorDT\Interfaces\IMotorSupplyDT.TcIO      <- a one-entry .zip
```

— and `ImportChild` faithfully **recreates that path under the destination**. Asking for
`POUs^MotorSupplyDT^Interfaces` therefore produced
`POUs^MotorSupplyDT^Interfaces^POUs^ContactorDT^Interfaces^IMotorSupplyDT`, the
verification (`newParent ^ name`) found nothing there, and the failure path then imported
the same archive under the **old** parent to restore it — recreating the nested path a
second time, and colliding on the name, so the "restored" object came back as
`IMotorSupplyDT_1` four folders deep. Net result of one call: the object gone from where
it belonged, two nested copies, one of them renamed, and an error message telling the
caller to import a recovery archive by hand.

It is not an edge case: it happens to **any object that lives in a subfolder**, which in
a project organised in folders is all of them.

The fix is one step between the export and the import: **repack the archive with the
source parent's directory prefix stripped from every entry**, so `ImportChild` has no path
to recreate and drops the object exactly where it was asked to.

- The prefix is derived from the archive itself — the directory segments every entry
  shares — never from a translated tree path, so there is nothing to keep in sync with how
  the tree names its nodes.
- **A folder move keeps the folder's own segment** and strips only its parents, so the
  moved subtree arrives intact instead of being flattened into the destination.
- The restore path gets the same flattened archive, so a move that still fails now really
  does put the original back where it was, instead of leaving a nested duplicate.
- The response says what happened: `via: "export-flatten-delete-import"` and
  `strippedPathSegments`, which is `0` when the archive was already flat.

**Exercised on a real reorganisation** (`AUT_StandardDevicesDT`, eight objects out of three
device folders into four new role folders): eight moves, `strippedPathSegments: 3` each,
every object landing directly under the requested parent, `check_objects`
`allObjectsValid: true`, and the `.plcproj` on disk showing exactly eight rewritten
`<Compile Include>` paths and nothing else touched.

One thing the round trip does change, and it is worth knowing before a move: the file
comes back **normalised by TwinCAT**. Content, GUIDs and attributes are preserved
byte for byte, but empty `<Implementation><ST><![CDATA[]]></ST></Implementation>` blocks on
interface members are dropped (they carry nothing — an interface has no implementation) and
the file is rewritten with CRLF line endings. On six of the eight objects above the file
was identical; on the two that still carried those empty blocks the diff was six and nine
deleted lines, all of them empty implementation envelopes.

### 20. `set_target_platform` refused the platform a user-mode runtime needs

`tc_settings set_target_platform` accepted only a hard-coded pair, `TwinCAT RT (x86)` and
`TwinCAT RT (x64)`. A 4026 solution that targets a user-mode runtime builds for
**`TwinCAT OS (x64)`**, which the list did not know, so the one platform we actually needed
was rejected before XAE was ever asked.

The list is gone. The value is written, then **read back**: a platform the solution does not
offer fails loudly with what is still active, instead of being reported as done. The tool
description now also says what was learned the hard way: the solution configuration
(`xae set_configuration`) and the target platform are **independent** — setting one does not
move the other, and activating with the wrong platform writes a boot project the runtime
ignores.

---

## Change log — which commit carries which change

`vs2022-support`, oldest first. The sections above say *why* each change exists; this
table says *where it is*, which is what the upstream PRs get split along — one section
per PR, so a reviewer never has to read a commit that belongs to another fix.

| Date | Commit | Section | Change |
|---|---|---|---|
| 2026-08-27 | `34474ba` | §1 | Resolve the EnvDTE PIAs when TE1000 is integrated in VS2022 (no TcXaeShell install), plus `TE1000_PIA_DIR`. |
| 2026-08-27 | `48ef41e` | §1 | Fix the `Visual Studio\20222\` typo in the daemon `.csproj` — MSB3245 on every build, while the runtime path in `VsInterop.cs` was right, so the error list worked anyway. |
| 2026-08-27 | `fd7d147` | §5 | Resolve the IEC project node instead of assuming it, for the tools that need `ITcPlcIECProject` (`save_as_library` without a `treePath`). |
| 2026-08-27 | `5d0f7b4` | §2, §3, §4, §6 | Severity filter that declares what TwinCAT cannot tell apart; `xae shutdown_ide`; solution paths compared as paths, not strings; `TE1000_MODE` (later `TE1000_DEFAULT_MODE`). |
| 2026-08-27 | `f1d079c` | — | This file, linked from the README. |
| 2026-08-27 | `57e7d89` | §7 | `xae list_instances` / `attach` / `forceNew`, and a session cache that honours the request instead of keying on `progId` alone. |
| 2026-08-27 | `69fac74` | §7 | `shutdown_ide` honours an explicit instance target rather than closing whichever IDE the session happened to hold. |
| 2026-09-01 | `87f8887` | — | `BACKLOG.md`: rough edges and missing capabilities measured in a full day of real use. Removed once they were all built or recorded here — see **Still to do**. |
| 2026-09-02 | `4af5433` | §8, §9 | `xae list_projects` / `select_project`, per-call `tsProject`, `TE1000_DEFAULT_TSPROJECT`, ambiguity declared on reads and refused by the target-changing verbs; `list_configurations` / `set_configuration`, per-project `xae_build`, and the typed `PlatformName` read. |
| 2026-09-03 | `89997d5` | §10 | `string[]` vInfo for the members of a POU, so 609 Method, 608 Action and 616 Transition can be created; `accessor` and `implSeed`; interface members 610 / 612 / 654 / 655; accessors may omit their name; the create guard resolves the tree before declaring a failure. |
| 2026-09-08 | `9de0090` | §11 | TwinCAT HMI (TE2000): `hmi_project` / `hmi_symbol` / `hmi_function` / `hmi_publish`, the pkgdef-resolved automation ProgId, the HMI project walk and session cache, and the publish pre-flight that refuses a profile which would skip the server-extension configuration. |
| 2026-09-08 | `3851865` | §12-§18 | `xae output`; `save:true` that actually saves and says whether it settled (the singular POU verbs ignored the flag outright); `save_as_library` reporting the real failure and taking `overwrite`; `create_solution` / `add_project` / `find_project_template`; the HMI side finished -- publish run for real, internal symbols early bound, `hmi_file` for controls; `open_solution`'s own budget plus diagnostics that answer while the STA thread is busy; and `plc_tests`, the TcUnit verdict. `BACKLOG.md` removed. |
| 2026-09-09 | `bca6377` | §19 | `plc_pou move` repacks the export archive flat, so a move lands under the parent it was given instead of recreating the old folder path under it -- and a failed move restores the original instead of leaving a nested duplicate. |
| 2026-10-08 | (this commit) | §20 | `set_target_platform` takes any platform the solution offers (`TwinCAT OS (x64)` for a user-mode runtime) and reads it back, instead of a hard-coded RT x86/x64 list. |

Upstream's own [CHANGELOG.md](CHANGELOG.md) is left untouched: it tracks their releases,
and a fork writing into it would collide on every merge from `upstream`.

---

## Things to know before using this (not bugs)

- **Save before you build.** A `plc_pou` write lands in the IDE's in-memory copy. Build
  without `xae save_all` first and you get the verdict for the **previous** version of
  the code — measured: the same build reported `lastBuildInfo: 0` with a broken symbol
  in memory, and `2` after the save. A "compile, read errors, fix" loop without the
  save reads a stale verdict and looks like it is passing.
- **Write PLC code through the tools, not the filesystem.** With the solution open, VS
  compiles its own copy; a `.TcPOU` edited from outside is invisible until the solution
  is reloaded (`closeExisting` + `discardChanges`, ~21 s).
- **`install_library` names the artifact after the file on disk.** Installing an export
  called `Foo_from_mcp.library` leaves the repository version folder holding that name
  instead of `Foo.library`. Name the exported file exactly as the library.
- **An IDE started through automation dies when you let go of it.** Instances created by
  the daemon shut down once the last COM reference is dropped — attach elsewhere and the
  one you started disappears. IDEs a person opened are unaffected; only ours are this
  short-lived. Use `list_instances` to see what actually survived rather than assuming.
- **The `ALLOW_*` tokens are not a human gate.** They are parameters the agent writes
  itself, enforced in `index.js`. The real gate is the MCP client's own permission
  prompt.
- **`BuildVInfo` carries two vInfo shapes, on purpose.** Creating the MEMBERS of a POU
  needs a `string[]` with the IEC language spelled out (`"ST"`); the top-level POU types
  and the Property have always been created with an `object[]` holding the language as a
  number (§10). Both work, and only the first is what Beckhoff's own samples use. The
  `object[]` cases are left alone deliberately: they are the ones proven in daily use, and
  a preventive sweep would put the whole authoring path at risk to gain consistency and
  nothing else. Worth aligning the day something already touches those cases — with 602,
  603, 604 and 611 all re-tested, not assumed.
- **Two identical builds of the daemon produce different bytes.** `Te1000Daemon.csproj`
  sets `<Deterministic>true</Deterministic>` and the in-box MSBuild compiler
  (`Framework64\v4.0.30319`) ignores it. Measured 2026-09-08 — same source, same
  configuration, same output directory, `obj` deleted in between:
  `48BA5514…` then `7186BA29…`. So "rebuild and compare the hash" proves nothing about an
  install, and a mismatch is **not** evidence of a source difference — which is the worse
  half, because it sends you looking for a change that is not there. Install by **copying**
  the binary that passed the tests into `bin\Release`; then the hashes are equal by
  construction and the check is real.
- **The front and the daemon reload differently.** `Te1000Daemon.exe` is its own process
  and comes back on the next call, so a daemon change can be tested immediately (stop it,
  rebuild, call). `index.js` and `toolSchemas.js` are loaded by the MCP client at session
  start, so a new tool or parameter is **not visible until the session restarts** — until
  then, drive the new daemon action through `daemonClient.js`.

---

## Still to do

There used to be a separate `BACKLOG.md` here. It is gone: everything on it was either
built (§12-§18), or was never a defect and is now recorded under **Things to know** above
— the two `vInfo` shapes, and the non-deterministic daemon build. Keeping a working list
alongside a record of what was measured meant the same facts written twice, drifting apart.

What is left:

- **Send each fix above upstream as its own PR.** That is what the change log's
  one-section-per-row shape is for.
- **Static analysis and documentation.** `RunStaticAnalysis()` + `ExportToSarif()` and doc
  generation are the last two things `TcCIBuilder` has that this does not, and therefore
  the last reason `ci.yml` shells out to it (steps 9 and 11). The test verdict no longer
  is — see §18.
- **Getting the data out of a Scope recording.** `tc_measurement` can create a Scope
  project, add children, rename them and start/stop a recording, but not **retrieve** what
  was recorded: `IMeasurementScope` exposes `SaveSVD`, `ExportCSV` and `LookUpChild` and
  `MeasurementActions.cs` leaves all three unexposed, marked UNVERIFIED. Start/stop
  without export is a button, not a measurement, and it blocks the obvious workflow —
  record a machine cycle, export it, compare it against the previous run. Verification is
  cheap (call them by reflection the way `ScopeHelper` already calls `CreateChild` /
  `StartRecord`), but four things have to be pinned down before they are exposed: the
  **signatures** (the four verified calls return an `int` rc; assume the same until
  measured), whether the paths are absolute and what happens when the file exists
  (silently overwrite is the likely answer, and the verb should say so), whether either
  call is **synchronous** — an export that returns before the file is complete hands the
  caller a truncated read, which looks like corrupt data — and whether they can be called
  **while recording** or only after `StopRecord`. Like `analytics_create`, none of it means
  anything without a live target and a real channel, so the test needs more than a project
  node.
- **A guard for contention on a shared user-mode runtime.** When an IDE, a CI runner and
  this server share one UmRT, activating or running tests from here can disturb a build
  someone else is in the middle of — and §18 makes that easier to do by accident, not
  harder. Not specific to us: anyone running a CI runner on the engineering host has it,
  so it belongs upstream too.

### Not the server's problem

Recorded so they are not mistaken for bugs here.

- **A stale installed library.** After rebuilding a base library, a consumer can fail with
  errors pointing at a library nobody touched. The cure is to rebuild and reinstall that
  intermediate library — and only that one: the link that breaks is the one between the
  changed library and the consumer that failed, not the whole chain.
- **Incremental build after an out-of-band library change.** Once a library has been
  rebuilt elsewhere, the consumer needs `rebuild`, not `build`; the incremental one fails
  with the reasonless message §12 exists to explain.
- **`{attribute 'hide'}` removes a member from the ADS symbol table**, so an HMI binding
  onto it goes quiet. A PLC-side decision, not an automation one.
- **`plc_pou rename` trips a CoDeSys assertion.** Renaming a DUT raises a modal
  `Assertion Failed: Abort=Quit, Retry=Debug, Ignore=Continue` with a stack through
  `_3S.CoDeSys.UML.DiagramController` — `RefactoringPerformer.PerformRefactoring` loading
  every object and tripping over the UML context, on a project that has no diagrams at
  all. **Ignore** completes the rename correctly, references included, and several arrive
  in a row; each one blocks the daemon until it is answered. Not our bug, but ours to live
  with, so there is now a ready-made rule for it in `dialog-allowlist.json` — as an
  **example**, not in `rules`. Auto-clicking through an assertion is exactly the rule that
  will one day hide a real failure, so switching it on has to be a decision someone made:
  copy it in before a batch of renames, take it out afterwards.
