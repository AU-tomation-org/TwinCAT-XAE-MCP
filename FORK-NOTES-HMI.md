# Fork notes — TwinCAT HMI (TE2000) support

**Status: research, no code.** This file stages everything measured on 2026-09-04 about
driving TwinCAT HMI from automation, so that an implementation can start from facts instead
of from a spike. When the `hmi_*` verbs land, fold the parts that survive into
[FORK-NOTES.md](FORK-NOTES.md) as another numbered section and delete this file.

Everything below was measured on this machine: VS2022 Enterprise 17.14, TE2000 engineering
23.0.421, HMI framework 14.5.1, `TcHmiAutomation.dll` version 1.0.0.0. Method lists come from
reflection over the shipped assembly, not from documentation.

---

## 1. The entry point

```powershell
$dte = [Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.17.0')
$hmi = $dte.GetObject('Beckhoff.TcHmi.1.12')     # ITcHmiAutomation
```

Verified against a live IDE: `Version` = 1, `GetProcessId()` = the devenv pid the ROT reports,
`GetHmiProjects()` = 2 for a solution holding two `.hmiproj`.

The name is **not guessable** — eight plausible spellings all returned
`DISP_E_MEMBERNOTFOUND`. It is registered by the TE2000 VS package, in
`…\Common7\IDE\Extensions\Beckhoff Automation GmbH\TwinCAT HMI\TcHmiPackage.pkgdef`:

```
[$RootKey$\Packages\{16a09aeb-7616-4147-ab22-721f8fb090fc}\Automation]
"Beckhoff.TcHmi.1.12"=""
```

**The `1.12` suffix tracks the HMI framework generation.** Do not hardcode it. Either parse
that pkgdef (its path is stable relative to the VS install), or probe a small range of
suffixes and keep the one that answers. A hardcoded name is a silent break on the next TE2000
upgrade, and it will look like "the HMI package is not installed".

### How the neighbours are reached — measured, not assumed

Three different acquisition patterns live in one solution. Getting this wrong looks like
"the product is not installed", so it is worth writing down.

| surface | how you get it | interface |
|---|---|---|
| HMI (TE2000) | `dte.GetObject("Beckhoff.TcHmi.1.12")` | `ITcHmiAutomation` |
| System manager (XAE) | `project.Object` per project; `dte.GetObject("TcSysManager")` as the solution-wide fallback this daemon already uses | `ITcSysManager` |
| Measurement / Scope (TE130X) | `project.Object` **only** — there is no DTE automation object | `IMeasurementScope` |

`TcatSysManager` is *not* a valid name (`DISP_E_MEMBERNOTFOUND`); the working one is
`TcSysManager`.

The full list of automation objects the installed Beckhoff packages register, swept out of the
`.pkgdef` files under `Common7\IDE\Extensions\` and then each one probed against a live IDE:

| package | name | probe |
|---|---|---|
| TwinCAT HMI | `Beckhoff.TcHmi.1.12` | answers |
| TwinCAT XAE Base | `TcProjects` — *"Project Collection for TwinCAT SystemManager Automation"* | answers, `Count` = 3 on a 4-project solution — **unexplored**, and possibly a cleaner project enumerator than walking `Solution.Projects` |
| TwinCAT XAE Integration | `BeckhoffXaeHelperPackageGeneral` | answers — unexplored |

Nothing is registered for Measurement, which is why `MeasurementActions.cs` resolves
`IMeasurementScope` from the project node instead. That file is also where the sharpest
neighbouring lesson already lives: **`IMeasurementScope` is a vtable/IUnknown interface and
cannot be late-bound through `dynamic`**, so it is invoked by reflection against the interface
type found by name in the loaded `TwinCAT.Measurement.AutomationInterface.dll`. `ITcHmiAutomation`
is not in that category — it is a dispatch interface and answers late-bound calls — but its
*children* (`ITcHmiProject`, `ITcHmiFile`, …) were only exercised late-bound from PowerShell,
where property reads came back empty. Assume nothing until the daemon binds them early.

So both families are reached from the same `DTE` and belong in the **same COM session on the
same STA thread**. That is the main architectural consequence: this is a new family of verbs
inside this server, not a second MCP server. Two servers attaching to one IDE reintroduce
exactly the orphaned-devenv and ROT-ambiguity problems that FORK-NOTES sections 3 and 7 exist
to solve.

## 2. It is real COM, so bind early

`TcHmiAutomation.dll` (in `…\TE2000-HMI-Engineering\MSBuild\`, also under
`…\VisualStudio\2022\`) is `[ComVisible(true)]`, assembly GUID
`db3528fc-8dfc-4a70-930c-9258257d6802`, and every interface carries its own GUID:

| interface | GUID |
|---|---|
| `ITcHmiAutomation` | `148C5A9B-4A87-4198-93F4-BCF598BD7847` |
| `ITcHmiProject` | `0CC8DBFF-86E4-4DA6-A82B-2C1299BCF5D4` |
| `ITcHmiFile` | `585762DB-D429-4A99-975D-B2B5BF4839E4` |
| `ITcHmiMappedSymbol` | `7D83709E-5C7F-4A1B-83A1-229E352430CB` |
| `ITcHmiServer` / `2` / `3` | `C3147548-…` / `D9F88FF5-…` / `81BAE47E-43B4-4069-8AEC-27AC07604CEE` |
| `Publish.ITcHmiProfile` | `1FA067F7-56E0-4DDF-AB94-5096EAC2B965` |
| `Publish.ITcHmiPublishCallback` | `EDE88DE9-A678-48DA-A1CA-4325808B4CD2` |
| `Publish.ITcHmiPublishResult` | `0B64604C-24A4-4A75-B94D-DB8523C5D0D3` |

`ITcHmiAutomation` is registered under
`HKLM\SOFTWARE\Classes\Wow6432Node\Interface\{148C5A9B-…}`.

The daemon should **reference the assembly and bind early**, the way it already does for the
EnvDTE PIAs (FORK-NOTES section 1). Late binding was tried from PowerShell and is painful:
children come back as bare `System.__ComObject` and property reads return empty. Note the
assembly ships in two places — resolve it the way the pkgdef does, from
`HKCU\Software\Beckhoff\TwinCAT3\3.1@InstallDir` + `..\Functions\TE2000-HMI-Engineering\`,
rather than assuming a path.

---

## 3. The object model, as far as reflection shows it

### `ITcHmiAutomation`

```
ITcHmiProject[] GetHmiProjects()
ITcHmiProject   GetHmiProject(string name)
ITcHmiProject   GetHmiProject(EnvDTE.Project project)
ITcHmiFile      GetHmiFile(EnvDTE.ProjectItem item)
ITcHmiProject   CreateHmiProject(string templateName, string solutionDirectory, string projectName)
EnvDTE.Project  CreateProject(string templateName, string solutionDirectory, string projectName)
EnvDTE.Project[] GetProjects()
void SaveAllFiles()
void CloseSolution()
void SuppressUi(bool state)
void ShowToolWindow(ToolWindowClassifier classifier)
int  GetProcessId()
int  Version { get; set; }
```

`GetHmiProject` takes an `EnvDTE.Project`, so the existing project-selection machinery
(`xae list_projects` / `select_project`, FORK-NOTES section 8) extends to HMI projects
naturally: same solution walk, different node kind.

`SuppressUi(bool)` looks like the switch that matters for unattended runs. **Untested** — and
it is a *mutating* call on the user's IDE, so it needs the same care as
`xae shutdown_ide`: opt-in, restored afterwards, never on by default.

### `ITcHmiProject`

| group | methods |
|---|---|
| items | `AddView(path)`, `AddUserControl(path)`, `AddContent(path)`, `AddTheme(name)` → all return `ITcHmiFile`; `AddLocalization(name, isoLanguage)` |
| project config | `ChangeConfig(ConfigFields, object)`, `GetConfigValue(ConfigFields)`, `ChangeStartupView`, `ChangeTheme`, `ChangeLoginPage`, `ChangeLocale`, `ChangeScaleMode`, `ChangeWebsocketIntervalTime` / `…Timeout` / `…SystemTimeout`, `ToggleSubscriptionMode` |
| symbols | `GetMappedSymbolInstance()`, `GetSymbolInstance(name, value)`, `AddInternalSymbol`, `GetInternalSymbol(name)`, `GetInternalSymbolInstance(name, value, type, persist, isReadonly)`, `RemoveInternalSymbol`, `RefreshSymbols()` |
| controls | `GetControlInstance(node, ITcHmiFile ctx)`, `GetControlAttributeInstance(name, value)`, `GetControlAccessRightInstance()` |
| localization | `AddLocalizationEntry(key, text)`, `AddLocalizationEntries(entries)`, `RemoveLocalizationEntry/Entries` |
| NuGet | `AddNuGetPackage(source, identifier, version)`, `RemoveNuGetPackage(identifier)`, `GetNuGetSources()` |
| build | `Build(string solutionConfiguration, bool updateUi)`, `Clean(bool updateUi)` |
| publish | `Publish(string profileName, bool updateUi, ITcHmiPublishCallback)`, `Publish(ITcHmiProfile, …)`, `GetPublishProfile(name)`, `GetProfileInstance(name)`, `IsValidPublishProfile(name \| profile)`, `IsPublishRunning()`, `GetPublishResult()` |
| misc | `Rename(name)`, `IsReady()`, `ShowInBrowser(build, https)`, `GetProjectInformation()`, `GetPermissionInterface()`, `GetRecipeInterface()`, `GetServerInterface()` |

There is **no `Name` property**; identity comes from `GetProjectInformation()` →
`ITcHmiProjectInformation`. A late-bound `.Name` silently returns empty, which is how the
spike first mis-read "2 projects with no names".

### Server and symbols

`GetServerInterface()` returns `ITcHmiServer`, with `ITcHmiServer2` / `ITcHmiServer3`
extensions. The one that matters:

```
// ITcHmiServer3
bool MapSymbol(string mapName, string internalName, string subSymbolName, string domain)
```

`ITcHmiMappedSymbol` exposes `MappedName`, `Domain`, `Type`, `DataTypeDisplayName`,
`ReadOnly`, `Hidden`, `HistorizeSettings`, `ProjectContext`, plus
`ApplyHistorizeSettings(…)`.

`ITcHmiServerExtension` is reachable through
`GetServerExtensionInstance(ITcHmiServer serverAi)` — that is the surface for the ADS
extension configuration, i.e. the runtimes block discussed in section 6 below.

### What is missing

**There is no `AddFunction`.** Views, UserControls, themes, localizations, content files and
NuGet packages have first-class verbs; Functions do not. A Function has to be assembled from
`AddContent` for the files plus `ChangeConfig` for the `dependencyFiles` registration.

That gap is the strongest argument *for* the verb rather than against it. A Function's name
lives in **six places** — the `.ts` filename, the `export function` name, the string passed to
`registerFunctionEx`, `function.name` and `function.displayName` in the `.function.json`, and
the `dependencyFiles[].name` entry in `tchmiconfig.json` — and the IDE's own rename aligns
only part of them *and* deletes module-scope variables declared outside the function body. A
verb that writes all six by construction is more reliable than the IDE, not a shortcut around
it. The same applies to `dependencyFiles[].type`, which the wizard sets to `EsModule` where
namespace-style `.js` needs `JavaScript`, producing a `ReferenceError` at runtime and a clean
compile.

---

## 3-bis. `AddUserControl` exercised for real, 2026-09-08 — and it works late-bound

The first mutating call of this family actually used in anger, driving the live IDE from
Windows PowerShell 5.1 to create the `SafetyCircuit` control of `AUT_Workbench_HMI`. It
settles two of the doubts above and adds one argument for the verb.

```powershell
$dte  = [Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.17.0')
$hmi  = $dte.GetObject('Beckhoff.TcHmi.1.12')
$proj = @($hmi.GetHmiProjects())[0]
$file = $proj.AddUserControl('UserControls\SafetyCircuit.usercontrol')   # returns ITcHmiFile
$hmi.SaveAllFiles()
```

- **METHODS dispatch late-bound; only property READS come back empty.** Section 2 says the
  children were "only exercised late-bound from PowerShell, where property reads came back
  empty" and warns to assume nothing. Now measured: `IsReady()` answers, `AddUserControl`
  does the whole job, `SaveAllFiles` works. What stays empty is exactly what section 3 says
  is empty — `ITcHmiProjectInformation.Name` and friends. **Bind early for reading, but a
  write verb does not need it**, which lowers the cost of the first `hmi_*` verbs
  considerably.
- **The path is project-relative, with backslashes and the extension**:
  `UserControls\SafetyCircuit.usercontrol`. The `.usercontrol.json` is created alongside,
  unasked.
- **It writes THREE registrations, and that is the argument for the verb**, stronger than
  section 5 puts it. Creating one user control touched:

  | file | what it got |
  |---|---|
  | `<proj>.hmiproj` | two `<Content Include>` items, the `.json` carrying `<DependentUpon>` |
  | `Properties\tchmiconfig.json` | an entry in the user-control list, `{"url": "UserControls/SafetyCircuit.usercontrol"}` |
  | `Properties\tchmi.project.Schema.json` | the control's own definition, `tchmi:project#/definitions/SafetyCircuit`, with `frameworkUserControlConfig` pointing at the `.json` |

  A hand-written file plus a hand-written `.hmiproj` entry — the obvious shortcut — gets one
  of the three and silently loses the other two.
- **A modal dialog never appeared**, so `SuppressUi` was not needed for this call. The
  DialogWatcher stays the safety net for `Publish`, which is where section 6 says the
  trouble is.

### And a trap for whatever `hmi_project build` returns

**An HMI project build stays GREEN with a misspelled binding attribute.** Measured the same
day: `data-tchmi-ctrljsondata` renamed to `data-tchmi-ctrljsondataBROKEN` on a
`TcHmiUserControlHost`, build run, `lastBuildInfo` **0** and an empty Error List. So a build
verdict on this family means "the project packages", not "the bindings are wired" — the same
shape of false green as `xae_build` on a PLC **library** project (FORK-NOTES, and
`tc-build-install` §4b). Any `hmi_project` verb must say so, or its callers will read a green
build as a working panel.

Related, and worth a verb of its own eventually: **symbols are resolved from an explicit
list**, `Server\TcHmiSrv\TcHmiSrv.Config.default.json`, not dynamically. A binding onto a
symbol nobody declared is null at run time and silent at build time. That is `hmi_symbol`
(section 4, item 2), and it is more valuable than its ranking suggests.

---

## 4. Where it plugs into this codebase

| concern | file today | what HMI needs |
|---|---|---|
| DTE + sysmanager acquisition and caching | `daemon/ComSession.cs` | cache `ITcHmiAutomation` beside `_sysManager`, same staleness and health-check rules, same `_stale` reset paths |
| per-family verbs | `daemon/Actions/*Actions.cs` | a new `HmiActions.cs`; `PlcPouActions.cs` is the closest model (create/modify inside a project) |
| tool schemas exposed to MCP | `toolSchemas.js` | `hmi_project`, `hmi_function`, `hmi_symbol`, `hmi_view`, `hmi_publish` |
| project selection | `ComSession.ListProjects` / `select_project` | HMI projects are a different node kind in the same solution; decide whether `select_project` spans both or gets a sibling |
| modal dialogs | `daemon/DialogWatcher.cs` | publish and item-creation can raise dialogs; `SuppressUi` may remove the need, but the watcher stays the safety net |

Suggested verbs, most valuable first — the ranking is by what actually cost time in the field,
not by API surface:

1. **`hmi_publish`** — `Publish(profile, …)` plus `GetPublishResult()`, and a verdict that is
   *not* just "the task returned true" (see section 6).
2. **`hmi_symbol`** — `MapSymbol` / mapped-symbol enumeration. In-process with the PLC side,
   the schema `$ref` can be derived from the PLC symbol table instead of guessed.
3. **`hmi_function`** — create and rename, keeping the six identity points and
   `dependencyFiles[].type` consistent.
4. **`hmi_project`** — `AddView` / `AddUserControl` / `AddContent`, `ChangeConfig`,
   `GetProjectInformation`, `Build` / `Clean`.
5. **`hmi_view`** — `GetControlInstance` / `GetControlAttributeInstance` for placing widgets
   and bindings instead of editing HTML as text.

---

## 5. Why the file system alone is not enough

An HMI project is an MSBuild project with explicit `<Content Include>` items. The publish task
receives `ProjectFiles="@(Folder);@(Content)"` and the build log enumerates exactly those
items — there is no directory scan anywhere in the pipeline.

A file dropped on disk and not declared in the `.hmiproj` therefore **does not exist** for
build or publish: it is not copied to `bin\`, it is not uploaded, and **nothing reports it**.
This is why an automation verb has to go through `AddContent` / `AddView` rather than writing
files: the second half of the job is the project entry, and for UserControls and Functions
there is a third half in `tchmiconfig.json`.

Editing files that are *already declared* — views, `Server\**\*.Config.*.json`,
`Properties\tchmipublish.config.json` — is safe from outside the IDE and needs no automation.

---

## 6. Field-measured traps any implementation must respect

These were paid for on 2026-09-04 while publishing two HMIs by hand. Each one is a silent
failure: the operation reports success and the result is wrong.

1. **`serverExtensions` in the publish profile must be `[]`.** With the five extensions listed
   and `publishServerExtension: true` — which is what the IDE's publish dialog writes — the
   publish succeeds, uploads the project, and **does not push the server extension
   configuration**. The ADS runtimes block stays at the server's own default. With `[]` the
   same publish writes runtimes and symbols. Measured on the server storage: `PROJECTNAME`
   written at 18:33, ADS rows still stamped 18:23 (instance creation time); after the fix, ADS
   rows land one second after `PROJECTNAME`, which is also what a known-good project shows.

2. **A publish verdict needs verification, not a return code.** The check that catches case 1
   is, against
   `C:\ProgramData\Beckhoff\TF2000 TwinCAT 3 HMI Server\service\<instance>\storage.db`:

   ```sql
   SELECT d.name, v.path, v.value, v.updated
   FROM value v JOIN domain d ON d.id = v.domainid
   WHERE v.path LIKE 'RUNTIMES::%::NETID' OR v.path = 'PROJECTNAME'
   ```

   If the ADS rows are older than `PROJECTNAME`, the configuration was not pushed. Whether
   `GetPublishResult()` / `ITcHmiPublishResult` already says this is **the first thing to test**
   when `hmi_publish` is written — if it does, prefer it; if it does not, the verb should run
   the check itself rather than report a false success.

3. **`Server\ADS\ADS.Config.remote.json` ships from the template as `PLC1 @ 127.0.0.1.1.1`.**
   The runtime name is the second segment of every symbol path, so a wrong one kills every
   symbol at once and the publish says nothing. `.default.json` is the engineering server's
   copy and needs the same treatment.

4. **One server instance hosts one project.** Publishing a second project to the same
   `TcHmiSrv` instance replaces the first. New instances are cheap and need neither
   administrator rights nor a service restart:

   ```
   TcHmiSrv.exe --serviceAddInstance=<name> --endpoint=http://127.0.0.1:<p1> \
       --endpoint=https://127.0.0.1:<p2> --initializeAdminPassword=<pwd> \
       --serviceDir="C:\ProgramData\Beckhoff\TF2000 TwinCAT 3 HMI Server"
   ```

   `HTTP 401` from the new endpoint means healthy (auth required); `460` means the license is
   missing on the **local** TwinCAT, which is where a Windows service asks — not on the runtime
   where the PLC runs.

5. **Symbol mappings live in `Server\TcHmiSrv\TcHmiSrv.Config.default.json`**, one entry per
   symbol, of the shape

   ```json
   "ADS.<runtime>.MAIN._TestCounter": {
       "ACCESS": 3, "DOMAIN": "ADS", "DYNAMIC": true,
       "MAPPING": "<runtime>::MAIN::_TestCounter",
       "SCHEMA": { "$ref": "tchmi:general#/definitions/DINT" },
       "USEMAPPING": true }
   ```

   Base types resolve against `tchmi.general.Schema.json` (86 definitions; `BOOL`, `DINT`,
   `INT`, `UDINT`, `LREAL` all present). PLC-specific types use
   `tchmi:server#/definitions/ADS-<runtime>.<type>`. Getting this `$ref` right by hand is
   guesswork; deriving it from the PLC symbol table is the point of doing it in-process.

---

## 7. Open questions, in the order they should be answered

1. **Does `ITcHmiAutomation` work with no solution open, or with the IDE started headless?**
   Everything measured so far had a solution loaded. The answer decides whether `hmi_*` verbs
   can create a project from nothing (`CreateHmiProject`) the way `xae open_solution` does.
2. **What does `SuppressUi(true)` actually suppress, and is it sticky?** It has to be restored,
   and a crash between set and restore leaves the user's IDE in a modified state.
3. **Does `ITcHmiPublishResult` carry a real verdict** — per-extension, or just a boolean? See
   trap 2.
4. **Is `ITcHmiPublishCallback` mandatory or nullable?** The daemon is single-STA; a callback
   invoked from another thread needs the same message-filter discipline as everything else.
5. **How to enumerate an existing project's views/controls.** `GetControlInstance(node, ctx)`
   implies a node model that reflection does not spell out; `GetHmiFile(ProjectItem)` is
   probably the way in.

Not a question for now: **headless publish outside the IDE**. `msbuild <proj>.hmiproj
/t:Publish` builds fine and then dies in the publish task with
`FileNotFoundException: Microsoft.VisualStudio.Package.LanguageService.15.0`, an assembly that
lives in `Common7\IDE`. The task expects the VS shell. A single dll copied next to
`TcHmiMSBuild.Publish.dll` would probably resolve it — MSBuild probes the task assembly's own
folder, which is how that folder is already populated with `EnvDTE.dll` and twenty VS interop
assemblies — but it modifies a vendor install directory and CI publish is not a requirement
yet.

---

## 8. Reproducing the probe

```powershell
# Windows PowerShell 5.1, not pwsh 7: GetActiveObject is not in .NET Core.
$dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.17.0')
$hmi = $dte.GetObject('Beckhoff.TcHmi.1.12')
$hmi.Version; $hmi.GetProcessId(); $hmi.GetHmiProjects().Count

# The interface list, without a solution:
$asm = [System.Reflection.Assembly]::LoadFrom(
  "C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\MSBuild\TcHmiAutomation.dll")
$asm.GetTypes() | Where-Object IsPublic | Select-Object FullName
```

Read-only calls only: `Version`, `GetProcessId`, `GetHmiProjects`, `GetProjectInformation`.
`SuppressUi`, `CloseSolution`, `Rename`, `Publish` and every `Add*` / `Change*` mutate the
user's IDE or project.
