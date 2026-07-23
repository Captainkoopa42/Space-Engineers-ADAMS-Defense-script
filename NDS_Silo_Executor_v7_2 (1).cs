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
// READY DETECTION / BUILD-ON-COMMAND:
//   Empty is a healthy operational state. Projector RemainingBlocks == 0 is
//   NEVER treated as proof that a missile exists at startup. The script only
//   marks LaunchReady after it intentionally started a build and watched that
//   build complete, or after persisted Storage plus merge verification proves
//   the same safe state after a reload.
//
//   If a mount has a "<name> Merge" group configured, a connected merge block
//   is the physical-presence authority. Mounts without merge verification are
//   supported, but after reload they rely on persisted script state and should
//   be considered less reliable than merge-verified mounts.
//
// MANUAL BUILD (v7.2 addition):
//   build:<mount>:<type>   — force one specific Empty mount to start
//                            building <type> immediately, independent of
//                            anything in the mission queue.
//   buildall:<type>        — same, applied to every currently-Empty mount.
//   resetmount:<mount>     — operator recovery for FaultedLoaded/unknown
//                            occupied states after physically clearing bay.
//   Manual builds stop at LaunchReady and never launch without a mission.
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
const int TICKS_POST_LAUNCH  = 180;   // legacy fallback; kept for operator familiarity
const int TICKS_BUILD_CHECK  = 30;
const int TICKS_SEPARATION_TIMEOUT = 360;
const int TICKS_POST_SEPARATION_HOLD = 60;


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
enum MountState { Unavailable, Empty, Building, LaunchReady, Launching, PostLaunch, FaultedLoaded }

class MissileMount
{
    public string      Name;
    public MountState  State      = MountState.Empty;
    public MissileType? LoadedType = null;
    public int         StateTick  = 0;
    public bool        BuildOrdered = false;
    public bool        ManualBuild = false;
    public MissionStep LaunchStep = null;
    public string      FaultReason = "";
    public string      ValidationStatus = "Not scanned";

    public IMyProgrammableBlock                   PB;
    public Dictionary<MissileType, IMyProjector>   Projectors = new Dictionary<MissileType, IMyProjector>();
    public List<IMyShipWelder>                     Welders    = new List<IMyShipWelder>();
    public List<IMyDoor>                            Doors      = new List<IMyDoor>();
    public List<IMyShipMergeBlock>                  MergeBlocks = new List<IMyShipMergeBlock>();

    public IMyProjector ActiveProjector =>
        LoadedType.HasValue && Projectors.ContainsKey(LoadedType.Value)
        ? Projectors[LoadedType.Value] : null;

    public bool PBValid => PB != null && PB.IsFunctional;

    public bool IsUsable { get { return State != MountState.Unavailable && PBValid && FunctionalWelderCount > 0 && Projectors.Count > 0; } }

    public int FunctionalWelderCount { get { return Welders.Count(w => w.IsFunctional); } }

    public bool IsReadyWith(MissileType type) { return State == MountState.LaunchReady && LoadedType == type && PBValid; }

    public bool CanAcceptBuild { get { return State == MountState.Empty && IsUsable; } }

    public bool Supports(MissileType type) { return IsUsable && Projectors.ContainsKey(type) && Projectors[type] != null && Projectors[type].IsFunctional; }

    // Whether this mount even uses the merge-verification convention at all.
    public bool HasMergeConvention => MergeBlocks.Count > 0;

    // True only if at least one configured merge block is actually connected —
    // hardware-level proof a built missile is physically attached.
    public bool MergeConfirmsPresent => MergeBlocks.Count > 0 && MergeBlocks.Any(m => m.IsConnected);
}

List<MissileMount> mounts = new List<MissileMount>();
List<string> debugErrors = new List<string>();
bool isReadyForOperation = false;  // installation operational; does not require loaded missiles
bool sharedInfrastructureValid = false;
string sharedInfrastructureStatus = "Not validated";


// ── MISSION MODEL ─────────────────────────────────────

class MissionStep
{
    public MissileType Type;
    public int Count;
    public int Fired;
    public string Status = "Queued";
    public string BlockReason = "";
    public bool Complete { get { return Fired >= Count; } }
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
    LoadMountStorage();
    ValidateRequiredBlocks();  // validates shared infrastructure plus at least one usable mount
    PrintStatus();
}

public void Main(string argument, UpdateType updateSource)
{
    tickCounter += 10;
    if (!string.IsNullOrEmpty(argument)) ProcessCommand(argument);
    if (!hubAddressReceived) CheckForHubAddress();

    if ((updateSource & UpdateType.Update10) != 0)
    {
        if (hubAddressReceived)
        {
            SendStatusToHub();
            if (isReadyForOperation) CheckForIncomingMessages();
        }
        RunMountStates();
        RunSiloLogic();
        UpdateLaunchPanel();
        UpdateDebugPanel();
    }
}


// ── VALIDATION ──────────────────────────────────────

bool ValidateRequiredBlocks()
{
    debugErrors.Add("-------------------------------------");
    var criticalErrors = new List<string>();

    var doorGroup = GridTerminalSystem.GetBlockGroupWithName(SHARED_DOOR_GROUP);
    if (doorGroup == null)
        criticalErrors.Add("Shared Door Group '" + SHARED_DOOR_GROUP + "' not found");

    var prelaunchTimer = GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER) as IMyTimerBlock;
    if (prelaunchTimer == null || !prelaunchTimer.IsFunctional)
        criticalErrors.Add("Prelaunch Timer '" + PRELAUNCH_TIMER + "' missing or not functional");

    sharedInfrastructureValid = criticalErrors.Count == 0;
    sharedInfrastructureStatus = sharedInfrastructureValid ? "OK" : string.Join("; ", criticalErrors.ToArray());

    int usable = mounts.Count(m => m.IsUsable);
    isReadyForOperation = sharedInfrastructureValid && usable > 0;

    if (!sharedInfrastructureValid)
    {
        debugErrors.Insert(0, "╔════ STARTUP VALIDATION FAILED ════╗");
        foreach (var err in criticalErrors) debugErrors.Insert(1, "CRITICAL: " + err);
        debugErrors.Insert(2, "FIX: Create missing shared launch blocks or rename existing ones.");
        debugErrors.Insert(3, "════════════════════════════════════");
    }
    else if (usable == 0)
    {
        isReadyForOperation = false;
        debugErrors.Insert(0, "╔════ STARTUP VALIDATION FAILED ════╗");
        debugErrors.Insert(1, "CRITICAL: Zero usable missile mounts.");
        debugErrors.Insert(2, "FIX: Repair at least one mount PB, welder group, and projector.");
        debugErrors.Insert(3, "════════════════════════════════════");
    }
    else
    {
        debugErrors.Insert(0, "✓ Silo Operational: YES. Usable Mounts: " + usable + "/" + mounts.Count + ". Empty silos are Ready.");
    }

    SaveMountStorage();
    return isReadyForOperation;
}


// ── BLOCK DISCOVERY & DIAGNOSTICS ──────────────────────

void FindAllMountBlocks()
{
    mounts.Clear();
    debugErrors.Clear();
    debugErrors.Add("Last Scan: " + DateTime.Now.ToString("HH:mm:ss"));
    debugErrors.Add("Subgrid Support: " + (allowSubgrids ? "ENABLED" : "DISABLED"));
    debugErrors.Add("Merge verification: recommended physical-presence authority; non-merge mounts use persisted build state only.");
    debugErrors.Add("-------------------------------------");

    foreach (string name in MISSILE_MANIFEST)
    {
        var mount = new MissileMount { Name = name };
        string lName = name.ToLower();
        var reasons = new List<string>();

        var pbs = new List<IMyProgrammableBlock>();
        GridTerminalSystem.GetBlocksOfType(pbs, b =>
            (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b != Me && b.IsFunctional &&
            b.CustomName.ToLower().Contains(lName) &&
            b.CustomName.ToLower().Contains(PB_SUFFIX.ToLower().Trim()));
        if (pbs.Count > 0) mount.PB = pbs[0]; else reasons.Add("missing missile PB");

        foreach (MissileType mtype in ALL_TYPES)
        {
            string suffix = PROJECTOR_SUFFIX[mtype];
            var projs = new List<IMyProjector>();
            GridTerminalSystem.GetBlocksOfType(projs, b =>
                (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b.IsFunctional &&
                b.CustomName.ToLower().Contains(lName) &&
                b.CustomName.ToLower().Contains(suffix.ToLower().Trim()));
            if (projs.Count > 0) mount.Projectors[mtype] = projs[0];
            else debugErrors.Add("[" + name + "] Capability missing: " + TYPE_CODE[mtype] + " projector");
        }
        if (mount.Projectors.Count == 0) reasons.Add("no valid missile-type projectors");

        var wg = GridTerminalSystem.GetBlockGroupWithName(name + WELDER_GROUP_SUF);
        if (wg != null) wg.GetBlocksOfType(mount.Welders, w => w.IsFunctional);
        if (mount.Welders.Count == 0) reasons.Add("missing functional welders");

        var dg = GridTerminalSystem.GetBlockGroupWithName(name + DOOR_GROUP_SUF);
        if (dg != null) dg.GetBlocksOfType(mount.Doors, d => d.IsFunctional);

        var mg = GridTerminalSystem.GetBlockGroupWithName(name + MERGE_GROUP_SUF);
        if (mg != null) mg.GetBlocksOfType(mount.MergeBlocks, m => m.IsFunctional);

        foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
        SetWelders(mount, false);

        if (reasons.Count > 0)
        {
            mount.State = MountState.Unavailable;
            mount.ValidationStatus = "Unavailable — " + string.Join(", ", reasons.ToArray());
        }
        else
        {
            mount.State = MountState.Empty;
            mount.ValidationStatus = "Usable";
        }

        mounts.Add(mount);
        debugErrors.Add("[" + name + "] " + mount.ValidationStatus);
        debugErrors.Add("  Supports: " + SupportedTypesText(mount));
        debugErrors.Add("  Merge verification: " + (mount.HasMergeConvention ? (mount.MergeConfirmsPresent ? "CONNECTED" : "configured/disconnected") : "not configured"));
        debugErrors.Add(" ");
    }
}

void PrintStatus()
{
    Echo("NDS Silo " + ndsId + " — " + mounts.Count + " mounts, parallel limit " + MAX_PARALLEL_STEP_TYPES);
    Echo("Operational: " + (isReadyForOperation ? "YES" : "NO") + " Shared=" + sharedInfrastructureStatus);
    Echo("Usable Mounts: " + mounts.Count(m => m.IsUsable) + "/" + mounts.Count);
    foreach (var m in mounts)
        Echo("  " + m.Name + ": " + m.State + " " + m.ValidationStatus + " Supports=" + SupportedTypesText(m));
}

string SupportedTypesText(MissileMount mount)
{
    var codes = new List<string>();
    foreach (MissileType t in ALL_TYPES) if (mount.Supports(t)) codes.Add(TYPE_CODE[t]);
    return codes.Count == 0 ? "NONE" : string.Join(" ", codes.ToArray());
}

// ── PERSISTED MOUNT STATE ───────────────────────────

void LoadMountStorage()
{
    var lines = string.IsNullOrWhiteSpace(Storage)
        ? new string[0]
        : Storage.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    foreach (string raw in lines)
    {
        var parts = raw.Split('|');
        if (parts.Length < 4 || parts[0] != "M") continue;
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        if (mount == null || mount.State == MountState.Unavailable) continue;
        MountState st;
        MissileType mt;
        bool hasType = TryParseType(parts[3], out mt);
        bool ordered = parts.Length > 4 && parts[4] == "1";
        if (!Enum.TryParse(parts[2], out st)) continue;

        if ((st == MountState.Building || st == MountState.LaunchReady || st == MountState.Launching || st == MountState.PostLaunch || st == MountState.FaultedLoaded) && hasType)
        {
            if (mount.HasMergeConvention)
            {
                if (mount.MergeConfirmsPresent || st == MountState.Building)
                { mount.State = st; mount.LoadedType = mt; mount.BuildOrdered = ordered; }
                else
                { mount.State = MountState.Empty; mount.LoadedType = null; }
            }
            else if (ordered)
            {
                mount.State = st; mount.LoadedType = mt; mount.BuildOrdered = true;
                if (st == MountState.LaunchReady) mount.FaultReason = "No merge verification; readiness restored from Storage, not projector RemainingBlocks alone.";
            }
            else
            {
                mount.State = MountState.Empty; mount.LoadedType = null;
            }
        }
    }
    foreach (var mount in mounts)
    {
        if (mount.State == MountState.Empty && mount.HasMergeConvention && mount.MergeConfirmsPresent)
        {
            mount.State = MountState.FaultedLoaded;
            mount.FaultReason = "Merge indicates a physical missile/object is present, but persisted loaded type is unknown.";
        }
    }
}

void SaveMountStorage()
{
    var sb = new System.Text.StringBuilder();
    foreach (var m in mounts)
    {
        sb.Append("M|").Append(m.Name).Append("|").Append(m.State).Append("|")
          .Append(m.LoadedType.HasValue ? m.LoadedType.Value.ToString() : "None").Append("|")
          .Append(m.BuildOrdered ? "1" : "0").Append("\n");
    }
    Storage = sb.ToString();
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
            case MountState.Launching:
                RunLaunchingState(mount);
                break;
            case MountState.PostLaunch:
                RunPostLaunchState(mount);
                break;
        }
    }
    SaveMountStorage();
}

void RunBuildState(MissileMount mount)
{
    if (tickCounter % TICKS_BUILD_CHECK != 0) return;
    if (!mount.LoadedType.HasValue) { FaultMount(mount, "Build has no missile type"); return; }

    var proj = mount.ActiveProjector;
    if (proj == null || !proj.IsFunctional) { FaultMount(mount, "Build projector missing or nonfunctional"); return; }

    proj.Enabled = true;
    SetWelders(mount, true);

    if (proj.RemainingBlocks == 0)
    {
        if (mount.HasMergeConvention && !mount.MergeConfirmsPresent) return;
        proj.Enabled = false;
        SetWelders(mount, false);
        mount.State = MountState.LaunchReady;
        mount.FaultReason = mount.HasMergeConvention ? "" : "Ready from intentional completed build; add merge blocks for reliable physical confirmation.";
        Echo(mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] LAUNCH READY");
    }
}

void RunLaunchingState(MissileMount mount)
{
    bool separated = mount.HasMergeConvention ? !mount.MergeConfirmsPresent : (tickCounter - mount.StateTick >= TICKS_POST_LAUNCH);
    if (separated)
    {
        mount.State = MountState.PostLaunch;
        mount.StateTick = tickCounter;
        if (mount.LaunchStep != null) mount.LaunchStep.Fired++;
        Echo(mount.Name + " separation confirmed; holding doors open.");
        return;
    }
    if (tickCounter - mount.StateTick > TICKS_SEPARATION_TIMEOUT)
    {
        FaultMount(mount, "SEPARATION TIMEOUT - missile still present");
    }
}

void RunPostLaunchState(MissileMount mount)
{
    if (tickCounter - mount.StateTick < TICKS_POST_SEPARATION_HOLD) return;
    foreach (var door in mount.Doors) door.CloseDoor();
    foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
    SetWelders(mount, false);
    mount.State = MountState.Empty;
    mount.LoadedType = null;
    mount.BuildOrdered = false;
    mount.ManualBuild = false;
    mount.LaunchStep = null;
    mount.FaultReason = "";
}

void FaultMount(MissileMount mount, string reason)
{
    mount.State = MountState.FaultedLoaded;
    mount.FaultReason = reason;
    SetWelders(mount, false);
    foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
    debugErrors.Insert(0, "[" + mount.Name + "] LAUNCH/LOAD FAULT: " + reason);
    Echo(mount.Name + ": LAUNCH FAULT - Missile still present - " + reason);
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
    AssignBuilds();
    UpdateMissionBlocks();

    bool launching = mounts.Any(m => m.State == MountState.Launching || m.State == MountState.PostLaunch);
    bool readyForMission = missionQueue.Count > 0 && mounts.Any(m => m.State == MountState.LaunchReady && !m.ManualBuild);

    switch (siloState)
    {
        case SiloState.Idle:
            if (readyForMission || launching)
            {
                OpenSharedDoors();
                siloState = SiloState.OpeningDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.OpeningDoors:
            if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN)
            {
                TriggerPreLaunchTimer();
                siloState = SiloState.PreLaunch;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.PreLaunch:
            if (tickCounter - siloStateTick >= TICKS_PRELAUNCH)
            {
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.Active:
            AttemptFires();
            if (!readyForMission && !launching)
            {
                siloState = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.ClosingDoors:
            if (readyForMission || launching)
            {
                OpenSharedDoors();
                siloState = SiloState.OpeningDoors;
                siloStateTick = tickCounter;
            }
            else if (tickCounter - siloStateTick >= TICKS_POST_SEPARATION_HOLD)
            {
                CloseSharedDoors();
                siloState = SiloState.Idle;
            }
            break;
    }
}

void AssignBuilds()
{
    if (!isReadyForOperation || missionQueue.Count == 0) return;
    var mission = missionQueue[0];
    var active = GetActiveSteps(mission);
    if (active.Count == 0) return;

    var needed = new Dictionary<MissileType, int>();
    var allocated = new Dictionary<MissileType, int>();
    foreach (var step in active)
    {
        if (!needed.ContainsKey(step.Type)) needed[step.Type] = 0;
        needed[step.Type] += (step.Count - step.Fired);
    }
    foreach (MissileType t in needed.Keys.ToList()) allocated[t] = 0;

    foreach (var mount in mounts)
        if ((mount.State == MountState.Building || mount.State == MountState.LaunchReady || mount.State == MountState.Launching || mount.State == MountState.PostLaunch)
            && mount.LoadedType.HasValue && needed.ContainsKey(mount.LoadedType.Value) && !mount.ManualBuild)
            allocated[mount.LoadedType.Value]++;

    foreach (var mount in mounts)
    {
        if (!mount.CanAcceptBuild) continue;
        MissileType? best = null;
        int bestShortfall = 0;
        foreach (var type in needed.Keys)
        {
            if (!mount.Supports(type)) continue;
            int shortfall = needed[type] - allocated[type];
            if (shortfall > bestShortfall) { bestShortfall = shortfall; best = type; }
        }
        if (best.HasValue)
        {
            StartBuild(mount, best.Value, false);
            allocated[best.Value]++;
        }
    }
}

void UpdateMissionBlocks()
{
    if (missionQueue.Count == 0) return;
    var mission = missionQueue[0];
    var active = GetActiveSteps(mission);
    foreach (var step in active)
    {
        if (step.Complete) { step.Status = "Complete"; continue; }
        if (!mounts.Any(m => m.Supports(step.Type)))
        {
            step.Status = "Blocked";
            step.BlockReason = "Mission blocked: no usable mount supports " + TYPE_CODE[step.Type];
            if (!debugErrors.Contains(step.BlockReason)) debugErrors.Insert(0, step.BlockReason);
            continue;
        }
        if (mounts.Any(m => m.State == MountState.FaultedLoaded && m.LoadedType == step.Type)) step.Status = "Faulted";
        else if (mounts.Any(m => m.State == MountState.Launching && m.LoadedType == step.Type)) step.Status = "Launching";
        else if (mounts.Any(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type && !m.ManualBuild)) step.Status = "LaunchReady";
        else if (mounts.Any(m => m.State == MountState.Building && m.LoadedType == step.Type)) step.Status = "Building";
        else step.Status = "Queued";
    }
}

void StartBuild(MissileMount mount, MissileType type, bool manual)
{
    if (!mount.Supports(type)) { Echo("Cannot build " + TYPE_CODE[type] + " on " + mount.Name + ": unsupported."); return; }
    foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
    var proj = mount.Projectors[type];
    proj.Enabled = true;
    mount.LoadedType = type;
    mount.State = MountState.Building;
    mount.StateTick = tickCounter;
    mount.BuildOrdered = true;
    mount.ManualBuild = manual;
    mount.LaunchStep = null;
    mount.FaultReason = "";
    SetWelders(mount, true);
    SaveMountStorage();
    Echo(mount.Name + " building [" + TYPE_CODE[type] + "]" + (manual ? " (manual)" : ""));
}

void AttemptFires()
{
    if (missionQueue.Count == 0) return;
    var mission = missionQueue[0];
    var active = GetActiveSteps(mission);
    if (active.Count == 0)
    {
        if (mission.Complete)
        {
            missionQueue.RemoveAt(0);
            Echo("Mission complete: " + ParseGPSName(mission.GPS));
        }
        return;
    }

    foreach (var mount in mounts)
    {
        if (mount.State != MountState.LaunchReady || !mount.LoadedType.HasValue || mount.ManualBuild) continue;
        var step = active.FirstOrDefault(s => s.Type == mount.LoadedType.Value && !s.Complete);
        if (step == null) continue;

        OpenSharedDoors();
        foreach (var door in mount.Doors) door.OpenDoor();
        bool fired = mount.PBValid && mount.PB.TryRun(mission.GPS);
        if (fired)
        {
            mount.State = MountState.Launching;
            mount.StateTick = tickCounter;
            mount.LaunchStep = step;
            step.Status = "Launching";
            Echo("Started launch " + mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] → " + ParseGPSName(mission.GPS));
        }
        else
        {
            FaultMount(mount, "TryRun failed");
            step.Status = "Faulted";
            step.BlockReason = mount.Name + ": LAUNCH FAULT - TryRun failed; missile still present";
        }
    }

    if (mission.Complete && !mounts.Any(m => m.State == MountState.Launching || m.State == MountState.PostLaunch))
    {
        missionQueue.RemoveAt(0);
        Echo("Mission complete: " + ParseGPSName(mission.GPS));
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
    int empty    = mounts.Count(m => m.State == MountState.Empty && m.IsUsable);
    int building = mounts.Count(m => m.State == MountState.Building);
    int ready    = mounts.Count(m => m.State == MountState.LaunchReady);
    string status = isReadyForOperation ? "Ready" : "Busy";
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
            if (mount.CanAcceptBuild && mount.Supports(type))
            { StartBuild(mount, type, true); started++; }
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
        if (!mount.Supports(type))
        { Echo($"build: {mount.Name} does not support {TYPE_CODE[type]}."); return; }

        StartBuild(mount, type, true);
        Echo($"Manual build started: {mount.Name} → [{TYPE_CODE[type]}]");
        return;
    }


    if (l.StartsWith("resetmount:"))
    {
        string mountName = arg.Substring(11).Trim();
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(mountName, StringComparison.OrdinalIgnoreCase));
        if (mount == null) { Echo("resetmount: unknown mount '" + mountName + "'"); return; }
        if (mount.State == MountState.Unavailable) { Echo("resetmount: " + mount.Name + " is unavailable; run refresh after repairs."); return; }
        foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
        SetWelders(mount, false);
        mount.State = MountState.Empty;
        mount.LoadedType = null;
        mount.BuildOrdered = false;
        mount.ManualBuild = false;
        mount.LaunchStep = null;
        mount.FaultReason = "Reset by operator. Verify bay is physically empty before rebuilding.";
        SaveMountStorage();
        Echo("resetmount: " + mount.Name + " set to Empty. Verify physical bay is clear.");
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
    sb.AppendLine($"Operational: {(isReadyForOperation ? "YES" : "NO")}");
    sb.AppendLine($"Shared: {sharedInfrastructureStatus}");
    sb.AppendLine($"Usable Mounts: {mounts.Count(m => m.IsUsable)}/{mounts.Count}");
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
        string mergeStr = m.HasMergeConvention ? (m.MergeConfirmsPresent ? " [merge:CONNECTED]" : " [merge:DISCONNECTED]") : " [merge:not configured]";
        sb.AppendLine($"{m.Name}: {m.State.ToString().ToUpper()} {typeStr}{remStr}{mergeStr}");
        sb.AppendLine($"  Supports: {SupportedTypesText(m)}");
        if (!string.IsNullOrEmpty(m.FaultReason)) sb.AppendLine($"  Fault/Note: {m.FaultReason}");
    }

    if (missionQueue.Count > 0)
    {
        var active     = GetActiveSteps(missionQueue[0]);
        var activeSet  = new HashSet<MissionStep>(active);

        sb.AppendLine();
        sb.AppendLine($"── Mission: {ParseGPSName(missionQueue[0].GPS)} ──");
        foreach (var s in missionQueue[0].Steps)
        {
            string tag = s.Complete ? "Complete" : (activeSet.Contains(s) ? s.Status : "Queued");
            sb.AppendLine($"{TYPE_CODE[s.Type]} {s.Fired}/{s.Count} [{tag}]");
            if (!string.IsNullOrEmpty(s.BlockReason)) sb.AppendLine("  " + s.BlockReason);
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
