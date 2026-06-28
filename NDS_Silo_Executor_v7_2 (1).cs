// =====================================================
// NDS UNIT COMMUNICATION v7.2 - Silo Executor (With Debug + Fixed Ready Detection)
// Multi-Type Strike Package Architecture (MTSPA) — Silo Layer
// =====================================================
//
// MISSILE TYPES (5 — matches MTSPA spec):
//   Normal | Nuke | Decoy | AirTurret | PillarTurret
//
// BLOCK NAMING — for a mount named "ICBM 1" (spec word order: Type before "Projector"):
//   PB:                "ICBM 1 Programming Block"      (shared, type-agnostic)
//   Projector Normal:  "ICBM 1 Normal Projector"
//   Projector Nuke:    "ICBM 1 Nuke Projector"
//   Projector Decoy:   "ICBM 1 Decoy Projector"
//   Projector AirT.:   "ICBM 1 AirTurret Projector"
//   Projector PillarT.:"ICBM 1 PillarTurret Projector"
//   Welder group:      "ICBM 1 Welders"                (shared, all types)
//   Doors (optional):  "ICBM 1 Doors"                  (per-mount bay doors)
//   Merge (optional):  "ICBM 1 Merge"                  (per-mount cradle merge,
//                       final verification that a built missile is physically
//                       present — see "READY DETECTION" below)
//
// MISSION MODEL:
//   A TargetMission carries an ORDERED list of MissionSteps.
//   Each step has a type, a count, and a fired counter.
//
// READY DETECTION (v7.2 fix):
//   The projector is the PRIMARY teller for "is a build finished" —
//   RemainingBlocks == 0 is trusted on its own, regardless of whether the
//   projector is currently Enabled. A correctly-finished build gets disabled
//   by this script as part of normal operation, so requiring Enabled here
//   would make every already-built mount misread as Empty on the very next
//   scan or recompile — which is exactly the bug this version fixes.
//
//   If a mount has a "<name> Merge" group configured, a connected merge
//   block there is the FINAL VERIFICATION — hardware-level proof something
//   real is physically attached, used as the tie-breaker if the projector's
//   read alone is ambiguous. Mounts with no merge group configured fall back
//   to trusting the projector's read on its own.
//
// MANUAL BUILD (v7.2 addition):
//   build:<mount>:<type>   — force one specific Empty mount to start
//                            building <type> immediately, independent of
//                            anything in the mission queue.
//   buildall:<type>        — same, applied to every currently-Empty mount.
//   Useful for pre-stocking inventory before any target order arrives.
// =====================================================


// ── MISSILE TYPE ─────────────────────────────────────
enum MissileType { Normal, Nuke, Decoy, AirTurret, PillarTurret }

static readonly MissileType[] ALL_TYPES = {
    MissileType.Normal,
    MissileType.Nuke,
    MissileType.Decoy,
    MissileType.AirTurret,
    MissileType.PillarTurret
};

// Short codes used in IGC payloads and on the LCD
static readonly Dictionary<MissileType, string> TYPE_CODE = new Dictionary<MissileType, string>
{
    { MissileType.Normal,       "NORM"   },
    { MissileType.Nuke,         "NUKE"   },
    { MissileType.Decoy,        "DEC"    },
    { MissileType.AirTurret,    "AIR"    },
    { MissileType.PillarTurret, "PILLAR" },
};


// ── MISSILE MANIFEST ─────────────────────────────────
// Edit this list to match your mount names.
static readonly List<string> MISSILE_MANIFEST = new List<string>
{
    "ICBM 1",
    "ICBM 2",
    "ICBM 3",
    "ICBM 4",
};


// ── BLOCK NAME SUFFIXES ───────────────────────────────
const string PB_SUFFIX        = " Programming Block";
const string WELDER_GROUP_SUF = " Welders";
const string DOOR_GROUP_SUF   = " Doors";
const string MERGE_GROUP_SUF  = " Merge";   // optional — final verification for Ready detection

// Spec naming order: "<mount name> <Type> Projector"
static readonly Dictionary<MissileType, string> PROJECTOR_SUFFIX
    = new Dictionary<MissileType, string>
{
    { MissileType.Normal,       " Normal Projector"       },
    { MissileType.Nuke,         " Nuke Projector"         },
    { MissileType.Decoy,        " Decoy Projector"        },
    { MissileType.AirTurret,    " AirTurret Projector"    },
    { MissileType.PillarTurret, " PillarTurret Projector" },
};


// ── SHARED INFRASTRUCTURE ────────────────────────────
const string SHARED_DOOR_GROUP  = "Missile Doors";
const string PRELAUNCH_TIMER    = "Missile PreLaunch";
const string LAUNCH_PANEL_NAME  = "Missile Launch Panel";
const string DEBUG_PANEL_NAME   = "Missile Debug Panel";
const string STATUS_LCD_NAME    = "Silo Status LCD";  // NEW: For persistent startup status


// ── TIMING (Update10 ticks, ~0.167s each) ─────────────
const int TICKS_DOOR_OPEN    = 60;
const int TICKS_PRELAUNCH    = 60;
const int TICKS_POST_LAUNCH  = 180;
const int TICKS_BUILD_CHECK  = 30;


// ── PARALLEL STEP LIMIT ───────────────────────────────
// How many distinct step types may be actively building/firing at once
// for the current mission. Adjustable at runtime with "limit:N".
int MAX_PARALLEL_STEP_TYPES = 2;


// ── SUBGRID SUPPORT ─────────────────────────────────────
bool allowSubgrids = true;   // ← Set to false to restore strict main-grid-only behavior


// ── IGC ───────────────────────────────────────────────
string ndsId;
long   hubAddress          = 0;
bool   hubAddressReceived  = false;
string sharedSecret        = "YourSecretKey123";
string hubChannelTag       = "NDS_TO_HUB";
string targetsTag          = "HUB_TO_NDS_TARGETS";
string hubAddressChannel   = "HUB_ADDRESS";
IMyUnicastListener   acknowledgmentListener;
IMyBroadcastListener hubAddressListener;
int tickCounter = 0;
int messageId   = 0;


// ── MOUNT STATE ───────────────────────────────────────
enum MountState { Empty, Building, Ready, Cooling }

class MissileMount
{
    public string      Name;
    public MountState  State      = MountState.Empty;
    public MissileType? LoadedType = null;
    public int         StateTick  = 0;

    public IMyProgrammableBlock                   PB;
    public Dictionary<MissileType, IMyProjector>   Projectors = new Dictionary<MissileType, IMyProjector>();
    public List<IMyShipWelder>                     Welders    = new List<IMyShipWelder>();
    public List<IMyDoor>                            Doors      = new List<IMyDoor>();
    public List<IMyShipMergeBlock>                  MergeBlocks = new List<IMyShipMergeBlock>();

    public IMyProjector ActiveProjector =>
        LoadedType.HasValue && Projectors.ContainsKey(LoadedType.Value)
        ? Projectors[LoadedType.Value] : null;

    public bool PBValid => PB != null && PB.IsFunctional;

    public bool IsReadyWith(MissileType type) =>
        State == MountState.Ready && LoadedType == type && PBValid;

    public bool CanAcceptBuild => State == MountState.Empty;

    // Whether this mount even uses the merge-verification convention at all.
    public bool HasMergeConvention => MergeBlocks.Count > 0;

    // True only if at least one configured merge block is actually connected —
    // hardware-level proof a built missile is physically attached.
    public bool MergeConfirmsPresent => MergeBlocks.Count > 0 && MergeBlocks.Any(m => m.IsConnected);
}

List<MissileMount> mounts = new List<MissileMount>();
List<string> debugErrors = new List<string>();
bool isReadyForOperation = false;  // NEW: Track if silo can safely launch


// ── MISSION MODEL ─────────────────────────────────────

class MissionStep
{
    public MissileType Type;
    public int Count;
    public int Fired;
    public bool Complete => Fired >= Count;
}

class TargetMission
{
    public string GPS;
    public List<MissionStep> Steps = new List<MissionStep>();
    public bool Complete => Steps.All(s => s.Complete);
}

List<TargetMission> missionQueue = new List<TargetMission>();


// ── SILO DOOR STATE ───────────────────────────────────
enum SiloState { Idle, OpeningDoors, PreLaunch, Active, ClosingDoors }
SiloState siloState     = SiloState.Idle;
int       siloStateTick = 0;


// ── PROGRAM ───────────────────────────────────────────

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    acknowledgmentListener = IGC.UnicastListener;
    hubAddressListener     = IGC.RegisterBroadcastListener(hubAddressChannel);

    ndsId = Me.CustomData.Split('\n')[0].Trim();
    if (string.IsNullOrEmpty(ndsId) || ndsId.Contains("="))
        ndsId = Me.CubeGrid.EntityId.ToString();

    FindAllMountBlocks();
    ValidateRequiredBlocks();  // NEW: Validate before allowing operation
    PrintStatus();
}

public void Main(string argument, UpdateType updateSource)
{
    tickCounter += 10;
    if (!string.IsNullOrEmpty(argument)) ProcessCommand(argument);
    if (!hubAddressReceived) CheckForHubAddress();

    if ((updateSource & UpdateType.Update10) != 0)
    {
        if (hubAddressReceived && isReadyForOperation)  // NEW: Only run if validated
        {
            SendStatusToHub();
            CheckForIncomingMessages();
        }
        RunMountStates();
        RunSiloLogic();
        UpdateLaunchPanel();
        UpdateDebugPanel();
    }
}


// ── VALIDATION (NEW) ──────────────────────────────────

bool ValidateRequiredBlocks()
{
    // NEW: Check for critical blocks on startup
    var criticalErrors = new List<string>();
    
    var doorGroup = GridTerminalSystem.GetBlockGroupWithName(SHARED_DOOR_GROUP);
    if (doorGroup == null)
        criticalErrors.Add($"CRITICAL: Shared Door Group '{SHARED_DOOR_GROUP}' not found! Launches will hang.");
    
    var prelaunchTimer = GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER);
    if (prelaunchTimer == null)
        criticalErrors.Add($"CRITICAL: Prelaunch Timer '{PRELAUNCH_TIMER}' not found! Launches will stall.");
    
    if (criticalErrors.Count > 0)
    {
        isReadyForOperation = false;
        debugErrors.Insert(0, "╔════ STARTUP VALIDATION FAILED ════╗");
        foreach (var err in criticalErrors)
            debugErrors.Insert(1, err);
        debugErrors.Insert(2, "FIX: Create missing blocks or rename existing ones.");
        debugErrors.Insert(3, "════════════════════════════════════");
        Echo("SILO STARTUP FAILED - Check debug panel");
        return false;
    }
    
    isReadyForOperation = true;
    debugErrors.Insert(0, "✓ All required blocks validated. Ready for operation.");
    return true;
}


// ── BLOCK DISCOVERY & DIAGNOSTICS ──────────────────────

void FindAllMountBlocks()
{
    mounts.Clear();
    debugErrors.Clear();
    debugErrors.Add($"Last Scan: {DateTime.Now.ToString("HH:mm:ss")}");
    debugErrors.Add($"Subgrid Support: {(allowSubgrids ? "ENABLED" : "DISABLED")}");
    debugErrors.Add("-------------------------------------");

    if (GridTerminalSystem.GetBlockGroupWithName(SHARED_DOOR_GROUP) == null)
        debugErrors.Add($"[INFO] Shared Door Group '{SHARED_DOOR_GROUP}' not found. (Safe to ignore if no silo doors).");

    if (GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER) == null)
        debugErrors.Add($"[ERROR] Shared Timer '{PRELAUNCH_TIMER}' not found! (Launch sequence will stall).");

    if (GridTerminalSystem.GetBlockWithName(LAUNCH_PANEL_NAME) == null)
        debugErrors.Add($"[WARNING] Main LCD '{LAUNCH_PANEL_NAME}' not found. (Optional telemetry).");

    debugErrors.Add("-------------------------------------");

    foreach (string name in MISSILE_MANIFEST)
    {
        var mount    = new MissileMount { Name = name };
        string lName = name.ToLower();
        bool bayHasErrors = false;

        // PB Check
        var pbs = new List<IMyProgrammableBlock>();
        GridTerminalSystem.GetBlocksOfType(pbs, b =>
            (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b != Me &&
            b.CustomName.ToLower().Contains(lName) &&
            b.CustomName.ToLower().Contains(PB_SUFFIX.ToLower().Trim()));
        if (pbs.Count > 0) mount.PB = pbs[0];
        else { debugErrors.Add($"[{name}] ERROR: Missing PB '{name}{PB_SUFFIX}'"); bayHasErrors = true; }

        // Projector Checks
        // v7.2 FIX: do NOT force-disable found projectors here. Doing so
        // before state detection runs (below) destroys the only signal
        // available for telling an already-built or in-progress mount
        // apart from a genuinely empty one — every projector would read
        // Enabled=false the instant it's found, which is exactly the bug
        // that made every mount misreport as Empty on every scan.
        foreach (MissileType mtype in ALL_TYPES)
        {
            string suffix = PROJECTOR_SUFFIX[mtype];
            var projs = new List<IMyProjector>();
            GridTerminalSystem.GetBlocksOfType(projs, b =>
                (allowSubgrids || b.CubeGrid == Me.CubeGrid) &&
                b.CustomName.ToLower().Contains(lName) &&
                b.CustomName.ToLower().Contains(suffix.ToLower().Trim()));
            if (projs.Count > 0)
            {
                mount.Projectors[mtype] = projs[0];
                // No forced Enabled change here — left exactly as found.
            }
            else { debugErrors.Add($"[{name}] ERROR: Missing Projector '{name}{suffix}'"); bayHasErrors = true; }
        }

        // Welder Group Check
        var wg = GridTerminalSystem.GetBlockGroupWithName(name + WELDER_GROUP_SUF);
        if (wg != null)
        {
            wg.GetBlocksOfType(mount.Welders);
            if (mount.Welders.Count == 0)
            { debugErrors.Add($"[{name}] ERROR: Welder Group '{name}{WELDER_GROUP_SUF}' is empty!"); bayHasErrors = true; }
        }
        else { debugErrors.Add($"[{name}] ERROR: Missing Welder Group '{name}{WELDER_GROUP_SUF}'"); bayHasErrors = true; }

        // Door Group Check (optional)
        var dg = GridTerminalSystem.GetBlockGroupWithName(name + DOOR_GROUP_SUF);
        if (dg != null) dg.GetBlocksOfType(mount.Doors);

        // Merge Group Check (optional — used as final verification for Ready detection)
        var mg = GridTerminalSystem.GetBlockGroupWithName(name + MERGE_GROUP_SUF);
        if (mg != null)
        {
            mg.GetBlocksOfType(mount.MergeBlocks);
            debugErrors.Add($"[{name}] INFO: Merge verification enabled ({mount.MergeBlocks.Count} block(s)).");
        }

        // ── READY / BUILDING DETECTION (v7.2 fix) ────────────────────────
        // Projector is the primary teller: RemainingBlocks == 0 means done,
        // checked regardless of Enabled. If a merge convention is configured
        // for this mount, a connected merge block has final say — it must
        // also confirm before a "done" read is trusted as Ready.
        mount.State      = MountState.Empty;
        mount.LoadedType = null;

        bool mergeConfirms = mount.HasMergeConvention && mount.MergeConfirmsPresent;

        foreach (MissileType mtype in ALL_TYPES)
        {
            IMyProjector proj;
            if (!mount.Projectors.TryGetValue(mtype, out proj)) continue;

            if (proj.Enabled && proj.RemainingBlocks > 0)
            {
                mount.State = MountState.Building; mount.LoadedType = mtype; break;
            }

            if (proj.RemainingBlocks == 0)
            {
                // Mount has a merge group but it isn't currently connected —
                // don't trust the projector's read in isolation. Skip this
                // type and keep looking; if nothing else matches, the mount
                // correctly falls through to Empty.
                if (mount.HasMergeConvention && !mergeConfirms) continue;

                proj.Enabled = false;
                SetWelders(mount, false);
                mount.State = MountState.Ready; mount.LoadedType = mtype;
                break;
            }
        }

        if (mount.State == MountState.Empty && mergeConfirms)
            debugErrors.Add($"[{name}] WARNING: Merge shows occupied but no projector confirmed a type — verify manually.");

        mounts.Add(mount);

        if (!bayHasErrors)
            debugErrors.Add($"[{name}] OK - All blocks found and linked. State={mount.State}" +
                            (mount.LoadedType.HasValue ? $" [{TYPE_CODE[mount.LoadedType.Value]}]" : ""));

        debugErrors.Add(" ");
    }
}

void PrintStatus()
{
    Echo($"NDS Silo {ndsId} — {mounts.Count} mounts, parallel limit {MAX_PARALLEL_STEP_TYPES}");
    Echo($"Subgrids: {(allowSubgrids ? "ENABLED" : "DISABLED")}");
    Echo($"Ready for Operation: {(isReadyForOperation ? "YES" : "NO - FIX BLOCKS FIRST")}");
    foreach (var m in mounts)
        Echo($"  {m.Name}: PB={m.PBValid} Proj={m.Projectors.Count}/5 Welders={m.Welders.Count} State={m.State}");
}


// ── MOUNT STATE RUNNER ────────────────────────────────

void RunMountStates()
{
    foreach (var mount in mounts)
    {
        switch (mount.State)
        {
            case MountState.Building:
                RunBuildState(mount);
                break;
            case MountState.Cooling:
                if (tickCounter - mount.StateTick >= TICKS_POST_LAUNCH)
                {
                    ReScanPB(mount);
                    SetWelders(mount, false);
                    mount.State      = MountState.Empty;
                    mount.LoadedType = null;
                }
                break;
        }
    }
}

void RunBuildState(MissileMount mount)
{
    if (tickCounter % TICKS_BUILD_CHECK != 0) return;
    if (!mount.LoadedType.HasValue) return;

    var proj = mount.ActiveProjector;
    if (proj == null) { mount.State = MountState.Empty; mount.LoadedType = null; return; }

    if (!proj.Enabled) proj.Enabled = true;
    SetWelders(mount, true);

    if (proj.RemainingBlocks == 0)
    {
        // Same primary/final-verification rule applies here as in the
        // initial scan: projector says done, merge (if configured) gets
        // final say before we commit to Ready.
        if (mount.HasMergeConvention && !mount.MergeConfirmsPresent) return;

        proj.Enabled = false;
        SetWelders(mount, false);
        mount.State = MountState.Ready;
        Echo($"{mount.Name} [{TYPE_CODE[mount.LoadedType.Value]}] READY");
    }
}

void ReScanPB(MissileMount mount)
{
    var pbs = new List<IMyProgrammableBlock>();
    GridTerminalSystem.GetBlocksOfType(pbs, b =>
        (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b != Me &&
        b.CustomName.ToLower().Contains(mount.Name.ToLower()) &&
        b.CustomName.ToLower().Contains(PB_SUFFIX.ToLower().Trim()));
    if (pbs.Count > 0) mount.PB = pbs[0];
}

void SetWelders(MissileMount mount, bool on)
{
    foreach (var w in mount.Welders)
        if (w.IsFunctional || !on) w.Enabled = on;
}


// ── ACTIVE STEP WINDOW ────────────────────────────────
List<MissionStep> GetActiveSteps(TargetMission mission)
{
    if (mission == null) return new List<MissionStep>();
    return mission.Steps.Where(s => !s.Complete).Take(MAX_PARALLEL_STEP_TYPES).ToList();
}


// ── SILO LOGIC ────────────────────────────────────────

void RunSiloLogic()
{
    bool anyOrders = missionQueue.Count > 0;

    switch (siloState)
    {
        case SiloState.Idle:
            if (anyOrders)
            {
                OpenSharedDoors();
                siloState     = SiloState.OpeningDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.OpeningDoors:
            if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN)
            {
                TriggerPreLaunchTimer();
                siloState     = SiloState.PreLaunch;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.PreLaunch:
            if (tickCounter - siloStateTick >= TICKS_PRELAUNCH)
                siloState = SiloState.Active;
            break;

        case SiloState.Active:
            AssignBuilds();
            AttemptFires();
            if (missionQueue.Count == 0)
            {
                CloseSharedDoors();
                siloState     = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.ClosingDoors:
            if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN)
                siloState = SiloState.Idle;
            break;
    }
}

void AssignBuilds()
{
    if (missionQueue.Count == 0) return;
    var mission = missionQueue[0];
    var active  = GetActiveSteps(mission);
    if (active.Count == 0) return;

    var needed   = new Dictionary<MissileType, int>();
    var building = new Dictionary<MissileType, int>();
    foreach (var step in active)
    {
        if (!needed.ContainsKey(step.Type)) needed[step.Type] = 0;
        needed[step.Type] += (step.Count - step.Fired);
    }
    foreach (MissileType t in needed.Keys.ToList()) building[t] = 0;

    foreach (var mount in mounts)
        if ((mount.State == MountState.Building || mount.State == MountState.Ready)
            && mount.LoadedType.HasValue && needed.ContainsKey(mount.LoadedType.Value))
            building[mount.LoadedType.Value]++;

    foreach (var mount in mounts)
    {
        if (!mount.CanAcceptBuild) continue;

        MissileType? best = null;
        int bestShortfall = 0;
        foreach (var type in needed.Keys)
        {
            if (!mount.Projectors.ContainsKey(type)) continue;
            int shortfall = needed[type] - building[type];
            if (shortfall > bestShortfall) { bestShortfall = shortfall; best = type; }
        }

        if (best.HasValue)
        {
            StartBuild(mount, best.Value);
            building[best.Value]++;
        }
    }
}

void StartBuild(MissileMount mount, MissileType type)
{
    foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
    var proj = mount.Projectors[type];
    proj.Enabled     = true;
    mount.LoadedType = type;
    mount.State      = MountState.Building;
    mount.StateTick  = tickCounter;
    SetWelders(mount, true);
    Echo($"{mount.Name} building [{TYPE_CODE[type]}]");
}

void AttemptFires()
{
    if (missionQueue.Count == 0) return;
    var mission = missionQueue[0];
    var active  = GetActiveSteps(mission);
    if (active.Count == 0)
    {
        if (mission.Complete)
        {
            missionQueue.RemoveAt(0);
            Echo($"Mission complete: {ParseGPSName(mission.GPS)}");
        }
        return;
    }

    foreach (var mount in mounts)
    {
        if (mount.State != MountState.Ready || !mount.LoadedType.HasValue) continue;

        var step = active.FirstOrDefault(s => s.Type == mount.LoadedType.Value && !s.Complete);
        if (step == null) continue;

        bool fired = mount.PB.TryRun(mission.GPS);
        if (fired)
        {
            step.Fired++;
            mount.State     = MountState.Cooling;
            mount.StateTick = tickCounter;
            foreach (var door in mount.Doors) door.OpenDoor();
            Echo($"Fired {mount.Name} [{TYPE_CODE[mount.LoadedType.Value]}] → " +
                 $"{ParseGPSName(mission.GPS)} ({step.Fired}/{step.Count})");
        }
        else
        {
            Echo($"{mount.Name} TryRun failed — resetting");
            mount.State     = MountState.Cooling;
            mount.StateTick = tickCounter;
        }
    }

    if (mission.Complete)
    {
        missionQueue.RemoveAt(0);
        Echo($"Mission complete: {ParseGPSName(mission.GPS)}");
    }
}


// ── DOOR CONTROL ──────────────────────────────────────

void OpenSharedDoors()  => ApplyToDoorGroup(SHARED_DOOR_GROUP, d => d.OpenDoor());
void CloseSharedDoors()
{
    ApplyToDoorGroup(SHARED_DOOR_GROUP, d => d.CloseDoor());
    foreach (var mount in mounts) foreach (var door in mount.Doors) door.CloseDoor();
}
void ApplyToDoorGroup(string groupName, Action<IMyDoor> action)
{
    var grp = GridTerminalSystem.GetBlockGroupWithName(groupName);
    if (grp == null) return;
    var doors = new List<IMyDoor>();
    grp.GetBlocksOfType(doors);
    foreach (var d in doors) action(d);
}
void TriggerPreLaunchTimer()
{
    var t = GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER) as IMyTimerBlock;
    if (t != null) t.Trigger();
}


// ── IGC ───────────────────────────────────────────────

void CheckForHubAddress()
{
    while (hubAddressListener.HasPendingMessage)
    {
        var msg = hubAddressListener.AcceptMessage();
        long addr;
        if (long.TryParse(msg.Data.ToString(), out addr))
        { hubAddress = addr; hubAddressReceived = true; Echo($"Hub linked: {addr}"); }
    }
}

void SendStatusToHub()
{
    if (tickCounter % 300 != 0) return;
    int empty    = mounts.Count(m => m.State == MountState.Empty);
    int building = mounts.Count(m => m.State == MountState.Building);
    int ready    = mounts.Count(m => m.State == MountState.Ready);
    string status = (siloState == SiloState.Idle && ready > 0) ? "Ready" : "Busy";
    string payload = $"{sharedSecret}:{ndsId}:{messageId}:Status:{status}:{empty}:{building}:{ready}";
    IGC.SendUnicastMessage(hubAddress, hubChannelTag, payload);
    messageId++;
    
    // NEW: Write status to LCD for long-term memory
    var statusLcd = GridTerminalSystem.GetBlockWithName(STATUS_LCD_NAME) as IMyTextPanel;
    if (statusLcd != null)
    {
        statusLcd.ContentType = ContentType.TEXT_AND_IMAGE;
        statusLcd.WriteText($"[{DateTime.Now:HH:mm:ss}] {status} | E:{empty} B:{building} R:{ready}\n", true);
    }
}

void CheckForIncomingMessages()
{
    while (acknowledgmentListener.HasPendingMessage)
    {
        var igcMsg = acknowledgmentListener.AcceptMessage();
        if (igcMsg.Tag == targetsTag) ProcessIncomingMission(igcMsg);
    }
}

void ProcessIncomingMission(MyIGCMessage igcMsg)
{
    string raw   = igcMsg.Data.ToString();
    var    parts = raw.Split(new[] { ':' }, 3);
    if (parts.Length < 3 || parts[0] != sharedSecret) return;

    string tag  = parts[1];
    string rest = parts[2];

    TargetMission mission = null;

    if (tag.Equals("Mission", StringComparison.OrdinalIgnoreCase))
        mission = ParseMissionFormat(rest);
    else if (tag.Equals("Targets", StringComparison.OrdinalIgnoreCase))
        mission = ParseLegacyFormat(rest);

    if (mission == null || mission.Steps.Count == 0) return;

    missionQueue.Add(mission);
    string stepSummary = string.Join(", ", mission.Steps.Select(s => $"{TYPE_CODE[s.Type]} x{s.Count}"));
    Echo($"Mission queued: {ParseGPSName(mission.GPS)} [{stepSummary}]");

    IGC.SendUnicastMessage(hubAddress, hubChannelTag,
        $"{sharedSecret}:{ndsId}:{messageId}:Ack:Received");
    messageId++;
}

// New format: GPS:Name:X:Y:Z:|DEC:4|NUKE:2|AIR:3
TargetMission ParseMissionFormat(string rest)
{
    var segments = rest.Split('|');
    if (segments.Length < 2) return null;

    var mission = new TargetMission { GPS = segments[0].Trim() };
    for (int i = 1; i < segments.Length; i++)
    {
        var sub = segments[i].Split(':');
        if (sub.Length < 2) continue;
        MissileType type;
        int count;
        if (TryParseType(sub[0].Trim(), out type) && int.TryParse(sub[1].Trim(), out count) && count > 0)
            mission.Steps.Add(new MissionStep { Type = type, Count = count, Fired = 0 });
    }
    return mission.Steps.Count > 0 ? mission : null;
}

// Legacy format: GPS:Name:X:Y:Z:  or  ...|COUNT  or  ...|COUNT|TYPE  or  ...|COUNT|All
TargetMission ParseLegacyFormat(string raw)
{
    string gps   = raw;
    int    count = 1;
    string typeStr = "Normal";

    int pipeIdx = raw.LastIndexOf('|');
    if (pipeIdx >= 0)
    {
        string trailer = raw.Substring(pipeIdx + 1).Trim();
        gps = raw.Substring(0, pipeIdx).Trim();
        var tp = trailer.Split('|');
        if (tp.Length >= 2) { int.TryParse(tp[0], out count); typeStr = tp[1].Trim(); }
        else if (!int.TryParse(tp[0], out count)) { typeStr = tp[0]; count = 1; }
    }

    var mission = new TargetMission { GPS = gps };

    if (typeStr.Equals("All", StringComparison.OrdinalIgnoreCase))
    {
        foreach (MissileType t in ALL_TYPES)
            mission.Steps.Add(new MissionStep { Type = t, Count = count, Fired = 0 });
    }
    else
    {
        MissileType type;
        if (!TryParseType(typeStr, out type)) type = MissileType.Normal;
        mission.Steps.Add(new MissionStep { Type = type, Count = count, Fired = 0 });
    }

    return mission;
}

bool TryParseType(string code, out MissileType type)
{
    string c = code.Trim().ToUpper();
    foreach (var kv in TYPE_CODE)
        if (kv.Value == c) { type = kv.Key; return true; }
    return Enum.TryParse(code.Trim(), true, out type);
}


// ── COMMANDS ──────────────────────────────────────────

void ProcessCommand(string arg)
{
    string l = arg.ToLower().Trim();

    if (l.StartsWith("limit:"))
    {
        int n;
        if (int.TryParse(l.Substring(6).Trim(), out n) && n >= 1)
        { MAX_PARALLEL_STEP_TYPES = n; Echo($"Parallel step limit set to {n}"); }
        return;
    }

    // buildall:<type> — force every currently-Empty mount that has the
    // matching projector to start building <type> right now, independent
    // of the mission queue.
    if (l.StartsWith("buildall:"))
    {
        string typeStr = l.Substring(9).Trim();
        MissileType type;
        if (!TryParseType(typeStr, out type)) { Echo($"buildall: unknown type '{typeStr}'"); return; }

        int started = 0;
        foreach (var mount in mounts)
        {
            if (mount.CanAcceptBuild && mount.Projectors.ContainsKey(type))
            { StartBuild(mount, type); started++; }
        }
        Echo($"buildall [{TYPE_CODE[type]}]: started {started} mount(s).");
        return;
    }

    // build:<mount name>:<type> — force one specific Empty mount to start
    // building <type> right now, independent of the mission queue.
    if (l.StartsWith("build:"))
    {
        string rest = arg.Substring(6); // original-case substring, mount names may not be all-lowercase
        int sep = rest.IndexOf(':');
        if (sep < 0) { Echo("Usage: build:<mount name>:<type>"); return; }

        string mountName = rest.Substring(0, sep).Trim();
        string typeStr   = rest.Substring(sep + 1).Trim();

        var mount = mounts.FirstOrDefault(m => m.Name.Equals(mountName, StringComparison.OrdinalIgnoreCase));
        if (mount == null) { Echo($"build: unknown mount '{mountName}'"); return; }

        MissileType type;
        if (!TryParseType(typeStr, out type)) { Echo($"build: unknown type '{typeStr}'"); return; }

        if (!mount.CanAcceptBuild)
        { Echo($"build: {mount.Name} is not Empty (currently {mount.State}) — cannot start a manual build."); return; }
        if (!mount.Projectors.ContainsKey(type))
        { Echo($"build: {mount.Name} has no {TYPE_CODE[type]} projector."); return; }

        StartBuild(mount, type);
        Echo($"Manual build started: {mount.Name} → [{TYPE_CODE[type]}]");
        return;
    }

    switch (l)
    {
        case "refresh":
            FindAllMountBlocks(); ValidateRequiredBlocks(); PrintStatus(); break;
        case "closedoors":
            CloseSharedDoors(); break;
        case "status":
            Echo($"Parallel limit: {MAX_PARALLEL_STEP_TYPES}");
            Echo($"Ready for Operation: {(isReadyForOperation ? "YES" : "NO")}");
            Echo($"Subgrids: {(allowSubgrids ? "ENABLED" : "DISABLED")}");
            foreach (var m in mounts)
                Echo($"{m.Name}: {m.State} Type={(m.LoadedType.HasValue ? TYPE_CODE[m.LoadedType.Value] : "none")} PB={m.PBValid}" +
                     (m.HasMergeConvention ? $" Merge={m.MergeConfirmsPresent}" : ""));
            if (missionQueue.Count > 0)
            {
                Echo($"Active mission: {ParseGPSName(missionQueue[0].GPS)}");
                foreach (var s in missionQueue[0].Steps)
                    Echo($"  {TYPE_CODE[s.Type]} {s.Fired}/{s.Count}{(s.Complete ? " [done]" : "")}");
            }
            break;
        case "clearqueue":
            missionQueue.Clear(); Echo("Mission queue cleared."); break;
    }
}


// ── HELPERS ───────────────────────────────────────────

string ParseGPSName(string gps)
{
    var p = gps.Split(':');
    return p.Length > 1 ? p[1] : gps;
}


// ── LCD ───────────────────────────────────────────────

void UpdateLaunchPanel()
{
    var lcd = GridTerminalSystem.GetBlockWithName(LAUNCH_PANEL_NAME) as IMyTextPanel;
    if (lcd == null) return;
    lcd.ContentType = ContentType.TEXT_AND_IMAGE;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("=== NDS SILO ===");
    sb.AppendLine($"ID:     {ndsId}");
    sb.AppendLine($"Hub:    {(hubAddressReceived ? "LINKED" : "SEARCHING")}");
    sb.AppendLine($"Ready:  {(isReadyForOperation ? "YES" : "NO - FIX BLOCKS")}");
    sb.AppendLine($"Silo:   {siloState}");
    sb.AppendLine($"Limit:  {MAX_PARALLEL_STEP_TYPES} parallel types");
    sb.AppendLine($"Subgrids: {(allowSubgrids ? "ENABLED" : "DISABLED")}");
    sb.AppendLine($"Queue:  {missionQueue.Count} mission(s)");
    sb.AppendLine();
    sb.AppendLine("── Mounts ──");
    foreach (var m in mounts)
    {
        string typeStr  = m.LoadedType.HasValue ? $"[{TYPE_CODE[m.LoadedType.Value]}]" : "[empty]";
        int    rem      = (m.ActiveProjector != null) ? m.ActiveProjector.RemainingBlocks : 0;
        string remStr   = m.State == MountState.Building ? $" {rem}blk" : "";
        string mergeStr = m.HasMergeConvention ? (m.MergeConfirmsPresent ? " [merge:OK]" : " [merge:--]") : "";
        sb.AppendLine($"{m.Name}: {m.State.ToString().ToUpper()} {typeStr}{remStr}{mergeStr}");
    }

    if (missionQueue.Count > 0)
    {
        var active     = GetActiveSteps(missionQueue[0]);
        var activeSet  = new HashSet<MissionStep>(active);

        sb.AppendLine();
        sb.AppendLine($"── Mission: {ParseGPSName(missionQueue[0].GPS)} ──");
        foreach (var s in missionQueue[0].Steps)
        {
            string tag = s.Complete ? "[done]" : (activeSet.Contains(s) ? "[active]" : "[queued]");
            sb.AppendLine($"{TYPE_CODE[s.Type]} {s.Fired}/{s.Count} {tag}");
        }

        if (missionQueue.Count > 1)
            sb.AppendLine($"+ {missionQueue.Count - 1} more mission(s) waiting");
    }

    lcd.WriteText(sb.ToString());
}

void UpdateDebugPanel()
{
    var lcd = GridTerminalSystem.GetBlockWithName(DEBUG_PANEL_NAME) as IMyTextPanel;
    if (lcd == null) return;

    lcd.ContentType = ContentType.TEXT_AND_IMAGE;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("=== SILO DIAGNOSTICS ===");

    if (debugErrors.Count == 0)
    {
        sb.AppendLine("Awaiting initial scan...");
        sb.AppendLine("Run argument 'refresh' on the PB.");
    }
    else
    {
        foreach (string line in debugErrors)
        {
            sb.AppendLine(line);
        }
    }

    lcd.WriteText(sb.ToString());
}
