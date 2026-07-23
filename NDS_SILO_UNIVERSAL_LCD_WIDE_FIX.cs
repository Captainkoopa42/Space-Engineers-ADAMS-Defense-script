// MISSION INTAKE FIX: Hub orders are queued even while the silo is Busy/faulted.
// CLEAN PB SOURCE - ORIGINAL TYPE-PROJECTOR CONTROL RESTORED - PASTE THIS ENTIRE FILE INTO THE SPACE ENGINEERS PROGRAMMABLE BLOCK
// =====================================================
// NDS UNIT COMMUNICATION v7.2 - Silo Executor (Original Type-Projector Control + Safety Fixes)
// Multi-Type Strike Package Architecture (MTSPA) — Silo Layer
// =====================================================
//
// MISSILE TYPES (5 — matches MTSPA spec):
//   Normal | Nuke | Decoy | AirTurret | PillarTurret
//
// BLOCK NAMING — for a mount named "ICBM 1" (spec word order: Type before "Projector"):
//   PB:                "ICBM 1 Programmable Block"      (shared, type-agnostic)
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
const string PB_SUFFIX        = " Programmable Block";
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
const string STATUS_LCD_NAME    = "Silo Status LCD";  // persistent compact status history

// UNIVERSAL TEXT-SURFACE ROUTING:
// Any block that exposes an LCD/text surface can display script output by adding
// one or more tags anywhere in its block name:
//   [NDS:LAUNCH]       dedicated LCD or surface 0
//   [NDS:DEBUG]        dedicated LCD or surface 0
//   [NDS:STATUS]       dedicated LCD or surface 0
//   [NDS:LAUNCH:2]     surface index 2 on a cockpit, control seat, PB, etc.
// Multiple tags may be placed on one multi-surface block, for example:
//   Main Cockpit [NDS:LAUNCH:0] [NDS:DEBUG:1]
// The three legacy exact block names above remain supported for compatibility.


// ── TIMING (Update10 ticks, ~0.167s each) ─────────────
const int TICKS_DOOR_OPEN    = 60;
const int TICKS_PRELAUNCH    = 60;
const int TICKS_POST_LAUNCH  = 180;   // legacy fallback; kept for operator familiarity
const int TICKS_BUILD_CHECK  = 30;
const int TICKS_SEPARATION_TIMEOUT = 360;
const int TICKS_POST_SEPARATION_HOLD = 240;  // about 4 seconds total; adds ~3 seconds of clearance before doors close
// tickCounter advances by 10 on every Update10 call, so 60 counter units ~= 1 second.
// Hangar doors can take longer than the old 180-unit (~3 second) timeout.
const int TICKS_DOOR_OPEN_TIMEOUT = 1200;  // ~20 seconds
const int TICKS_DOOR_CLOSE_TIMEOUT = 1200; // ~20 seconds
const int TICKS_BUILD_COMPLETE_MERGE_TIMEOUT = 180;
const int TICKS_PROJECTOR_START_TIMEOUT = 180;


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
    public int         BuildDoorWaitStartTick = -1;
    // Tracks whether this build has ever shown a valid projection. RemainingBlocks
    // is zero both for a completed projection and for no projection, so this flag
    // prevents a missing/disabled projector from being mistaken for completion.
    public bool        ProjectionWasObserved = false;
    public int         ProjectionMissingStartTick = -1;
    public bool        MergeGroupConfigured = false;
    public int         FunctionalMergeCount = 0;
    // "MergeWasConnectedAtLaunch" records positive physical presence when
    // the prelaunch sequence is committed, before the timer is triggered.
    public bool        MergeWasConnectedAtLaunch = false;
    public int         MergeBlockCountAtLaunch = 0;
    // True when the prelaunch timer intentionally released the merge before
    // TryRun. In that case separation uses the timed fallback instead of
    // treating the already-disconnected merge as instant departure proof.
    public bool        MergeReleasedBeforeTryRun = false;
    public string      ValidationStatus = "Not scanned";

    public IMyProgrammableBlock                   PB;
    public Dictionary<MissileType, IMyProjector>   Projectors = new Dictionary<MissileType, IMyProjector>();
    public List<IMyShipWelder>                     Welders    = new List<IMyShipWelder>();
    public List<IMyDoor>                            Doors      = new List<IMyDoor>();
    public List<IMyShipMergeBlock>                  MergeBlocks = new List<IMyShipMergeBlock>();

    public IMyProjector ActiveProjector =>
        LoadedType.HasValue && Projectors.ContainsKey(LoadedType.Value)
        ? Projectors[LoadedType.Value] : null;

    public bool PBValid { get { return PB != null && PB.IsFunctional; } }

    // The missile PB is part of the projected missile. In a true build-on-command
    // empty bay it does not exist yet, so it must NOT be required for construction.
    public int FunctionalWelderCount { get { return Welders.Count(w => w != null && w.IsFunctional); } }

    public bool HasBuildHardware
    {
        get
        {
            return State != MountState.Unavailable &&
                   FunctionalWelderCount > 0 &&
                   Projectors.Any(kv => kv.Value != null && kv.Value.IsFunctional);
        }
    }

    public bool HasLaunchHardware
    {
        get
        {
            return HasBuildHardware && PBValid;
        }
    }

    // Kept for existing readiness/diagnostic code. "Hardware capable" here means
    // capable of accepting a build, not that a missile PB already exists.
    public bool IsHardwareCapable { get { return HasBuildHardware; } }

    public bool IsUsable { get { return HasBuildHardware; } }

    public bool IsAvailableBuildMount { get { return State == MountState.Empty && HasBuildHardware; } }

    public bool IsOccupied { get { return State == MountState.Building || State == MountState.LaunchReady || State == MountState.Launching || State == MountState.PostLaunch || State == MountState.FaultedLoaded; } }

    public bool IsReadyWith(MissileType type) { return State == MountState.LaunchReady && LoadedType == type && PBValid; }

    public bool CanAcceptBuild { get { return IsAvailableBuildMount; } }

    public bool Supports(MissileType type)
    {
        return HasBuildHardware && Projectors.ContainsKey(type) &&
               Projectors[type] != null && Projectors[type].IsFunctional;
    }

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
HashSet<string> selectedLaunchMounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

class DisplayTarget
{
    public IMyTerminalBlock Block;
    public IMyTextSurface Surface;
    public int SurfaceIndex;
}

List<DisplayTarget> launchDisplays = new List<DisplayTarget>();
List<DisplayTarget> debugDisplays  = new List<DisplayTarget>();
List<DisplayTarget> statusDisplays = new List<DisplayTarget>();


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

    FindAllMountBlocks(true);
    DiscoverDisplaySurfaces(true);
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
            // Always drain and parse Hub missions while linked. Operational
            // readiness controls build/launch execution, not message receipt.
            // This prevents a faulted silo from silently ignoring an order that
            // should remain queued until the operator repairs or resets it.
            CheckForIncomingMessages();
            isReadyForOperation = CanAcceptMissionsNow();
            SendStatusToHub();
        }
        isReadyForOperation = CanAcceptMissionsNow();
        RunMountStates();
        isReadyForOperation = CanAcceptMissionsNow();
        RunSiloLogic();

        // Re-scan LCD/text-surface routing every ~10 seconds so newly renamed
        // displays begin working without recompiling or running refresh.
        if (tickCounter % 600 == 0)
            DiscoverDisplaySurfaces(false);

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
        debugErrors.Insert(1, "Ready rule: shared infrastructure OK and at least one mount with functional projectors/welders. Empty bays do not require a missile PB.");
    }

    SaveMountStorage();
    return isReadyForOperation;
}


// ── BLOCK DISCOVERY & DIAGNOSTICS ──────────────────────

// ── FLEXIBLE BLOCK-NAME MATCHING ───────────────────────
// Space Engineers names the block type "Programmable Block". Older versions
// of this script searched for "Programming Block". These helpers accept both,
// and ignore spaces, punctuation, and optional '#' characters in mount names.
string NormalizeBlockName(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    var sb = new System.Text.StringBuilder();
    foreach (char c in value.ToLowerInvariant())
        if (char.IsLetterOrDigit(c)) sb.Append(c);
    return sb.ToString();
}

bool BlockNameMatchesMount(string blockName, string mountName)
{
    return NormalizeBlockName(blockName).Contains(NormalizeBlockName(mountName));
}

bool IsMissileProgrammableBlockName(string blockName, string mountName)
{
    // GetBlocksOfType already guarantees this is a programmable block. Matching
    // the mount name is enough and also accepts names such as "ICBM 1 PB".
    return BlockNameMatchesMount(blockName, mountName);
}

bool BlockNameContainsSuffix(string blockName, string suffix)
{
    return NormalizeBlockName(blockName).Contains(NormalizeBlockName(suffix));
}

void FindAllMountBlocks(bool safeStartupShutdown)
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
        var reasons = new List<string>();

        var pbs = new List<IMyProgrammableBlock>();
        GridTerminalSystem.GetBlocksOfType(pbs, b =>
            (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b != Me && b.IsFunctional &&
            IsMissileProgrammableBlockName(b.CustomName, name));
        if (pbs.Count > 0)
            mount.PB = pbs[0];
        else
            debugErrors.Add("[" + name + "] INFO: Missile PB not present yet (normal for an Empty build-on-command bay).");

        foreach (MissileType mtype in ALL_TYPES)
        {
            string suffix = PROJECTOR_SUFFIX[mtype];
            var projs = new List<IMyProjector>();
            GridTerminalSystem.GetBlocksOfType(projs, b =>
                (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b.IsFunctional &&
                BlockNameMatchesMount(b.CustomName, name) &&
                BlockNameContainsSuffix(b.CustomName, suffix));
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

        // Prefer the historical block group convention, but also discover
        // directly named blocks such as "ICBM 1 Merge Block".
        var mg = GridTerminalSystem.GetBlockGroupWithName(name + MERGE_GROUP_SUF);
        if (mg == null) mg = GridTerminalSystem.GetBlockGroupWithName(name + " Merge Block");
        if (mg != null) mg.GetBlocksOfType(mount.MergeBlocks);

        if (mount.MergeBlocks.Count == 0)
        {
            var namedMerges = new List<IMyShipMergeBlock>();
            GridTerminalSystem.GetBlocksOfType(namedMerges, b =>
                (allowSubgrids || b.CubeGrid == Me.CubeGrid) &&
                BlockNameMatchesMount(b.CustomName, name) &&
                NormalizeBlockName(b.CustomName).Contains("merge"));
            foreach (var merge in namedMerges)
                if (!mount.MergeBlocks.Contains(merge)) mount.MergeBlocks.Add(merge);
        }

        mount.MergeGroupConfigured = mount.MergeBlocks.Count > 0;
        if (mount.MergeGroupConfigured)
        {
            mount.FunctionalMergeCount = mount.MergeBlocks.Count(m => m != null && m.IsFunctional);
            if (GetFunctionalMergeCount(mount) == 0) reasons.Add("merge verification found but no functional merge blocks");
        }

        if (safeStartupShutdown)
        {
            foreach (var kv in mount.Projectors) kv.Value.Enabled = false;
            SetWelders(mount, false);
        }

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
        debugErrors.Add("  Missile PB: " + (mount.PBValid ? mount.PB.CustomName : "not present (expected while Empty)"));
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

bool AllConfiguredMergeHardwareFunctional(MissileMount mount)
{
    if (!mount.MergeGroupConfigured) return true;
    return mount.MergeBlocks.Count > 0 && mount.MergeBlocks.All(m => m != null && m.IsFunctional);
}

bool MergeVerificationHealthyForLaunching(MissileMount mount)
{
    if (!mount.MergeGroupConfigured) return true;
    return mount.MergeWasConnectedAtLaunch &&
           mount.MergeBlockCountAtLaunch > 0 &&
           mount.MergeBlocks.Count == mount.MergeBlockCountAtLaunch &&
           AllConfiguredMergeHardwareFunctional(mount);
}

bool CanAcceptMissionsNow()
{
    return sharedInfrastructureValid && !launchSafetyFault && SharedDoorsHardwareOk() && PrelaunchTimerReady() && mounts.Any(m => IsServiceableMount(m));
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
    public int BuildDoorWaitStartTick;
    public bool ProjectionWasObserved;
    public int ProjectionMissingStartTick;
    public bool MergeWasConnectedAtLaunch;
    public int MergeBlockCountAtLaunch;
    public bool MergeReleasedBeforeTryRun;
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
            BuildDoorWaitStartTick = m.BuildDoorWaitStartTick,
            ProjectionWasObserved = m.ProjectionWasObserved,
            ProjectionMissingStartTick = m.ProjectionMissingStartTick,
            MergeWasConnectedAtLaunch = m.MergeWasConnectedAtLaunch,
            MergeBlockCountAtLaunch = m.MergeBlockCountAtLaunch,
            MergeReleasedBeforeTryRun = m.MergeReleasedBeforeTryRun,
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
    FindAllMountBlocks(false);
    DiscoverDisplaySurfaces(true);
    Storage = storageBefore;
    LoadMountStorage(false);
    selectedLaunchMounts.Clear();
    ReconcileMountSnapshots(snapshots);
    NormalizeMountOutputsAfterRefresh();
    ValidateRequiredBlocks();
    if (launchSafetyFault)
    {
        string clearReason;
        if (CanClearDoorFault(out clearReason))
        {
            launchSafetyFault = false;
            launchSafetyFaultReason = "";
            ResumeAfterDoorFault();
        }
    }
    PrintStatus();
}

void SetProjectorSelection(MissileMount mount, MissileType? selectedType, bool enableSelected)
{
    foreach (var kv in mount.Projectors)
    {
        bool shouldEnable = enableSelected && selectedType.HasValue &&
            kv.Key == selectedType.Value && kv.Value != null && kv.Value.IsFunctional;
        kv.Value.Enabled = shouldEnable;
    }
}

bool ProjectorShowsBlueprint(IMyProjector projector)
{
    if (projector == null || !projector.IsFunctional) return false;
    return projector.IsProjecting || projector.TotalBlocks > 0 || projector.RemainingBlocks > 0;
}

void NormalizeMountOutputsAfterRefresh()
{
    foreach (var mount in mounts)
    {
        bool runBuild = mount.State == MountState.Building &&
            mount.LoadedType.HasValue && mount.ActiveProjector != null &&
            ConstructionDoorsClosedForMount(mount) && !LaunchPathOwnsDoors();

        // Original operating model: only the selected type projector is enabled.
        // The projector already stores its blueprint; it does not need an
        // IsProjecting/TotalBlocks pre-validation gate before welding starts.
        SetProjectorSelection(mount, mount.LoadedType, runBuild);
        SetWelders(mount, runBuild);
    }
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
        mount.BuildDoorWaitStartTick = old.BuildDoorWaitStartTick;
        mount.ProjectionWasObserved = old.ProjectionWasObserved;
        mount.ProjectionMissingStartTick = old.ProjectionMissingStartTick;
        mount.MergeWasConnectedAtLaunch = old.MergeWasConnectedAtLaunch;
        mount.MergeBlockCountAtLaunch = old.MergeBlockCountAtLaunch;
        mount.MergeReleasedBeforeTryRun = old.MergeReleasedBeforeTryRun;
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
            var proj = mount.ActiveProjector;
            if (proj == null || !proj.IsFunctional)
                EnterFault(mount, FaultType.BuildFault, "Refresh found Building state but the selected projector is missing or damaged.", false);
            // Do not require IsProjecting during refresh. The projector may have
            // been deliberately paused for launch-door ownership. RunBuildState
            // re-enables it and performs the bounded projection check.
        }
        else if (old.State == MountState.LaunchReady)
        {
            if (mount.MergeGroupConfigured && !mount.MergeConfirmsPresent)
                EnterFault(mount, FaultType.UnknownLoadedFault, "LaunchReady missile lost merge physical-presence confirmation during refresh.", false);
        }
        else if (old.State == MountState.Launching)
        {
            if (mount.LaunchStep == null)
            {
                EnterFault(mount, FaultType.ReloadRecoveryFault, "Runtime refresh found Launching without a live MissionStep reservation.", true);
            }
            else if (mount.MergeGroupConfigured)
            {
                if (!MergeVerificationHealthyForLaunching(mount))
                    EnterFault(mount, FaultType.SeparationFault, "SEPARATION FAULT - merge verification hardware changed, disappeared, or became damaged during refresh; departure is unconfirmed", true);
                else if (!MergeConnectedLive(mount))
                    ConfirmSeparation(mount);
            }
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
        bool projectionObserved = parts.Length > 12 && parts[12] == "1";
        int projectionMissingTick = -1;
        if (parts.Length > 13) int.TryParse(parts[13], out projectionMissingTick);
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
            mount.ProjectionWasObserved = projectionObserved || (st == MountState.Building && ordered);
            mount.ProjectionMissingStartTick = coldStartup ? -1 : projectionMissingTick;
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
          .Append(m.StateTick).Append("|")
          .Append(m.ProjectionWasObserved ? "1" : "0").Append("|")
          .Append(m.ProjectionMissingStartTick).Append("\n");
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
    if (proj == null || !proj.IsFunctional) return false;
    return mount.ProjectionWasObserved || ProjectorShowsBlueprint(proj);
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
    mount.MergeWasConnectedAtLaunch = false;
    mount.MergeBlockCountAtLaunch = 0;
    mount.MergeReleasedBeforeTryRun = false;
    Echo(mount.Name + " separation confirmed; holding doors open.");
}

void RunBuildState(MissileMount mount)
{
    if (tickCounter % TICKS_BUILD_CHECK != 0) return;
    if (!mount.LoadedType.HasValue)
    {
        FaultMount(mount, "Build has no missile type");
        return;
    }

    var proj = mount.ActiveProjector;
    if (proj == null || !proj.IsFunctional)
    {
        SetProjectorSelection(mount, null, false);
        SetWelders(mount, false);
        EnterFault(mount, FaultType.BuildFault, "Build projector missing or nonfunctional", false);
        return;
    }

    // Another launch owns the doors. Pause this build without changing the
    // selected type or discarding its build state.
    if (LaunchPathOwnsDoors())
    {
        SetProjectorSelection(mount, mount.LoadedType, false);
        SetWelders(mount, false);
        mount.BuildDoorWaitStartTick = -1;
        return;
    }

    if (!SharedDoorsHardwareOk())
    {
        SetProjectorSelection(mount, mount.LoadedType, false);
        SetWelders(mount, false);
        EnterDoorFault("Shared construction door hardware damaged while building " + mount.Name);
        return;
    }
    if (!MountDoorsHardwareOk(mount))
    {
        SetProjectorSelection(mount, mount.LoadedType, false);
        SetWelders(mount, false);
        EnterFault(mount, FaultType.BuildFault,
            "Private mount door hardware damaged/nonfunctional while building", false);
        return;
    }
    if (!ConstructionDoorsClosedForMount(mount))
    {
        SetProjectorSelection(mount, mount.LoadedType, false);
        SetWelders(mount, false);
        if (mount.BuildDoorWaitStartTick < 0) mount.BuildDoorWaitStartTick = tickCounter;
        CloseSharedDoors();
        if (tickCounter - mount.BuildDoorWaitStartTick >= TICKS_DOOR_CLOSE_TIMEOUT)
            EnterDoorFault("Construction doors failed to close while building " + mount.Name);
        return;
    }
    mount.BuildDoorWaitStartTick = -1;

    // Restore the original proven control model:
    //   1. Every projector OFF.
    //   2. Only the projector for LoadedType ON.
    //   3. Welders ON immediately.
    // The projector already contains its blueprint, so IsProjecting and
    // TotalBlocks are not prerequisites for beginning the build.
    SetProjectorSelection(mount, mount.LoadedType, true);
    SetWelders(mount, true);

    if (proj.RemainingBlocks > 0)
    {
        mount.MergeWaitStartTick = -1;
        mount.ProjectionMissingStartTick = -1;
        return;
    }

    // RemainingBlocks == 0 can mean completed or no active projection. On your
    // merge-equipped silos, the connected merge block is the physical completion
    // authority, matching the original script's intended behavior.
    if (mount.HasMergeConvention && !MergeConnectedLive(mount))
    {
        if (mount.MergeWaitStartTick < 0) mount.MergeWaitStartTick = tickCounter;
        if (tickCounter - mount.MergeWaitStartTick >= TICKS_BUILD_COMPLETE_MERGE_TIMEOUT)
        {
            SetProjectorSelection(mount, mount.LoadedType, false);
            SetWelders(mount, false);
            EnterFault(mount, FaultType.BuildFault,
                "BUILD WAIT TIMEOUT - selected " + TYPE_CODE[mount.LoadedType.Value] +
                " projector reports zero remaining blocks but the missile merge never connected. " +
                "Verify the correct blueprint is loaded and aligned.", false);
        }
        return;
    }

    // Give the newly welded missile PB time to appear in the terminal system.
    ReScanPB(mount);
    if (!mount.PBValid)
    {
        if (mount.ProjectionMissingStartTick < 0)
            mount.ProjectionMissingStartTick = tickCounter;

        if (tickCounter - mount.ProjectionMissingStartTick >= TICKS_PROJECTOR_START_TIMEOUT)
        {
            SetProjectorSelection(mount, mount.LoadedType, false);
            SetWelders(mount, false);
            EnterFault(mount, FaultType.BuildFault,
                "BUILD COMPLETE, MISSILE PB NOT FOUND - The selected projector completed and the merge connected, " +
                "but no functional missile PB matching " + mount.Name + " was found.", false);
        }
        return;
    }

    SetProjectorSelection(mount, mount.LoadedType, false);
    SetWelders(mount, false);
    mount.MergeWaitStartTick = -1;
    mount.ProjectionMissingStartTick = -1;
    mount.ProjectionWasObserved = false;
    mount.State = MountState.LaunchReady;
    mount.ManualBuild = false;
    mount.Fault = FaultType.None;
    mount.FaultReason = mount.HasMergeConvention ? "" :
        "Ready from intentional completed build; add merge blocks for reliable physical confirmation.";
    Echo(mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] LAUNCH READY");
}


void RunLaunchingState(MissileMount mount)
{
    if (mount.MergeGroupConfigured)
    {
        if (!MergeVerificationHealthyForLaunching(mount))
        {
            EnterFault(mount, FaultType.SeparationFault, "SEPARATION FAULT - merge verification hardware changed, disappeared, or became damaged; departure is unconfirmed", true);
            return;
        }

        if (mount.MergeReleasedBeforeTryRun)
        {
            // The prelaunch timer released the merge before TryRun, so there is no
            // post-command connected-to-disconnected edge to observe. Use the same
            // bounded fallback used by non-merge missiles while holding doors open.
            if (tickCounter - mount.StateTick >= TICKS_POST_LAUNCH)
            {
                ConfirmSeparation(mount);
                return;
            }
        }
        else if (!MergeConnectedLive(mount))
        {
            // Normal case: clean connected-to-disconnected transition after TryRun.
            ConfirmSeparation(mount);
            return;
        }
    }
    else if (tickCounter - mount.StateTick >= TICKS_POST_LAUNCH)
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
    mount.MergeWasConnectedAtLaunch = false;
    mount.MergeBlockCountAtLaunch = 0;
    mount.MergeReleasedBeforeTryRun = false;
    mount.FaultReason = "";
    mount.ProjectionWasObserved = false;
    mount.ProjectionMissingStartTick = -1;
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
    mount.BuildDoorWaitStartTick = -1;
    mount.ProjectionMissingStartTick = -1;
    SetWelders(mount, false);
    SetProjectorSelection(mount, null, false);
    ConvertLaunchReservationToFault(mount);
    mount.MergeWasConnectedAtLaunch = false;
    mount.MergeBlockCountAtLaunch = 0;
    mount.MergeReleasedBeforeTryRun = false;
    selectedLaunchMounts.Remove(mount.Name);
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
    mount.PB = null;
    var pbs = new List<IMyProgrammableBlock>();
    GridTerminalSystem.GetBlocksOfType(pbs, b =>
        (allowSubgrids || b.CubeGrid == Me.CubeGrid) && b != Me && b.IsFunctional &&
        IsMissileProgrammableBlockName(b.CustomName, mount.Name));
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

bool ConstructionDoorsClosedForMount(MissileMount mount)
{
    if (!SharedDoorsClosed()) return false;
    return mount.Doors.Count == 0 || DoorListInState(mount.Doors, DoorStatus.Closed);
}

bool AllConstructionDoorsClosed()
{
    if (!SharedDoorsClosed()) return false;
    foreach (var m in mounts)
    {
        // Faulted/unavailable bays are isolated from normal construction. Their
        // private doors must not stop healthy independent bays from operating.
        if (m.State == MountState.FaultedLoaded || m.State == MountState.Unavailable) continue;
        if (m.Doors.Count > 0 && !DoorListInState(m.Doors, DoorStatus.Closed)) return false;
    }
    return true;
}

string LaunchCandidateProblem(MissileMount mount, MissileType type)
{
    if (mount == null) return "mount missing";
    if (mount.State != MountState.LaunchReady) return "state is " + mount.State;
    if (!mount.LoadedType.HasValue || mount.LoadedType.Value != type) return "loaded type does not match";
    if (!mount.PBValid) return "missile PB missing or damaged";
    IMyProjector proj;
    if (!mount.Projectors.TryGetValue(type, out proj) || proj == null || !proj.IsFunctional) return "projector missing or damaged";
    if (!MountDoorsHardwareOk(mount)) return "mount door missing or damaged";
    if (mount.MergeGroupConfigured && !AllConfiguredMergeHardwareFunctional(mount)) return "merge verification hardware missing or damaged";
    if (mount.MergeGroupConfigured && !MergeConnectedLive(mount)) return "merge not connected";
    return "";
}

bool IsEligibleLaunchCandidate(MissileMount mount, MissileType type)
{
    return string.IsNullOrEmpty(LaunchCandidateProblem(mount, type));
}

void SelectLaunchCandidates()
{
    selectedLaunchMounts.Clear();
    if (missionQueue.Count == 0) return;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (var step in active)
    {
        int need = step.RemainingDemand;
        if (need <= 0) continue;
        foreach (var name in MISSILE_MANIFEST)
        {
            if (need <= 0) break;
            var m = mounts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!IsEligibleLaunchCandidate(m, step.Type)) continue;
            if (selectedLaunchMounts.Contains(m.Name)) continue;
            selectedLaunchMounts.Add(m.Name);
            need--;
        }
    }
}

bool IsSelectedLaunchCandidate(MissileMount mount)
{
    return selectedLaunchMounts.Contains(mount.Name);
}

bool RequiredLaunchDoorsOpen()
{
    if (!SharedDoorsInState(DoorStatus.Open)) return false;
    foreach (var m in mounts)
    {
        if (!IsSelectedLaunchCandidate(m)) continue;
        if (m.Doors.Count > 0 && !DoorListInState(m.Doors, DoorStatus.Open)) return false;
    }
    return true;
}

void OpenDoorsForLaunchDemand()
{
    OpenSharedDoors();
    foreach (var m in mounts)
    {
        // Keep the private bay doors open for both committed candidates and
        // missiles that have already accepted TryRun but have not completed
        // post-launch cleanup.
        if (!IsSelectedLaunchCandidate(m) &&
            m.State != MountState.Launching &&
            m.State != MountState.PostLaunch) continue;
        foreach (var door in m.Doors) if (door.IsFunctional) door.OpenDoor();
    }
}


bool LaunchPathOwnsDoors()
{
    return siloState == SiloState.OpeningDoors || siloState == SiloState.PreLaunch || siloState == SiloState.Active ||
        mounts.Any(m => m.State == MountState.Launching || m.State == MountState.PostLaunch) || RequiresDoorsHeldOpen();
}

bool LaunchSelectionStillValid()
{
    if (selectedLaunchMounts.Count == 0) return false;
    if (missionQueue.Count == 0) return false;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (string name in selectedLaunchMounts.ToList())
    {
        var m = mounts.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (m == null || !m.LoadedType.HasValue) return false;
        var step = active.FirstOrDefault(st => st.Type == m.LoadedType.Value && st.RemainingDemand > 0);
        if (step == null || !IsEligibleLaunchCandidate(m, step.Type)) return false;
    }
    return true;
}

bool CommitSelectedLaunchesForPrelaunch(out string reason)
{
    reason = "";
    if (selectedLaunchMounts.Count == 0)
    {
        reason = "no selected launch candidates";
        return false;
    }

    foreach (string name in selectedLaunchMounts.ToList())
    {
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (mount == null || !mount.LoadedType.HasValue)
        {
            reason = name + ": selected mount/type disappeared";
            return false;
        }
        if (!IsEligibleLaunchCandidate(mount, mount.LoadedType.Value))
        {
            reason = mount.Name + ": " + LaunchCandidateProblem(mount, mount.LoadedType.Value);
            return false;
        }

        mount.MergeWasConnectedAtLaunch = mount.MergeGroupConfigured && MergeConnectedLive(mount);
        mount.MergeBlockCountAtLaunch = mount.MergeGroupConfigured ? mount.MergeBlocks.Count : 0;
        mount.MergeReleasedBeforeTryRun = false;

        if (mount.MergeGroupConfigured && !mount.MergeWasConnectedAtLaunch)
        {
            reason = mount.Name + ": merge was not connected at prelaunch commitment";
            return false;
        }
    }
    return true;
}

bool CommittedLaunchSelectionStillSafe(out string reason)
{
    reason = "";
    if (selectedLaunchMounts.Count == 0 || missionQueue.Count == 0)
    {
        reason = "launch selection or mission was cleared";
        return false;
    }

    var active = GetActiveSteps(missionQueue[0]);
    foreach (string name in selectedLaunchMounts.ToList())
    {
        var mount = mounts.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (mount == null || mount.State != MountState.LaunchReady || !mount.LoadedType.HasValue)
        {
            reason = name + ": selected missile is no longer LaunchReady";
            return false;
        }
        if (!active.Any(step => step.Type == mount.LoadedType.Value && step.RemainingDemand > 0))
        {
            reason = mount.Name + ": mission demand no longer exists";
            return false;
        }
        if (!mount.PBValid)
        {
            reason = mount.Name + ": missile PB missing or damaged during prelaunch";
            return false;
        }
        IMyProjector proj;
        if (!mount.Projectors.TryGetValue(mount.LoadedType.Value, out proj) || proj == null || !proj.IsFunctional)
        {
            reason = mount.Name + ": active projector record missing or damaged during prelaunch";
            return false;
        }
        if (!MountDoorsHardwareOk(mount))
        {
            reason = mount.Name + ": launch door hardware damaged during prelaunch";
            return false;
        }
        if (mount.MergeGroupConfigured && !MergeVerificationHealthyForLaunching(mount))
        {
            reason = mount.Name + ": merge verification hardware changed or became damaged during prelaunch";
            return false;
        }
        // A clean prelaunch timer release is allowed here. The merge was positively
        // connected when committed, and all verification hardware is still healthy.
    }
    return true;
}

bool BeginLaunchDoorSequence()
{
    SelectLaunchCandidates();
    if (selectedLaunchMounts.Count == 0) return false;
    OpenDoorsForLaunchDemand();
    siloState = SiloState.OpeningDoors;
    siloStateTick = tickCounter;
    return true;
}

bool SiloIdleWithDoorsClosed()
{
    return siloState == SiloState.Idle && AllConstructionDoorsClosed();
}

bool AnyConstructionDoorOpenOrMoving()
{
    if (!AllConstructionDoorsClosed()) return true;
    return false;
}

void ResumeAfterDoorFault()
{
    if (AllConstructionDoorsClosed())
    {
        siloState = SiloState.Idle;
    }
    else if (HasEligibleLaunchReadyForDemand())
    {
        if (!BeginLaunchDoorSequence())
        {
            launchSafetyFault = true;
            launchSafetyFaultReason = "No eligible launch candidate remains after door-fault recovery.";
            siloState = SiloState.DoorFault;
        }
    }
    else if (HasRawLaunchReadyForDemand())
    {
        MarkIneligibleLaunchReadyStepsBlocked();
        launchSafetyFault = true;
        launchSafetyFaultReason = "No eligible launch candidate remains after door-fault recovery.";
        siloState = SiloState.DoorFault;
    }
    else if (AnyConstructionDoorOpenOrMoving())
    {
        CloseSharedDoors();
        siloState = SiloState.ClosingDoors;
        siloStateTick = tickCounter;
    }
    else
    {
        launchSafetyFault = true;
        if (string.IsNullOrEmpty(launchSafetyFaultReason)) launchSafetyFaultReason = "No safe resume path after door fault.";
        siloState = SiloState.DoorFault;
    }
    isReadyForOperation = CanAcceptMissionsNow();
}

bool CanClearDoorFault(out string reason)
{
    reason = "";
    if (!SharedDoorsHardwareOk()) { reason = "shared door group missing or damaged"; return false; }
    if (!PrelaunchTimerReady()) { reason = "prelaunch timer missing or damaged"; return false; }
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

bool HasRawLaunchReadyForDemand()
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

bool HasEligibleLaunchReadyForDemand()
{
    if (missionQueue.Count == 0) return false;
    var active = GetActiveSteps(missionQueue[0]);
    foreach (var step in active)
    {
        if (step.RemainingDemand <= 0) continue;
        if (mounts.Any(m => IsEligibleLaunchCandidate(m, step.Type))) return true;
    }
    return false;
}

void MarkIneligibleLaunchReadyStepsBlocked()
{
    if (missionQueue.Count == 0) return;
    foreach (var step in GetActiveSteps(missionQueue[0]))
    {
        if (step.RemainingDemand <= 0) continue;
        var raw = mounts.Where(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type).ToList();
        if (raw.Count == 0 || raw.Any(m => IsEligibleLaunchCandidate(m, step.Type))) continue;
        var reasons = raw.Select(m => m.Name + ": " + LaunchCandidateProblem(m, step.Type)).ToArray();
        step.Status = "Blocked";
        step.BlockReason = TYPE_CODE[step.Type] + " step blocked - no eligible LaunchReady mount. " + string.Join("; ", reasons);
        if (!debugErrors.Contains(step.BlockReason)) debugErrors.Insert(0, step.BlockReason);
    }
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

    // Finish the entire current build batch before any launch takes ownership
    // of the doors. This prevents RunBuildState from cycling unfinished
    // projectors and welders off/on during a launch.
    bool building = mounts.Any(m => m.State == MountState.Building);
    bool launching = mounts.Any(m => m.State == MountState.Launching);
    bool postLaunch = mounts.Any(m => m.State == MountState.PostLaunch);
    bool readyForMission = !building && HasEligibleLaunchReadyForDemand();
    if (!building && !readyForMission && HasRawLaunchReadyForDemand()) MarkIneligibleLaunchReadyStepsBlocked();
    bool separationFault = mounts.Any(m => m.State == MountState.FaultedLoaded && m.Fault == FaultType.SeparationFault);

    switch (siloState)
    {
        case SiloState.Idle:
            if (launching)
            {
                OpenSharedDoors();
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            else if (readyForMission)
            {
                if (!BeginLaunchDoorSequence())
                    MarkIneligibleLaunchReadyStepsBlocked();
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

            // The executor owns the doors throughout launch preparation. Reassert
            // OPEN every update so a timer action or another script cannot quietly
            // close them and cancel the launch.
            OpenDoorsForLaunchDemand();

            if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN_TIMEOUT && !RequiredLaunchDoorsOpen())
            { EnterDoorFault("Required shared or mount launch doors failed to open before timeout."); break; }
            if (!LaunchSelectionStillValid())
            {
                SelectLaunchCandidates();
                if (selectedLaunchMounts.Count == 0)
                {
                    MarkIneligibleLaunchReadyStepsBlocked();
                    CloseSharedDoors();
                    siloState = SiloState.ClosingDoors;
                    siloStateTick = tickCounter;
                    break;
                }
                OpenDoorsForLaunchDemand();
            }
            if (RequiredLaunchDoorsOpen() && tickCounter - siloStateTick >= TICKS_DOOR_OPEN)
            {
                string commitReason;
                if (!CommitSelectedLaunchesForPrelaunch(out commitReason))
                {
                    EnterDoorFault("Cannot commit prelaunch: " + commitReason);
                    break;
                }
                if (!PrelaunchTimerReady() || !TriggerPreLaunchTimer())
                {
                    EnterDoorFault("Prelaunch timer missing or damaged before launch.");
                    break;
                }
                siloState = SiloState.PreLaunch;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.PreLaunch:
            if (!PrelaunchTimerReady()) { EnterDoorFault("Prelaunch timer damaged during prelaunch delay."); break; }

            // The timer may intentionally release the missile merge or may contain
            // a legacy door-close action. Keep the doors open and validate the
            // committed hardware without demanding that the merge remain connected.
            OpenDoorsForLaunchDemand();
            string prelaunchReason;
            if (!CommittedLaunchSelectionStillSafe(out prelaunchReason))
            {
                EnterDoorFault("Prelaunch commitment became unsafe: " + prelaunchReason);
                break;
            }

            if (tickCounter - siloStateTick >= TICKS_PRELAUNCH && RequiredLaunchDoorsOpen())
            {
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            else if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN_TIMEOUT && !RequiredLaunchDoorsOpen())
            {
                EnterDoorFault("Launch doors were closed or obstructed during prelaunch and could not be reopened.");
            }
            break;

        case SiloState.Active:
            // Do not allow any timer or secondary script to close the launch path
            // between prelaunch completion and TryRun.
            OpenDoorsForLaunchDemand();

            bool activeHasLaunching = mounts.Any(m => m.State == MountState.Launching);
            bool activeHasPostLaunch = mounts.Any(m => m.State == MountState.PostLaunch);
            if (!activeHasLaunching && !activeHasPostLaunch && selectedLaunchMounts.Count > 0)
            {
                string activeReason;
                if (!CommittedLaunchSelectionStillSafe(out activeReason))
                {
                    EnterDoorFault("Committed launch became unsafe before fire command: " + activeReason);
                    break;
                }
                if (!RequiredLaunchDoorsOpen())
                {
                    if (tickCounter - siloStateTick >= TICKS_DOOR_OPEN_TIMEOUT)
                        EnterDoorFault("Required launch doors could not be held open before fire command.");
                    break;
                }
            }

            AttemptFires();

            bool nowBuilding = mounts.Any(m => m.State == MountState.Building);
            bool nowLaunching = mounts.Any(m => m.State == MountState.Launching);
            bool nowPostLaunch = mounts.Any(m => m.State == MountState.PostLaunch);
            bool nowEligibleReady = !nowBuilding && HasEligibleLaunchReadyForDemand();
            if (!nowEligibleReady && !nowLaunching && !nowPostLaunch)
            {
                CloseSharedDoors();
                siloState = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
            break;

        case SiloState.DoorFault:
            break;

        case SiloState.ClosingDoors:
            if (launching)
            {
                OpenSharedDoors();
                siloState = SiloState.Active;
                siloStateTick = tickCounter;
            }
            else if (readyForMission)
            {
                if (!BeginLaunchDoorSequence())
                    MarkIneligibleLaunchReadyStepsBlocked();
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
                if (AllConstructionDoorsClosed()) { selectedLaunchMounts.Clear(); siloState = SiloState.Idle; }
            }
            break;
    }
}

void AssignBuilds()
{
    if (!CanAcceptMissionsNow() || missionQueue.Count == 0) return;
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
        else if (mounts.Any(m => IsEligibleLaunchCandidate(m, step.Type))) { step.Status = "LaunchReady"; step.BlockReason = ""; }
        else if (mounts.Any(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type))
        {
            var raw = mounts.Where(m => m.State == MountState.LaunchReady && m.LoadedType == step.Type).ToList();
            step.Status = "Blocked";
            step.BlockReason = TYPE_CODE[step.Type] + " step blocked - no eligible LaunchReady mount. " + string.Join("; ", raw.Select(m => m.Name + ": " + LaunchCandidateProblem(m, step.Type)).ToArray());
        }
        else if (mounts.Any(m => m.State == MountState.Building && m.LoadedType == step.Type)) step.Status = "Building";
        else step.Status = "Queued";
    }
}

void StartBuild(MissileMount mount, MissileType type, bool manual)
{
    if (!mount.Supports(type))
    {
        Echo("Cannot build " + TYPE_CODE[type] + " on " + mount.Name + ": unsupported.");
        return;
    }

    mount.LoadedType = type;
    mount.State = MountState.Building;
    mount.StateTick = tickCounter;
    mount.BuildOrdered = true;
    mount.ManualBuild = manual;
    mount.LaunchStep = null;
    mount.Fault = FaultType.None;
    mount.FaultReason = "";
    mount.MergeWaitStartTick = -1;
    mount.BuildDoorWaitStartTick = -1;
    mount.ProjectionWasObserved = false;
    mount.ProjectionMissingStartTick = -1;
    mount.MergeWasConnectedAtLaunch = false;
    mount.MergeBlockCountAtLaunch = 0;
    mount.MergeReleasedBeforeTryRun = false;

    // This is the original build-selection behavior: all projectors off, only
    // the requested missile-type projector on, and welders on immediately.
    SetProjectorSelection(mount, type, true);
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
            selectedLaunchMounts.Clear();
            Echo("Mission complete: " + ParseGPSName(mission.GPS));
        }
        return;
    }

    foreach (var mount in mounts)
    {
        if (mount.State != MountState.LaunchReady || !mount.LoadedType.HasValue || !IsSelectedLaunchCandidate(mount)) continue;
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

        // CommitSelectedLaunchesForPrelaunch already recorded positive physical
        // presence. Record whether the timer released the merge before TryRun so
        // launch accounting can use the timed fallback instead of instant proof.
        mount.MergeReleasedBeforeTryRun = mount.MergeGroupConfigured && !MergeConnectedLive(mount);

        bool fired = mount.PB.TryRun(mission.GPS);
        if (fired)
        {
            mount.State = MountState.Launching;
            mount.StateTick = tickCounter;
            step.Status = "Launching";
            mount.ManualBuild = false;
            // It is no longer a pending LaunchReady candidate. Door control still
            // includes Launching/PostLaunch mounts through OpenDoorsForLaunchDemand.
            selectedLaunchMounts.Remove(mount.Name);
            SaveMountStorage();
            Echo("Started launch " + mount.Name + " [" + TYPE_CODE[mount.LoadedType.Value] + "] → " + ParseGPSName(mission.GPS));
        }
        else
        {
            mount.MergeWasConnectedAtLaunch = false;
            mount.MergeBlockCountAtLaunch = 0;
            mount.MergeReleasedBeforeTryRun = false;
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
    if (mount.MergeGroupConfigured && !AllConfiguredMergeHardwareFunctional(mount))
    { reason = "merge verification hardware is configured but missing/damaged/nonfunctional"; return false; }
    if (mount.HasMergeConvention)
    {
        // The missile had to be connected when prelaunch was committed. A timer may
        // intentionally release it before TryRun, so current disconnection alone is
        // not a launch-command fault.
        if (!mount.MergeWasConnectedAtLaunch || !MergeVerificationHealthyForLaunching(mount))
        { reason = "merge physical-presence commitment was not preserved through prelaunch"; return false; }
    }
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
    
    // Append compact history to every surface assigned the STATUS role.
    WriteDisplayTargets(statusDisplays,
        $"[{DateTime.Now:HH:mm:ss}] {status} | E:{empty} B:{building} R:{ready}\n", true);
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
        selectedLaunchMounts.Remove(mount.Name);
        mount.MergeWasConnectedAtLaunch = false;
        mount.MergeBlockCountAtLaunch = 0;
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
            if (AllConstructionDoorsClosed())
            {
                siloState = SiloState.Idle;
            }
            else
            {
                CloseSharedDoors();
                siloState = SiloState.ClosingDoors;
                siloStateTick = tickCounter;
            }
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
        mount.ProjectionWasObserved = false;
        mount.ProjectionMissingStartTick = -1;
        mount.Fault = FaultType.None;
        mount.LaunchReserved = 0;
        mount.OrphanedFaultReserved = 0;
        mount.LaunchStep = null;
        mount.MergeWasConnectedAtLaunch = false;
        mount.MergeBlockCountAtLaunch = 0;
        selectedLaunchMounts.Remove(mount.Name);
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

    if (l == "resetallfaults:confirm")
    {
        if (mounts.Any(m => m.LaunchReserved > 0 || m.FaultReserved > 0 || m.OrphanedFaultReserved > 0))
        {
            Echo("resetallfaults rejected: at least one mount has a reserved launch outcome.");
            return;
        }

        int resetCount = 0;
        foreach (var mount in mounts)
        {
            if (mount.State != MountState.FaultedLoaded) continue;
            SetProjectorSelection(mount, null, false);
            SetWelders(mount, false);
            mount.State = MountState.Empty;
            mount.LoadedType = null;
            mount.BuildOrdered = false;
            mount.ManualBuild = false;
            mount.LaunchStep = null;
            mount.Fault = FaultType.None;
            mount.FaultReason = "Reset by operator after physical empty-bay inspection.";
            mount.LaunchReserved = 0;
            mount.FaultReserved = 0;
            mount.OrphanedFaultReserved = 0;
            mount.MergeWaitStartTick = -1;
            mount.BuildDoorWaitStartTick = -1;
            mount.ProjectionWasObserved = false;
            mount.ProjectionMissingStartTick = -1;
            mount.MergeWasConnectedAtLaunch = false;
            mount.MergeBlockCountAtLaunch = 0;
            mount.MergeReleasedBeforeTryRun = false;
            selectedLaunchMounts.Remove(mount.Name);
            resetCount++;
        }

        if (!AllConstructionDoorsClosed())
        {
            CloseSharedDoors();
            siloState = SiloState.ClosingDoors;
            siloStateTick = tickCounter;
        }
        else
        {
            siloState = SiloState.Idle;
        }
        SaveMountStorage();
        isReadyForOperation = CanAcceptMissionsNow();
        debugErrors.Insert(0, "[RECOVERY] resetallfaults cleared " + resetCount + " unreserved mount fault(s). Operational=" + (isReadyForOperation ? "YES" : "NO"));
        Echo("resetallfaults: reset " + resetCount + " unreserved FaultedLoaded mount(s). Operational=" + (isReadyForOperation ? "YES" : "NO") + ". Physical empty-bay inspection was asserted by operator.");
        return;
    }

    if (l == "cleardoorfault")
    {
        string reason;
        if (!CanClearDoorFault(out reason)) { Echo("cleardoorfault rejected: " + reason); return; }
        launchSafetyFault = false;
        launchSafetyFaultReason = "";
        ResumeAfterDoorFault();
        Echo("Door fault cleared after live validation.");
        return;
    }

    if (l == "lcdscan")
    {
        DiscoverDisplaySurfaces(true);
        Echo("LCD scan complete.");
        Echo("Launch surfaces: " + launchDisplays.Count);
        foreach (var d in launchDisplays)
            Echo("  LAUNCH: " + d.Block.CustomName + " [surface " + d.SurfaceIndex + "]");
        Echo("Debug surfaces: " + debugDisplays.Count);
        foreach (var d in debugDisplays)
            Echo("  DEBUG: " + d.Block.CustomName + " [surface " + d.SurfaceIndex + "]");
        Echo("Status surfaces: " + statusDisplays.Count);
        foreach (var d in statusDisplays)
            Echo("  STATUS: " + d.Block.CustomName + " [surface " + d.SurfaceIndex + "]");
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
            missionQueue.Clear();
            selectedLaunchMounts.Clear();
            if (!mounts.Any(m => m.State == MountState.Launching || m.State == MountState.PostLaunch) && !RequiresDoorsHeldOpen())
            {
                if (AllConstructionDoorsClosed())
                    siloState = SiloState.Idle;
                else
                {
                    CloseSharedDoors();
                    siloState = SiloState.ClosingDoors;
                    siloStateTick = tickCounter;
                }
            }
            Echo("Mission queue cleared."); break;
    }
}


// ── HELPERS ───────────────────────────────────────────

string ParseGPSName(string gps)
{
    var p = gps.Split(':');
    return p.Length > 1 ? p[1] : gps;
}


// ── UNIVERSAL LCD / TEXT-SURFACE ROUTING ─────────────

void DiscoverDisplaySurfaces(bool writeDiagnostics)
{
    launchDisplays.Clear();
    debugDisplays.Clear();
    statusDisplays.Clear();

    // Preserve the original exact-name setup. These names now also work when
    // assigned to a cockpit, control seat, programmable block, or other surface
    // provider; surface 0 is used in that case.
    RegisterLegacyDisplay(LAUNCH_PANEL_NAME, launchDisplays);
    RegisterLegacyDisplay(DEBUG_PANEL_NAME, debugDisplays);
    RegisterLegacyDisplay(STATUS_LCD_NAME, statusDisplays);

    var blocks = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocks(blocks);
    foreach (var block in blocks)
    {
        if (block == null) continue;
        if (!allowSubgrids && block.CubeGrid != Me.CubeGrid) continue;
        RegisterTaggedDisplays(block);
    }

    if (writeDiagnostics)
    {
        debugErrors.RemoveAll(s =>
            s.StartsWith("LCD routing:") ||
            s.StartsWith("LCD tags:") ||
            s.StartsWith("LCD note:"));

        debugErrors.Add("LCD routing: Launch=" + launchDisplays.Count +
                        " Debug=" + debugDisplays.Count +
                        " Status=" + statusDisplays.Count);
        debugErrors.Add("LCD tags may be placed in Custom Name OR Custom Data.");
        debugErrors.Add("LCD tags: [NDS:LAUNCH:n] [NDS:DEBUG:n] [NDS:STATUS:n]. Dedicated single-screen LCDs ignore n.");
    }
}

void RegisterLegacyDisplay(string exactName, List<DisplayTarget> targets)
{
    var block = GridTerminalSystem.GetBlockWithName(exactName);
    AddDisplayTarget(block, 0, targets);
}

void RegisterTaggedDisplays(IMyTerminalBlock block)
{
    // Accept routing tags in either the visible block name or Custom Data.
    // Duplicate registrations are filtered by AddDisplayTarget.
    RegisterTaggedDisplaysFromText(block, block.CustomName ?? "");
    RegisterTaggedDisplaysFromText(block, block.CustomData ?? "");
}

void RegisterTaggedDisplaysFromText(IMyTerminalBlock block, string sourceText)
{
    if (string.IsNullOrWhiteSpace(sourceText)) return;

    int searchFrom = 0;
    while (searchFrom < sourceText.Length)
    {
        int start = sourceText.IndexOf("[NDS:", searchFrom, StringComparison.OrdinalIgnoreCase);
        if (start < 0) break;

        int end = sourceText.IndexOf(']', start + 5);
        if (end < 0) break;

        string token = sourceText.Substring(start + 5, end - (start + 5)).Trim();
        string[] parts = token.Split(':');
        string role = parts.Length > 0 ? parts[0].Trim().ToUpperInvariant() : "";
        int surfaceIndex = 0;

        if (parts.Length > 1 && !int.TryParse(parts[1].Trim(), out surfaceIndex))
            surfaceIndex = -1;

        if (surfaceIndex >= 0)
        {
            if (role == "LAUNCH" || role == "MAIN")
                AddDisplayTarget(block, surfaceIndex, launchDisplays);
            else if (role == "DEBUG" || role == "DIAG")
                AddDisplayTarget(block, surfaceIndex, debugDisplays);
            else if (role == "STATUS")
                AddDisplayTarget(block, surfaceIndex, statusDisplays);
        }

        searchFrom = end + 1;
    }
}

void AddDisplayTarget(IMyTerminalBlock block, int requestedSurfaceIndex, List<DisplayTarget> targets)
{
    if (block == null || requestedSurfaceIndex < 0) return;

    IMyTextSurface surface = null;
    int actualSurfaceIndex = requestedSurfaceIndex;

    // Any terminal block that is itself a direct text surface is a dedicated
    // single-screen display. This includes vanilla LCD, Wide LCD, transparent
    // LCD, and compatible modded panels. Because it only has one screen, accept
    // any supplied index and normalize it to surface 0.
    var directSurface = block as IMyTextSurface;
    if (directSurface != null)
    {
        surface = directSurface;
        actualSurfaceIndex = 0;
    }
    else
    {
        // Cockpits, seats, programmable blocks, and other multi-screen blocks
        // expose their screens through IMyTextSurfaceProvider.
        var provider = block as IMyTextSurfaceProvider;
        if (provider == null) return;
        if (requestedSurfaceIndex >= provider.SurfaceCount) return;
        surface = provider.GetSurface(requestedSurfaceIndex);
    }

    if (surface == null) return;

    if (targets.Any(t => t.Block != null &&
                         t.Block.EntityId == block.EntityId &&
                         t.SurfaceIndex == actualSurfaceIndex))
        return;

    // Force the selected surface out of script/image mode and into writable text
    // mode immediately. Do not overwrite the player's font, colors, or sizing.
    surface.ContentType = ContentType.TEXT_AND_IMAGE;

    targets.Add(new DisplayTarget
    {
        Block = block,
        Surface = surface,
        SurfaceIndex = actualSurfaceIndex
    });
}

void WriteDisplayTargets(List<DisplayTarget> targets, string text, bool append = false)
{
    foreach (var target in targets)
    {
        if (target == null || target.Block == null || target.Surface == null) continue;
        if (!target.Block.IsFunctional) continue;

        target.Surface.ContentType = ContentType.TEXT_AND_IMAGE;
        target.Surface.WriteText(text, append);
    }
}


// ── LCD ───────────────────────────────────────────────

void UpdateLaunchPanel()
{
    if (launchDisplays.Count == 0) return;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("=== NDS SILO ===");
    sb.AppendLine($"ID:     {ndsId}");
    sb.AppendLine($"Hub:    {(hubAddressReceived ? "LINKED" : "SEARCHING")}");
    sb.AppendLine($"Operational: {(CanAcceptMissionsNow() ? "YES" : "NO")}");
    sb.AppendLine($"Shared: {(SharedDoorsHardwareOk() && PrelaunchTimerReady() ? "OK" : "FAULT")}");
    sb.AppendLine($"Serviceable Mounts: {mounts.Count(m => IsServiceableMount(m))}/{mounts.Count}");
    sb.AppendLine($"Silo:   {siloState}");
    if (launchSafetyFault) sb.AppendLine($"DoorFault: {launchSafetyFaultReason}");
    sb.AppendLine($"Limit:  {MAX_PARALLEL_STEP_TYPES} parallel types");
    sb.AppendLine($"Subgrids: {(allowSubgrids ? "ENABLED" : "DISABLED")}");
    sb.AppendLine($"Queue:  {missionQueue.Count} mission(s)");
    if (missionQueue.Count > 0 && !CanAcceptMissionsNow())
        sb.AppendLine("QUEUE HELD: silo is Busy/faulted; mission retained until recovery.");
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

    WriteDisplayTargets(launchDisplays, sb.ToString());
}

void UpdateDebugPanel()
{
    if (debugDisplays.Count == 0) return;

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

    WriteDisplayTargets(debugDisplays, sb.ToString());
}