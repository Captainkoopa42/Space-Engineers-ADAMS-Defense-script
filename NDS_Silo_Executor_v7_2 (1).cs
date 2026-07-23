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
const int TICKS_DOOR_OPEN_TIMEOUT = 180;
const int TICKS_DOOR_CLOSE_TIMEOUT = 180;
const int TICKS_BUILD_COMPLETE_MERGE_TIMEOUT = 180;


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
enum FaultType { None, BuildFault, LaunchCommandFault, SeparationFault, UnknownLoadedFault, ReloadRecoveryFault, DoorFault }

class MissileMount
{
    public string      Name;
    public MountState  State      = MountState.Empty;
    public MissileType? LoadedType = null;
    public int         StateTick  = 0;
    public bool        BuildOrdered = false;
    public bool        ManualBuild = false;
    public MissionStep LaunchStep = null;
    public FaultType   Fault = FaultType.None;
    public string      FaultReason = "";
    public int         LaunchReserved = 0;
    public int         FaultReserved = 0;
    public int         OrphanedFaultReserved = 0;
    public int         MergeWaitStartTick = -1;
    public bool        MergeGroupConfigured = false;
    public int         FunctionalMergeCount = 0;
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

    public bool IsHardwareCapable { get { return State != MountState.Unavailable && PBValid && FunctionalWelderCount > 0 && Projectors.Count > 0; } }

    public bool IsUsable { get { return IsHardwareCapable; } }

    public bool IsAvailableBuildMount { get { return State == MountState.Empty && IsHardwareCapable; } }

    public bool IsOccupied { get { return State == MountState.Building || State == MountState.LaunchReady || State == MountState.Launching || State == MountState.PostLaunch || State == MountState.FaultedLoaded; } }

    public int FunctionalWelderCount { get { return Welders.Count(w => w.IsFunctional); } }

    public bool IsReadyWith(MissileType type) { return State == MountState.LaunchReady && LoadedType == type && PBValid; }

    public bool CanAcceptBuild { get { return IsAvailableBuildMount; } }

    public bool Supports(MissileType type) { return IsUsable && Projectors.ContainsKey(type) && Projectors[type] != null && Projectors[type].IsFunctional; }

    // Whether this mount even uses the merge-verification convention at all.
    public bool HasMergeConvention { get { return MergeGroupConfigured; } }

    // True only if at least one configured merge block is actually connected —
    // hardware-level proof a built missile is physically attached.
    public bool MergeConfirmsPresent { get { return MergeGroupConfigured && MergeBlocks.Count > 0 && MergeBlocks.All(m => m.IsFunctional) && MergeBlocks.Any(m => m.IsConnected); } }
}

List<MissileMount> mounts = new List<MissileMount>();
List<string> debugErrors = new List<string>();
bool isReadyForOperation = false;  // installation operational; does not require loaded missiles
bool sharedInfrastructureValid = false;
string sharedInfrastructureStatus = "Not validated";
bool launchSafetyFault = false;
string launchSafetyFaultReason = "";


// ── MISSION MODEL ─────────────────────────────────────

class MissionStep
{
    public MissileType Type;
    public int Count;
    public int Fired;
    public int InFlight;
    public int FaultReserved;
    public string Status = "Queued";
    public string BlockReason = "";
    public int RemainingDemand { get { return Math.Max(0, Count - Fired - InFlight - FaultReserved); } }
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
enum SiloState { Idle, OpeningDoors, PreLaunch, Active, ClosingDoors, DoorFault }
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
    LoadMountStorage(true);
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
            isReadyForOperation = CanAcceptMissionsNow();
            if (CanAcceptMissionsNow()) CheckForIncomingMessages();
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

    int usable = mounts.Count(m => m.IsHardwareCapable);
    int serviceable = mounts.Count(m => IsServiceableMount(m));
    isReadyForOperation = CanAcceptMissionsNow();

    if (!sharedInfrastructureValid)
    {
        debugErrors.Insert(0, "╔════ STARTUP VALIDATION FAILED ════╗");
        foreach (var err in criticalErrors) debugErrors.Insert(1, "CRITICAL: " + err);
        debugErrors.Insert(2, "FIX: Create missing shared launch blocks or rename existing ones.");
        debugErrors.Insert(3, "════════════════════════════════════");
    }
    else if (serviceable == 0)
    {
        isReadyForOperation = false;
        debugErrors.Insert(0, "╔════ STARTUP VALIDATION FAILED ════╗");
        debugErrors.Insert(1, "CRITICAL: Zero serviceable missile mounts.");
        debugErrors.Insert(2, "FIX: Repair or reset at least one mount that can build or launch safely.");
        debugErrors.Insert(3, "════════════════════════════════════");
    }
    else
    {
        debugErrors.Insert(0, "✓ Silo Operational: YES. Serviceable Mounts: " + serviceable + "/" + mounts.Count + ". Empty silos are Ready.");
        debugErrors.Insert(1, "Ready rule: shared infrastructure OK and at least one Empty/Building/LaunchReady/Launching/PostLaunch hardware-capable mount; FaultedLoaded does not count.");
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
        if (dg != null)
        {
            dg.GetBlocksOfType(mount.Doors);
            if (mount.Doors.Count == 0) reasons.Add("door group configured but empty");
            else if (mount.Doors.Any(d => !d.IsFunctional)) reasons.Add("configured mount door damaged/nonfunctional");
        }

        var mg = GridTerminalSystem.GetBlockGroupWithName(name + MERGE_GROUP_SUF);
        mount.MergeGroupConfigured = mg != null;
        if (mg != null)
        {
            mg.GetBlocksOfType(mount.MergeBlocks);
            mount.FunctionalMergeCount = mount.MergeBlocks.Count(m => m.IsFunctional);
            if (GetFunctionalMergeCount(mount) == 0) reasons.Add("merge group configured but no functional merge blocks");
        }

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
        debugErrors.Add("  Merge verification: " + (mount.MergeGroupConfigured ? (mount.FunctionalMergeCount > 0 ? (mount.MergeConfirmsPresent ? "CONNECTED" : "functional/disconnected") : "CONFIGURED BUT NO FUNCTIONAL MERGE") : "not configured"));
        debugErrors.Add(" ");
    }
}

void PrintStatus()
{
    Echo("NDS Silo " + ndsId + " — " + mounts.Count + " mounts, parallel limit " + MAX_PARALLEL_STEP_TYPES);
    Echo("Operational: " + (isReadyForOperation ? "YES" : "NO") + " Shared=" + sharedInfrastructureStatus);
    Echo("Serviceable Mounts: " + mounts.Count(m => IsServiceableMount(m)) + "/" + mounts.Count);
    foreach (var m in mounts)
        Echo("  " + m.Name + ": " + m.State + " " + m.ValidationStatus + " Supports=" + SupportedTypesText(m));
}

int GetFunctionalMergeCount(MissileMount mount)
{
    if (!mount.MergeGroupConfigured) return 0;
    return mount.MergeBlocks.Count(m => m != null && m.IsFunctional);
}

bool MergeHardwareReady(MissileMount mount)
{
    return !mount.MergeGroupConfigured || GetFunctionalMergeCount(mount) > 0;
}

bool MergeConnectedLive(MissileMount mount)
{
    return mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) > 0 && mount.MergeBlocks.Any(m => m != null && m.IsFunctional && m.IsConnected);
}

bool CanAcceptMissionsNow()
{
    return sharedInfrastructureValid && !launchSafetyFault && SharedDoorsHardwareOk() && mounts.Any(m => IsServiceableMount(m));
}

bool IsServiceableMount(MissileMount mount)
{
    return mount.IsHardwareCapable && MountDoorsHardwareOk(mount) && mount.State != MountState.Unavailable && mount.State != MountState.FaultedLoaded;
}

string SupportedTypesText(MissileMount mount)
{
    var codes = new List<string>();
    foreach (MissileType t in ALL_TYPES) if (mount.Supports(t)) codes.Add(TYPE_CODE[t]);
    return codes.Count == 0 ? "NONE" : string.Join(" ", codes.ToArray());
}


class MountSnapshot
{
    public string Name;
    public MountState State;
    public MissileType? LoadedType;
    public bool BuildOrdered;
    public bool ManualBuild;
    public FaultType Fault;
    public string FaultReason;
    public int LaunchReserved;
    public int FaultReserved;
    public int OrphanedFaultReserved;
    public int MergeWaitStartTick;
    public MissionStep LaunchStep;
    public int StateTick;
}

Dictionary<string, MountSnapshot> CaptureMountSnapshots()
{
    var snap = new Dictionary<string, MountSnapshot>(StringComparer.OrdinalIgnoreCase);
    foreach (var m in mounts)
    {
        snap[m.Name] = new MountSnapshot
        {
            Name = m.Name,
            State = m.State,
            LoadedType = m.LoadedType,
            BuildOrdered = m.BuildOrdered,
            ManualBuild = m.ManualBuild,
            Fault = m.Fault,
            FaultReason = m.FaultReason,
            LaunchReserved = m.LaunchReserved,
            FaultReserved = m.FaultReserved,
            OrphanedFaultReserved = m.OrphanedFaultReserved,
            MergeWaitStartTick = m.MergeWaitStartTick,
            LaunchStep = m.LaunchStep,
            StateTick = m.StateTick
        };
    }
    return snap;
}

void RefreshMountsSafely()
{
    string storageBefore = Storage;
    var snapshots = CaptureMountSnapshots();
    FindAllMountBlocks();
    Storage = storageBefore;
    LoadMountStorage(false);
    ReconcileMountSnapshots(snapshots);
    ValidateRequiredBlocks();
    if (launchSafetyFault)
    {
        string clearReason;
        if (CanClearDoorFault(out clearReason))
        {
            launchSafetyFault = false;
            launchSafetyFaultReason = "";
            siloState = AllConstructionDoorsClosed() ? SiloState.Idle : siloState;
        }
    }
    PrintStatus();
}

void ReconcileMountSnapshots(Dictionary<string, MountSnapshot> snapshots)
{
    foreach (var mount in mounts)
    {
        MountSnapshot old;
        if (!snapshots.TryGetValue(mount.Name, out old)) continue;
        bool oldOccupied = old.State == MountState.Building || old.State == MountState.LaunchReady || old.State == MountState.Launching || old.State == MountState.PostLaunch || old.State == MountState.FaultedLoaded;
        if (!oldOccupied) continue;

        bool becameUnavailable = mount.State == MountState.Unavailable;
        mount.State = old.State;
        mount.LoadedType = old.LoadedType;
        mount.BuildOrdered = old.BuildOrdered;
        mount.ManualBuild = old.ManualBuild;
        mount.Fault = old.Fault;
        mount.FaultReason = old.FaultReason;
        mount.LaunchReserved = old.LaunchReserved;
        mount.FaultReserved = old.FaultReserved;
        mount.OrphanedFaultReserved = old.OrphanedFaultReserved;
        mount.MergeWaitStartTick = old.MergeWaitStartTick;
        mount.LaunchStep = old.LaunchStep;
        mount.StateTick = old.StateTick;

        if (becameUnavailable)
        {
            if (old.State == MountState.Launching && mount.LaunchStep != null && mount.LaunchReserved > 0)
            {
                mount.LaunchStep.InFlight = Math.Max(0, mount.LaunchStep.InFlight - mount.LaunchReserved);
                mount.LaunchStep.FaultReserved += mount.LaunchReserved;
                mount.FaultReserved += mount.LaunchReserved;
                mount.LaunchReserved = 0;
            }
            EnterFault(mount, FaultType.UnknownLoadedFault, "HARDWARE FAULT WHILE OCCUPIED - Previous state: " + old.State + "; mission shot remains reserved if one was active.", true);
            continue;
        }

        if (mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) == 0)
        {
            EnterFault(mount, FaultType.UnknownLoadedFault, "Merge group is configured but no functional merge blocks are available; cannot verify physical state.", true);
            continue;
        }

        if (old.State == MountState.Building)
        {
            if (!BuildProjectorHasProjection(mount))
                EnterFault(mount, FaultType.BuildFault, "Refresh found Building state but selected projector has no valid active projection.", false);
        }
        else if (old.State == MountState.LaunchReady)
        {
            if (mount.MergeGroupConfigured && !mount.MergeConfirmsPresent)
                EnterFault(mount, FaultType.UnknownLoadedFault, "LaunchReady missile lost merge physical-presence confirmation during refresh.", false);
        }
        else if (old.State == MountState.Launching)
        {
            if (mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) > 0 && !MergeConnectedLive(mount))
                ConfirmSeparation(mount);
            else if (mount.LaunchStep == null)
                EnterFault(mount, FaultType.ReloadRecoveryFault, "Runtime refresh found Launching without a live MissionStep reservation.", true);
        }
        else if (old.State == MountState.PostLaunch)
        {
            // Runtime refresh keeps the live StateTick so the remaining hold delay is preserved.
        }
        else if (old.State == MountState.FaultedLoaded)
        {
            // Faulted loaded states require explicit operator recovery even if merge later disconnects.
        }
    }
}


// ── PERSISTED MOUNT STATE ───────────────────────────

void LoadMountStorage(bool coldStartup)
{
    var lines = string.IsNullOrWhiteSpace(Storage)
        ? new string[0]
        : Storage.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
    foreach (string raw in lines)
    {
        var parts = raw.Split('|');
        if (parts.Length < 4 || parts[0] != "M") continue;
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        if (mount == null) continue;
        MountState st;
        MissileType mt;
        FaultType ft = FaultType.None;
        bool hasType = TryParseType(parts[3], out mt);
        bool ordered = parts.Length > 4 && parts[4] == "1";
        bool manual = parts.Length > 5 && parts[5] == "1";
        if (parts.Length > 6) Enum.TryParse(parts[6], out ft);
        string reason = parts.Length > 7 ? parts[7].Replace("~", "|") : "";
        int reserved = 0;
        if (parts.Length > 8) int.TryParse(parts[8], out reserved);
        int faultReserved = 0;
        if (parts.Length > 9) int.TryParse(parts[9], out faultReserved);
        int orphaned = 0;
        if (parts.Length > 10) int.TryParse(parts[10], out orphaned);
        int savedTick = tickCounter;
        if (parts.Length > 11) int.TryParse(parts[11], out savedTick);
        if (!Enum.TryParse(parts[2], out st)) continue;

        if (st == MountState.Building || st == MountState.LaunchReady || st == MountState.Launching || st == MountState.PostLaunch || MountState.FaultedLoaded == st)
        {
            bool hardwareUnavailableOnLoad = mount.State == MountState.Unavailable;
            mount.State = st;
            mount.LoadedType = hasType ? (MissileType?)mt : null;
            mount.BuildOrdered = ordered;
            mount.ManualBuild = manual;
            mount.Fault = ft;
            mount.FaultReason = reason;
            mount.LaunchReserved = reserved;
            mount.FaultReserved = faultReserved;
            mount.OrphanedFaultReserved = orphaned;
            mount.StateTick = coldStartup ? tickCounter : savedTick;
            if (!hasType && st != MountState.FaultedLoaded)
            {
                EnterFault(mount, coldStartup ? FaultType.ReloadRecoveryFault : FaultType.UnknownLoadedFault, "Stored occupied state had no known missile type; physical inspection required.", true);
            }

            if (coldStartup && faultReserved > 0)
            {
                mount.OrphanedFaultReserved += faultReserved;
                mount.FaultReserved = 0;
                faultReserved = 0;
                mount.FaultReason = "Cold reload lost original mission context; previous FaultReserved is now orphaned.";
            }

            if (hardwareUnavailableOnLoad)
            {
                ConvertLaunchReservationToFault(mount);
                EnterFault(mount, FaultType.UnknownLoadedFault, "HARDWARE UNAVAILABLE WHILE STORED OCCUPIED - previous state " + st + "; physical reconciliation required.", true);
            }
            else if (mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) == 0)
            {
                EnterFault(mount, FaultType.UnknownLoadedFault, "Merge group is configured but no functional merge blocks are available; cannot verify restored physical state.", true);
            }
            else if (mount.HasMergeConvention && st == MountState.LaunchReady && !mount.MergeConfirmsPresent)
            {
                EnterFault(mount, FaultType.UnknownLoadedFault, "Stored LaunchReady missile lacks merge physical-presence confirmation after reload.", false);
            }
            else if (coldStartup && st == MountState.Launching)
            {
                if (mount.LaunchReserved <= 0 && mount.OrphanedFaultReserved == 0) mount.OrphanedFaultReserved = 1;
                EnterFault(mount, FaultType.ReloadRecoveryFault, "Cold reload lost original mission context during Launching; unresolved shot outcome must be resolved by operator.", true);
            }
            else if (coldStartup && st == MountState.PostLaunch)
            {
                EnterFault(mount, FaultType.ReloadRecoveryFault, "Cold reload restored PostLaunch without a valid remaining hold timer; operator must inspect and resolve.", true);
            }
            else if (!mount.HasMergeConvention && st == MountState.LaunchReady)
            {
                mount.FaultReason = "No merge verification; readiness restored from Storage, not projector RemainingBlocks alone.";
            }
        }
    }
    foreach (var mount in mounts)
    {
        if (mount.State == MountState.Empty && mount.HasMergeConvention && mount.MergeConfirmsPresent)
        {
            EnterFault(mount, FaultType.UnknownLoadedFault, "Merge indicates a physical missile/object is present, but persisted loaded type is unknown.", false);
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
          .Append(m.BuildOrdered ? "1" : "0").Append("|")
          .Append(m.ManualBuild ? "1" : "0").Append("|")
          .Append(m.Fault).Append("|")
          .Append((m.FaultReason == null ? "" : m.FaultReason).Replace("|", "~").Replace("\n", " ")).Append("|")
          .Append(m.LaunchReserved).Append("|")
          .Append(m.FaultReserved).Append("|")
          .Append(m.OrphanedFaultReserved).Append("|")
          .Append(m.StateTick).Append("\n");
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

bool BuildProjectorHasProjection(MissileMount mount)
{
    var proj = mount.ActiveProjector;
    return proj != null && proj.IsFunctional && proj.IsProjecting && proj.TotalBlocks > 0;
}

void ConfirmSeparation(MissileMount mount)
{
    mount.State = MountState.PostLaunch;
    mount.StateTick = tickCounter;
    if (mount.LaunchStep != null && mount.LaunchReserved > 0)
    {
        mount.LaunchStep.InFlight = Math.Max(0, mount.LaunchStep.InFlight - mount.LaunchReserved);
        mount.LaunchStep.Fired += mount.LaunchReserved;
    }
    mount.LaunchReserved = 0;
    mount.Fault = FaultType.None;
    Echo(mount.Name + " separation confirmed; holding doors open.");
}

void RunBuildState(MissileMount mount)
{
    if (tickCounter % TICKS_BUILD_CHECK != 0) return;
    if (!mount.LoadedType.HasValue) { FaultMount(mount, "Build has no missile type"); return; }

    var proj = mount.ActiveProjector;
    if (proj == null || !proj.IsFunctional) { EnterFault(mount, FaultType.BuildFault, "Build projector missing or nonfunctional", false); return; }

    proj.Enabled = true;
    SetWelders(mount, true);

    if (!BuildProjectorHasProjection(mount))
    {
        EnterFault(mount, FaultType.BuildFault, "BUILD FAULT — selected " + TYPE_CODE[mount.LoadedType.Value] + " projector has no valid blueprint/projection", false);
        return;
    }

    if (proj.RemainingBlocks > 0) return;

    if (proj.RemainingBlocks == 0)
    {
        if (mount.HasMergeConvention && !MergeConnectedLive(mount))
        {
            if (mount.MergeWaitStartTick < 0) mount.MergeWaitStartTick = tickCounter;
            if (tickCounter - mount.MergeWaitStartTick >= TICKS_BUILD_COMPLETE_MERGE_TIMEOUT)
            {
                proj.Enabled = false;
                SetWelders(mount, false);
                EnterFault(mount, FaultType.BuildFault, "BUILD COMPLETE, MERGE NOT CONNECTED - Check projection alignment and cradle merge block", false);
            }
            return;
        }
        proj.Enabled = false;
        SetWelders(mount, false);
        mount.State = MountState.LaunchReady;
        mount.ManualBuild = false; // build origin must not permanently block later mission use
        mount.Fault = FaultType.None;
        mount.FaultReason = mount.HasMergeConvention ? "" : "Ready from intentional completed build; add merge blocks for reliable physical confirmation.";
        Echo(mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] LAUNCH READY");
    }
}

void RunLaunchingState(MissileMount mount)
{
    if (mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) == 0)
    {
        EnterFault(mount, FaultType.SeparationFault, "SEPARATION FAULT - merge group has no functional merge blocks; shared doors held open", true);
        return;
    }
    bool separated = mount.MergeGroupConfigured ? !MergeConnectedLive(mount) : (tickCounter - mount.StateTick >= TICKS_POST_LAUNCH);
    if (separated)
    {
        ConfirmSeparation(mount);
        return;
    }
    if (tickCounter - mount.StateTick > TICKS_SEPARATION_TIMEOUT)
    {
        EnterFault(mount, FaultType.SeparationFault, "SEPARATION FAULT - missile may obstruct doors; shared doors held open", true);
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
    mount.Fault = FaultType.None;
    mount.LaunchReserved = 0;
    mount.FaultReserved = 0;
    mount.OrphanedFaultReserved = 0;
    mount.LaunchStep = null;
    mount.FaultReason = "";
}

void ConvertLaunchReservationToFault(MissileMount mount)
{
    if (mount.LaunchReserved <= 0) return;
    if (mount.LaunchStep != null)
    {
        mount.LaunchStep.InFlight = Math.Max(0, mount.LaunchStep.InFlight - mount.LaunchReserved);
        mount.LaunchStep.FaultReserved += mount.LaunchReserved;
        mount.FaultReserved += mount.LaunchReserved;
    }
    else
    {
        mount.OrphanedFaultReserved += mount.LaunchReserved;
    }
    mount.LaunchReserved = 0;
}

void EnterFault(MissileMount mount, FaultType fault, string reason, bool holdDoorsOpen)
{
    bool sameFault = mount.State == MountState.FaultedLoaded && mount.Fault == fault && mount.FaultReason == reason;
    mount.State = MountState.FaultedLoaded;
    mount.Fault = fault;
    mount.FaultReason = reason;
    mount.MergeWaitStartTick = -1;
    SetWelders(mount, false);
    foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
    ConvertLaunchReservationToFault(mount);
    if (holdDoorsOpen)
    {
        OpenSharedDoors();
        foreach (var door in mount.Doors) door.OpenDoor();
    }
    string prefix = fault == FaultType.SeparationFault ? "SEPARATION FAULT" : (fault == FaultType.LaunchCommandFault ? "LAUNCH COMMAND FAULT" : (fault == FaultType.BuildFault ? "BUILD FAULT" : (fault == FaultType.DoorFault ? "DOOR FAULT" : "LOAD FAULT")));
    if (!sameFault)
    {
        debugErrors.Insert(0, "[" + mount.Name + "] " + prefix + ": " + reason);
        Echo(mount.Name + ": " + prefix + " - " + reason);
    }
}

void FaultMount(MissileMount mount, string reason)
{
    EnterFault(mount, FaultType.UnknownLoadedFault, reason, false);
}

void ReleaseFaultReservation(MissileMount mount, bool countAsFired)
{
    if (mount.LaunchStep == null || mount.FaultReserved <= 0) return;
    int n = mount.FaultReserved;
    mount.LaunchStep.FaultReserved = Math.Max(0, mount.LaunchStep.FaultReserved - n);
    if (countAsFired) mount.LaunchStep.Fired += n;
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

bool SharedDoorsHardwareOk()
{
    var grp = GridTerminalSystem.GetBlockGroupWithName(SHARED_DOOR_GROUP);
    if (grp == null) return false;
    var doors = new List<IMyDoor>();
    grp.GetBlocksOfType(doors);
    return doors.Count > 0 && doors.All(d => d != null && d.IsFunctional);
}

bool MountDoorsHardwareOk(MissileMount mount)
{
    return mount.Doors.Count == 0 || mount.Doors.All(d => d != null && d.IsFunctional);
}

void EnterDoorFault(string reason)
{
    if (launchSafetyFault && launchSafetyFaultReason == reason && siloState == SiloState.DoorFault) return;
    launchSafetyFault = true;
    launchSafetyFaultReason = reason;
    siloState = SiloState.DoorFault;
    siloStateTick = tickCounter;
    debugErrors.Insert(0, "[DOOR FAULT] " + reason);
    Echo("DOOR FAULT: " + reason);
}

bool DoorListInState(List<IMyDoor> doors, DoorStatus status)
{
    if (doors == null) return false;
    foreach (var d in doors)
        if (d == null || !d.IsFunctional || d.Status != status) return false;
    return true;
}

bool SharedDoorsInState(DoorStatus status)
{
    var grp = GridTerminalSystem.GetBlockGroupWithName(SHARED_DOOR_GROUP);
    if (grp == null) return false;
    var doors = new List<IMyDoor>();
    grp.GetBlocksOfType(doors);
    if (doors.Count == 0) return false;
    return DoorListInState(doors, status);
}

bool SharedDoorsClosed()
{
    return SharedDoorsInState(DoorStatus.Closed);
}

bool AllConstructionDoorsClosed()
{
    if (!SharedDoorsClosed()) return false;
    foreach (var m in mounts)
        if (m.Doors.Count > 0 && !DoorListInState(m.Doors, DoorStatus.Closed)) return false;
    return true;
}

bool RequiredLaunchDoorsOpen()
{
    if (!SharedDoorsInState(DoorStatus.Open)) return false;
    if (missionQueue.Count == 0) return true;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (var m in mounts)
    {
        if (m.State != MountState.LaunchReady || !m.LoadedType.HasValue) continue;
        if (!active.Any(s => s.Type == m.LoadedType.Value && s.RemainingDemand > 0)) continue;
        if (m.Doors.Count > 0 && !DoorListInState(m.Doors, DoorStatus.Open)) return false;
    }
    return true;
}

void OpenDoorsForLaunchDemand()
{
    OpenSharedDoors();
    if (missionQueue.Count == 0) return;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (var m in mounts)
    {
        if (m.State != MountState.LaunchReady || !m.LoadedType.HasValue) continue;
        if (!active.Any(s => s.Type == m.LoadedType.Value && s.RemainingDemand > 0)) continue;
        foreach (var door in m.Doors) if (door.IsFunctional) door.OpenDoor();
    }
}

bool SiloIdleWithDoorsClosed()
{
    return siloState == SiloState.Idle && AllConstructionDoorsClosed();
}

bool CanClearDoorFault(out string reason)
{
    reason = "";
    if (!SharedDoorsHardwareOk()) { reason = "shared door group missing or damaged"; return false; }
    foreach (var m in mounts)
        if (!MountDoorsHardwareOk(m)) { reason = m.Name + " mount door damaged/nonfunctional"; return false; }
    if (RequiresDoorsHeldOpen()) { reason = "unresolved launch safety fault requires doors held open"; return false; }
    if (AllConstructionDoorsClosed() || RequiredLaunchDoorsOpen()) return true;
    reason = "doors are neither all safely closed nor open for an existing launch";
    return false;
}

bool RequiresDoorsHeldOpen()
{
    return mounts.Any(m => m.State == MountState.FaultedLoaded &&
        (m.Fault == FaultType.SeparationFault || m.Fault == FaultType.ReloadRecoveryFault ||
         (m.OrphanedFaultReserved > 0) || (m.FaultReserved > 0 && m.LaunchStep != null)));
}

void RecommandDoorsOpenForSafetyHold()
{
    OpenSharedDoors();
    foreach (var m in mounts)
        if (m.State == MountState.FaultedLoaded)
            foreach (var d in m.Doors) if (d.IsFunctional) d.OpenDoor();
}

bool HasLaunchReadyForDemand()
{
    if (missionQueue.Count == 0) return false;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (var step in active)
    {
        if (step.RemainingDemand <= 0) continue;
        if (mounts.Any(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type)) return true;
    }
    return false;
}

void RunSiloLogic()
{
    if (RequiresDoorsHeldOpen())
    {
        RecommandDoorsOpenForSafetyHold();
        UpdateMissionBlocks();
        return;
    }

    if (SiloIdleWithDoorsClosed()) AssignBuilds();
    UpdateMissionBlocks();

    bool launching = mounts.Any(m => m.State == MountState.Launching);
    bool postLaunch = mounts.Any(m => m.State == MountState.PostLaunch);
    bool readyForMission = HasLaunchReadyForDemand();
    bool separationFault = mounts.Any(m => m.State == MountState.FaultedLoaded && m.Fault == FaultType.SeparationFault);

    switch (siloState)
    {
        case SiloState.Idle:
            if (readyForMission || launching)
            {
                OpenDoorsForLaunchDemand();
                siloState = SiloState.OpeningDoors;
                siloStateTick = tickCounter;
            }
            else if (postLaunch)
            {
                OpenSharedDoors();
                siloState = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.OpeningDoors:
            if (!SharedDoorsHardwareOk()) { EnterDoorFault("Shared launch doors missing or damaged while opening."); break; }
            if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN_TIMEOUT && !RequiredLaunchDoorsOpen())
            { EnterDoorFault("Required shared or mount launch doors failed to open before timeout."); break; }
            if (RequiredLaunchDoorsOpen() && tickCounter - siloStateTick >= TICKS_DOOR_OPEN)
            {
                if (!PrelaunchTimerReady() || !TriggerPreLaunchTimer()) { EnterDoorFault("Prelaunch timer missing or damaged before launch."); break; }
                siloState = SiloState.PreLaunch;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.PreLaunch:
            if (!PrelaunchTimerReady()) { EnterDoorFault("Prelaunch timer damaged during prelaunch delay."); break; }
            if (tickCounter - siloStateTick >= TICKS_PRELAUNCH)
            {
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.Active:
            AttemptFires();
            if (!readyForMission && !launching && !postLaunch)
            {
                siloState = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.DoorFault:
            break;

        case SiloState.ClosingDoors:
            if (readyForMission || launching)
            {
                OpenSharedDoors();
                siloState = SiloState.OpeningDoors;
                siloStateTick = tickCounter;
            }
            else if (separationFault)
            {
                OpenSharedDoors();
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            else if (tickCounter - siloStateTick >= TICKS_DOOR_CLOSE_TIMEOUT && !AllConstructionDoorsClosed())
            {
                EnterDoorFault("Required construction doors failed to close before timeout.");
            }
            else if (tickCounter - siloStateTick >= TICKS_POST_SEPARATION_HOLD)
            {
                CloseSharedDoors();
                if (AllConstructionDoorsClosed()) siloState = SiloState.Idle;
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
        needed[step.Type] += step.RemainingDemand;
    }
    foreach (MissileType t in needed.Keys.ToList()) allocated[t] = 0;

    foreach (var mount in mounts)
        if ((mount.State == MountState.Building || mount.State == MountState.LaunchReady || mount.State == MountState.Launching || mount.State == MountState.PostLaunch)
            && mount.LoadedType.HasValue && needed.ContainsKey(mount.LoadedType.Value))
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
        else if (mounts.Any(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type)) step.Status = "LaunchReady";
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
    mount.MergeWaitStartTick = -1;
    SetWelders(mount, true);
    SaveMountStorage();
    Echo(mount.Name + " building [" + TYPE_CODE[type] + "]" + (manual ? " (manual)" : ""));
}

void AttemptFires()
{
    if (missionQueue.Count == 0 || siloState != SiloState.Active) return;
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
        if (mount.State != MountState.LaunchReady || !mount.LoadedType.HasValue) continue;
        var step = active.FirstOrDefault(s => s.Type == mount.LoadedType.Value && !s.Complete && s.RemainingDemand > 0);
        if (step == null) continue;

        mount.LaunchStep = step;
        mount.LaunchReserved = 1;
        step.InFlight++;

        string validation;
        if (!ValidateLaunchReadyMount(mount, out validation))
        {
            EnterFault(mount, FaultType.LaunchCommandFault, validation, false);
            step.Status = "Faulted";
            step.BlockReason = mount.Name + ": LAUNCH COMMAND FAULT - " + validation;
            continue;
        }

        bool fired = mount.PB.TryRun(mission.GPS);
        if (fired)
        {
            mount.State = MountState.Launching;
            mount.StateTick = tickCounter;
            step.Status = "Launching";
            mount.ManualBuild = false;
            SaveMountStorage();
            Echo("Started launch " + mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] → " + ParseGPSName(mission.GPS));
        }
        else
        {
            EnterFault(mount, FaultType.LaunchCommandFault, "TryRun failed; missile still present", false);
            step.Status = "Faulted";
            step.BlockReason = mount.Name + ": LAUNCH COMMAND FAULT - TryRun failed; missile still present";
        }
    }

    if (mission.Complete && !mounts.Any(m => m.State == MountState.Launching || m.State == MountState.PostLaunch))
    {
        missionQueue.RemoveAt(0);
        Echo("Mission complete: " + ParseGPSName(mission.GPS));
    }
}

bool ValidateLaunchReadyMount(MissileMount mount, out string reason)
{
    reason = "";
    if (!mount.PBValid) { reason = "missile PB missing or not functional"; return false; }
    if (!mount.LoadedType.HasValue) { reason = "loaded missile type is unknown"; return false; }
    IMyProjector proj;
    if (!mount.Projectors.TryGetValue(mount.LoadedType.Value, out proj) || proj == null || !proj.IsFunctional)
    { reason = "active projector/build record does not match loaded type"; return false; }
    if (mount.MergeGroupConfigured && GetFunctionalMergeCount(mount) == 0)
    { reason = "merge verification hardware is configured but damaged/nonfunctional"; return false; }
    if (mount.HasMergeConvention && !MergeConnectedLive(mount))
    { reason = "merge verification does not show a physical missile present"; return false; }
    if (!RequiredLaunchDoorsOpen())
    { reason = "required shared or mount doors are not open/functioning"; return false; }
    if (siloState != SiloState.Active)
    { reason = "doors/prelaunch sequence has not completed"; return false; }
    return true;
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
bool PrelaunchTimerReady()
{
    var t = GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER) as IMyTimerBlock;
    return t != null && t.IsFunctional;
}

bool TriggerPreLaunchTimer()
{
    var t = GridTerminalSystem.GetBlockWithName(PRELAUNCH_TIMER) as IMyTimerBlock;
    if (t == null || !t.IsFunctional) return false;
    t.Trigger();
    return true;
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
    string status = CanAcceptMissionsNow() ? "Ready" : "Busy";
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

        if (!SiloIdleWithDoorsClosed()) { Echo("Manual build rejected: silo doors are not closed and idle."); return; }

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

        if (!SiloIdleWithDoorsClosed())
        { Echo("Manual build rejected: silo doors are not closed and idle."); return; }
        if (!mount.CanAcceptBuild)
        { Echo($"build: {mount.Name} is not Empty (currently {mount.State}) — cannot start a manual build."); return; }
        if (!mount.Supports(type))
        { Echo($"build: {mount.Name} does not support {TYPE_CODE[type]}."); return; }

        StartBuild(mount, type, true);
        Echo($"Manual build started: {mount.Name} → [{TYPE_CODE[type]}]");
        return;
    }



    if (l.StartsWith("resolvemount:"))
    {
        string rest = arg.Substring(13).Trim();
        int sep = rest.LastIndexOf(':');
        if (sep < 0) { Echo("Usage: resolvemount:<mount name>:notlaunched|departed|unknown"); return; }
        string mountName = rest.Substring(0, sep).Trim();
        string outcome = rest.Substring(sep + 1).Trim().ToLower();
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(mountName, StringComparison.OrdinalIgnoreCase));
        if (mount == null) { Echo("resolvemount: unknown mount '" + mountName + "'"); return; }
        if (mount.State != MountState.FaultedLoaded) { Echo("resolvemount: " + mount.Name + " is not FaultedLoaded."); return; }
        if (outcome == "unknown")
        {
            Echo("resolvemount: " + mount.Name + " kept faulted; mission reservation remains blocked.");
            return;
        }
        if (outcome != "notlaunched" && outcome != "departed")
        { Echo("Usage: resolvemount:<mount name>:notlaunched|departed|unknown"); return; }
        bool hasLiveReservation = mount.LaunchStep != null && mount.FaultReserved > 0;
        bool hasOrphanedReservation = mount.OrphanedFaultReserved > 0;
        if (!hasLiveReservation && !hasOrphanedReservation)
        { Echo("resolvemount: " + mount.Name + " has no launch reservation; use resetmount after physical inspection."); return; }

        bool firedUpdated = false;
        if (hasLiveReservation)
        {
            ReleaseFaultReservation(mount, outcome == "departed");
            firedUpdated = outcome == "departed";
        }
        else if (hasOrphanedReservation)
        {
            mount.OrphanedFaultReserved = 0;
            Echo("resolvemount: cold reload lost original mission context; no mission Fired counter can be updated.");
        }
        foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
        SetWelders(mount, false);
        if (outcome == "departed")
        {
            mount.State = MountState.PostLaunch;
            mount.StateTick = tickCounter;
            mount.Fault = FaultType.None;
            mount.FaultReason = "Operator confirmed departed after accounting fault; post-launch hold active.";
            siloState = SiloState.ClosingDoors;
            siloStateTick = tickCounter;
            Echo("resolvemount: " + mount.Name + (firedUpdated ? " confirmed departed; Fired incremented and post-launch hold started." : " confirmed departed; orphaned reservation cleared with no mission counter update."));
        }
        else
        {
            mount.State = MountState.Empty;
            mount.LoadedType = null;
            mount.BuildOrdered = false;
            mount.ManualBuild = false;
            mount.Fault = FaultType.None;
            mount.FaultReason = "Operator confirmed not launched and bay physically cleared/repaired.";
            if (!AllConstructionDoorsClosed()) { siloState = SiloState.ClosingDoors; siloStateTick = tickCounter; }
            Echo("resolvemount: " + mount.Name + " confirmed not launched; reservation released and mount set Empty.");
        }
        mount.FaultReserved = 0;
        mount.OrphanedFaultReserved = 0;
        mount.LaunchReserved = 0;
        mount.LaunchStep = null;
        SaveMountStorage();
        return;
    }

    if (l.StartsWith("resetmount:"))
    {
        string mountName = arg.Substring(11).Trim();
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(mountName, StringComparison.OrdinalIgnoreCase));
        if (mount == null) { Echo("resetmount: unknown mount '" + mountName + "'"); return; }
        if (mount.State == MountState.Unavailable) { Echo("resetmount: " + mount.Name + " is unavailable; run refresh after repairs."); return; }
        if (mount.LaunchReserved > 0 || mount.FaultReserved > 0 || mount.OrphanedFaultReserved > 0) { Echo("resetmount: " + mount.Name + " has a reserved mission shot; use resolvemount:<mount>:notlaunched|departed|unknown."); return; }
        foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
        SetWelders(mount, false);
        mount.State = MountState.Empty;
        mount.LoadedType = null;
        mount.BuildOrdered = false;
        mount.ManualBuild = false;
        mount.Fault = FaultType.None;
        mount.LaunchReserved = 0;
        mount.OrphanedFaultReserved = 0;
        mount.LaunchStep = null;
        mount.FaultReason = "Reset by operator. WARNING: operator must verify bay is physically clear before rebuilding; resetmount does not prove safe clearance.";
        if (!AllConstructionDoorsClosed())
        {
            CloseSharedDoors();
            siloState = SiloState.ClosingDoors;
            siloStateTick = tickCounter;
        }
        else siloState = SiloState.Idle;
        SaveMountStorage();
        Echo("resetmount: " + mount.Name + " set to Empty. Verify physical bay is clear.");
        return;
    }

    if (l == "cleardoorfault")
    {
        string reason;
        if (!CanClearDoorFault(out reason)) { Echo("cleardoorfault rejected: " + reason); return; }
        launchSafetyFault = false;
        launchSafetyFaultReason = "";
        siloState = AllConstructionDoorsClosed() ? SiloState.Idle : SiloState.OpeningDoors;
        siloStateTick = tickCounter;
        Echo("Door fault cleared after live validation.");
        return;
    }

    switch (l)
    {
        case "refresh":
            RefreshMountsSafely(); break;
        case "closedoors":
            if (RequiresDoorsHeldOpen()) { Echo("closedoors rejected: unresolved launch-safety fault requires doors held open."); break; }
            CloseSharedDoors(); break;
        case "forceclosedoors":
            Echo("FORCE CLOSE WARNING: operator asserts physical inspection is complete."); CloseSharedDoors(); break;
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
    sb.AppendLine($"Operational: {(CanAcceptMissionsNow() ? "YES" : "NO")}");
    sb.AppendLine($"Shared: {sharedInfrastructureStatus}");
    sb.AppendLine($"Serviceable Mounts: {mounts.Count(m => IsServiceableMount(m))}/{mounts.Count}");
    sb.AppendLine($"Silo:   {siloState}");
    if (launchSafetyFault) sb.AppendLine($"DoorFault: {launchSafetyFaultReason}");
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
        string mergeStr = !m.MergeGroupConfigured ? " [merge:not configured]" : (m.FunctionalMergeCount == 0 ? " [merge:CONFIGURED/NO FUNCTIONAL]" : (m.MergeConfirmsPresent ? " [merge:CONNECTED]" : " [merge:DISCONNECTED]"));
        sb.AppendLine($"{m.Name}: {m.State.ToString().ToUpper()} {typeStr}{remStr}{mergeStr}");
        sb.AppendLine($"  Supports: {SupportedTypesText(m)}");
        sb.AppendLine($"  Fault: {m.Fault} Reserved:{m.FaultReserved}");
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
    if (launchSafetyFault) sb.AppendLine("DOOR FAULT: " + launchSafetyFaultReason);

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
