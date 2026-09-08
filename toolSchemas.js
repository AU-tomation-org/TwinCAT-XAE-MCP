"use strict";
// toolSchemas.js — SINGLE SOURCE OF TRUTH for every MCP tool's wire surface
// (name -> { description, inputSchema }). index.js registers tools by looking
// them up here and attaching the per-tool handler; it no longer carries inline
// zod schema literals.
//
// WHY THIS FILE EXISTS
//   Previously a tool's input schema lived inline in index.js's registerTool({...})
//   call, and the handler ALSO hand-listed each field when building the daemon
//   payload. Adding one pass-through param (e.g. search's `refresh`) meant editing
//   index.js in two places. Centralizing the schema here removes the schema half of
//   that duplication: a pass-through param is added by editing ONLY this file (a
//   param that needs a genuine input->payload transform still touches the handler,
//   and any new daemon BEHAVIOR is real C# — both are out of scope of the wart).
//
// SCHEMA-PRESERVING GUARANTEE
//   These are the exact same zod raw-shape objects and description strings that
//   used to sit inline in index.js, moved verbatim. The MCP SDK derives each tool's
//   JSON-Schema `inputSchema` from these zod shapes, so the wire-visible tools/list
//   (names, descriptions, inputSchemas) is byte-for-byte identical before/after.
//   Do NOT "tidy" a schema here without re-running the tools/list diff snapshot.
//   Since SDK v2 the raw shapes are wrapped in z.object() ONCE at the bottom of
//   this file (v2 deprecates the raw-shape registerTool overload); the wrap adds
//   nothing to the wire — z.object(shape) is exactly what v1 applied internally.
//   One SDK-level change rides the v2 upgrade regardless of the wrap: each
//   emitted inputSchema's $schema dialect is now JSON Schema draft 2020-12
//   (SDK v1 emitted draft-07). Diffed against live SDK 1.29.0 output: that
//   $schema URI is the ONLY difference — properties, enums, defaults, required
//   lists, descriptions and tool order are byte-identical. Keep authoring
//   entries as raw shapes.
//
// The confirmation tokens and the XAE action map live here too, because the schema
// descriptions interpolate the tokens; index.js re-exports them so its handlers
// keep referencing the same single definitions.

const z = require("zod/v4");

// --- Confirmation tokens (referenced by both schema descriptions and handlers) ---
const ACTIVATE_CONFIRMATION = "ALLOW_TWINCAT_ACTIVATE";
const RESTART_CONFIRMATION = "ALLOW_TWINCAT_RESTART";
const XAE_COMMAND_CONFIRMATION = "ALLOW_XAE_COMMAND_EXEC";
const PLC_LOGOUT_CONFIRMATION = "ALLOW_PLC_LOGOUT";
const DELETE_CONFIRMATION = "ALLOW_TWINCAT_DELETE";
const PLC_DOWNLOAD_CONFIRMATION = "ALLOW_PLC_DOWNLOAD";
const PLC_LIBRARY_REPO_CONFIRMATION = "ALLOW_PLC_LIBRARY_REPO";
const ROUTE_WRITE_CONFIRMATION = "ALLOW_TWINCAT_ROUTE_WRITE";
const MODULE_CONTEXT_CONFIRMATION = "ALLOW_TWINCAT_MODULE_CONTEXT";
const CPP_PUBLISH_CONFIRMATION = "ALLOW_CPP_PUBLISH";
const MEASUREMENT_RECORD_CONFIRMATION = "ALLOW_MEASUREMENT_RECORD";
const LICENSE_ACTIVATE_CONFIRMATION = "ALLOW_LICENSE_ACTIVATE";
const IDE_SHUTDOWN_CONFIRMATION = "ALLOW_XAE_SHUTDOWN";
const HMI_PUBLISH_CONFIRMATION = "ALLOW_HMI_PUBLISH";
const PLC_TESTS_CONFIRMATION = "ALLOW_PLC_TESTS";

const CONFIRMATIONS = {
  ACTIVATE_CONFIRMATION,
  RESTART_CONFIRMATION,
  XAE_COMMAND_CONFIRMATION,
  PLC_LOGOUT_CONFIRMATION,
  DELETE_CONFIRMATION,
  PLC_DOWNLOAD_CONFIRMATION,
  PLC_LIBRARY_REPO_CONFIRMATION,
  ROUTE_WRITE_CONFIRMATION,
  MODULE_CONTEXT_CONFIRMATION,
  CPP_PUBLISH_CONFIRMATION,
  MEASUREMENT_RECORD_CONFIRMATION,
  LICENSE_ACTIVATE_CONFIRMATION,
  IDE_SHUTDOWN_CONFIRMATION,
  HMI_PUBLISH_CONFIRMATION,
  PLC_TESTS_CONFIRMATION,
};

// XAE single-tool action name -> daemon action name (used by the xae handler).
const XAE_ACTIONS = {
  status: "xae_status",
  open_solution: "xae_open_solution",
  save_all: "xae_save_all",
  active_document: "xae_get_active_document",
  selected_items: "xae_get_selected_items",
  error_list: "xae_get_error_list",
  clear_error_list: "xae_clear_error_list",
  list_commands: "xae_list_commands",
  dialog_probe: "dialog_probe",
  dialog_resolve: "dialog_resolve",
  shutdown_ide: "xae_shutdown_ide",
  list_instances: "xae_list_instances",
  attach: "xae_attach",
  list_projects: "xae_list_projects",
  select_project: "xae_select_project",
  list_configurations: "xae_list_configurations",
  set_configuration: "xae_set_configuration",
  output: "xae_get_output",
};

// Tools that reach a TwinCAT project's system manager, and therefore take
// `tsProject` to name WHICH project of the solution to work on. The parameter is
// added to each schema below in one pass, and index.js forwards it for every tool
// without each handler having to thread it through (see callScope there).
const PROJECT_SCOPED_TOOLS = [
  "xae", "xae_build", "tc_tree", "tc_ethercat", "tc_link", "tc_system", "tc_mapping",
  "nc", "tc_task", "plc_download", "plc_project", "plc_pou", "plc_library",
  "tc_route", "tc_settings", "tc_fieldbus", "tc_module", "tc_cpp", "tc_measurement",
  "tc_license", "tc_variant", "twincat_activate_configuration", "twincat_restart_runtime",
  "plc_tests",
];

// --- The tool schemas, keyed by tool name. Each entry is the EXACT config object
// (description + zod inputSchema raw shape) that registerTool consumes. ----------
const toolSchemas = {
  xae: {
    description: "XAE shell: status, open_solution (solutionPath; closeExisting:true reopens, discardChanges:true closes the current solution WITHOUT saving before reopening), save_all, active_document, selected_items, error_list (default 50, in Error List order; pass limit to widen, severityFilter:'errors'|'warnings' to filter before the cap — count still reports the true matching total), clear_error_list, list_commands (filter regex, limit), dialog_probe (read-only: is a modal dialog blocking XAE right now? returns its title/text/buttons; never clicks anything), dialog_resolve (button, remember) — click a chosen button on the live modal dialog and optionally remember it in the allowlist; pair with dialog_probe. Destructive prompts (activate/restart/download/safety) are refused for auto-remember (the click still happens once). shutdown_ide (save? default true, confirm=\"ALLOW_XAE_SHUTDOWN\") — close the IDE this session drives: settles dirty documents (saved, or discarded with save:false), closes the solution, then Quit(). Attaches only to an already-running IDE, never starts one. Killing the daemon without this leaves devenv orphaned, holding the solution open. list_instances \u2014 every running IDE for this progId with pid, the solution it has open, and which one this session is on; read-only, attaches to nothing and starts nothing. attach (pid | solutionPath) \u2014 bind this session to ONE named instance; a miss is an error listing what IS running, never a fallback to another IDE. The binding sticks for later calls, including tools that expose no mode of their own. Use list_instances then attach instead of relying on mode:\"active\", which means \"whichever instance the ROT happens to list first with a solution open\". list_projects — the TwinCAT projects (.tsproj) of the open solution with name, path, TargetNetId and PLC-project count, and which one is bound; read-only. select_project (name | path) — bind the session to ONE of them, the same contract as attach one level down: a miss lists what IS there, and the binding sticks for later calls. It matters because a solution holds one system manager PER project (tree, target NetId, boot flags, activation): with several present and none chosen, reads answer from the first in solution order and are flagged tsProjectAmbiguous, while the actions that change the target (activate, restart, boot flags, download, set_netid) refuse until one is named. Any tool may also name it per call with tsProject. list_configurations — the solution configurations and which is active; set_configuration (name, platform?) activates one, e.g. \"Release|TwinCAT OS (x64)\" — xae_build builds the ACTIVE configuration, so a solution left on another platform yields a verdict that differs from CI's for no visible reason. output (pane? default \"Build\", tail? default 200) — the last N lines of an Output window pane, and the pane names. This is where a build failure explains itself: a solution build that breaks inside one PLC project can leave a SINGLE Error List row (\"'TwinCAT XAE': Project 'X' build for platform 'Y' failed.\") with no file, no code and no compiler row, and the reason is only in this window. A pane that is not there is an error naming the ones that are.",
    inputSchema: {
      action: z.enum(Object.keys(XAE_ACTIONS)),
      solutionPath: z.string().optional(),
      closeExisting: z.boolean().optional(),
      discardChanges: z.boolean().optional(),
      filter: z.string().optional(),
      limit: z.number().int().positive().max(5000).optional(),
      severityFilter: z.enum(["all", "errors", "warnings"]).optional().describe("error_list: filter by severity before the cap (errorLevel values are vsBuildErrorLevelHigh=error / Medium=warning / Low=message). TwinCAT PLC rows are the exception: the PLC compiler reports errors, warnings and info ALL as Medium, so severity cannot be told apart there -- those rows are kept under any filter and flagged severityUndecidable, with severityUndecidableCount on the response; read the descriptions to tell them apart. Default all"),
      button: z.string().optional(),
      remember: z.boolean().optional(),
      save: z.boolean().optional().describe("shutdown_ide: save dirty documents before closing (default true); false discards them"),
      pid: z.number().int().positive().optional().describe("attach: process id of the IDE to bind to, from list_instances"),
      name: z.string().optional().describe("select_project: project name (or the .tsproj filename without extension), from list_projects. set_configuration: configuration name, e.g. \"Release\" or \"Release|TwinCAT OS (x64)\""),
      path: z.string().optional().describe("select_project: full path of the .tsproj to bind to, from list_projects"),
      platform: z.string().optional().describe("set_configuration: platform of the configuration to activate, e.g. \"TwinCAT OS (x64)\"; may instead be given inside name after a |"),
      attachPid: z.number().int().positive().optional().describe("any action: use the IDE with this pid instead of the mode heuristic; like attach, the session stays bound to it afterwards"),
      attachSolution: z.string().optional().describe("any action: use the IDE that has this solution open; like attach, the session stays bound to it afterwards"),
      forceNew: z.boolean().optional().describe("any action: start a brand new IDE for this call, whatever is already running (mode:\"create\" alone reuses the one this session started)"),
      confirm: z.string().optional().describe("shutdown_ide: must equal ALLOW_XAE_SHUTDOWN to close the IDE"),
      pane: z.string().optional().describe("output: which Output window pane, default \"Build\". Matched exactly first, then case-insensitively as a substring, because pane names are localized"),
      tail: z.number().int().positive().max(20000).optional().describe("output: how many trailing lines to return, default 200"),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional().describe("DTE attach mode; default active (open_solution: activeOrCreate)"),
    },
  },

  xae_build: {
    description: "Clean/Build/Rebuild the active solution configuration; waits for completion by default. The response reports the configuration it built, since that is what decides whether the verdict matches CI's (see xae list_configurations / set_configuration). project (name, UniqueName or .tsproj path) builds ONE project of the solution instead of all of it — build only: EnvDTE has no per-project clean or rebuild. configuration overrides the active one for that single-project build.",
    inputSchema: {
      action: z.enum(["clean", "build", "rebuild"]),
      project: z.string().optional().describe("build one project of the solution instead of the whole solution; name, UniqueName or .tsproj path (see xae list_projects)"),
      configuration: z.string().optional().describe("with project: the solution configuration to build it in, e.g. \"Release|TwinCAT OS (x64)\"; default is the active one"),
      waitForFinish: z.boolean().default(true),
      timeoutMs: z.number().int().positive().max(3600000).default(1800000),
    },
  },

  xae_command: {
    description: `Execute a raw XAE/DTE command by name (e.g. View.SolutionExplorer). Guarded: confirm="${XAE_COMMAND_CONFIRMATION}".`,
    inputSchema: {
      confirm: z.string(),
      commandName: z.string(),
      args: z.string().optional(),
    },
  },

  tc_tree: {
    description: "TwinCAT System Manager tree items; paths use ^ separators (e.g. TIPC^MyPlc, TIID^Device 2 (EtherCAT)^Box 1). BATCH-FIRST: for more than one item use the matching *_batch action — N ops in ONE DTE attach, continue-on-error roll-up {count,succeeded,failed,results:[{...,ok,error?}]}, instead of an attach per call. Actions, grouped single / batch: READ identity — get / get_batch (paths:[...]); TEST existence — exists / exists_batch (paths:[...]); READ xml — get_xml (ProduceXml raw XML; summary:true for a compact identity + slot-module list); WRITE params — set_xml / set_xml_batch (items:[{path,xml}]) (ConsumeXml; compact unless returnXml:true); RENAME — rename / rename_batch (renames:[{name|path,newName}]) (keeps IO links intact); CREATE — create / create_batch (creates:[{parent,name,subType,before?,createInfo?}]); create VALIDATES the child and errors clearly on a malformed/ghost result instead of silently succeeding — adding an EtherCAT box needs a proper ESI-based createInfo (bare subType 9099 with no createInfo produces a blank-named ghost), recorded per-entry as ok:false; to ADD EtherCAT terminals/boxes from the ESI prefer the dedicated tc_ethercat tool; DELETE — delete / delete_batch (deletes:[{parent,name}], GUARDED: dryRun:true previews which children exist, confirm=\"ALLOW_TWINCAT_DELETE\" to actually delete). Mutating *_batch verbs accept save:true to save once after the batch. No batch form: children (lists child items, incl. CPX-AP/Festo sub-modules), import (.xti under path), export (name to file), focus (Solution Explorer).",
    inputSchema: {
      action: z.enum(["get", "children", "exists", "exists_batch", "get_batch", "get_xml", "set_xml", "set_xml_batch", "rename", "rename_batch", "create", "create_batch", "delete", "delete_batch", "import", "export", "focus"]),
      path: z.string().optional(),
      paths: z.array(z.string()).optional(),
      xml: z.string().optional(),
      returnXml: z.boolean().optional(),
      summary: z.boolean().optional(),
      name: z.string().optional(),
      renames: z.array(z.object({ name: z.string().optional(), path: z.string().optional(), newName: z.string() })).optional(),
      items: z.array(z.object({ path: z.string(), xml: z.string() })).optional(),
      creates: z.array(z.object({ parent: z.string(), name: z.string(), subType: z.number().int(), before: z.string().optional(), createInfo: z.string().optional() })).optional(),
      deletes: z.array(z.object({ parent: z.string(), name: z.string() })).optional(),
      subType: z.number().int().optional(),
      before: z.string().optional().describe("insert before this sibling"),
      createInfo: z.string().optional(),
      file: z.string().optional(),
      reconnect: z.boolean().default(true),
      newName: z.string().optional(),
      confirm: z.string().optional(),
      dryRun: z.boolean().optional(),
      save: z.boolean().optional(),
    },
  },

  tc_ethercat: {
    description:
      "Create EtherCAT IO boxes (terminals/couplers) NATIVELY. Each module is added via the GUI's own \"Add Box\" route — ITcSmTreeItem.CreateChild(name, 9099, before, \"<productString>\") — so TwinCAT expands the box FROM ITS OWN ESI: a fully populated, non-hollow box (correct identity, SyncManagers, FMMUs, full <EtherCAT> mailbox/CoE/FoE element, complete PDOs+entries) for ANY class — digital, analog (in AND out), IO-Link, mailbox, DC, couplers. createInfo is the PLAIN PRODUCT STRING (the bare type = latest revision, or a revision-pinned form), NOT identity XML/numbers. " +
      "ONE unified shape — a single box and a whole multi-coupler design are the SAME operation: racks:[{ parent:\"<EtherCAT coupler/master tree path>\", modules:[{ type:\"EL1008\", name?:\"Term 7 (EL1008)\", revision?, before?:\"<sibling name>\" }] }]. A single box is just racks:[{parent, modules:[{type}]}]. Modules are created in array order (left-to-right terminal order); `before` inserts ahead of a named sibling; name omitted defaults to type. " +
      "Revision pinning: pass `revision` as the full Beckhoff product string suffix \"<pppp>-<rrrr>\" (decimal), e.g. type:\"EL1008\" revision:\"0000-0017\" → RevisionNo #x00110000; you may also pass the whole pinned string in `revision` (e.g. \"EL1008-0000-0017\"). Bare type = latest revision. " +
      "NO fallback — if CreateChild produces a ghost/unknown type, that ONE module is a clean ok:false (any stray child is cleaned up) and the rest continue. Optional save:true saves the solution once after everything. Returns a flat roll-up {count, succeeded, failed, results:[{parent, type, name, ok, error?}]}.",
    inputSchema: {
      racks: z.array(z.object({
        parent: z.string(),
        modules: z.array(z.object({
          type: z.string(),
          name: z.string().optional(),
          revision: z.string().optional(),
          before: z.string().optional().describe("insert before this sibling"),
        })),
      })),
      save: z.boolean().optional(),
    },
  },

  tc_link: {
    description: "Variable links (producer↔consumer); dot-form PLC subfields auto-resolve to XAE ^ subitem form. BATCH-FIRST: for more than one link use link_batch/unlink_batch — N ops in ONE DTE attach, failures-only roll-up by default ({count,succeeded,failed}; details:true for every row incl. resolved paths), instead of an attach per link. Actions, grouped single / batch: LINK — link (a=source, b=destination) / link_batch (links:[{a,b}]); UNLINK — unlink (a, optional b; a alone removes all its links) / unlink_batch (links:[{a,b?}]); resolve (report valid path forms for a — compact {originalPath,resolvedPath,resolved} on success; verbose:true for the attempts[] permutation list); links (a=item path; reports that item's current variable links — closes the discover→act→verify loop). link/resolve drop resolution detail by default — verbose:true restores it. Mutating batch verbs accept save:true to save once after the batch.",
    inputSchema: {
      action: z.enum(["link", "unlink", "resolve", "link_batch", "unlink_batch", "links"]),
      a: z.string(),
      b: z.string().optional(),
      autoResolve: z.boolean().default(true),
      links: z.array(z.object({ a: z.string(), b: z.string().optional() })).optional(),
      save: z.boolean().optional(),
      verbose: z.boolean().optional().describe("link/resolve: return the full resolution detail (producerResolution/consumerResolution or attempts[]); default compact"),
      details: z.boolean().optional().describe("link_batch/unlink_batch: include ok:true rows (with resolved paths); default failures-only"),
    },
  },

  tc_system: {
    description: "System Manager: get_netid, set_netid (netId), errors (latest messages), rescan_plc (path, default TIPC), scan_io_boxes (path = IO device node).",
    inputSchema: {
      action: z.enum(["get_netid", "set_netid", "errors", "rescan_plc", "scan_io_boxes"]),
      netId: z.string().optional(),
      path: z.string().optional(),
    },
  },

  tc_mapping: {
    description:
      'Bulk variable-mapping (ALL links) ops on the loaded TwinCAT project via ITcSysManager2/3, whole-project (no tree path): ' +
      'produce (read-only) — ProduceMappingInfo serializes every current variable link/mapping to ONE XML blob (the IDE\'s "Export Mapping Information"); the blob is returned as raw XML. ' +
      'consume (xml) — ConsumeMappingInfo re-applies/merges a previously produced blob, ADDING links; MUTATES the offline config only (no runtime impact until a later twincat_activate_configuration); optional save:true saves the solution after. ' +
      'clear — ClearMappingInfo deletes ALL variable links project-wide; destructive, GUARDED: requires confirm="' + DELETE_CONFIRMATION + '" (reuses the existing delete token); optional save:true. ' +
      'These are PROJECT-WIDE config-tree ops, NOT runtime writes. SAFETY: the mapping blob spans the whole project and CAN include TwinSAFE I/O image links — produce/consume/clear may touch safety-related links; by policy nothing should write toward safety, so run produce FIRST as a backup and treat the blob as opaque (export -> store -> consume round-trip). The exact XML schema is undocumented; test-import hand-edited blobs in the IDE before relying on them.',
    inputSchema: {
      action: z.enum(["produce", "consume", "clear"]),
      xml: z.string().optional(),
      confirm: z.string().optional(),
      save: z.boolean().optional(),
    },
  },

  nc: {
    description: "NC motion tree: tasks (list under TINC), axes (path = task, default first task), axis (path = full axis path, returns info + children).",
    inputSchema: {
      action: z.enum(["tasks", "axes", "axis"]),
      path: z.string().optional(),
    },
  },

  tc_task: {
    description:
      "RT tasks under TIRT (+ RT-core settings under TIRS, + a PLC project's LinkedTask under TIPC). CONFIG-ONLY — no confirm token needed; runtime needs a later activate/restart; TISC (safety) untouched. " +
      "Tree paths use ^ separators (e.g. TIRT^PlcTask). Actions: " +
      "list (tasks under TIRT); get (path; summary:true -> identity + parsed TaskDef tags instead of full XML); " +
      "create (name; withImage default true = SubType 0 / false = SubType 1 no image; before?; cycleTimeUs?/priority? applied after create via ConsumeXml; save?); " +
      "set_params (path; cycleTimeUs [us, converted to 100ns ticks = us*10] / priority [0-255] / autoStart; OR xml = raw <TreeItem>..</TreeItem> escape hatch [mutually exclusive with the typed fields]; returnXml?; save?). CAVEAT: the TaskDef tag names for cycle/priority/autostart are UNCONFIRMED against the AI docs — prefer the xml escape hatch and verify with get(summary) before trusting the typed fields; " +
      "add_image_var (path = a with-image task's Inputs/Outputs node; varName; dataType e.g. BOOL/INT/DINT; startAddress? default -1 = append; save?); " +
      "get_rt_settings (TIRS; summary:true -> parsed MaxCPUs/Affinity/per-CPU LoadLimit/BaseTime/LatencyWarning); " +
      "set_rt_settings (maxCPUs / affinity [TwinCAT hex token e.g. #x0000000000000007] / cpus [{id,loadLimit?,baseTimeNs?,latencyWarningUs?}]; OR xml escape hatch; returnXml?; save?); " +
      "bind_cpu (path; affinity = a name [CPU1..CPU8, MaskSingle/Dual/Quad/Hexa/Oct/All, None] OR a raw #x.. token; returnXml?; save?); " +
      "get_linked_task (path? = PLC root under TIPC, default first child of TIPC); " +
      "set_linked_task (path? = PLC root; linkedTask = XAE tree path of the RT task, e.g. TIRT^PlcTask; save?).",
    inputSchema: {
      action: z.enum([
        "list", "get", "create", "set_params", "add_image_var",
        "get_rt_settings", "set_rt_settings", "bind_cpu",
        "get_linked_task", "set_linked_task",
      ]),
      path: z.string().optional(),
      name: z.string().optional(),
      withImage: z.boolean().optional(),
      before: z.string().optional().describe("insert before this sibling task"),
      cycleTimeUs: z.number().optional(),
      priority: z.number().int().min(0).max(255).optional(),
      autoStart: z.boolean().optional(),
      summary: z.boolean().optional(),
      xml: z.string().optional(),
      returnXml: z.boolean().optional(),
      varName: z.string().optional(),
      dataType: z.string().optional(),
      startAddress: z.number().int().optional(),
      maxCPUs: z.number().int().optional(),
      affinity: z.string().optional(),
      cpus: z.array(z.object({
        id: z.number().int(),
        loadLimit: z.number().int().optional(),
        baseTimeNs: z.number().int().optional(),
        latencyWarningUs: z.number().int().optional(),
      })).optional(),
      linkedTask: z.string().optional(),
      save: z.boolean().optional(),
    },
  },

  plc_download: {
    description: 'Deploy the active PLC project. Guarded: confirm="ALLOW_PLC_DOWNLOAD" (deploys a boot project to the live target). method "bootproject" (default): headless via ITcPlcProject — writes the boot project to the target boot dir; twincat_restart_runtime loads and runs it. method "command": legacy DTE command route (needs a shell with window automation). autoLogout (default true): if the IDE is logged into the PLC, log out first via UI Automation so any source edits deferred by the online lock are applied before deploy. Never logs back in.',
    inputSchema: {
      confirm: z.string().optional(),
      method: z.enum(["bootproject", "command"]).default("bootproject"),
      treePath: z.string().optional().describe("PLC root node, default first project under TIPC"),
      autostart: z.boolean().default(true),
      commandName: z.string().optional(),
      autoLogout: z.boolean().default(true),
    },
  },

  plc_session: {
    description: `PLC online-session control via UI Automation (the DTE Login/Logout commands are unavailable on the 64-bit shell). action "status" (read-only): reports { loggedIn }. action "logout": logs the IDE out of the PLC — this also applies any source edits the online lock deferred ("loaded after logout"). Never logs back in. Guarded: logout needs confirm="${PLC_LOGOUT_CONFIRMATION}".`,
    inputSchema: {
      action: z.enum(["status", "logout"]),
      confirm: z.string().optional(),
    },
  },

  plc_project: {
    description:
      'PLC (IEC) project lifecycle on the open solution. Tree paths use ^ separators; the PLC ROOT node is TIPC^<name>, the nested project INSTANCE node is TIPC^<name>^<name> Project. NODE MATTERS: ITcPlcProject (boot flags / generate_boot) is on the ROOT; ITcPlcIECProject* (plcopen_export/import / save_as_library) is on the INSTANCE node. ' +
      'Actions: create_from_template (name, template, before?, save?) — new PLC project from a stock template; open (name, file=.plcproj/.tpzip, subType 0 copy/1 move/2 use-in-place, before?, save?) — import an existing project; info (treePath? default first under TIPC) — read identity (nestedProjectName/instanceName/childCount); set_boot_flags (treePath? = ROOT, autostart?, tmcFileCopy?) — config-only boot flags; ' +
      'plcopen_export (file, treePath? = INSTANCE, selection?) — write PLCopen XML; plcopen_import (file, treePath? = INSTANCE, options 0 NONE/1 RENAME/2 REPLACE/3 SKIP, selection?, folderStructure? default true, save?) — import PLCopen XML; save_as_library (file, treePath? = INSTANCE, install? default false — install:true mutates the local library repository, overwrite? default false) — save project as .library. SaveAsLibrary never overwrites: onto an existing target it fails, so re-exporting needs overwrite:true, which deletes the target first and reports replacedExisting. The response also carries fileWritten, read back from disk, because a call that returns without throwing is not by itself proof a file arrived. ' +
      'GUARDED (live runtime/target writes), require confirm="' + PLC_DOWNLOAD_CONFIRMATION + '" and default to no-op: generate_boot_project (treePath? = ROOT, autostart? default true) — generates the boot project to the target boot dir (restart runtime to load); online (command login/logout/start/stop/reset_cold/reset_origin, treePath? — changes live online/runtime state; the ConsumeXml envelope is UNVERIFIED on this build and surfaces GetLastXmlError verbatim, reset_* need a prior login, build>=4010). Safety projects are deliberately out of scope.',
    inputSchema: {
      action: z.enum([
        "create_from_template", "open", "info", "set_boot_flags",
        "generate_boot_project", "online",
        "plcopen_export", "plcopen_import", "save_as_library",
      ]),
      name: z.string().optional(),
      template: z.enum(["Standard PLC Template", "Empty PLC Template"]).optional(),
      file: z.string().optional(),
      treePath: z.string().optional(),
      subType: z.number().int().min(0).max(2).optional(),
      before: z.string().optional().describe("insert before this sibling PLC project"),
      autostart: z.boolean().optional(),
      tmcFileCopy: z.boolean().optional(),
      command: z.enum(["login", "logout", "start", "stop", "reset_cold", "reset_origin"]).optional(),
      options: z.number().int().min(0).max(3).optional(),
      selection: z.string().optional(),
      folderStructure: z.boolean().optional(),
      install: z.boolean().optional(),
      overwrite: z.boolean().optional().describe("save_as_library: delete an existing target .library before exporting; without it an existing file is refused by name"),
      save: z.boolean().optional(),
      confirm: z.string().optional(),
    },
  },

  plc_pou: {
    description:
      "PLC object authoring + code edit on the open solution (OFFLINE engineering only — edits land in-memory and reach a runtime only via a later guarded plc_download + twincat_restart_runtime). Tree paths use ^ separators; safety (TISC-rooted) paths are rejected by policy. " +
      "CREATE — create / create_batch (parent, name, subType, language?, returnType?, accessor?, extends?, implements?, declText?, implSeed?, before?): CreateChild sub-types 602 Program, 603 Function (returnType required), 604 FunctionBlock, 605 Enum, 606 Struct, 607 Union, 608 Action, 609 Method, 610 interface Method, 611 Property (returnType required), 612 interface Property (returnType required), 613 property Get, 614 property Set, 615 GVL, 616 Transition, 618 Interface, 619 Visualization, 623 Alias, 629 ParameterList, 631 UML, 654 interface property Get, 655 interface property Set. language IECLANGUAGETYPES 0 NONE/1 ST/2 IL/3 SFC/4 FBD/5 CFC/6 LD (default 1). extends/implements for FB 604 / Program 602 derivation (618 uses extends as its base); declText seeds DUT/GVL decl. MEMBERS of a POU (608 Action, 609 Method, 616 Transition, 613/614 accessors) are created UNDER it — parent is the POU's ^ path, not the folder's; an INTERFACE takes 610/612 (+654/655), never 609/611. accessor (PUBLIC|PRIVATE|PROTECTED|INTERNAL) applies to 609/611/613/614; 613/614/654/655 may omit name (defaults Get/Set). implSeed is an initial implementation in TwinCAT wrapper XML (e.g. <ST><![CDATA[(* body *)]]></ST>) for 608/609/613/614/616 — normally omitted, use set_impl after create. For code POUs prefer set_decl after create. " +
      "FOLDERS — create_folder (parent, name, before?) creates a PLC folder (sub-type 601) under parent (a PLC subtree node, POUs/DUTs/GVLs container, or another folder); returns the same shape as create. create_folder_batch (creates:[{parent,name,before?}], save?) loops continue-on-error, returns {count,succeeded,failed,results} KEEPING success rows (each carries the created child identity) — list a parent-folder entry before its child (array order). create / create_batch already author INTO a folder when parent is the folder's path — no separate action needed. " +
      "TEMPLATE — import_template (parent, paths[]) imports POU template file(s) (CreateChild sub-type 58). " +
      "READ (cheap-first: outline for structure; get_decl/get_impl with grep{} or range{} to slice; full text only when the whole body is needed — a large full read returns a hint nudging the next call to slice) — get_decl / get_impl / get_document / get_graphical (path). get_decl/get_impl take an optional range {start,end} (1-based inclusive line slice, clamped) OR grep {pattern, context?} (regex over lines + context each side); mutually exclusive; default full text. Both report lineCount; get_impl also returns language (textual 1 ST/2 IL; graphical 3 SFC/4 FBD/5 CFC/6 LD -> lineCount:0 + {graphical:true, hint}). get_graphical (path) READ-ONLY inspects a graphical (LD/FBD/SFC/CFC) body: returns {language,languageName,itemType,source,readOnly,xml} where xml is the object's <Implementation> network XML (NWL 'BoxTree' for LD/FBD/IL, or the SFC/CFC archive), pulled live from the POU document (for an Action/Method/Transition it reads the PARENT POU's document, since get_document/GetDocumentXml only work on a top-level POU). Diagnostic only — graphical bodies are NOT text-editable; change them in the XAE GUI. Refuses textual languages (use get_impl). outline (path) returns structure WITHOUT full text: header + varBlocks + child code items. " +
      "WRITE — set_decl / set_decl_batch (path, declText); set_impl / set_impl_batch (path, exactly one of implText|implXml — implXml is TwinCAT object XML, round-trip only, for graphical languages); set_document (path, documentXml). " +
      "SURGICAL TEXT EDIT (read-modify-write, returns ONLY the changed region +/-2 ctx; target decl|impl, CRLF/LF preserved; refuses graphical impl): replace (find literal substring, replaceWith, expectCount? default 1 — fails without writing on 0 or count mismatch); replace_lines (start, end, text — 1-based inclusive span, OOB throws); insert (exactly one of at|after|before, text); insert_in_var_block (block e.g. VAR_INPUT, text, occurrence? — inserts before that block's END_VAR); append (text — default target impl). All surgical writes accept validate:true to run CheckAllObjects after (default off). " +
      "DISCOVER (cheap-first: find for path-by-name; search only for content patterns) — tree (plcPath?, path? subtree root, depth?, typeFilter?) does a read-only recursive Child() walk of the IEC project and returns {plcPath,projectPath,rootPath,count,tree:[{path,name,type,itemType,subType?,childCount,children?,truncated?}]} (type is a normalized label: Program/FB/Function/FunctionBlock/Struct/Enum/Union/Alias/GVL/Interface/Method/Property/Action/Transition/Visualization/ParameterList/UML/Folder/Project/Task/Unknown; depth 1 = direct children only; typeFilter is a comma list of type labels to KEEP, ancestors retained as scaffolding). find (plcPath?, path?, name? substring or /regex/, typeFilter?; at least one of name/typeFilter) returns a FLAT {plcPath,projectPath,count,matches:[{path,name,type,itemType,subType?,childCount}]} so a caller can resolve a ^ path from a name without the whole nested blob. " +
      "GREP — search (pattern [regex/.NET or substring], ignoreCase?, declOnly?|implOnly? [mutually exclusive], plcPath?, path? subtree root, maxResults? default 50/max 5000 — raise for exhaustive scans; truncated:true signals the cap was hit) is a project-wide find-in-code: walks every code object under the IEC project, greps DeclarationText + (ST-only) ImplementationText line-by-line, returns {pattern,plcPath,scanned,searched,count,truncated,matches:[{path,section:'decl'|'impl',line,text}]}; graphical bodies are scanned-but-not-searched. Read-only/offline. Decl/impl text is CACHED (warm repeat sub-100ms vs ~16s cold); the cache self-invalidates on edits through this tool, dirty-checks open IDE editors, and is backstopped by a file-save watcher — pass refresh:true to force a full live re-pull. " +
      "DELETE — delete (path OR parent+name) GUARDED offline delete of one PLC object via parent.DeleteChild; dryRun:true previews {wouldDelete,target}, confirm=\"ALLOW_TWINCAT_DELETE\" to actually delete; verifies the child exists first, refuses TISC. " +
      "LIFECYCLE (OFFLINE, unguarded, refuses TISC) — rename (path, newName = bare name) renames in place, returns {path,newName,newPath}. move (path, newParent, before?) reparents one object preserving decl/impl/document/sub-objects via export-import-delete in ONE attach (no native reparent exists); refuses no-op/into-self/into-own-descendant moves; returns {path,newParent,newPath,name,via}. " +
      "BUILD-CHECK — check_objects (plcPath?, default first PLC under TIPC) runs CheckAllObjects on the nested IEC project (no download). Mutating batch verbs (create_batch, set_decl_batch, set_impl_batch) accept save:true to save once after the batch.",
    inputSchema: {
      action: z.enum([
        "create", "create_batch", "create_folder", "create_folder_batch", "import_template",
        "get_decl", "get_impl", "get_document", "get_graphical", "outline",
        "set_decl", "set_decl_batch", "set_impl", "set_impl_batch", "set_document",
        "check_objects",
        "replace", "replace_lines", "insert", "insert_in_var_block", "append",
        "tree", "find", "search", "delete", "rename", "move",
      ]),
      parent: z.string().optional(),
      name: z.string().optional(),
      subType: z.number().int().optional(),
      language: z.number().int().min(0).max(6).optional(),
      returnType: z.string().optional(),
      extends: z.string().optional(),
      implements: z.string().optional(),
      declText: z.string().optional(),
      accessor: z.string().optional().describe("create: PUBLIC|PRIVATE|PROTECTED|INTERNAL; 609 Method, 611 Property, 613 Get, 614 Set"),
      implSeed: z.string().optional().describe("create: initial implementation in TwinCAT wrapper XML, e.g. <ST><![CDATA[(* body *)]]></ST>; 608/609/613/614/616 only"),
      before: z.union([z.string(), z.number().int()]).optional().describe("create: sibling name to insert before (string). insert: 1-based line to insert before (int, alias of at)"),
      paths: z.array(z.string()).optional(),
      path: z.string().optional(),
      implText: z.string().optional(),
      implXml: z.string().optional(),
      documentXml: z.string().optional(),
      plcPath: z.string().optional(),
      creates: z.array(z.object({
        parent: z.string(),
        name: z.string(),
        subType: z.number().int().optional().describe("required for create_batch (POU/DUT/GVL sub-type); omit for create_folder_batch (always 601 PLC folder)"),
        language: z.number().int().min(0).max(6).optional(),
        returnType: z.string().optional(),
        extends: z.string().optional(),
        implements: z.string().optional(),
        declText: z.string().optional(),
        accessor: z.string().optional(),
        implSeed: z.string().optional(),
        before: z.string().optional(),
      })).optional(),
      items: z.array(z.object({
        path: z.string(),
        declText: z.string().optional(),
        implText: z.string().optional(),
        implXml: z.string().optional(),
      })).optional(),
      save: z.boolean().optional(),
      details: z.boolean().optional().describe("set_decl_batch/set_impl_batch: include ok:true rows; default failures-only ({count,succeeded,failed} always reported). create_batch/create_folder_batch always keep success rows (they carry child identity), so details is a no-op there."),
      range: z.object({ start: z.number().int(), end: z.number().int() }).optional().describe("get_decl/get_impl: 1-based inclusive line slice; mutually exclusive with grep"),
      grep: z.object({ pattern: z.string(), context: z.number().int().optional() }).optional().describe("get_decl/get_impl: regex over lines + context each side (default 2); mutually exclusive with range"),
      target: z.enum(["decl", "impl"]).optional().describe("surgical edit target; default decl (append defaults impl)"),
      find: z.string().optional().describe("replace: exact literal substring (NOT regex)"),
      replaceWith: z.string().optional(),
      expectCount: z.number().int().optional().describe("replace: required occurrence count (default 1)"),
      start: z.number().int().optional().describe("replace_lines: 1-based inclusive first line"),
      end: z.number().int().optional().describe("replace_lines: 1-based inclusive last line"),
      at: z.number().int().optional().describe("insert before this 1-based line (lineCount+1 appends)"),
      after: z.number().int().optional().describe("insert after this 1-based line"),
      block: z.string().optional().describe("insert_in_var_block: VAR-block keyword e.g. VAR_INPUT"),
      occurrence: z.number().int().optional().describe("insert_in_var_block: which matching block (1-based, default 1)"),
      text: z.string().optional().describe("replacement / insert / append text"),
      validate: z.boolean().optional().describe("surgical writes: run CheckAllObjects after the edit (default off)"),
      depth: z.number().int().positive().optional().describe("tree: max recursion depth (1 = direct children only); default unlimited"),
      typeFilter: z.string().optional().describe("tree/find: comma list of normalized type labels to keep/match (case-insensitive), e.g. 'FB,Method,Struct'"),
      dryRun: z.boolean().optional().describe("delete: preview the target without deleting"),
      confirm: z.string().optional().describe("delete: must equal ALLOW_TWINCAT_DELETE to actually delete"),
      newName: z.string().optional().describe("rename: new bare object name (not a path)"),
      newParent: z.string().optional().describe("move: ^-separated destination parent tree path (TISC refused)"),
      pattern: z.string().optional().describe("search: regex (.NET syntax) or plain substring, matched per-line against each object's decl/impl text"),
      ignoreCase: z.boolean().optional().describe("search: case-insensitive match (default false)"),
      declOnly: z.boolean().optional().describe("search: search only DeclarationText; mutually exclusive with implOnly"),
      implOnly: z.boolean().optional().describe("search: search only ImplementationText (ST-only); mutually exclusive with declOnly"),
      maxResults: z.number().int().positive().max(5000).optional().default(50).describe("search: cap on returned match rows (default 50, max 5000; raise for exhaustive scans); stops the walk and sets truncated:true when hit"),
      refresh: z.boolean().optional().describe("search: force a full live re-pull, bypassing the decl/impl text cache for the searched scope (default false). Open editors are always dirty-checked automatically; use this only as an escape hatch after structural ops or for paranoia."),
    },
  },

  plc_library: {
    description:
      'PLC library references / placeholders / repositories via ITcPlcLibraryManager on the References node (TIPC^<plc>^<plc> Project^References). referencesPath defaults to the first PLC under TIPC. ' +
      'READ (no side effects): list (References → name/kind library|placeholder/displayName/distributor/version), scan (ScanLibraries → installed libs name/version/distributor/displayName; pass filter to avoid the full catalogue dump), repos (Repositories → name/folder). ' +
      'WRITE — OFFLINE .plcproj edits, NO runtime impact (not confirm-gated): add_library (name, version?, company?), add_placeholder (name, defLib?/defVer?/defDist? — omit defLib for the name-only form), set_resolution (placeholder, lib, version?, dist?), freeze (name? — omit to freeze ALL), remove_reference (name = library or placeholder). Each accepts save:true to File.SaveAll after the edit. ' +
      'LANDMINE: a .plcproj library-reference edit (add/remove/repin a library or placeholder, set resolution) requires a full solution close+reopen in XAE before it takes effect; adding source files alone does not — the response surfaces this note. ' +
      'REPO ADMIN — GUARDED, mutates the machine-wide TwinCAT library store (no runtime change, but shared-machine state): install_library (repo, libPath, overwrite?), uninstall_library (repo, lib, version?, dist?), insert_repository (name, folder, index?), remove_repository (name), move_repository (name, index). These require confirm="' + PLC_LIBRARY_REPO_CONFIRMATION + '". Nothing here targets the safety system (References live only under TIPC).',
    inputSchema: {
      action: z.enum([
        "list", "scan", "repos",
        "add_library", "add_placeholder", "set_resolution", "freeze", "remove_reference",
        "install_library", "uninstall_library", "insert_repository", "remove_repository", "move_repository",
      ]),
      referencesPath: z.string().optional().describe("References node path; default = first PLC under TIPC"),
      filter: z.string().optional().describe("scan: case-insensitive substring on library name; omit for the full installed list"),
      name: z.string().optional(),
      version: z.string().optional(),
      company: z.string().optional(),
      defLib: z.string().optional(),
      defVer: z.string().optional(),
      defDist: z.string().optional(),
      placeholder: z.string().optional(),
      lib: z.string().optional(),
      dist: z.string().optional(),
      repo: z.string().optional(),
      libPath: z.string().optional(),
      overwrite: z.boolean().optional(),
      folder: z.string().optional(),
      index: z.number().int().optional(),
      save: z.boolean().optional(),
      confirm: z.string().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional().describe("DTE attach mode; default active"),
    },
  },

  tc_route: {
    description:
      'ADS routes via the System Manager TIRR (Routes) node, ConsumeXml/ProduceXml. READ (unguarded — a transient search trigger, never persists a route): ' +
      'list — existing static routes under RemoteConnections (best-effort name/netId/address); ' +
      'broadcast_search — LAN-wide UDP discovery (timeoutMs settle wait, default ~4000ms) → targets [{name,netId,ipAddr}]; ' +
      'search_host — direct by host (hostname or IP; needs TwinCAT 3.1 build>=4020.10, older builds return found:false) → {found, target:{name,netId,ipAddr,version,os}}. ' +
      'WRITE (GUARDED, require confirm="' + ROUTE_WRITE_CONFIRMATION + '", default NO-OP): ' +
      'add_route — credentialed route to a remote target (remoteName, remoteNetId, one of remoteIpAddr|remoteHostName; optional userName/password/noEncryption/localName); ' +
      'add_project_route — lighter project-local entry (name, netId, one of ipAddr|hostName). ' +
      'NOTE: route changes via TIRR ConsumeXml take effect in the engineering project; whether they propagate to the live target depends on the current target connection — this does NOT auto-activate. Nothing here targets the safety system (config/engineering-side only).',
    inputSchema: {
      action: z.enum(["list", "broadcast_search", "search_host", "add_route", "add_project_route"]),
      confirm: z.string().optional(),
      timeoutMs: z.number().int().positive().max(60000).optional(),
      host: z.string().optional(),
      remoteName: z.string().optional(),
      remoteNetId: z.string().optional(),
      remoteIpAddr: z.string().optional(),
      remoteHostName: z.string().optional(),
      userName: z.string().optional(),
      password: z.string().optional(),
      noEncryption: z.boolean().optional(),
      localName: z.string().optional(),
      name: z.string().optional(),
      netId: z.string().optional(),
      ipAddr: z.string().optional(),
      hostName: z.string().optional(),
    },
  },

  tc_settings: {
    description:
      'XAE engineering settings & packaging. OFFLINE/engineering-only: NONE of these write toward a runtime or change runtime state (their runtime effect, if any, lands only on a SEPARATE later activate/download), so none are confirm-gated. Tree paths use ^ separators; safety (TISC-rooted) paths are rejected by policy in set_disabled/set_independent_file/save_plc_archive. Actions: ' +
      'get_silent_mode / set_silent_mode (enabled) — TcAutomationSettings.SilentMode; suppresses AI message-box dialogs (TC3.1>=4020.0; older builds throw). A good companion to the dialog watchdog. ' +
      'get_target_platform / set_target_platform (platform = "TwinCAT RT (x86)" | "TwinCAT RT (x64)") — ITcSysManager7.ConfigurationManager.ActiveTargetPlatform; switching platform invalidates prior build output, so rebuild (xae_build) before activate/download. ' +
      'save_solution_archive (file = absolute .tszip) — ITcSysManager9.SaveAsArchive, whole solution; parent dir must exist (not created). ' +
      'save_plc_archive (file = absolute .tpzip, name? = PLC child under TIPC, default first child) — ExportChild of the PLC project. ' +
      'get_independent_file / set_independent_file (path, enabled) — ITcSmTreeItem6.SaveInOwnFile (store node settings in its own file vs inline in .tsproj). ' +
      'get_disabled (path) — reads ITcSmTreeItem.Disabled, returns {disabled:0|1|2, state:SMDS_NOT_DISABLED|SMDS_DISABLED|SMDS_PARENT_DISABLED}; SMDS_PARENT_DISABLED(2) is a derived read-only state. set_disabled (path, disabled) — sets 0/1 only (2 is never settable).',
    inputSchema: {
      action: z.enum([
        "get_silent_mode", "set_silent_mode",
        "get_target_platform", "set_target_platform",
        "save_solution_archive", "save_plc_archive",
        "get_independent_file", "set_independent_file",
        "get_disabled", "set_disabled",
      ]),
      enabled: z.boolean().optional(),
      disabled: z.boolean().optional(),
      platform: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
      name: z.string().optional(),
    },
  },

  tc_fieldbus: {
    description:
      "Create + configure NON-EtherCAT fieldbus masters/slaves/boxes (PROFINET / PROFIBUS / CANopen / DeviceNet / EAP net-vars) via ITcSmTreeItem CreateChild + ClaimResources + ConsumeXml. OFFLINE CONFIG ONLY — no confirm token needed; runtime needs a later activate/restart; TISC (safety) paths refused. For EtherCAT terminals/boxes use tc_ethercat instead. " +
      "BATCH-FIRST: for more than one device use create_batch (N ops in ONE DTE attach, continue-on-error roll-up {count,succeeded,failed,results:[{parent,name,ok,child?,claimed?,error?}]}). " +
      "SubType cheat-sheet — PROFINET ctrl 113/119/126/140, dev 115/118/142/143; PROFIBUS master 86 slave 97; CANopen master 87 slave 98; DeviceNet master 41/73/88 slave 62/74/99 monitor 59 box 5203; EAP device 112 publisher 9051 subscriber 9052. " +
      "Actions: " +
      "create_device (parent? default TIID / EAP device path, name, subType, before?, vInfo?, claimIndex?, save?) — CreateChild a master/slave/box; claimIndex immediately ClaimResources to bind underlying hardware; a wrong subType/vInfo ghost is cleaned up and reported as failure; " +
      "create_batch (creates:[{parent?,name,subType,before?,vInfo?,claimIndex?}], save?); " +
      "list_resources (path) — read-only; probes ITcSmTreeItem5.ResourcesCount then ResourceCount (Beckhoff pages disagree on the name) and reports which answered; " +
      "claim_resources (path, index [1-based per Beckhoff examples], save?) — bind the node to underlying FC/EL hardware (offline config edit, NOT a runtime write); " +
      "create_gsd_box (controllerPath, name, gsdPath, moduleIdentNumber, subType [REQUIRED — PN device subType, depends on controller variant], boxFlags? [GENERATE_NAME_FROM_PAB 0x0004 / GET_STATIONNAME 0x0400 / SET_NOT_IP_TO_OS 0x4000], dapNumber?, before?, save?) — PROFINET GSD/GSDML box; vInfo = gsdPath#moduleIdentNumber#boxFlags#dapNumber. CAVEAT: GSD box subType + vInfo format from a doc summary, confirm against infosys 1041677067 before relying on it; " +
      "add_netvar (boxPath = EAP publisher/subscriber box, name, dataType [IEC type as vInfo, e.g. BOOL/INT], before?, save?) — EAP pub/sub variable (SubType 0; resulting ItemType 35 publisher / 36 subscriber); " +
      "set_station_address (path = PROFIBUS slave/box, address, save?) — discovers the address element via ProduceXml then ConsumeXml a minimal envelope (the bare-number form is unverified and NOT shipped); if discovery fails, use get_xml + set_xml; " +
      "import_dbc (masterPath = CANopen master, fileName [.dbc], importExtendedMessages?, importMultiplexedDataMessages?, keepUnchangedMessages?, communicateWithSlavesFromDbcFile?, save?) — CanOpenMaster/ImportDbcFile config import (requires TC3.1 build >= 4018); " +
      "get_xml (path) — raw ProduceXml passthrough for discovering real param elements; " +
      "set_xml (path, xml = partial <TreeItem> XML, returnXml?, save?) — generic ConsumeXml escape hatch for any fieldbus param not covered above.",
    inputSchema: {
      action: z.enum([
        "create_device", "create_batch", "list_resources", "claim_resources",
        "create_gsd_box", "add_netvar", "set_station_address", "import_dbc",
        "get_xml", "set_xml",
      ]),
      parent: z.string().optional(),
      name: z.string().optional(),
      subType: z.number().int().optional(),
      before: z.string().optional().describe("insert before this sibling"),
      vInfo: z.string().optional(),
      claimIndex: z.number().int().optional(),
      creates: z.array(z.object({
        parent: z.string().optional(),
        name: z.string(),
        subType: z.number().int(),
        before: z.string().optional(),
        vInfo: z.string().optional(),
        claimIndex: z.number().int().optional(),
      })).optional(),
      path: z.string().optional(),
      index: z.number().int().optional(),
      controllerPath: z.string().optional(),
      gsdPath: z.string().optional(),
      moduleIdentNumber: z.string().optional(),
      boxFlags: z.number().int().optional(),
      dapNumber: z.string().optional(),
      boxPath: z.string().optional(),
      dataType: z.string().optional(),
      address: z.number().int().optional(),
      masterPath: z.string().optional(),
      fileName: z.string().optional(),
      importExtendedMessages: z.boolean().optional(),
      importMultiplexedDataMessages: z.boolean().optional(),
      keepUnchangedMessages: z.boolean().optional(),
      communicateWithSlavesFromDbcFile: z.boolean().optional(),
      xml: z.string().optional(),
      returnXml: z.boolean().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional().describe("DTE attach mode; default active"),
      save: z.boolean().optional(),
    },
  },

  tc_module: {
    description:
      "TcCOM module objects under TIRC^TcCOM Objects (paths use ^ separators). CONFIG-TIME ONLY — no INIT/PREOP/SAFEOP/OP transitions (not an Automation Interface feature); nothing here activates config, downloads, or touches the runtime/safety system. Actions: " +
      "list (read-only) — enumerate module instances via ITcModuleManager3, returns {count,modules:[{moduleTypeName,moduleInstanceName,classId,oid,objectId,parentOid}]} (oids are DECIMAL; XAE shows hex). " +
      "create (name, by=\"classid\"|\"name\", id, before?) — CreateChild under TcCOM Objects: by=classid -> subType 0, id = module GUID/ClassID e.g. {8f5fdcff-...}; by=name -> subType 1, id = registered module type name e.g. \"NewModule\"; a malformed/ghost child is cleaned up and reported as an error. " +
      "get_xml (path) — ProduceXml of the instance (Parameters / DataAreas / Symbols, with current CreateSymbol/CreateSymbols flags). set_xml (path, xml, returnXml?) — ConsumeXml escape hatch for parameters not exposed as typed properties. " +
      "enable_symbols (path, parameters?, dataAreas?, returnXml?) — convenience toggle: sets CreateSymbol=true on Parameter nodes and/or CreateSymbols=true on DataArea AreaNo nodes via ProduceXml/ConsumeXml. CAVEAT: the XPath/attribute names are from a how-to summary, NOT verified against a literal ProduceXml dump — call get_xml on a real module first and fall back to set_xml if the toggle reports changed:false. " +
      "To wire module DataArea symbols to PLC/IO/other-module variables (symbols must already exist via enable_symbols), use tc_link link/unlink. " +
      "set_context (path, taskObjectId, contextId?) — assign the instance to a task's execution context; taskObjectId/contextId are DECIMAL oids (XAE shows hex). GUARDED: changes the activated mapping/runtime context, requires confirm=\"" + MODULE_CONTEXT_CONFIRMATION + "\" and defaults to no-op.",
    inputSchema: {
      action: z.enum(["list", "create", "get_xml", "set_xml", "enable_symbols", "set_context"]),
      path: z.string().optional(),
      name: z.string().optional(),
      by: z.enum(["classid", "name"]).optional(),
      id: z.string().optional(),
      before: z.string().optional().describe("insert before this sibling under TcCOM Objects"),
      xml: z.string().optional(),
      returnXml: z.boolean().optional(),
      parameters: z.boolean().optional(),
      dataAreas: z.boolean().optional(),
      taskObjectId: z.number().int().optional().describe("decimal ObjectId of the target task (XAE shows it in hex)"),
      contextId: z.number().int().optional(),
      confirm: z.string().optional(),
    },
  },

  tc_cpp: {
    description:
      "TwinCAT C++ projects/modules under TIXC (paths use ^ separators). C++ ONLY — TISC (safety) untouched; runtime needs a later activate/download. VS-hosted-safe: create/open go purely through ITcSmTreeItem.CreateChild on TIXC (no New/Open/SaveConfiguration). Actions: " +
      "create_project (name, template, before?) — CreateChild a new C++ project node under TIXC from a wizard; template = \"TwinCAT C++ Project Wizard\" | \"TcVersionedDriverWizard\" | \"TcModuleCyclicCallerWizard\" (or a full template .vcxproj/.tczip path — if a wizard NAME is rejected, fall back to the file path). " +
      "create_module (projectPath = TIXC^<proj>, name, template? default \"TwinCAT Class Wizard\", before?) — CreateChild a module/class on an existing C++ project. " +
      "open (file = existing .vcxproj/.tczip, subType? 0 copy into solution dir (default) /1 move/2 use-in-place, before?) — import an existing C++ project; the project is NOT renamed (CreateChild name is empty). " +
      "tmc_codegen (projectPath) — offline StartTmcCodeGenerator (regenerates C++ from the .tmc; no runtime impact). " +
      "set_props (projectPath, bootProjectEncryption? None|Target, saveProjectSources?) — offline config edit via ConsumeXml (at least one prop required). " +
      "build (projectName = the .vcxproj DTE project Name/UniqueName, config? default \"Release|TwinCAT RT (x64)\", waitForFinish? default true, timeoutMs? default 1800000) — compile a single C++ project via SolutionBuild2.BuildProject; compiles only, does NOT deploy. " +
      "publish (projectPath, confirm) — GUARDED, requires confirm=\"" + CPP_PUBLISH_CONFIRMATION + "\" and defaults to no-op: builds the module for ALL platforms and exports the deployable/shippable driver artifacts (long-running); does NOT itself activate/restart the runtime. " +
      "CAVEAT: the ConsumeXml wrapper element for C++ project params is from a doc summary (Set-TreeItemXml surfaces GetLastXmlError, so a wrong element fails loudly); ProduceXml the project node once to confirm element names before relying on tmc_codegen/set_props/publish.",
    inputSchema: {
      action: z.enum(["create_project", "create_module", "open", "tmc_codegen", "set_props", "build", "publish"]),
      name: z.string().optional(),
      template: z.string().optional(),
      projectPath: z.string().optional(),
      file: z.string().optional(),
      subType: z.number().int().min(0).max(2).optional(),
      before: z.string().optional().describe("insert before this sibling"),
      bootProjectEncryption: z.enum(["None", "Target"]).optional(),
      saveProjectSources: z.boolean().optional(),
      projectName: z.string().optional(),
      config: z.string().optional(),
      waitForFinish: z.boolean().optional(),
      timeoutMs: z.number().int().positive().max(3600000).optional(),
      confirm: z.string().optional(),
    },
  },

  tc_measurement: {
    description:
      "Measurement (TE130X Scope View) projects + TwinCAT Analytics (TIAN) logger/stream config. Scope/Analytics PROJECTS are separate EnvDTE.Project nodes (AddFromTemplate), NOT System Manager tree nodes; TIAN logger/stream nodes ARE System Manager children (CreateChild/DeleteChild under TIAN). Requires the respective products installed (scope + analytics templates) — if absent the action fails with a clear 'tooling not installed' message rather than a raw COM HRESULT. Actions: " +
      "scope_create (name, template? = full .tcmproj path [default: first installed under TE130X-Scope-View\\Templates\\Projects], destination? = folder [default: solution dir]) — AddFromTemplate a new Scope project; " +
      "scope_add_child (project, parentPath? = ^-path of names from scope root, name?, elementType? default 0) — CreateChild(out item,name,elementType); only elementType 0 is VERIFIED, non-zero values are EXPERIMENTAL; deep parentPath resolves by enumerating existing children by name; " +
      "scope_rename (project, path, newName) — ChangeName on the element at path; " +
      "scope_record (project, state 'start'|'stop') — StartRecord/StopRecord; GUARDED: state='start' performs LIVE data acquisition and requires confirm=\"" + MEASUREMENT_RECORD_CONFIRMATION + "\" (state='stop' needs no confirm); " +
      "analytics_create (name, template? = full Analytics project template path [must resolve or pass explicitly], destination? = folder) — AddFromTemplate a new Analytics project (project creation ONLY; network/function wiring is UNVERIFIED and not implemented); " +
      "logger_create (name, before?) — CreateChild a DataLogger (subType 1) under TIAN (config edit, no confirm); " +
      "logger_delete (name, dryRun?, confirm) — DeleteChild under TIAN, GUARDED confirm=\"" + DELETE_CONFIRMATION + "\" (dryRun:true previews existence without deleting); " +
      "stream_create (name, before?) — CreateChild a StreamHelper (subType 0) under TIAN (config edit, no confirm); " +
      "stream_delete (name, dryRun?, confirm) — DeleteChild under TIAN, GUARDED confirm=\"" + DELETE_CONFIRMATION + "\"; the actual node name is '<name>_Obj1 (StreamHelper)' (the suffix is appended for you). " +
      "For raw ProduceXml/ConsumeXml on a TIAN logger/stream node (e.g. 'TIAN^<loggerName>') use tc_tree get_xml/set_xml. " +
      "OMITTED as UNVERIFIED: Scope data-export (SaveSVD/ExportCSV/ExportTDMS/ExportBinary/ExportDAT), Scope-Server (ShowControl/CloseControl/Disconnect), LookUpChild, and all Scope/Analytics enums. Nothing here targets the safety system.",
    inputSchema: {
      action: z.enum([
        "scope_create", "scope_add_child", "scope_rename", "scope_record",
        "analytics_create",
        "logger_create", "logger_delete", "stream_create", "stream_delete",
      ]),
      name: z.string().optional(),
      template: z.string().optional(),
      destination: z.string().optional(),
      project: z.string().optional(),
      parentPath: z.string().optional().describe("^-path of names from the scope project root to the parent element"),
      path: z.string().optional(),
      newName: z.string().optional(),
      elementType: z.number().int().optional().describe("CreateChild elementType; only 0 is verified"),
      state: z.enum(["start", "stop"]).optional(),
      before: z.string().optional().describe("insert before this sibling under TIAN"),
      xml: z.string().optional(),
      returnXml: z.boolean().optional(),
      summary: z.boolean().optional(),
      dryRun: z.boolean().optional(),
      confirm: z.string().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional().describe("DTE attach mode; default active"),
    },
  },

  tc_license: {
    description:
      'TwinCAT licensing on the TIRC^License node (requires TC3.1 >= 4022.4; older targets have no AvailableLicenseDevices/ActivateResponseFile support and ProduceXml/ConsumeXml return empty or error — the HRESULT is surfaced, not masked). Nothing here touches the safety system (TIRC^License is real-time/licensing config). Actions: ' +
      'list (read-only) — discover available dongle license devices via ProduceXml; returns {treePath, devices:[{name,pathName,typeName,objectId}]} (pass raw:true to also include the full License-node ProduceXml blob). ' +
      'add (name, device) — OFFLINE config edit: CreateChild a license-device child under License bound to a dongle that MUST already exist in the I/O tree (device = its display-name e.g. "Term 2 (EL6070)" OR its ObjectID e.g. "50462722" from list). This only links the License node to existing hardware; it does NOT create the dongle terminal — add the EL6070 (etc.) first via tc_ethercat/tc_tree. Not confirm-gated (config-only). ' +
      'activate_response (confirm, path, oemGuid?) — GUARDED, requires confirm="' + LICENSE_ACTIVATE_CONFIRMATION + '" and defaults to no-op: ConsumeXml the ActivateResponseFile command to activate an OEM license response file (path = absolute path to the .tmc/.reresponse file). oemGuid is "only required in special cases" and accepts any value; defaults to 0 when omitted. This is a license-activation state change.',
    inputSchema: {
      action: z.enum(["list", "add", "activate_response"]),
      raw: z.boolean().optional(),
      name: z.string().optional(),
      device: z.string().optional().describe('dongle display-name (e.g. "Term 2 (EL6070)") or ObjectID string from list'),
      path: z.string().optional().describe("absolute path to the OEM license response (.tmc/.reresponse) file"),
      oemGuid: z.string().optional(),
      confirm: z.string().optional(),
    },
  },

  tc_variant: {
    description:
      "Project VARIANT management on the open solution (needs TCatSysManagerLib >= 3.3.0.0; older installs get_* return empty and set_*/disable surface a clear COM error). OFFLINE CONFIG ONLY — no confirm token needed; runtime needs a later activate/download; per-item disable/enable refuses TISC (safety) paths. Optional save:true does File.SaveAll once after a write. Actions: " +
      "get_config (read-only) — returns the raw <ProjectVariants> XML (round-trip this FIRST to capture the live shape before editing). " +
      "get_current (read-only) — active variant name; empty string => no variant active / not configured. " +
      "set_config (xml, save?) — replaces the WHOLE variant definition: a <ProjectVariants> document with <Group><Name>..</Name><Member>VariantName</Member>..</Group> and/or standalone <Variant><Name>..</Name></Variant> children (raw XML verbatim, schema not validated). " +
      "select (variant, save?) — sets the active variant by name (e.g. \"Variant3\") or a group in bracket form (e.g. \"[Group1]\"); must already exist in the config. " +
      "disable / enable (path, save?) — sets disabled state on a tree item FOR THE ACTIVE VARIANT; path uses ^ separators (e.g. TIID^Device 2 (EtherCAT)^Box 1, TIPC^MyPlc) and MUST NOT be under TISC. The readback `disabled` int may report SMDS_PARENT_DISABLED=2 (read-only state: disabled because an ancestor is) — never written, only echoed.",
    inputSchema: {
      action: z.enum(["get_config", "get_current", "set_config", "select", "disable", "enable"]),
      xml: z.string().optional(),
      variant: z.string().optional(),
      path: z.string().optional(),
      save: z.boolean().optional(),
    },
  },

  twincat_activate_configuration: {
    description: `Activate the TwinCAT configuration on the target. Guarded: confirm="${ACTIVATE_CONFIRMATION}".`,
    inputSchema: { confirm: z.string() },
  },

  twincat_restart_runtime: {
    description: `Start/restart the TwinCAT runtime on the target. Guarded: confirm="${RESTART_CONFIRMATION}".`,
    inputSchema: { confirm: z.string() },
  },

  hmi_project: {
    description:
      "TwinCAT HMI (TE2000) projects in the open solution. Reached from the SAME IDE as every other tool here (dte.GetObject on the TE2000 automation ProgId, resolved from the package's pkgdef, not hardcoded), so an HMI call and a PLC call share one COM session. " +
      "WHICH project: HMI projects are .hmiproj nodes, a different kind from the .tsproj that tsProject names — they have their own parameter, hmiProject (name or path, from list). With exactly one in the solution it is used automatically; with several and none named, reads take the first and flag hmiProjectAmbiguous while WRITES refuse and list them. Actions: " +
      "list (read-only) — the HMI projects with name, path and IsReady, plus the ProgId that answered and how it was resolved; " +
      "info (read-only) — GetProjectInformation (name, directory, guid), IsReady, IsPublishRunning, all 8 ConfigFields, and the counts of views / content / userControls / userFunctions / dependencyFiles read from tchmiconfig.json; " +
      "add_view | add_usercontrol | add_content (path) — project-RELATIVE path WITH the extension (e.g. UserControls\\Foo.usercontrol); an absolute path or the wrong extension is refused. Go through this rather than writing the file: ONE add writes several registrations (the .hmiproj <Content Include>, the tchmiconfig entry, and for a user control its definition in tchmi.project.Schema.json), and a hand-written file plus a hand-written .hmiproj entry gets one of them and silently loses the others. The response reports each registration read back from disk, and flags an incomplete one. A file the .hmiproj does not declare does NOT exist for build or publish, and nothing reports it; " +
      "add_theme (name), add_localization (name, isoLanguage); " +
      "config_get (field? — omit for all 8), config_set (field, value) — ActiveTheme, Locale, ScaleMode, StartupView, WebsocketIntervalTime, WebsocketTimeout, WebsocketSystemTimeout, LoginPage. Each goes through its own typed setter and the response carries a readback; ScaleMode takes a name (None | ScaleToFit | ScaleToFitWidth | ScaleToFitHeight | ScaleToFill) or 0-4; " +
      "build (configuration?, clean?) — Build/Clean on the HMI project. READ THE VERDICT NARROWLY: green means the project packages, NOT that the bindings are wired — a misspelled binding attribute builds green with an empty Error List (measured). Same shape of false green as xae_build on a PLC library; " +
      "save — SaveAllFiles. Writes default to save:true because the registrations reach disk only on save.",
    inputSchema: {
      action: z.enum([
        "list", "info", "add_view", "add_usercontrol", "add_content", "add_theme",
        "add_localization", "config_get", "config_set", "build", "save",
      ]),
      hmiProject: z.string().optional().describe("which HMI project: its name or its .hmiproj path, from action 'list'"),
      path: z.string().optional().describe("add_view/add_usercontrol/add_content: project-relative path WITH extension"),
      name: z.string().optional().describe("add_theme / add_localization"),
      isoLanguage: z.string().optional().describe("add_localization, e.g. 'en' or 'de'"),
      field: z.string().optional().describe("config_get/config_set: one of the 8 ConfigFields"),
      value: z.any().optional().describe("config_set: string for the text fields, whole number for the websocket timings, name or 0-4 for ScaleMode"),
      configuration: z.string().optional().describe("build: solution configuration name; default the active one"),
      clean: z.boolean().optional().describe("build: run Clean instead of Build"),
      updateUi: z.boolean().optional().describe("build/publish: let the IDE update its UI during the run (default false)"),
      save: z.boolean().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional().describe("DTE attach mode; default active"),
      timeoutMs: z.number().int().optional(),
    },
  },

  hmi_symbol: {
    description:
      "Symbols of a TwinCAT HMI project, through the project's server interface. Mapped symbols are an EXPLICIT list (Server\\TcHmiSrv\\TcHmiSrv.Config.default.json), not a live discovery: a binding onto a symbol nobody mapped is null at run time and silent at build time, which is why listing them is worth a verb. Actions: " +
      "list (read-only; refresh?, filter? substring on the mapped name, maxResults? default 500) — mapped name, domain, the tchmi schema $ref of its type, ReadOnly and Hidden; " +
      "map (mapName, internalName, domain? default ADS, subSymbolName?) — MapSymbol; the 4-argument form with subSymbolName needs ITcHmiServer3 and fails clearly on an older server rather than dropping the sub-symbol; " +
      "unmap (mapName, domain? default ADS, removeHistorizedData?); " +
      "internal_add (name, type — a tchmi schema ref such as \"tchmi:general#/definitions/Number\" —, value?, persist?, readOnly?) and internal_remove (name) — INTERNAL symbols, which needed the IDE's symbol tool until the daemon began binding TcHmiAutomation.dll early: ITcHmiInternalSymbol does not marshal through IDispatch (all four late-bound routes fail, the 5-argument overload included) and is reached by interface instead. " +
      "Both read the project back and report confirmed from that, not from the call: RemoveInternalSymbol answers FALSE on a symbol it has just removed, so its return value is a fact about the call and never the verdict.",
    inputSchema: {
      action: z.enum(["list", "map", "unmap", "internal_add", "internal_remove"]),
      hmiProject: z.string().optional(),
      mapName: z.string().optional(),
      internalName: z.string().optional(),
      name: z.string().optional().describe("internal_add / internal_remove: the internal symbol's name"),
      type: z.string().optional().describe("internal_add: the tchmi schema type ref, e.g. tchmi:general#/definitions/Number"),
      value: z.any().optional().describe("internal_add: the default value"),
      persist: z.boolean().optional().describe("internal_add: persist the value across restarts"),
      readOnly: z.boolean().optional().describe("internal_add: expose the symbol read-only"),
      subSymbolName: z.string().optional(),
      domain: z.string().optional().describe("default ADS"),
      removeHistorizedData: z.boolean().optional(),
      refresh: z.boolean().optional(),
      filter: z.string().optional(),
      maxResults: z.number().int().optional(),
      save: z.boolean().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional(),
      timeoutMs: z.number().int().optional(),
    },
  },

  hmi_file: {
    description:
      "Controls INSIDE a TwinCAT HMI file — a .view, .content or .usercontrol — through ITcHmiFile, so widgets and their attributes are placed by the tool that owns the format instead of by editing HTML as text. path is project-RELATIVE with the extension (e.g. \"Desktop.view\", \"UserControls\\\\Foo.usercontrol\"); an absolute path is refused. The file is located by walking the project's own ProjectItems, NOT Solution.FindProjectItem, which answers null on an HMI project even for a file the .hmiproj plainly declares. " +
      "Actions: identifiers (parent? — omit for the whole file, give an id for its children) — every control id, read-only; source / set_source (source) — the file's markup, for the cases the control verbs cannot reach; " +
      "add_control (identifier, type, and exactly ONE of parent (append inside it) / before / after (next to that sibling), attributes? [{name, value}]) — AddControl and its Before/After siblings. Its return value is reported as controlReturned and is NOT the verdict: it comes back null even when the control was added, so added/confirmed are read back from the file; " +
      "remove_control (identifier) — refuses an id the file does not list rather than reporting a cheerful false; beautify — re-indent; control (identifier) — a control's attributes. " +
      "MEASURED LIMIT: control and change_attributes do not work on this TE2000 build. ITcHmiFile.GetControl answers null for EVERY identifier, freshly added ones included, with the file open and IsOpenAndReady true, and ITcHmiProject.GetControlInstance wants a DOM node, not an id — so reading or changing the attributes of an EXISTING control is out of reach; both verbs say so, with the routes they tried. What works: add_control (attributes are set at creation), remove_control, source / set_source. " +
      "And the standing warning: putting a binding attribute in the file is not a binding that delivers. A misspelled one builds green with an empty Error List; only the running HMI can answer that.",
    inputSchema: {
      action: z.enum(["identifiers", "source", "set_source", "control", "add_control", "change_attributes", "remove_control", "beautify"]),
      hmiProject: z.string().optional(),
      path: z.string().describe("project-relative path WITH extension, e.g. Desktop.view"),
      identifier: z.string().optional().describe("the control's id"),
      parent: z.string().optional().describe("identifiers: list this control's children. add_control: append inside this control"),
      before: z.string().optional().describe("add_control: insert before this sibling"),
      after: z.string().optional().describe("add_control: insert after this sibling"),
      type: z.string().optional().describe("add_control: the control type, e.g. TcHmi.Controls.Beckhoff.TcHmiTextblock"),
      attributes: z.array(z.object({ name: z.string(), value: z.string().optional() })).optional().describe("[{name, value}]; the attribute objects are built by the project's own factory, so they are described here rather than passed in"),
      source: z.string().optional().describe("set_source: the whole markup"),
      save: z.boolean().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional(),
      timeoutMs: z.number().int().optional(),
    },
  },

  plc_tests: {
    description:
      "Run the TcUnit suite on the target and return the numbers — the verdict this server could not give, which is why the test result still had to come from CI. " +
      "run is GUARDED (confirm=\"" + PLC_TESTS_CONFIRMATION + "\") because it ACTIVATES the configuration and RESTARTS the runtime: that REPLACES whatever the target was running, and putting back what was there is the caller's job. The result says so every time. " +
      "The sequence: set the PLC project's boot autostart flag (and restore it afterwards unless keepAutostart), save + build (stopping on a non-zero lastBuildInfo — a suite that did not compile cannot be run), DELETE the previous tcunit_xunit_testresults.xml, activate, restart, then wait for the file to reappear and settle before parsing. The delete is the step that makes the verdict real: without it a run that produced nothing reads exactly like a run that passed. " +
      "WHICH Boot directory is matched to the target NetId by reading each user-mode runtime's TcRegistry.xml (the NetId is in there as binary hex), never picked by convention: reading the wrong one reports another target's numbers as these. No match is an error listing what was found; bootDir overrides. " +
      "The totals are SUMMED from the <testsuite> elements. The root <testsuites tests=\"N\"> attribute does not count the failures — a run with 3 red wrote tests=\"65\" against a suite sum of 68 — so it is reported separately and a disagreement is flagged. The same tally also arrives in the Error List as PlcTask rows at severity High (\"Successful tests: N\" / \"Failed tests: N\"), which the PLC compiler never uses; those are returned as an independent cross-check. " +
      "results (read-only) parses whatever file is on disk and warns that its age says nothing about the code you are asking about — check lastWriteUtc. " +
      "With several PLC projects under TIPC and no treePath this refuses: the boot flag has to go on the one that holds the suite.",
    inputSchema: {
      action: z.enum(["run", "results"]),
      confirm: z.string().optional(),
      treePath: z.string().optional().describe("run: the PLC ROOT node (TIPC^<name>) that holds the suite; required when there is more than one"),
      bootDir: z.string().optional().describe("override the runtime Boot directory instead of matching it to the target NetId"),
      build: z.boolean().optional().describe("run: build before activating (default true)"),
      buildTimeoutMs: z.number().int().positive().optional(),
      waitMs: z.number().int().positive().optional().describe("run: how long to wait for the results file (default 600000)"),
      keepAutostart: z.boolean().optional().describe("run: leave the boot autostart flag set instead of restoring what it was"),
    },
  },

  hmi_function: {
    description:
      "TwinCAT HMI Functions. There is NO AddFunction in the automation API — views, user controls, themes, localizations and content have first-class verbs, Functions do not — and ChangeConfig cannot help either (ConfigFields holds 8 members, none of them the function lists). So a Function's identity lives in NINE places and the IDE's own rename aligns only part of them (and deletes module-scope variables declared outside the function body): the .ts and .function.json file names, `function <N>` and `registerFunctionEx('<N>', ...)` in the source, function.name / function.displayName / dependencyFiles[0].name in the descriptor, and dependencyFiles[].name + userFunctions[].url in tchmiconfig.json — plus two <Content Include> entries in the .hmiproj. This tool writes all of them by construction. Actions: " +
      "list (read-only) — the functions on disk, and whether each is registered in tchmiconfig.json; " +
      "create (name, returnType?, description?, arguments? [{name, type?, description?, required?, bindable?, displayName?}], namespace?, sourceText?, descriptorJson?) — writes Functions\\<name>.ts and Functions\\<name>.function.json, then declares the SOURCE in the collection of the Functions folder. Only the source: the project system then pulls the descriptor in itself (nesting it DependentUpon the .ts) and writes both tchmiconfig registrations, and adding the descriptor on top of that produces a second, '<name> - Copy.function.json' declaration. namespace and the descriptor's $schema default to what the project's existing functions use. dependencyFiles type is EsModule, which is what an ES-module .ts compiles to — a namespace-style .js needs JavaScript instead, and getting that wrong compiles clean and throws ReferenceError at run time; " +
      "rename (name, newName) — renames the .ts THROUGH THE PROJECT and lets the descriptor follow. Order is load-bearing and its failure is silent: the descriptor is declared DependentUpon the source, the project system cascades a rename from parent to dependent, so renaming the descriptor first renames the source to '<new>.function.json.ts' and writes that stem into tchmiconfig. The response reports every rewrite count and whether any trace of the old name is left in the config. It renames the DEFINITION, not the call sites: bindings and events that call the function still name the old one.",
    inputSchema: {
      action: z.enum(["list", "create", "rename"]),
      hmiProject: z.string().optional(),
      name: z.string().optional(),
      newName: z.string().optional(),
      namespace: z.string().optional(),
      returnType: z.string().optional().describe("a tchmi schema $ref, e.g. tchmi:general#/definitions/Boolean"),
      description: z.string().optional(),
      arguments: z.array(z.object({
        name: z.string(),
        type: z.string().optional(),
        displayName: z.string().optional(),
        description: z.string().optional(),
        required: z.boolean().optional(),
        bindable: z.boolean().optional(),
      })).optional(),
      sourceText: z.string().optional().describe("create: the whole .ts, instead of the generated skeleton"),
      descriptorJson: z.string().optional().describe("create: the whole .function.json, instead of the generated one"),
      save: z.boolean().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional(),
      timeoutMs: z.number().int().optional(),
    },
  },

  hmi_publish: {
    description:
      `Publish a TwinCAT HMI project to a TcHmi server. GUARDED: publish requires confirm="${HMI_PUBLISH_CONFIRMATION}" — one server instance hosts ONE project, so publishing REPLACES whatever that instance was serving. Actions: ` +
      "profiles (read-only) — the publish profiles from Properties\\tchmipublish.config.json, each with its destination and whether it will actually push the server-extension configuration; " +
      "publish (profile, confirm, updateUi?, force?, waitMs?) — pre-flight, Publish, then WAIT for it. The pre-flight is the point: with serverExtensions populated (which is what the IDE's publish dialog writes) a publish SUCCEEDS, uploads the project, and silently does NOT push the server extension configuration — the ADS runtimes block stays at the server default and every symbol is null. That is refused here unless force:true; the cure is \"serverExtensions\": [] in the profile. Publish is ASYNCHRONOUS, so the call returns while the upload is still running and the result read straight after is empty; this waits for IsPublishRunning to go false (waitMs, default 600000) and reports waitedMs and completed. Two things measured on the first real publish: the optional progressCallback must be OMITTED, not passed as null — a null reaches IDispatch as VT_EMPTY and fails the whole call with \"Specified OLE variant is invalid\" — and GetPublishResult answers a plain BOOLEAN on this TE2000 build, not an ITcHmiPublishResult, so there is no Result code or SubmissionId to report. A green publish means the upload ran, NOT that the configuration landed: confirm that on the server storage, where the RUNTIMES::*::NETID rows must be no older than PROJECTNAME. Publishing also rewrites the profile file itself (the port becomes a number, socketTimeout appears) and touches engineering.html — expect both at git status; " +
      "result (read-only) — IsPublishRunning plus the last GetPublishResult.",
    inputSchema: {
      action: z.enum(["profiles", "publish", "result"]),
      hmiProject: z.string().optional(),
      profile: z.string().optional(),
      updateUi: z.boolean().optional(),
      force: z.boolean().optional().describe("publish anyway when the profile would skip the server-extension config"),
      waitMs: z.number().int().min(0).optional().describe("publish: how long to wait for the asynchronous publish to finish before reporting a partial result (default 600000)"),
      confirm: z.string().optional(),
      mode: z.enum(["active", "activeOrCreate", "create"]).optional(),
      timeoutMs: z.number().int().optional(),
    },
  },
};

// Which TwinCAT project of the solution the call works on. Declared in one place
// for every project-scoped tool: the alternative is a per-tool parameter that
// drifts, and a caller that has to remember which tools learned about it.
//
// Two guarded verbs already spell out what a wrong project costs, so they say it
// here too rather than in a generic sentence.
const TS_PROJECT_DESCRIPTION =
  "Which TwinCAT project (.tsproj) of the solution to work on: its name or its path, from xae list_projects. " +
  "A solution holds one system manager PER project — tree, target NetId, boot flags, activation all hang off it — " +
  "so with several open and none named the answer comes from the first in solution order (flagged tsProjectAmbiguous on the response). " +
  "Omit it to use the session's chosen project (xae select_project), which is the better way when every call in a row targets the same one.";

for (const name of PROJECT_SCOPED_TOOLS) {
  const schema = toolSchemas[name];
  if (!schema || schema.inputSchema.tsProject) continue;
  schema.inputSchema.tsProject = z.string().optional().describe(TS_PROJECT_DESCRIPTION);
}

// SDK v2 wants a real ZodObject per tool (raw shapes are a deprecated,
// auto-wrapped overload). One wrap here covers every entry and adds nothing to
// the wire — z.object(shape) is the same wrap v1 applied internally (the
// draft-2020-12 $schema dialect noted in the header comes from the SDK's
// converter, wrap or no wrap).
for (const schema of Object.values(toolSchemas)) {
  schema.inputSchema = z.object(schema.inputSchema);
}

module.exports = { toolSchemas, CONFIRMATIONS, XAE_ACTIONS, ...CONFIRMATIONS };
