# Agent quickstart — setting up and using this fork from another machine

For an AI agent (Claude Code or any MCP client) that has to install this server on a fresh
Windows VM and drive TwinCAT XAE with it **without reading false verdicts**. The README is
upstream's and describes upstream's defaults; where it disagrees with this page, this page
wins. The *why* of every fork change is in [FORK-NOTES.md](../FORK-NOTES.md).

---

## 0. Which IDE hosts TE1000 — decide this first

| Host | ProgID (`TE1000_PROGID`) | Bitness | Status in this fork |
|---|---|---|---|
| TE1000 integrated in **Visual Studio 2022** (4026) | `VisualStudio.DTE.17.0` | 64-bit | daily use, everything below measured here |
| **TcXaeShell64** (4026, VS2022-based) | `TcXaeShell.DTE.17.0` (the default) | 64-bit | upstream's reference host |
| **TcXaeShell of 4024** (VS2017-based) | `TcXaeShell.DTE.15.0` | **32-bit** | **NOT verified** — see below |

Find out which ProgIDs the machine has registered:

```powershell
Get-ChildItem Registry::HKEY_CLASSES_ROOT | Where-Object { $_.PSChildName -match '\.DTE\.\d' } | Select -Expand PSChildName
```

**On a 4024 XAE Shell (32-bit)** the daemon is still an x64 process. Talking to a 32-bit
out-of-process IDE goes through COM marshalling, which normally works for IDispatch, but nobody
has run this combination yet: upstream documents the 64-bit shell as a requirement. So the
first thing to do on such a machine is the **smoke test** (end of §1), and to report what it
says before relying on anything else. Also on 4024:

- the PIAs (`envdte*.dll`) come from `C:\Program Files (x86)\Beckhoff\TcXaeShell\Common7\IDE\PublicAssemblies`
  — probed automatically, both by `build.ps1` and at runtime;
- **there is no user-mode runtime** and no `TwinCAT OS (x64)` platform (§4): targets are
  `TwinCAT RT (x64)` / `TwinCAT RT (x86)`, with a real-time runtime on the machine or a remote target;
- the behaviours in §5-§9 were **measured on 4026**. Treat them as likely, not proven, and
  re-check the ones a verdict depends on (save-before-build, severity, `lastBuildInfo`).

## 1. Install

Prerequisites: TwinCAT 3.1 with TE1000 (see §0), .NET Framework 4.7.2+, Node.js 20+.

```powershell
git clone -b vs2022-support https://github.com/AU-tomation-org/TwinCAT-XAE-MCP.git
cd TwinCAT-XAE-MCP
npm install
powershell -ExecutionPolicy Bypass -File daemon\build.ps1   # -> daemon\bin\Release\Te1000Daemon.exe
```

- Clone **this fork** on branch **`vs2022-support`** — not `Edge-JB/...` as the README says,
  and not `main`, which tracks upstream.
- The repository may be private: authenticate with `gh auth login` and push/pull with
  `git -c credential.helper="!gh auth git-credential" ...`.
- `build.ps1` uses the in-box .NET Framework MSBuild (C# 5: no string interpolation, no
  `out var`, no expression-bodied members, no `nameof`, no tuples). If `EnvDTE80` does not
  resolve, set `TE1000_PIA_DIR` to the `Common7\IDE\PublicAssemblies` folder of your IDE.

Smoke test, in this order (each step isolates one layer):

```powershell
node daemon\test-ping.js     # daemon alone, no IDE: proves the build and the pipe
```

then, with the IDE **open on a solution** and the client configured (§2): `xae list_instances`
(sees the IDE through the ROT?), `xae attach pid:<pid>`, `xae list_projects`, and a
`xae_build build` followed by `xae error_list`. `CO_E_CLASSSTRING` means a wrong ProgID; an
empty `list_instances` with the IDE open means the ROT entry is not under that ProgID.

## 2. Register it with the client

Project-scoped `.mcp.json` (Claude Code), next to the solutions you work on:

```jsonc
{
  "mcpServers": {
    "twincat-xae": {
      "type": "stdio",
      "command": "node",
      "args": ["C:\\path\\to\\TwinCAT-XAE-MCP\\index.js"],
      "env": {
        "TE1000_PROGID": "VisualStudio.DTE.17.0",   // 4024 XAE Shell: "TcXaeShell.DTE.15.0"
        "TE1000_DAEMON_PIPE": "te1000-aut"
      }
    }
  }
}
```

| Variable | Why |
|---|---|
| `TE1000_PROGID` | The ProgID of §0. **Mandatory unless the host is TcXaeShell64**: the default (`TcXaeShell.DTE.17.0`) fails with `CO_E_CLASSSTRING` elsewhere. Every direct daemon payload (§10) needs the same value as `progId`. |
| `TE1000_DAEMON_PIPE` | Any name; a non-default one keeps this daemon apart from an upstream install on the same machine. |
| `TE1000_DEFAULT_MODE=create` | Optional. Without it an unqualified call attaches to **whatever IDE the ROT lists first** — possibly a person's, with their solution in it. |
| `TE1000_DEFAULT_TSPROJECT` | Optional. Default `.tsproj` when a solution holds more than one (§4). |

Restart the client session after registering: tools and schemas are loaded at session start.

## 3. Before the first call: look, then choose

```
Get-Process devenv,TcXaeShell,TcXaeShell64,Te1000Daemon -ErrorAction SilentlyContinue | Select Name,Id,MainWindowTitle
xae list_instances        -> pid, open solution, isCurrent, startedByUs (attaches to nothing)
xae attach pid:<pid>      -> binds the session to THAT IDE; sticks for later calls, xae_build included
```

- `mode` says *how* to get an IDE (`create` / `active` / `activeOrCreate`), never *which*. Use
  `attach`. A target that is not there is an error listing what is — never a silent fallback.
- An IDE **started by automation dies** when the last COM reference drops (e.g. attaching
  elsewhere). IDEs opened by a person are unaffected.
- First attach to a cold IDE can take **over two minutes**. `xae status` / `list_instances`
  answer even while the worker is busy (they report the action in flight).
- Quicker than `open_solution` on a just-started VS: `Start-Process devenv.exe <sln>`, wait
  for `MainWindowTitle` to name the solution, then `xae attach pid:<pid>`.

## 4. Choose the TwinCAT project and the configuration

A solution has one system manager per `.tsproj` (a library and its test suite are two). NetId,
`TIPC`, boot flags and activation depend on which one.

```
xae list_projects                                 -> name, path, targetNetId, isCurrent
xae select_project name:"<Lib>_Tests"             -> binds the session; redo after open_solution
xae list_configurations                           -> all configurations, which one is active
xae set_configuration name:"Release|TwinCAT OS (x64)"
tc_settings set_target_platform platform:"TwinCAT OS (x64)"
```

- With more than one `.tsproj` and no choice, **target-changing verbs refuse** (activate,
  restart, download, boot flags, `set_netid`, `set_target_platform`); reads answer from the
  first project and flag `tsProjectAmbiguous: true`. `tsProject` on a single call overrides
  for that call only.
- **`TwinCAT OS (x64)` is the platform of a user-mode runtime (UmRT, 4026 only).** On 4024 use
  `TwinCAT RT (x64)` / `TwinCAT RT (x86)`. Solution configuration
  and target platform are **independent**: setting one does not move the other, and activating
  with the wrong platform writes a boot project the runtime ignores.
- `xae_build` builds the **active** configuration; the response says which. A green build here
  and red in CI (or the reverse) is usually this.

## 5. The two traps that produce a false verdict

**Save before you build.** `plc_pou` writes land in the IDE's in-memory copy; a build without a
save compiles the **previous** version and reports its verdict. The loop is:

```
write -> xae save_all -> xae clear_error_list -> xae_build build -> xae error_list
```

With `save:true` on a write, check the response says `save.settled: true`. If it says `false`,
do **not** reopen the solution with `discardChanges` — that throws away what is still dirty.

**Severity is not a severity.** On PLC projects every row — error, `{warning}`, `{info}` — comes
back as `Medium`; `severityFilter:"errors"` would return nothing, so the fork keeps the rows and
marks them `severityUndecidable`. **The verdict is `lastBuildInfo` (0 = no errors)**; what is
broken is in the `description`s. A build that fails with a one-line, reasonless message:
the detail is in `xae output` (pane `Build`).

## 6. Verdicts that cannot fail (and what to use instead)

| Green that means nothing | Real verdict |
|---|---|
| `xae_build` on a **library-only** solution (no task, no code generated: declarations unchecked) | `plc_pou check_objects` (`allObjectsValid`) + Error List; better, build the **test** project |
| `check_objects` with new `TC_EVENTS.*` event classes (it does not resolve them) | build of the test project |
| `hmi_project build` (proves packaging, not bindings — see `verdictMeans`) | the HMI running |
| `tc_cpp tmc_codegen` → `tmcCodeGenerated: true` (writes nothing) | the C++ compiler |
| `tc_cpp publish` → `published: true` (nothing reaches the Repository) | the files in `Repository\<Vendor>\<Module>\<ver>\` |

General rule: break the case on purpose once and watch it go red; a check that cannot fail is
not a check.

## 7. Writing code through the tools

- **Write PLC code through `plc_pou`, never the filesystem**, while the solution is open: VS
  compiles its own copy. A file edited from outside is invisible until
  `open_solution closeExisting:true discardChanges:true` (~21 s).
- **New POUs/DUTs/GVLs: create them with `plc_pou create` / `create_batch`**, which register
  them in the `.plcproj`. Never add `<Compile Include>` by hand. For many objects: `create_batch`
  skeletons → `save_all` → check the `<Compile Include>` on disk → write full `.TcPOU`/`.TcIO`
  files from outside → `open_solution closeExisting:true` (mandatory, or VS builds the empty copies).
- Members: Method `subType: 609`, Property 611 on POUs; on **interfaces** 610 Method, 612
  Property, 654/655 Get/Set. Body with `set_impl`, header with `set_decl`. `set_document` does not
  work on interfaces.
- `plc_pou rename` of a DUT raises CoDeSys `Assertion Failed` dialogs: **Ignore** completes the
  rename. Follow with `xae save_all` and check the `.plcproj`.
- `install_library` names the installed artifact **after the file on disk**: export with the
  exact library name. `save_as_library` never overwrites unless `overwrite: true`.
- Process-image symbols for `tc_link`: `TIPC^<prj>^<prj> Instance^PlcTask Inputs^...` (not under
  `<prj> Project^PlcTask`). Copy the shape from `tc_link resolve` on a link that already exists.
- HMI: create files with `hmi_project add_view/add_usercontrol/add_content` or
  `hmi_function create` — a hand-written file the `.hmiproj` does not declare does not exist for
  build and publish. With more than one HMI project pass `hmiProject`.

## 8. Gated verbs and the shared runtime

The `confirm: "ALLOW_*"` tokens (`ALLOW_TWINCAT_ACTIVATE`, `ALLOW_TWINCAT_RESTART`,
`ALLOW_PLC_DOWNLOAD`, `ALLOW_PLC_TESTS`, `ALLOW_HMI_PUBLISH`, `ALLOW_XAE_SHUTDOWN`, ...) are **not
a human gate**: the agent writes them itself. The real gate is the client's permission prompt —
**ask the person before** activating, restarting, downloading or running tests.

- `plc_tests run` does the whole TcUnit sequence (boot flags, build, delete old results, activate,
  restart, wait, read). It **takes the runtime and replaces what was running there**. Totals are
  summed from the `<testsuite>`s.
- On an engineering VM the IDE, a CI runner and this server often share **one user-mode runtime**:
  an activation from here can break someone else's build or CI run. Ask first, and report what
  you left on the boot configuration.
- `hmi_publish publish` replaces the project on that TcHmiSrv instance (one project per instance).
- `xae shutdown_ide` (`ALLOW_XAE_SHUTDOWN`) closes an IDE cleanly; killing the daemon leaves
  `devenv` alive.

## 9. Known crashes and gaps

- `tc_module create` **crashes devenv** (`RPC failed 0x800706BE`): write the TcCOM instance in
  the `.xti` by hand. After a crash: kill every IDE process (`devenv` / `TcXaeShell`), start one, `attach` again.
- `tc_cpp create_module` fails with any template: generate the class from the template in
  `Components\Base\CppTemplate\Templates\` by hand.
- Dialogs with `Buttons: (none detected)` (some WPF dialogs, e.g. "modified outside... reload?"):
  read and click them with UI Automation from PowerShell. Read before clicking.
- Not available: static analysis / SARIF, documentation generation, exporting a Scope recording,
  reading the attributes of an existing HMI control.

## 10. Changing the server itself

- The **daemon** reloads on the next call: stop it **by path** (a bare
  `Stop-Process -Name Te1000Daemon` also kills the one in use), rebuild, call. It locks its own
  exe while running (MSB3027).
- **`index.js` / `toolSchemas.js` load at session start**: a new tool or parameter is invisible
  until the client restarts. Meanwhile drive the daemon through `daemonClient.js`
  (`runViaDaemon`; call `process.exit()` at the end, the pipe keeps Node alive).
- To test a new daemon next to the live one: build with `-p:OutputPath=<scratch>`, run it with
  `--pipe te1000-probe`, every payload carrying `progId` (the ProgID of §0). Never put a
  mutating action in a probe sequence.
- **Builds are not deterministic** (same source, different SHA256): install by **copying** the
  tested binary into `daemon\bin\Release`, not by rebuilding there.
- Record each change as a numbered section in [FORK-NOTES.md](../FORK-NOTES.md) and a row in its
  change log.
