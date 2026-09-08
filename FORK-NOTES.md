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
was nothing safe to publish to.

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
| 2026-09-01 | `87f8887` | — | [BACKLOG.md](BACKLOG.md): rough edges and missing capabilities measured in a full day of real use. |
| 2026-09-02 | `4af5433` | §8, §9 | `xae list_projects` / `select_project`, per-call `tsProject`, `TE1000_DEFAULT_TSPROJECT`, ambiguity declared on reads and refused by the target-changing verbs; `list_configurations` / `set_configuration`, per-project `xae_build`, and the typed `PlatformName` read. |
| 2026-09-03 | `89997d5` | §10 | `string[]` vInfo for the members of a POU, so 609 Method, 608 Action and 616 Transition can be created; `accessor` and `implSeed`; interface members 610 / 612 / 654 / 655; accessors may omit their name; the create guard resolves the tree before declaring a failure. |
| 2026-09-08 | (this change) | §11 | TwinCAT HMI (TE2000): `hmi_project` / `hmi_symbol` / `hmi_function` / `hmi_publish`, the pkgdef-resolved automation ProgId, the HMI project walk and session cache, and the publish pre-flight that refuses a profile which would skip the server-extension configuration. |

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

---

## Still to do

The working list — rough edges hit in real use, missing capabilities in order of value,
and what is deliberately out of scope — lives in [BACKLOG.md](BACKLOG.md). The summary:

- Send each fix above upstream as its own PR.
- Add what our CI needs and this server does not have yet: running the TcUnit suite
  (without the runtime restart other servers do), `RunStaticAnalysis()` +
  `ExportToSarif()`, and documentation generation.
- Finish the HMI side: internal symbols (they need the daemon to bind
  `TcHmiAutomation.dll` early, see §11), `ITcHmiFile` control-level editing for placing
  widgets and bindings without touching HTML as text, and an `hmi_publish publish` that
  has actually been run end to end.
- A guard for contention on a shared user-mode runtime. When an IDE, a CI runner and
  this server share one UmRT, activating or running tests from here can disturb a build
  someone else is in the middle of. That is not specific to us — anyone running a CI
  runner on the engineering host has it — so it belongs upstream too.
