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

### 6. `TE1000_MODE` — an environment default for `mode`

`mode` is a **per-call** parameter defaulting to `active`, and `active` attaches to
whichever running IDE the ROT offers first that has any solution open
(`ComSession.GetPreferredDteFromRot`). On a machine where someone is working in Visual
Studio, an unqualified call therefore reaches **their** IDE and builds **their**
solution. Some tools — `xae_build` among them — expose no `mode` parameter at all, so
per-call discipline cannot cover every path.

`TE1000_MODE` is the companion of the existing `TE1000_PROGID`. Set it to `create` to
keep a session on its own instance. An explicit per-call `mode` still wins.

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
- **The `ALLOW_*` tokens are not a human gate.** They are parameters the agent writes
  itself, enforced in `index.js`. The real gate is the MCP client's own permission
  prompt.

---

## Still to do

- Send each fix above upstream as its own PR.
- Add what our CI needs and this server does not have yet: running the TcUnit suite
  (without the runtime restart other servers do), `RunStaticAnalysis()` +
  `ExportToSarif()`, and documentation generation.
- A guard for contention on a shared user-mode runtime. When an IDE, a CI runner and
  this server share one UmRT, activating or running tests from here can disturb a build
  someone else is in the middle of. That is not specific to us — anyone running a CI
  runner on the engineering host has it — so it belongs upstream too.
