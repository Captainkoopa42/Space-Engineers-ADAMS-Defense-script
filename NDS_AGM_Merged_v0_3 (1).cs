// ============================================================
// NDS-AGM UNIFIED MISSILE SCRIPT v0.3 — COMPLETE
// ============================================================
// v0.3 changes from v0.2:
//   - All NDS function bodies included (was missing from v0.2)
//   - Fixed: duplicate aimVector and gyroOverrideState declarations
//   - Fixed: Vector3 * double type error in CameraGroup (cast to Vector3D)
//   - Fixed: duplicate ApplySecondStage (AGM version kept, NDS removed)
// ============================================================

// ── PHASE STATE MACHINE ──────────────────────────────────────
enum MissilePhase { IDLE, BALLISTIC, TERMINAL }
MissilePhase phase = MissilePhase.IDLE;

// ── TERMINAL PHASE CONFIGURATION ────────────────────────────
const double TERMINAL_ACTIVATION_RANGE = 1500.0;
const double AGM_PRONAV_CONSTANT       = 5.0;
const double AGM_PRONAV_ACCEL_CONSTANT = 0.05;
const double AGM_PD_GAIN_P             = 30.0;
const double AGM_PD_GAIN_D             = 10.0;
const double AGM_AIM_LIMIT             = 6.3;
const double AGM_THRUST_ACCEL_MIN      = 1.0;
const double AGM_THRUST_ESTIMATE       = 1.0;
const bool   AGM_COMPENSATE_GRAVITY    = true;
const bool   AGM_ACTIVE_HOMING         = true;
const bool   AGM_EXCLUDE_FRIENDLY      = true;
const bool   AGM_EXCLUDE_NEUTRAL       = false;
const double AGM_RAYCAST_MAX_DISTANCE  = 3000.0;
const double AGM_PROX_TRIGGER_RANGE    = 7.0;
const double RAYCAST_CONE_LIMIT        = 30.0;
const double RAYCAST_STEP_ANGLE        = 30.0;

// ── NDS VARIABLES ────────────────────────────────────────────
public double
version = 1.09, gravityWellMultiplier = 1.7182, gyroMultiplier = 0.8,
planetRadius = 0, waypointDistance = 1000, desiredElevationMultiplier = 0.15,
peakSpeed = 100, currentSpeed = 0, elevationVariance = 1200, lastSpeed = 0,
largeWarheadRange = 22, smallWarheadRange = 4.5, maxSpeed = 100, dropSpeed = 30,
missileStartDelayInMilliseconds = 500, lastPlanetDistance = 0,
disableThrustersAtDistanceFromTarget = 0, runClosingInOnTargetTimerDistance = 0,
stopDistance = 0, forwardNewtons = 0, totalMass = 0, shipLength = 0,
finalApproachRotationSpeedPercentage = 0, descentAccuracyForClosingInTimer = 50,
descentAccuracyForRotation = 50, currentDescentAccuracy = 9999999999,
separatorDistanceFilter = 50, distanceFromLaunchPointForSecondStage = 1000,
thrustAvailableForSecondStage = 0.5, turnDelayInSeconds = 5,
launchDelayInSeconds = 0, safeZoneRadius = 50, targetDistance = 999999999,
gravityRadius = 0, usableAtmosphericRangeStart = 0, usableAtmosphericRangeEnd = 0,
randomizationDistance = 0, speedPercentageDroppedForDetonation = 0.5,
speedDropDetonationDistance = 1500;

public string
mergeFilter = "", forwardBlockReferenceFilter = "", settingsBackup = "",
script = "NDS-AGM Unified Script", leavingPlanetTimerKeyword = "",
elevatingTimerKeyword = "", slowingDownForFinalApproachTimerKeyword = "",
closingInOnTargetTimerKeyword = "", secondStageKeyword = "2",
secondStageTimerKeyword = "", globalFilter = "", exclusionKeyword = "exclude";

public Vector3D
planetVector = new Vector3D(0,0,0), targetVector = new Vector3D(0,0,0),
currentVector = new Vector3D(0,0,0), currentWaypoint = new Vector3D(0,0,0),
planetaryWaypoint = new Vector3D(0,0,0), elevationWaypoint = new Vector3D(0,0,0),
launchVector = new Vector3D(0,0,0), directLaunchVector = new Vector3D(0,0,0),
directLaunchPosition = new Vector3D(0,0,0);

public IMyTerminalBlock
genRemote, genMergeBlock, genConnector, genPiston, genRotor,
genHomeDetachBlock, forwardReferenceBlock, genLaunchPanel,
leavingPlanetTimer, elevatingTimer, slowingDownForFinalApproachTimer,
closingInOnTargetTimer, secondStageTimer,
genRemoteStageTwo, genMergeBlockStageTwo, genConnectorStageTwo,
genRotorStageTwo, forwardReferenceBlockStageTwo;

public List<IMyTerminalBlock>
gyroList = new List<IMyTerminalBlock>(),
warheadList = new List<IMyTerminalBlock>(),
cameraList = new List<IMyTerminalBlock>(),
forwardThrustList = new List<IMyTerminalBlock>(),
allThrustList = new List<IMyTerminalBlock>(),
gyroListStageTwo = new List<IMyTerminalBlock>(),
forwardThrustListStageTwo = new List<IMyTerminalBlock>(),
allThrustListStageTwo = new List<IMyTerminalBlock>();

public int currentCameraIndex = 0, currentIdleTicks = 0, idleTicks = 0, saveTicks = 0;

public bool
active = false, connected = true, sameGridOnly = true, currentlyOnPlanet = false,
elevating = true, closingInOnTarget = false, planetaryTarget = false,
adjustElevation = false, leavingPlanet = false, accelerating = false,
slowingDownForFinalApproach = false, booted = false, correctVersion = true,
correctScript = true, movingTowardsPlanet = false, risingSpeed = true,
usePlanetaryWaypoint = false, decelerating = false, mergeBlockSeparation = true,
connectorSeparation = false, rotorSeparation = false, pistonSeparation = false,
ranLeavingPlanetTimer = false, ranElevatingTimer = false,
ranSlowingDownForFinalApproachTimer = false, ranClosingInOnTargetTimer = false,
thrustersEnabled = true, twoStageMissile = false, abandondedFirstStage = false,
direct = false, directPosition = false, targetEnemies = true, targetNeutrals = true,
targetAllies = false, targetOwner = false, targetUnowned = false, targetFaction = false,
determineMaxSpeed = true, rememberLaunchDetails = true;

public TimeSpan
planetRecheckSpan = new TimeSpan(0,0,0), startSpan = new TimeSpan(0,0,0),
turnDelaySpan = new TimeSpan(0,0,0), timeSinceLastRun = new TimeSpan(0,0,0),
launchDelay = new TimeSpan(0,0,0), stopSpan = new TimeSpan(0,0,0);

public DateTime tickStartTime = DateTime.Now;
public List<string> commandStringList = new List<string>();

// NDS gyro coroutine — BALLISTIC phase only
public IEnumerator<bool> gyroOverrideState;
public Vector3D aimVector = new Vector3D(0,0,0);

// NDS coroutine for block loading
public IEnumerator<bool> stateGetMissileBlocks;
public IEnumerator<bool> stateRunCommands;

// ── AGM VARIABLES ────────────────────────────────────────────
Vector3D agmPosition = Vector3D.Zero, agmVelocity = Vector3D.Zero;
Vector3D agmGravity = Vector3D.Zero;
double agmGravityMag = 0, agmSpeed = 0, agmThrustAccel = 0;
MatrixD agmOrientInverse = MatrixD.Identity;
Vector3D agmAimVector = Vector3D.Zero;
Vector3D agmTargetPos = Vector3D.Zero, agmTargetVel = Vector3D.Zero;
Vector3D agmTargetAccel = Vector3D.Zero, agmRelPos = Vector3D.Zero, agmRelVel = Vector3D.Zero;
long agmTargetEntityId = 0;
bool agmApproaching = false;
int agmTickCounter = 0;
AgmPdController agmPitchPD, agmYawPD;
AgmGyroManager agmGyros;
AgmHomingTracker agmTracker;
bool agmProxArmed = false, agmGyroNeedsReinit = false;
double agmLastClosingDist = double.MaxValue;

// ── PROGRAM ──────────────────────────────────────────────────
public Program()
{
    if (Me.CustomData.Length == 0) SaveData();
    else LoadData();
    Echo("NDS-AGM Unified — Waiting for target");
    currentVector = Me.GetPosition();
    turnDelaySpan = TimeSpan.FromSeconds(turnDelayInSeconds);
    launchDelay = TimeSpan.FromSeconds(launchDelayInSeconds);
    gyroOverrideState = ApplyGyroOverrideState();
    Runtime.UpdateFrequency = idleTicks == 0 ? UpdateFrequency.Update100 : UpdateFrequency.Update10;
}

// ── MAIN ─────────────────────────────────────────────────────
public void Main(string argument)
{
    try
    {
        tickStartTime = DateTime.Now;
        if (argument.Length != 0) Commands(argument);
        if (idleTicks <= 0 || currentIdleTicks >= idleTicks)
        {
            currentIdleTicks = 0;
            switch (phase)
            {
                case MissilePhase.IDLE:      TickIdle();      break;
                case MissilePhase.BALLISTIC: TickBallistic(); break;
                case MissilePhase.TERMINAL:  TickTerminal();  break;
            }
        }
        else currentIdleTicks++;
        Echo($"Phase:    {phase}");
        Echo($"Active:   {active}");
        Echo($"Target:   {targetVector.ToString("N0")}");
        Echo($"Distance: {targetDistance:N0}m");
    }
    catch { Echo("Error in Main()"); }
}

// ── IDLE PHASE ───────────────────────────────────────────────
void TickIdle()
{
    Echo("Waiting for target");
    if (settingsBackup.Length != Me.CustomData.Length || settingsBackup != Me.CustomData)
    {
        LoadData();
        turnDelaySpan = TimeSpan.FromSeconds(turnDelayInSeconds);
        launchDelay = TimeSpan.FromSeconds(launchDelayInSeconds);
    }
    CheckPanel();
    if (connectorSeparation) TriggerConnection();
    if (active)
    {
        timeSinceLastRun = Runtime.TimeSinceLastRun;
        if (connected)
        {
            if (launchDelay.TotalSeconds <= 0.0) Disconnect();
            else launchDelay -= timeSinceLastRun;
        }
        else if (!booted) Boot();
        else { phase = MissilePhase.BALLISTIC; Echo(">> BALLISTIC"); }
        if (rememberLaunchDetails) CheckSave();
    }
}

// ── BALLISTIC PHASE ──────────────────────────────────────────
void TickBallistic()
{
    try
    {
        timeSinceLastRun = Runtime.TimeSinceLastRun;
        if (abandondedFirstStage) Echo("Second Stage Active");
        currentSpeed = ((IMyShipController)genRemote).GetShipSpeed();
        currentVector = forwardReferenceBlock.GetPosition();
        targetDistance = Vector3D.Distance(currentVector, targetVector);
        if (determineMaxSpeed)
        {
            if (currentSpeed > peakSpeed) peakSpeed = currentSpeed;
            if (risingSpeed && currentSpeed <= lastSpeed) risingSpeed = false;
        }
        PlanetCheck();
        movingTowardsPlanet = MovingTorwardsPlanet();
        if (stopSpan.TotalSeconds <= 0.0) CalculateStopDistance();
        else stopSpan -= timeSinceLastRun;
        lastSpeed = currentSpeed;

        // ── TERMINAL HANDOFF ─────────────────────────────────
        if (closingInOnTarget && targetDistance <= TERMINAL_ACTIVATION_RANGE)
        { InitiateTerminalPhase(); return; }

        if (!abandondedFirstStage && twoStageMissile && SecondStageReady()) ApplySecondStage();
        if ((currentlyOnPlanet && closingInOnTarget) || direct || !currentlyOnPlanet)
        { currentDescentAccuracy = FinalApproachAccuracy(); Echo("Accuracy: " + currentDescentAccuracy.ToString("N1")); }
        if (direct) DirectScript();
        else        PositionScript();
        RunCommands();
    }
    catch { Echo("Error in TickBallistic()"); }
}

// ── TERMINAL PHASE INIT ──────────────────────────────────────
void InitiateTerminalPhase()
{
    Echo(">> TERMINAL — AGM active");
    StopGyro();
    var typedGyros = gyroList.OfType<IMyGyro>().ToList();
    agmGyros   = new AgmGyroManager(typedGyros, forwardReferenceBlock);
    agmPitchPD = new AgmPdController(AGM_PD_GAIN_P, AGM_PD_GAIN_D);
    agmYawPD   = new AgmPdController(AGM_PD_GAIN_P, AGM_PD_GAIN_D);
    agmTargetPos = targetVector; agmTargetVel = Vector3D.Zero; agmTargetEntityId = 0;
    if (AGM_ACTIVE_HOMING && cameraList.Count > 0)
    {
        var cams = cameraList.OfType<IMyCameraBlock>().ToList();
        agmTracker = new AgmHomingTracker(cams, AGM_EXCLUDE_FRIENDLY, AGM_EXCLUDE_NEUTRAL,
                                          RAYCAST_CONE_LIMIT, RAYCAST_STEP_ANGLE);
        agmTracker.AttemptInitialLock(targetVector, AGM_RAYCAST_MAX_DISTANCE);
    }
    else agmTracker = null;
    agmThrustAccel = ComputeAgmThrustAccel();
    Arm();
    phase = MissilePhase.TERMINAL;
    agmTickCounter = 0; agmProxArmed = false; agmLastClosingDist = double.MaxValue;
}

// ── TERMINAL PHASE TICK ──────────────────────────────────────
void TickTerminal()
{
    try
    {
        agmTickCounter++;
        timeSinceLastRun = Runtime.TimeSinceLastRun;
        UpdateAgmFlightState();
        if (agmTracker != null)
        {
            agmTracker.Tick(agmTickCounter);
            Vector3D tPos; Vector3D tVel; long tId;
            if (agmTracker.GetTarget(out tPos, out tVel, out tId))
            { agmTargetPos = tPos; agmTargetVel = tVel; agmTargetEntityId = tId; }
        }
        UpdateAgmTargetState();
        if (agmGyroNeedsReinit)
        {
            var typedGyros = gyroList.OfType<IMyGyro>().ToList();
            agmGyros = new AgmGyroManager(typedGyros, forwardReferenceBlock);
            agmPitchPD.Reset(); agmYawPD.Reset();
            agmGyroNeedsReinit = false;
        }
        ComputeProNav();
        if (AGM_COMPENSATE_GRAVITY && agmGravityMag > 0.001) ApplyGravityCompensation();
        agmGyros.Apply(agmAimVector, agmPitchPD, agmYawPD, AGM_AIM_LIMIT);
        ManageAgmThrust();
        CheckAgmDetonation();
        Echo($"AGM tick: {agmTickCounter}  Speed: {agmSpeed:N1}  Dist: {targetDistance:N0}");
        Echo($"Tracker: {(agmTracker != null ? agmTracker.StateString() : "none")}  Grav: {agmGravityMag:N2}");
    }
    catch { Echo("Error in TickTerminal()"); }
}

void UpdateAgmFlightState()
{
    var rc = (IMyShipController)genRemote;
    agmPosition  = forwardReferenceBlock.GetPosition();
    agmVelocity  = rc.GetShipVelocities().LinearVelocity;
    agmSpeed     = agmVelocity.Length();
    agmGravity   = rc.GetNaturalGravity();
    agmGravityMag = agmGravity.Length();
    MatrixD fwd = forwardReferenceBlock.WorldMatrix;
    MatrixD.Transpose(ref fwd, out agmOrientInverse);
    targetDistance = Vector3D.Distance(agmPosition, agmTargetPos);
    currentSpeed = agmSpeed; currentVector = agmPosition;
}

void UpdateAgmTargetState()
{
    agmRelPos = agmTargetPos - agmPosition;
    agmRelVel = agmTargetVel - agmVelocity;
    agmApproaching = Vector3D.Dot(agmRelVel, agmRelPos) < 0;
}

void ComputeProNav()
{
    if (agmRelPos.LengthSquared() < 0.001) return;
    Vector3D losUnit = Vector3D.Normalize(agmRelPos);
    Vector3D losRate = Vector3D.Cross(agmRelPos, agmRelVel) / Math.Max(agmRelPos.LengthSquared(), 1.0);
    Vector3D lateralAccelCmd = Vector3D.Cross(losRate, losUnit) * agmRelVel.Length();
    Vector3D tgtAccelLateral = agmTargetAccel - (agmTargetAccel.Dot(losUnit) * losUnit);
    Vector3D navCmd = (AGM_PRONAV_CONSTANT * lateralAccelCmd) + (AGM_PRONAV_ACCEL_CONSTANT * tgtAccelLateral);
    double effectiveThrustAccel = agmThrustAccel;
    if (AGM_COMPENSATE_GRAVITY && agmGravityMag > 0.001)
    {
        Vector3D mFwd = forwardReferenceBlock.WorldMatrix.Forward;
        double gravFwd = Math.Abs(Vector3D.Dot(agmGravity, mFwd));
        effectiveThrustAccel = Math.Max(agmThrustAccel - gravFwd, AGM_THRUST_ACCEL_MIN);
    }
    double lateralSq = navCmd.LengthSquared();
    double thrustBudget = effectiveThrustAccel * effectiveThrustAccel;
    double forwardComp = Math.Sqrt(Math.Max(thrustBudget - lateralSq, 0.0));
    Vector3D thrustDir = (losUnit * forwardComp) + navCmd;
    agmAimVector = Vector3D.TransformNormal(thrustDir, agmOrientInverse);
}

void ApplyGravityCompensation()
{
    Vector3D losUnit = Vector3D.Normalize(agmRelPos);
    Vector3D gravComp = agmGravity * Math.Abs(agmGravity.Dot(losUnit)) / agmGravityMag;
    agmAimVector -= Vector3D.TransformNormal(gravComp, agmOrientInverse);
}

void ManageAgmThrust()
{
    if (agmTickCounter % 60 == 0) agmThrustAccel = ComputeAgmThrustAccel();
    SetForwardThrust(1.0f);
}

double ComputeAgmThrustAccel()
{
    float maxThrust = 0f;
    foreach (var t in forwardThrustList)
        try { maxThrust += ((IMyThrust)t).MaxEffectiveThrust; } catch { }
    float mass = ((IMyShipController)genRemote).CalculateShipMass().TotalMass;
    if (mass <= 0) return AGM_THRUST_ACCEL_MIN;
    return Math.Max((maxThrust / mass) * AGM_THRUST_ESTIMATE, AGM_THRUST_ACCEL_MIN);
}

void CheckAgmDetonation()
{
    if (targetDistance <= currentSpeed * 3.0) Arm();
    double closing = agmLastClosingDist - targetDistance;
    agmLastClosingDist = targetDistance;
    if (agmProxArmed && closing < 0 && targetDistance <= AGM_PROX_TRIGGER_RANGE)
    { Echo("PROX DETONATION"); Detonate(); return; }
    if (targetDistance <= AGM_PROX_TRIGGER_RANGE * 3.0) agmProxArmed = true;
    double whdRange = Me.CubeGrid.GridSizeEnum == MyCubeSize.Large ? largeWarheadRange : smallWarheadRange;
    if (agmTracker != null && agmTracker.ConfirmHit(whdRange))
    { Echo("RAYCAST DETONATION"); Detonate(); }
}

// ── SECOND STAGE (with AGM gyro re-init flag) ────────────────
public void ApplySecondStage()
{
    abandondedFirstStage = true;
    if (genRemoteStageTwo             != null) genRemote             = genRemoteStageTwo;
    if (forwardReferenceBlockStageTwo != null) forwardReferenceBlock = forwardReferenceBlockStageTwo;
    else if (genRemoteStageTwo        != null) forwardReferenceBlock = genRemoteStageTwo;
    if (gyroListStageTwo.Count > 0)
    {
        StopGyro();
        gyroList = new List<IMyTerminalBlock>(gyroListStageTwo);
        agmGyroNeedsReinit = true;
    }
    if (forwardThrustListStageTwo.Count > 0) forwardThrustList = new List<IMyTerminalBlock>(forwardThrustListStageTwo);
    if (allThrustListStageTwo.Count > 0)
    {
        EnableThrusters(true); SetForwardThrust(0f);
        allThrustList = new List<IMyTerminalBlock>(allThrustListStageTwo);
    }
    if (genMergeBlockStageTwo != null) ((IMyFunctionalBlock)genMergeBlockStageTwo).Enabled = false;
    if (genConnectorStageTwo  != null) ((IMyFunctionalBlock)genConnectorStageTwo).Enabled  = false;
    if (genRotorStageTwo      != null) ((IMyMechanicalConnectionBlock)genRotorStageTwo).Detach();
    if (secondStageTimer      != null) ((IMyTimerBlock)secondStageTimer).Trigger();
    EnableThrusters(true);
}

// ════════════════════════════════════════════════════════════
// NDS FUNCTIONS (unchanged from original)
// ════════════════════════════════════════════════════════════

public void CheckSave()
{
    saveTicks++;
    if (saveTicks >= 20) { saveTicks = 0; SaveData(); }
}

public void Commands(string argument)
{
    string arg = argument.ToLower();
    if (arg.StartsWith("direct"))
    {
        direct = true;
        if (arg == "direct")
        {
            GetRemoteControl();
            if (genRemote != null)
            {
                directLaunchPosition = genRemote.GetPosition();
                directLaunchVector = genRemote.WorldMatrix.Forward;
                active = true;
            }
            else Echo("Direct launch failed, no remote control found");
        }
        else
        {
            if (Convert(argument.Substring(6).Trim(), ref targetVector))
            {
                active = true; elevating = false; closingInOnTarget = true;
                slowingDownForFinalApproach = false; directPosition = true;
                if (randomizationDistance > 0.0)
                {
                    Random rnd = new Random();
                    targetVector += Vector3D.Normalize(new Vector3D(rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000))) * rnd.NextDouble() * rnd.NextDouble() * randomizationDistance;
                }
            }
            else { Echo("Unable to convert: " + arg.Substring(6).Trim()); direct = false; }
        }
    }
    else if (Convert(argument, ref targetVector))
    {
        active = true;
        if (randomizationDistance > 0.0)
        {
            Random rnd = new Random();
            targetVector += Vector3D.Normalize(new Vector3D(rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000))) * rnd.NextDouble() * randomizationDistance;
        }
        return;
    }
    else
    {
        switch (arg)
        {
            case "save": SaveData(); break;
            case "load": LoadData(); break;
        }
    }
    SaveData();
}

public void DirectScript()
{
    Vector3D directVector;
    if (directPosition) { directVector = targetVector; Echo("Targeting Coordinates Given"); }
    else { directVector = directLaunchPosition + (directLaunchVector * (Vector3D.Distance(currentVector, directLaunchPosition) + 750.0)); Echo("Targeting Straight Ahead From Moment Of Launch"); }
    Echo(directVector.ToString("N0"));
    if (turnDelaySpan.TotalSeconds <= 0.0)
    { Point(directVector); ((IMyShipController)genRemote).DampenersOverride = true; Echo("Aiming"); }
    else { turnDelaySpan -= timeSinceLastRun; Echo("Turn Delay: " + turnDelaySpan.TotalSeconds + "s"); }
    if (Accelerate() || currentDescentAccuracy <= 10 || turnDelaySpan.TotalSeconds > 0.0)
    { SetForwardThrust(1f); Echo("Accelerating"); }
    else { SetForwardThrust(0f); Echo("Not Accelerating"); }
}

public void PositionScript()
{
    if (!closingInOnTarget && !slowingDownForFinalApproach) NextWaypoint();
    else if (slowingDownForFinalApproach && !closingInOnTarget) { Echo("Slowing Down For Final Approach"); PrepareForApproach(); }
    else if (closingInOnTarget) { Echo("Closing In On Target"); SetFinalPoint(); CheckDetonation(); }
    bool tooClose = Vector3D.Distance(currentVector, currentWaypoint) <= shipLength * 1.5;
    if (turnDelaySpan.TotalSeconds <= 0.0 && !tooClose) { Point(currentWaypoint); ((IMyShipController)genRemote).DampenersOverride = true; }
    else if (tooClose) StopGyro();
    else turnDelaySpan -= timeSinceLastRun;
    if (Accelerate()) SetForwardThrust(1f); else SetForwardThrust(0f);
    if (leavingPlanet) CheckTimer(ref leavingPlanetTimer, ref ranLeavingPlanetTimer);
    if (closingInOnTarget)
    {
        if (!currentlyOnPlanet || !planetaryTarget || descentAccuracyForClosingInTimer <= 0.0 || currentDescentAccuracy <= descentAccuracyForClosingInTimer)
            CheckTimer(ref closingInOnTargetTimer, ref ranClosingInOnTargetTimer, runClosingInOnTargetTimerDistance);
        if (disableThrustersAtDistanceFromTarget > 0.0 && thrustersEnabled && targetDistance <= disableThrustersAtDistanceFromTarget)
        { thrustersEnabled = false; EnableThrusters(); }
    }
    if (elevating) CheckTimer(ref elevatingTimer, ref ranElevatingTimer);
    else Echo("Stop Distance: " + stopDistance.ToString("N1"));
    if (slowingDownForFinalApproach) CheckTimer(ref slowingDownForFinalApproachTimer, ref ranSlowingDownForFinalApproachTimer);
    Echo("Planetary Target: " + planetaryTarget);
}

public void RunCommands()
{
    if (stateRunCommands != null)
    {
        bool hasMoreActions = stateRunCommands.MoveNext();
        if (!hasMoreActions) { stateRunCommands.Dispose(); stateRunCommands = null; }
    }
}

public bool SecondStageReady()
{
    double distance = Vector3D.Distance(launchVector, Me.GetPosition());
    if (distanceFromLaunchPointForSecondStage > 0) Echo("Distance For Second Stage: " + distance.ToString("N0") + "/" + distanceFromLaunchPointForSecondStage.ToString("N0"));
    return (distanceFromLaunchPointForSecondStage <= 0 || distance >= distanceFromLaunchPointForSecondStage) && SufficientSecondStageThrust();
}

public bool SufficientSecondStageThrust()
{
    if (thrustAvailableForSecondStage <= 0) return true;
    float maxNewtons = 0f, currentNewtons = 0f;
    for (int i = 0; i < forwardThrustList.Count; i++)
    { maxNewtons += ((IMyThrust)forwardThrustList[i]).MaxThrust; currentNewtons += ((IMyThrust)forwardThrustList[i]).MaxEffectiveThrust; }
    string thrustPercentage = thrustAvailableForSecondStage.ToString("N2");
    if (thrustAvailableForSecondStage > 1.0) thrustPercentage = (thrustAvailableForSecondStage / 100.0).ToString("N2");
    Echo("Thrust Available For Second Stage: " + currentNewtons.ToString("N0") + "/" + maxNewtons.ToString("N0") + "=" + (currentNewtons / maxNewtons).ToString("N0") + "/" + thrustPercentage);
    if (thrustAvailableForSecondStage > 1.0) return currentNewtons / maxNewtons >= (float)(thrustAvailableForSecondStage / 100.0);
    return currentNewtons / maxNewtons >= (float)thrustAvailableForSecondStage;
}

public void SetFinalPoint()
{
    if (currentlyOnPlanet && planetaryTarget && Vector3D.Distance(currentVector, planetVector) > Vector3D.Distance(targetVector, planetVector))
    {
        if (Vector3D.Distance(currentVector, targetVector) >= waypointDistance * 1.5)
            currentWaypoint = ElevationPoint(targetVector, Vector3D.Distance(currentVector, planetVector) - (Math.Min(Vector3D.Distance(currentVector, targetVector) * 0.5, 1000)));
        else currentWaypoint = targetVector;
    }
    else currentWaypoint = targetVector;
}

public void EnableThrusters(bool enabled = false)
{
    try { for (int i = 0; i < allThrustList.Count; i++) ((IMyFunctionalBlock)allThrustList[i]).Enabled = enabled; }
    catch { }
}

public void TriggerConnection()
{
    IMyTerminalBlock block = GetDisconnectBlock<IMyShipConnector>(false, true);
    if (block != null) ((IMyShipConnector)block).Connect();
}

public void CalculateStopDistance()
{
    stopSpan = TimeSpan.FromSeconds(10);
    if (totalMass == 0.0) totalMass = (double)((IMyShipController)genRemote).CalculateShipMass().TotalMass;
    SetNewtons();
    double acceleration = forwardNewtons / totalMass;
    if (currentSpeed >= acceleration) { stopDistance = Math.Floor(currentSpeed / acceleration); stopDistance += 3; }
    else stopDistance = (currentSpeed / acceleration) * 3.0;
    stopDistance *= currentSpeed / 2.0;
}

public void SetNewtons()
{
    forwardNewtons = 0;
    for (int i = 0; i < forwardThrustList.Count; i++)
        try { forwardNewtons += (double)((IMyThrust)forwardThrustList[i]).MaxEffectiveThrust; } catch { }
}

public void CheckPanel()
{
    if (genLaunchPanel == null) SetLaunchPanel();
    else
    {
        int index = 0;
        string[] gpsList = ((IMyTextSurface)genLaunchPanel).GetText().Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < gpsList.Length; i++)
        {
            if (!gpsList[i].StartsWith("-"))
            {
                Vector3D tempVector = new Vector3D(0,0,0);
                if (Convert(gpsList[i], ref tempVector) && tempVector != new Vector3D(0,0,0))
                {
                    active = true; targetVector = tempVector;
                    if (randomizationDistance > 0.0)
                    {
                        Random rnd = new Random();
                        targetVector += Vector3D.Normalize(new Vector3D(rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000), rnd.NextDouble() * rnd.Next(-1000,1000))) * rnd.NextDouble() * randomizationDistance;
                    }
                    index = i; break;
                }
            }
        }
        if (active)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < gpsList.Length; i++) { if (i == index) builder.Append("-"); builder.AppendLine(gpsList[i]); }
            ((IMyTextSurface)genLaunchPanel).WriteText(builder);
        }
    }
}

public void SetLaunchPanel()
{
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
    bool tempSameGridOnly = sameGridOnly; sameGridOnly = false;
    GetBlocks<IMyTextPanel>(ref blocks, "launch");
    sameGridOnly = tempSameGridOnly;
    if (blocks.Count > 0) genLaunchPanel = blocks[0];
}

public void StopGyro()
{
    for (int i = 0; i < gyroList.Count; i++)
        try { ((IMyGyro)gyroList[i]).Yaw = 0f; ((IMyGyro)gyroList[i]).Pitch = 0f; ((IMyGyro)gyroList[i]).Roll = 0f; ((IMyGyro)gyroList[i]).GyroOverride = false; }
        catch { }
}

public bool MovingTorwardsPlanet()
{
    if (currentlyOnPlanet)
    {
        double distanceFromPlanet = Vector3D.Distance(currentVector, planetVector);
        if (distanceFromPlanet < lastPlanetDistance) { lastPlanetDistance = distanceFromPlanet; return true; }
        lastPlanetDistance = distanceFromPlanet;
    }
    return false;
}

public void PrepareForApproach()
{
    if (!decelerating) { decelerating = true; currentWaypoint = currentVector; }
    Echo("Current Speed: " + currentSpeed.ToString("N1") + " <= Drop Speed: " + dropSpeed.ToString("N1"));
    if (currentSpeed <= dropSpeed + 0.15)
    {
        decelerating = false; Echo("Slow Enough");
        slowingDownForFinalApproach = false;
        if (targetDistance >= Vector3D.Distance(planetaryWaypoint, targetVector) && currentlyOnPlanet && Vector3D.Distance(currentVector, planetaryWaypoint) > stopDistance * 1.15)
        { Echo("Too Far"); closingInOnTarget = false; currentWaypoint = planetaryWaypoint; }
        else { Echo("Beginning Approach Towards Target"); closingInOnTarget = true; SetFinalPoint(); }
    }
}

public void CheckDetonation()
{
    Echo("Checking For Detonation");
    if (targetDistance <= currentSpeed * 3.0) { Arm(); Echo("ARMED"); }
    if (Safe() && ((lastSpeed * speedPercentageDroppedForDetonation >= currentSpeed && targetDistance <= speedDropDetonationDistance) || (cameraList.Count > 0 && Capture()))) Detonate();
}

public bool Safe()
{
    if (safeZoneRadius <= 0.0 || genHomeDetachBlock == null || (genHomeDetachBlock.CubeGrid == Me.CubeGrid && genMergeBlock != null && genMergeBlock.CubeGrid == Me.CubeGrid)) return true;
    try
    {
        if (genHomeDetachBlock.CubeGrid != Me.CubeGrid) return Vector3D.Distance(genHomeDetachBlock.GetPosition(), currentVector) >= safeZoneRadius;
        else if (genMergeBlock != null && genMergeBlock.CubeGrid != Me.CubeGrid) return Vector3D.Distance(genMergeBlock.GetPosition(), currentVector) >= safeZoneRadius;
    }
    catch { }
    return true;
}

public void Arm()
{ for (int i = 0; i < warheadList.Count; i++) try { ((IMyWarhead)warheadList[i]).IsArmed = true; } catch { } }

public void Detonate()
{ for (int i = 0; i < warheadList.Count; i++) try { ((IMyWarhead)warheadList[i]).Detonate(); } catch { } }

public bool PointingTorwardsPlanet()
{
    if (currentlyOnPlanet)
    {
        Vector3D forwardVector = currentVector + (forwardReferenceBlock.WorldMatrix.Forward * 25.0);
        return Vector3D.Distance(forwardVector, planetVector) < Vector3D.Distance(currentVector, planetVector);
    }
    return false;
}

public bool Accelerate()
{
    bool pointingTowardsPlanet = PointingTorwardsPlanet();
    if (slowingDownForFinalApproach) return false;
    if ((movingTowardsPlanet && !pointingTowardsPlanet) || (elevating && !pointingTowardsPlanet && ((determineMaxSpeed && risingSpeed) || currentSpeed < peakSpeed * 0.99)) || FinalApproach() || SlowingDown()) return true;
    if (!currentlyOnPlanet && determineMaxSpeed)
    {
        if (currentSpeed >= maxSpeed) { accelerating = false; return false; }
        if (!accelerating || currentSpeed > lastSpeed) { accelerating = true; return true; }
    }
    accelerating = false; return false;
}

public bool Capture()
{
    if (currentCameraIndex >= cameraList.Count) currentCameraIndex = 0;
    try
    {
        IMyCameraBlock camera = (IMyCameraBlock)cameraList[currentCameraIndex];
        currentCameraIndex++;
        double range = largeWarheadRange;
        if (Me.CubeGrid.GridSizeEnum == MyCubeSize.Small) range = smallWarheadRange;
        MyDetectedEntityInfo info = camera.Raycast(1f, 0f, 0f);
        double modifiedRange = range;
        for (int i = -30; i <= 30; i += 30)
            for (int x = -30; x <= 30; x += 30)
            {
                modifiedRange = range;
                if (i != 0 || x != 0) modifiedRange *= 1.5;
                info = camera.Raycast(modifiedRange, (float)i, (float)x);
                if (info.HitPosition.HasValue && ValidTarget(info)) return true;
            }
    }
    catch { }
    return false;
}

public bool ValidTarget(MyDetectedEntityInfo info)
{
    return (targetEnemies   && info.Relationship == MyRelationsBetweenPlayerAndBlock.Enemies) ||
           (targetNeutrals  && info.Relationship == MyRelationsBetweenPlayerAndBlock.Neutral) ||
           (targetAllies    && info.Relationship == MyRelationsBetweenPlayerAndBlock.Friends) ||
           (targetOwner     && info.Relationship == MyRelationsBetweenPlayerAndBlock.Owner)   ||
           (targetUnowned   && info.Relationship == MyRelationsBetweenPlayerAndBlock.NoOwnership) ||
           (targetFaction   && info.Relationship == MyRelationsBetweenPlayerAndBlock.FactionShare);
}

public bool SlowingDown()
{
    if (closingInOnTarget || (planetaryWaypoint != new Vector3D(0,0,0) && Vector3D.Distance(planetaryWaypoint, currentVector) <= waypointDistance * 2.0 && currentSpeed > stopDistance)) return false;
    return currentSpeed < maxSpeed && currentSpeed <= peakSpeed * 0.9;
}

public bool FinalApproach()
{
    if (!closingInOnTarget) return false;
    MatrixD matrix = forwardReferenceBlock.WorldMatrix;
    Vector3D forwardVector = currentVector + (matrix.Forward * targetDistance);
    return currentSpeed < peakSpeed * 0.99 && Vector3D.Distance(forwardVector, targetVector) <= 10;
}

public double FinalApproachAccuracy()
{
    if (direct || !currentlyOnPlanet) return Vector3D.Distance(targetVector, forwardReferenceBlock.GetPosition() + (forwardReferenceBlock.WorldMatrix.Forward * Vector3D.Distance(forwardReferenceBlock.GetPosition(), targetVector)));
    Vector3D tVector = Vector3D.Normalize(targetVector - planetVector), mVector = Vector3D.Normalize(currentVector - planetVector);
    double distance = Vector3D.Distance(currentVector, planetVector);
    return Vector3D.Distance((tVector * distance) + planetVector, (mVector * distance) + planetVector);
}

public bool WaypointProximity(Vector3D waypoint)
{ return Vector3D.Distance(currentVector, waypoint) < (waypointDistance * 0.5) + (currentSpeed * 2.5); }

public Vector3D GenerateWaypoint()
{
    Vector3D forwardVector;
    if (Vector3D.Distance(currentVector, planetaryWaypoint) <= stopDistance * 1.15 && GoodPath())
    { slowingDownForFinalApproach = true; forwardVector = planetaryWaypoint; }
    else
    {
        double desiredElevation = DesiredPlanetDistance(), elevation;
        if (adjustElevation && ((IMyShipController)genRemote).TryGetPlanetElevation(MyPlanetElevation.Surface, out elevation))
            if (Math.Abs((desiredElevation - planetRadius) - elevation) > elevationVariance) desiredElevation += (desiredElevation - planetRadius) - elevation;
        Vector3D directVector = currentVector + (Vector3D.Normalize(targetVector - currentVector) * (waypointDistance + (currentSpeed * 3.0)));
        forwardVector = ElevationPoint(directVector, desiredElevation);
    }
    return forwardVector;
}

public bool GoodPath()
{
    Vector3D currentVelocity = Vector3D.Normalize(((IMyShipController)genRemote).GetShipVelocities().LinearVelocity),
    straightVelocity = Vector3D.Normalize(planetaryWaypoint - currentVector) * 0.75;
    return Vector3D.Distance(currentVector + currentVelocity, planetaryWaypoint) <= Vector3D.Distance(currentVector + straightVelocity, planetaryWaypoint);
}

public void NextWaypoint()
{
    if (currentlyOnPlanet)
    {
        if (planetaryTarget) Echo("Distance To Planetary Waypoint: " + Vector3D.Distance(currentVector, planetaryWaypoint).ToString("N1"));
        if ((!planetaryTarget && Vector3D.Distance(currentVector, planetaryWaypoint) < planetRadius * 0.13) ||
            (leavingPlanet && targetDistance < Vector3D.Distance(planetVector, targetVector)))
        { Echo("Leaving Planet"); leavingPlanet = true; currentWaypoint = targetVector; if (!closingInOnTarget && Vector3D.Distance(currentVector, planetaryWaypoint) <= stopDistance * 1.15) slowingDownForFinalApproach = true; }
        else if (elevating)
        {
            Echo("Elevating"); currentWaypoint = elevationWaypoint;
            if (WaypointProximity(elevationWaypoint) || Vector3D.Distance(elevationWaypoint, planetVector) < Vector3D.Distance(currentVector, planetVector))
            { elevating = false; ranElevatingTimer = false; }
        }
        else { Echo("Navigating"); currentWaypoint = GenerateWaypoint(); }
    }
    else
    {
        Echo("Navigating"); currentWaypoint = targetVector;
        if (!closingInOnTarget && targetDistance <= 3000 + currentSpeed) closingInOnTarget = true;
    }
    if (closingInOnTarget) ActivateCameras();
}

public void CheckTimer(ref IMyTerminalBlock timerBlock, ref bool timerBool, double distance = 9999999)
{
    if (!timerBool && timerBlock != null && (distance <= 0.0 || targetDistance <= distance))
    { timerBool = true; ((IMyTimerBlock)timerBlock).Trigger(); }
}

public void ActivateCameras()
{
    for (int i = 0; i < cameraList.Count; i++)
        try { ((IMyFunctionalBlock)cameraList[i]).Enabled = true; ((IMyCameraBlock)cameraList[i]).EnableRaycast = true; } catch { }
}

public Vector3D ElevationPoint(Vector3D vector, double elevation)
{ return (Vector3D.Normalize(vector - planetVector) * elevation) + planetVector; }

public void SetForwardThrust(float percent)
{
    for (int i = 0; i < forwardThrustList.Count; i++)
        try { ((IMyThrust)forwardThrustList[i]).ThrustOverridePercentage = percent; } catch { }
}

public void PlanetCheck()
{
    planetRecheckSpan += timeSinceLastRun;
    if (planetRecheckSpan.TotalSeconds >= 1.0 && (!currentlyOnPlanet || !PlanetaryVector(currentVector))) GetPlanet();
}

public bool PlanetaryVector(Vector3D vector)
{ return currentlyOnPlanet && Vector3D.Distance(vector, planetVector) < planetRadius * gravityWellMultiplier; }

public void GetPlanet()
{
    planetRecheckSpan = new TimeSpan(0,0,0);
    Vector3D vector;
    if (((IMyShipController)genRemote).TryGetPlanetPosition(out vector))
    {
        planetVector = vector;
        if (!currentlyOnPlanet)
        {
            elevating = true; lastPlanetDistance = Vector3D.Distance(currentVector, planetVector);
            double elevation;
            if (((IMyShipController)genRemote).TryGetPlanetElevation(MyPlanetElevation.Sealevel, out elevation))
                planetRadius = Vector3D.Distance(planetVector, currentVector) - elevation;
            gravityRadius = planetRadius * gravityWellMultiplier;
            usableAtmosphericRangeStart = planetRadius * 1.125;
            usableAtmosphericRangeEnd = gravityRadius * 0.98;
            planetaryTarget = Vector3D.Distance(planetVector, targetVector) < gravityRadius;
            planetaryWaypoint = ElevationPoint(targetVector, DesiredPlanetDistance());
            usePlanetaryWaypoint = Vector3D.Distance(planetaryWaypoint, targetVector) > 800.0;
            elevationWaypoint = ElevationPoint(currentVector, DesiredPlanetDistance());
        }
        currentlyOnPlanet = true;
    }
    else { currentlyOnPlanet = false; planetaryTarget = false; usePlanetaryWaypoint = false; leavingPlanet = false; ranLeavingPlanetTimer = false; elevating = false; ranElevatingTimer = false; planetaryWaypoint = new Vector3D(0,0,0); }
}

public double DesiredPlanetDistance()
{ return usableAtmosphericRangeStart + ((usableAtmosphericRangeEnd - usableAtmosphericRangeStart) * desiredElevationMultiplier); }

public void GetRemoteControl()
{
    List<IMyTerminalBlock> blockList = new List<IMyTerminalBlock>();
    Vector3D vector = Me.GetPosition();
    GetBlocks<IMyRemoteControl>(ref blockList);
    if (blockList.Count > 0) genRemote = GetClosestInWorld(blockList, vector);
}

public void Boot()
{
    startSpan += timeSinceLastRun;
    if (startSpan.TotalMilliseconds >= missileStartDelayInMilliseconds)
    { booted = GetMissileBlocks(); if (booted) shipLength = Vector3D.Distance(new Vector3D(Me.CubeGrid.Min), new Vector3D(Me.CubeGrid.Max)) * (double)Me.CubeGrid.GridSize; }
}

public void Disconnect()
{
    try { GetClosestDisconnectBlock(); } catch { Echo("Error caught getting disconnection blocks"); }
    connected = false;
    try
    {
        if (genConnector != null) ((IMyFunctionalBlock)genConnector).Enabled = false;
        if (genMergeBlock != null) ((IMyFunctionalBlock)genMergeBlock).Enabled = false;
        if (genPiston != null) ((IMyMechanicalConnectionBlock)genPiston).Detach();
        if (genRotor != null) ((IMyMechanicalConnectionBlock)genRotor).Detach();
    }
    catch { Echo("Error caught disconnecting"); }
    Runtime.UpdateFrequency = UpdateFrequency.None;
    Runtime.UpdateFrequency &= ~UpdateFrequency.Update100;
    Runtime.UpdateFrequency &= ~UpdateFrequency.Update10;
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
}

public bool AcceptableSecondStageBlock(IMyTerminalBlock block, bool secondStage = false)
{
    if (!twoStageMissile) return true;
    string customData = block.CustomData.ToLower(), tempKey = secondStageKeyword.ToLower();
    return (secondStage && (tempKey.Length == 0 || customData.Contains(tempKey))) ||
           (!secondStage && (tempKey.Length == 0 || !customData.Contains(tempKey)));
}

public bool GetMissileBlocks()
{
    if (stateGetMissileBlocks == null) stateGetMissileBlocks = StateGetMissileBlocks();
    bool hasMoreActions = stateGetMissileBlocks.MoveNext();
    if (!hasMoreActions) { stateGetMissileBlocks.Dispose(); stateGetMissileBlocks = null; return true; }
    return false;
}

public IEnumerator<bool> StateGetMissileBlocks()
{
    double actionMult = 0.25;
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
    launchVector = Me.GetPosition();
    if (genRemote == null)
    {
        try { GetBlocks<IMyRemoteControl>(ref blocks); if (blocks.Count > 0) { genRemote = blocks[0]; forwardReferenceBlock = genRemote; } } catch { }
        if (!AvailableActions(actionMult)) yield return true;
    }
    if (twoStageMissile)
    {
        try { GetBlocks<IMyRemoteControl>(ref blocks, "", "", true); if (blocks.Count > 0) { genRemoteStageTwo = blocks[0]; if (genRemote == null) genRemote = genRemoteStageTwo; } else if (genRemote != null) genRemoteStageTwo = genRemote; } catch { }
        if (!AvailableActions(actionMult)) yield return true;
    }
    if (genRemote != null) forwardReferenceBlock = genRemote;
    else throw new Exception("\nNo remote control found");
    if (twoStageMissile && genRemoteStageTwo != null) ((IMyShipController)genRemoteStageTwo).DampenersOverride = true;
    try { GetPlanet(); } catch { }
    if (!AvailableActions(actionMult)) yield return true;
    GetBlocks<IMyGyro>(ref gyroList);
    if (!AvailableActions(actionMult)) yield return true;
    if (twoStageMissile)
    {
        try { GetBlocks<IMyGyro>(ref gyroListStageTwo, "", "", true); if (gyroList.Count == 0 && gyroListStageTwo.Count > 0) gyroList = new List<IMyTerminalBlock>(gyroListStageTwo); if (gyroListStageTwo.Count == 0 && gyroList.Count > 0) gyroListStageTwo = new List<IMyTerminalBlock>(gyroList); } catch { }
        if (!AvailableActions(actionMult)) yield return true;
    }
    if (gyroList.Count == 0) throw new Exception("\nNo gyros found");
    GetBlocks<IMyWarhead>(ref warheadList);
    if (!AvailableActions(actionMult)) yield return true;
    GetBlocks<IMyCameraBlock>(ref cameraList);
    if (!AvailableActions(actionMult)) yield return true;
    if (forwardBlockReferenceFilter.Length != 0)
    {
        GetBlocks<IMyTerminalBlock>(ref blocks, "", forwardBlockReferenceFilter.ToLower());
        if (!AvailableActions(actionMult)) yield return true;
        if (blocks.Count > 0) forwardReferenceBlock = blocks[0];
        if (twoStageMissile)
        {
            GetBlocks<IMyTerminalBlock>(ref blocks, "", forwardBlockReferenceFilter.ToLower(), true);
            if (!AvailableActions(actionMult)) yield return true;
            if (blocks.Count > 0) { forwardReferenceBlockStageTwo = blocks[0]; if (forwardReferenceBlock == null) forwardReferenceBlock = forwardReferenceBlockStageTwo; }
            else if (forwardReferenceBlock != null) forwardReferenceBlockStageTwo = forwardReferenceBlock;
        }
    }
    Matrix matrix = new Matrix();
    forwardReferenceBlock.Orientation.GetMatrix(out matrix);
    GridTerminalSystem.GetBlocksOfType<IMyThrust>(forwardThrustList, t => (!twoStageMissile || AcceptableSecondStageBlock(t)) && ForwardThruster(t, matrix));
    if (!AvailableActions(actionMult)) yield return true;
    if (twoStageMissile)
    {
        if (forwardReferenceBlockStageTwo != null && genRemoteStageTwo != null && forwardReferenceBlockStageTwo != genMergeBlockStageTwo) forwardReferenceBlockStageTwo.Orientation.GetMatrix(out matrix);
        else if (genRemoteStageTwo != null) genRemoteStageTwo.Orientation.GetMatrix(out matrix);
        GridTerminalSystem.GetBlocksOfType<IMyThrust>(forwardThrustListStageTwo, t => AcceptableSecondStageBlock(t, true) && ForwardThruster(t, matrix));
        if (!AvailableActions(actionMult)) yield return true;
        if (forwardThrustList.Count == 0 && forwardThrustListStageTwo.Count > 0) forwardThrustList = new List<IMyTerminalBlock>(forwardThrustListStageTwo);
        if (forwardThrustListStageTwo.Count == 0 && forwardThrustList.Count > 0) forwardThrustListStageTwo = new List<IMyTerminalBlock>(forwardThrustList);
    }
    if (leavingPlanetTimerKeyword.Length != 0) { GetBlocks<IMyTimerBlock>(ref blocks, leavingPlanetTimerKeyword.ToLower()); if (blocks.Count > 0) leavingPlanetTimer = blocks[0]; if (!AvailableActions(actionMult)) yield return true; }
    if (elevatingTimerKeyword.Length != 0) { GetBlocks<IMyTimerBlock>(ref blocks, elevatingTimerKeyword.ToLower()); if (blocks.Count > 0) elevatingTimer = blocks[0]; if (!AvailableActions(actionMult)) yield return true; }
    if (slowingDownForFinalApproachTimerKeyword.Length != 0) { GetBlocks<IMyTimerBlock>(ref blocks, slowingDownForFinalApproachTimerKeyword.ToLower()); if (blocks.Count > 0) slowingDownForFinalApproachTimer = blocks[0]; if (!AvailableActions(actionMult)) yield return true; }
    if (closingInOnTargetTimerKeyword.Length != 0) { GetBlocks<IMyTimerBlock>(ref blocks, closingInOnTargetTimerKeyword.ToLower()); if (blocks.Count > 0) closingInOnTargetTimer = blocks[0]; if (!AvailableActions(actionMult)) yield return true; }
    if (secondStageTimerKeyword.Length != 0) { GetBlocks<IMyTimerBlock>(ref blocks, secondStageTimerKeyword.ToLower()); if (blocks.Count > 0) secondStageTimer = blocks[0]; if (!AvailableActions(actionMult)) yield return true; }
    EnableThrusters(true);
    yield return false;
}

public void GetBlocks<T>(ref List<IMyTerminalBlock> blocks, string name = "", string customData = "", bool twoStage = false) where T : class
{
    blocks.Clear();
    GridTerminalSystem.GetBlocksOfType<T>(blocks, b =>
        GlobalBlockFilter(b) &&
        (name.Length == 0 || b.CustomName.ToLower().Contains(name)) &&
        (customData.Length == 0 || b.CustomData.ToLower().Contains(customData)) &&
        (!twoStage || !twoStageMissile || AcceptableSecondStageBlock(b)));
}

public bool GlobalBlockFilter(IMyTerminalBlock b)
{
    return (!sameGridOnly || b.CubeGrid == Me.CubeGrid) &&
           (!b.CustomData.ToLower().Contains(exclusionKeyword)) &&
           (globalFilter.Length == 0 || b.CustomData.ToLower().Contains(globalFilter));
}

public bool ForwardThruster(IMyTerminalBlock block, Matrix refMatrix)
{
    Matrix matrix; block.Orientation.GetMatrix(out matrix);
    string customData = block.CustomData.ToLower(), tempKey = secondStageKeyword.ToLower();
    if (GlobalBlockFilter(block) && (!twoStageMissile || tempKey.Length == 0 || !customData.Contains(tempKey))) allThrustList.Add(block);
    if (GlobalBlockFilter(block) && (twoStageMissile && (tempKey.Length == 0 || customData.Contains(tempKey)))) allThrustListStageTwo.Add(block);
    return matrix.Forward == refMatrix.Backward;
}

public bool Convert(string argument, ref Vector3D tempVector)
{
    try
    {
        string[] args = argument.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        Vector3D vector = new Vector3D(double.Parse(args[2]), double.Parse(args[3]), double.Parse(args[4]));
        Echo($"Coordinates Found: {vector.ToString("N0")}"); tempVector = vector; return true;
    }
    catch { }
    return false;
}

public IMyTerminalBlock GetPairedDisconnectBlock<T>(IMyTerminalBlock block) where T : class
{
    Vector3D position = block.GetPosition();
    List<IMyTerminalBlock> blockList = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocksOfType<T>(blockList, b => b != block && Vector3D.Distance(position, b.GetPosition()) <= 4.9);
    if (blockList.Count == 1) return blockList[0];
    if (blockList.Count > 1) return GetClosestInWorld(blockList, position);
    return block;
}

public void GetClosestDisconnectBlock()
{
    if (mergeBlockSeparation)
    {
        IMyTerminalBlock block = GetDisconnectBlock<IMyShipMergeBlock>();
        if (block != null) genMergeBlock = block;
        if (twoStageMissile) { block = GetDisconnectBlock<IMyShipMergeBlock>(true); if (block != null) genMergeBlockStageTwo = block; }
        if (block != null) { IMyTerminalBlock paired = GetPairedDisconnectBlock<IMyShipMergeBlock>(block); if (paired != block) genHomeDetachBlock = paired; }
    }
    if (connectorSeparation)
    {
        IMyTerminalBlock block = GetDisconnectBlock<IMyShipConnector>(false);
        if (block != null) genConnector = block;
        if (twoStageMissile) { block = GetDisconnectBlock<IMyShipConnector>(true); if (block != null) genConnectorStageTwo = block; }
        if (block != null && genHomeDetachBlock == null) { IMyTerminalBlock paired = GetPairedDisconnectBlock<IMyShipConnector>(block); if (paired != block) genHomeDetachBlock = paired; }
    }
    if (rotorSeparation)
    {
        IMyTerminalBlock block = GetDisconnectBlock<IMyMotorStator>();
        if (block != null) genRotor = block;
        if (twoStageMissile) { block = GetDisconnectBlock<IMyMotorStator>(true); if (block != null) genRotorStageTwo = block; }
        if (block != null && genHomeDetachBlock == null) genHomeDetachBlock = block;
    }
    if (pistonSeparation) { IMyTerminalBlock block = GetDisconnectBlock<IMyPistonBase>(); if (block != null) genPiston = block; if (block != null && genHomeDetachBlock == null) genHomeDetachBlock = block; }
}

public IMyTerminalBlock GetDisconnectBlock<T>(bool secondStage = false, bool readyToLock = false) where T : class
{
    List<IMyTerminalBlock> blockList = new List<IMyTerminalBlock>();
    string tempMergeFilter = mergeFilter.ToLower();
    GridTerminalSystem.GetBlocksOfType<T>(blockList, b => AcceptableDisconnectBlock(b, secondStage, readyToLock, tempMergeFilter));
    if (blockList.Count > 0) return GetClosest(blockList);
    return null;
}

public bool AcceptableDisconnectBlock(IMyTerminalBlock block, bool secondStage, bool readyToLock, string tempMergeFilter)
{
    string customData = block.CustomData.ToLower();
    return GlobalBlockFilter(block) && ReadyToLock(block, readyToLock) &&
           (mergeFilter.Length == 0 || customData.Contains(tempMergeFilter)) &&
           AcceptableSecondStageBlock(block, secondStage) &&
           (separatorDistanceFilter <= 0 || Vector3D.Distance(block.GetPosition(), Me.GetPosition()) <= separatorDistanceFilter);
}

public bool ReadyToLock(IMyTerminalBlock block, bool readyToLock)
{
    if (!readyToLock || !(block is IMyShipConnector)) return true;
    return ((IMyShipConnector)block).Status == MyShipConnectorStatus.Connectable;
}

public bool AvailableActions(double mult = 1.0)
{
    return Runtime.CurrentInstructionCount < Runtime.MaxInstructionCount * mult &&
           (DateTime.Now - tickStartTime).TotalMilliseconds <= 2.5 * mult;
}

public IMyTerminalBlock GetClosestInWorld(List<IMyTerminalBlock> blockList, Vector3D vector)
{
    if (blockList.Count == 1) return blockList[0];
    if (blockList.Count == 0) return Me;
    int index = 0; double distance = Vector3D.Distance(blockList[0].GetPosition(), vector);
    for (int i = 1; i < blockList.Count; i++) { double nd = Vector3D.Distance(blockList[i].GetPosition(), vector); if (nd < distance) { distance = nd; index = i; } }
    return blockList[index];
}

public IMyTerminalBlock GetClosest(List<IMyTerminalBlock> blockList)
{
    if (blockList.Count == 1) return blockList[0];
    Vector3D position = Convert(Me.Position), nextPosition;
    double distance = -1, nextDistance; int index = -1;
    for (int i = 0; i < blockList.Count; i++)
    {
        nextPosition = Convert(blockList[i].Position); nextDistance = Vector3D.Distance(position, nextPosition);
        if (distance == -1) { index = i; distance = nextDistance; }
        else if (nextDistance < distance) { genHomeDetachBlock = blockList[index]; distance = nextDistance; index = i; }
    }
    return blockList[index];
}

public Vector3D Convert(Vector3I vector) { return new Vector3D(vector.X, vector.Y, vector.Z); }

public bool ConvertVector(out Vector3D vector, string arg)
{
    vector = new Vector3D(0,0,0);
    try { string[] subArgs = arg.Split(':'); vector.X = double.Parse(subArgs[0]); vector.Y = double.Parse(subArgs[1]); vector.Z = double.Parse(subArgs[2]); }
    catch { return false; }
    return true;
}

public string ConvertVector(Vector3D vector) { return $"{Math.Round(vector.X,6)}:{Math.Round(vector.Y,6)}:{Math.Round(vector.Z,6)}"; }

public void Point(Vector3D vector)
{
    aimVector = GetDirectionTo(vector, forwardReferenceBlock);
    if (!elevating && !closingInOnTarget && currentlyOnPlanet) aimVector.Z = GetRoll(planetVector, forwardReferenceBlock);
    else if (closingInOnTarget && finalApproachRotationSpeedPercentage != 0.0 &&
            (descentAccuracyForRotation == 0.0 || !currentlyOnPlanet || !planetaryTarget || currentDescentAccuracy <= descentAccuracyForRotation))
    {
        if (finalApproachRotationSpeedPercentage > 1.0) aimVector.Z = (finalApproachRotationSpeedPercentage / 100.0) * Math.PI;
        else aimVector.Z = finalApproachRotationSpeedPercentage * Math.PI;
    }
    ApplyGyroOverride();
}

public double CappedValue(double value, double max)
{
    if (value == 0.0) return 0.0;
    double multiplier = value < 0 ? -1 : 1;
    return Math.Min(Math.Abs(value), max) * multiplier;
}

public void ApplyGyroOverride()
{
    aimVector = new Vector3D(CappedValue(aimVector.X, Math.PI * 0.5), CappedValue(aimVector.Y, Math.PI * 0.5), CappedValue(aimVector.Z, Math.PI * 0.5));
    gyroOverrideState.MoveNext();
}

public IEnumerator<bool> ApplyGyroOverrideState()
{
    while (true)
    {
        var rotationVec = new Vector3D(-aimVector.X, aimVector.Y, aimVector.Z);
        var shipMatrix = forwardReferenceBlock.WorldMatrix;
        var relativeRotationVec = Vector3D.TransformNormal(rotationVec, shipMatrix);
        double reps = 0, limit = 10;
        if (Math.Ceiling((double)gyroList.Count / 4.0) < limit) limit = Math.Ceiling((double)gyroList.Count / 4.0);
        foreach (IMyGyro thisGyro in gyroList)
        {
            try
            {
                var gyroMatrix = thisGyro.WorldMatrix;
                var transformedRotationVec = Vector3D.TransformNormal(relativeRotationVec, Matrix.Transpose(gyroMatrix));
                thisGyro.Pitch = (float)transformedRotationVec.X;
                thisGyro.Yaw   = (float)transformedRotationVec.Y;
                thisGyro.Roll  = (float)transformedRotationVec.Z;
                thisGyro.GyroOverride = true;
            }
            catch { }
            if (reps >= limit) { reps = 0; yield return true; }
        }
        yield return true;
    }
}

public double GetRoll(Vector3D targetVec, IMyTerminalBlock originBlock)
{
    MatrixD matrix = originBlock.WorldMatrix;
    Vector3D originVector = originBlock.GetPosition(), forwardVector = matrix.Down * 5.0, rightVector = matrix.Right * 5.0;
    forwardVector += originVector; rightVector += originVector;
    double originRange = Vector3D.Distance(originVector, targetVec), forwardRange = Vector3D.Distance(forwardVector, targetVec),
           rightRange = Vector3D.Distance(rightVector, targetVec), rightLength = Vector3D.Distance(rightVector, originVector);
    double ThetaY = Math.Acos((rightRange * rightRange - rightLength * rightLength - originRange * originRange) / (-2.0 * rightLength * originRange));
    double outYaw = 90.0 - (ThetaY * 180.0 / Math.PI);
    if (originRange < forwardRange) outYaw = 180.0 - outYaw;
    if (outYaw > 180.0) outYaw = -1.0 * (360.0 - outYaw);
    return (outYaw / 180.0) * Math.PI * gyroMultiplier;
}

public Vector3D GetDirectionTo(Vector3D targetVec, IMyTerminalBlock originBlock)
{
    MatrixD matrix = originBlock.WorldMatrix;
    Vector3D originVector = originBlock.GetPosition(), forwardVector = matrix.Forward * 5.0, rightVector = matrix.Right * 5.0, upVector = matrix.Up * 5.0;
    forwardVector += originVector; rightVector += originVector; upVector += originVector;
    double originRange = Vector3D.Distance(originVector, targetVec), forwardRange = Vector3D.Distance(forwardVector, targetVec),
           upRange = Vector3D.Distance(upVector, targetVec), rightRange = Vector3D.Distance(rightVector, targetVec),
           upLength = Vector3D.Distance(upVector, originVector), rightLength = Vector3D.Distance(rightVector, originVector);
    double ThetaP = Math.Acos((upRange * upRange - upLength * upLength - originRange * originRange) / (-2.0 * upLength * originRange)),
           ThetaY = Math.Acos((rightRange * rightRange - rightLength * rightLength - originRange * originRange) / (-2.0 * rightLength * originRange));
    double outPitch = 90.0 - (ThetaP * 180.0 / Math.PI), outYaw = 90.0 - (ThetaY * 180.0 / Math.PI);
    if (originRange < forwardRange) outPitch = 180 - outPitch;
    if (outPitch > 180.0) outPitch = -1 * (360 - outPitch);
    if (originRange < forwardRange) outYaw = 180.0 - outYaw;
    if (outYaw > 180.0) outYaw = -1.0 * (360.0 - outYaw);
    return new Vector3D((outPitch / 180.0) * Math.PI * gyroMultiplier, (outYaw / 180.0) * Math.PI * gyroMultiplier, 0);
}

public void SaveData()
{
    StringBuilder builder = new StringBuilder();
    builder.AppendLine("Flight Settings"); builder.AppendLine();
    builder.AppendLine("adjustElevation=" + adjustElevation);
    builder.AppendLine("descentAccuracyForRotation=" + descentAccuracyForRotation);
    builder.AppendLine("desiredElevationMultiplier=" + desiredElevationMultiplier);
    builder.AppendLine("determineMaxSpeed=" + determineMaxSpeed);
    builder.AppendLine("disableThrustersAtDistanceFromTarget=" + disableThrustersAtDistanceFromTarget);
    builder.AppendLine("dropSpeed=" + dropSpeed);
    builder.AppendLine("elevationVariance=" + elevationVariance);
    builder.AppendLine("finalApproachRotationSpeedPercentage=" + finalApproachRotationSpeedPercentage);
    builder.AppendLine("gyroMultiplier=" + gyroMultiplier);
    builder.AppendLine("maxSpeed=" + maxSpeed);
    builder.AppendLine("waypointDistance=" + waypointDistance);
    builder.AppendLine(); builder.AppendLine("Launch Settings"); builder.AppendLine();
    builder.AppendLine("forwardBlockReferenceFilter=" + forwardBlockReferenceFilter);
    builder.AppendLine("launchDelayInSeconds=" + launchDelayInSeconds);
    builder.AppendLine("mergeFilter=" + mergeFilter);
    builder.AppendLine("missileStartDelayInMilliseconds=" + missileStartDelayInMilliseconds);
    builder.AppendLine("separatorDistanceFilter=" + separatorDistanceFilter);
    builder.AppendLine("mergeBlockSeparation=" + mergeBlockSeparation);
    builder.AppendLine("connectorSeparation=" + connectorSeparation);
    builder.AppendLine("rotorSeparation=" + rotorSeparation);
    builder.AppendLine("pistonSeparation=" + pistonSeparation);
    builder.AppendLine("sameGridOnly=" + sameGridOnly);
    builder.AppendLine("turnDelayInSeconds=" + turnDelayInSeconds);
    builder.AppendLine(); builder.AppendLine("Detonation Settings"); builder.AppendLine();
    builder.AppendLine("safeZoneRadius=" + safeZoneRadius);
    builder.AppendLine("randomizationDistance=" + randomizationDistance);
    builder.AppendLine("targetEnemies=" + targetEnemies);
    builder.AppendLine("targetNeutrals=" + targetNeutrals);
    builder.AppendLine("targetAllies=" + targetAllies);
    builder.AppendLine("targetOwner=" + targetOwner);
    builder.AppendLine("targetUnowned=" + targetUnowned);
    builder.AppendLine("targetFaction=" + targetFaction);
    builder.AppendLine("speedPercentageDroppedForDetonation=" + speedPercentageDroppedForDetonation);
    builder.AppendLine("speedDropDetonationDistance=" + speedDropDetonationDistance);
    builder.AppendLine(); builder.AppendLine("Timer Settings"); builder.AppendLine();
    builder.AppendLine("closingInOnTargetTimerKeyword=" + closingInOnTargetTimerKeyword);
    builder.AppendLine("descentAccuracyForClosingInTimer=" + descentAccuracyForClosingInTimer);
    builder.AppendLine("runClosingInOnTargetTimerDistance=" + runClosingInOnTargetTimerDistance);
    builder.AppendLine("elevatingTimerKeyword=" + elevatingTimerKeyword);
    builder.AppendLine("leavingPlanetTimerKeyword=" + leavingPlanetTimerKeyword);
    builder.AppendLine("slowingDownForFinalApproachTimerKeyword=" + slowingDownForFinalApproachTimerKeyword);
    builder.AppendLine(); builder.AppendLine("Two-Stage Missile Settings"); builder.AppendLine();
    builder.AppendLine("distanceFromLaunchPointForSecondStage=" + distanceFromLaunchPointForSecondStage);
    builder.AppendLine("secondStageKeyword=" + secondStageKeyword);
    builder.AppendLine("secondStageTimerKeyword=" + secondStageTimerKeyword);
    builder.AppendLine("thrustAvailableForSecondStage=" + thrustAvailableForSecondStage);
    builder.AppendLine("twoStageMissile=" + twoStageMissile);
    builder.AppendLine(); builder.AppendLine("Extra Filter Settings"); builder.AppendLine();
    builder.AppendLine("globalFilter=" + globalFilter);
    builder.AppendLine("exclusionKeyword=" + exclusionKeyword);
    builder.AppendLine(); builder.AppendLine("Run Settings"); builder.AppendLine();
    builder.AppendLine("gravityWellMultiplier=" + gravityWellMultiplier);
    builder.AppendLine("idleTicks=" + idleTicks);
    builder.AppendLine("largeWarheadRange=" + largeWarheadRange);
    builder.AppendLine("smallWarheadRange=" + smallWarheadRange);
    builder.AppendLine(); builder.AppendLine("Multiplayer Settings");
    builder.AppendLine("Be Careful Modifying Settings Beyond 'rememberLaunchDetails'"); builder.AppendLine();
    builder.AppendLine("rememberLaunchDetails=" + rememberLaunchDetails);
    if (rememberLaunchDetails)
    {
        builder.AppendLine("active=" + active); builder.AppendLine("connected=" + connected);
        builder.AppendLine("currentlyOnPlanet=" + currentlyOnPlanet); builder.AppendLine("elevating=" + elevating);
        builder.AppendLine("closingInOnTarget=" + closingInOnTarget); builder.AppendLine("planetaryTarget=" + planetaryTarget);
        builder.AppendLine("leavingPlanet=" + leavingPlanet); builder.AppendLine("slowingDownForFinalApproach=" + slowingDownForFinalApproach);
        builder.AppendLine("movingTowardsPlanet=" + movingTowardsPlanet); builder.AppendLine("risingSpeed=" + risingSpeed);
        builder.AppendLine("usePlanetaryWaypoint=" + usePlanetaryWaypoint); builder.AppendLine("decelerating=" + decelerating);
        builder.AppendLine("ranLeavingPlanetTimer=" + ranLeavingPlanetTimer); builder.AppendLine("ranElevatingTimer=" + ranElevatingTimer);
        builder.AppendLine("ranSlowingDownForFinalApproachTimer=" + ranSlowingDownForFinalApproachTimer);
        builder.AppendLine("ranClosingInOnTargetTimer=" + ranClosingInOnTargetTimer);
        builder.AppendLine("thrustersEnabled=" + thrustersEnabled); builder.AppendLine("abandondedFirstStage=" + abandondedFirstStage);
        builder.AppendLine("direct=" + direct); builder.AppendLine("directPosition=" + directPosition);
        builder.AppendLine("peakSpeed=" + peakSpeed); builder.AppendLine("lastSpeed=" + lastSpeed);
        builder.AppendLine("lastPlanetDistance=" + lastPlanetDistance); builder.AppendLine("stopDistance=" + stopDistance);
        builder.AppendLine("currentDescentAccuracy=" + currentDescentAccuracy); builder.AppendLine("targetDistance=" + targetDistance);
        builder.AppendLine("planetVector=" + ConvertVector(planetVector)); builder.AppendLine("targetVector=" + ConvertVector(targetVector));
        builder.AppendLine("planetaryWaypoint=" + ConvertVector(planetaryWaypoint)); builder.AppendLine("elevationWaypoint=" + ConvertVector(elevationWaypoint));
        builder.AppendLine("launchVector=" + ConvertVector(launchVector)); builder.AppendLine("directLaunchVector=" + ConvertVector(directLaunchVector));
        builder.AppendLine("directLaunchPosition=" + ConvertVector(directLaunchPosition));
    }
    builder.AppendLine(); builder.AppendLine("script=" + script); builder.AppendLine("version=" + version);
    Me.CustomData = builder.ToString().Trim().Replace("\r", String.Empty);
    settingsBackup = Me.CustomData; Storage = Me.CustomData;
}

public void LoadData()
{
    if (Me.CustomData.Length != 0 && Storage != Me.CustomData) Storage = Me.CustomData;
    if (Storage.Length != 0)
    {
        if (Me.CustomData.Length == 0) Me.CustomData = Storage;
        settingsBackup = Me.CustomData;
        if (Me.CustomData.Length == 0 && Storage.Length != 0) Me.CustomData = Storage;
        string[] settingArray = Storage.Replace(";", "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        commandStringList.Clear();
        for (int i = 0; i < settingArray.Length; i++)
            if (settingArray[i].Contains("=") || settingArray[i].ToLower().Contains("action:"))
                try { ProcessSetting(settingArray[i]); } catch { }
        if (!correctScript || !correctVersion) SaveData();
        correctScript = true; correctVersion = true;
        settingsBackup = Me.CustomData;
    }
    else SaveData();
}

public void ProcessSetting(string settingString)
{
    string settingDelimiter = "";
    if (settingString.Contains("=")) settingDelimiter = "=";
    else if (settingString.Contains("|")) settingDelimiter = "|";
    string settingKey = "", settingValue = "";
    if (settingDelimiter.Length != 0)
    {
        settingKey = settingString.Substring(0, settingString.IndexOf(settingDelimiter));
        settingValue = settingString.Substring(settingString.IndexOf(settingDelimiter) + 1).ToLower();
    }
    bool settingBool = settingValue.ToLower() == "true";
    double settingDouble = 0.0; try { settingDouble = double.Parse(settingValue); } catch { }
    switch (settingKey)
    {
        case "gravityWellMultiplier": gravityWellMultiplier = settingDouble; break;
        case "gyroMultiplier": gyroMultiplier = settingDouble; break;
        case "waypointDistance": waypointDistance = settingDouble; break;
        case "desiredElevationMultiplier": desiredElevationMultiplier = settingDouble; break;
        case "elevationVariance": elevationVariance = settingDouble; break;
        case "largeWarheadRange": largeWarheadRange = settingDouble; break;
        case "smallWarheadRange": smallWarheadRange = settingDouble; break;
        case "maxSpeed": maxSpeed = settingDouble; peakSpeed = settingDouble; break;
        case "dropSpeed": dropSpeed = settingDouble; break;
        case "missileStartDelayInMilliseconds": missileStartDelayInMilliseconds = settingDouble; break;
        case "disableThrustersAtDistanceFromTarget": disableThrustersAtDistanceFromTarget = settingDouble; break;
        case "runClosingInOnTargetTimerDistance": runClosingInOnTargetTimerDistance = settingDouble; break;
        case "finalApproachRotationSpeedPercentage": finalApproachRotationSpeedPercentage = settingDouble; break;
        case "descentAccuracyForClosingInTimer": descentAccuracyForClosingInTimer = settingDouble; break;
        case "descentAccuracyForRotation": descentAccuracyForRotation = settingDouble; break;
        case "separatorDistanceFilter": separatorDistanceFilter = settingDouble; break;
        case "distanceFromLaunchPointForSecondStage": distanceFromLaunchPointForSecondStage = settingDouble; break;
        case "thrustAvailableForSecondStage": thrustAvailableForSecondStage = settingDouble; break;
        case "turnDelayInSeconds": turnDelayInSeconds = settingDouble; break;
        case "launchDelayInSeconds": launchDelayInSeconds = settingDouble; break;
        case "safeZoneRadius": safeZoneRadius = settingDouble; break;
        case "randomizationDistance": randomizationDistance = settingDouble; break;
        case "speedDropDetonationDistance": speedDropDetonationDistance = settingDouble; break;
        case "speedPercentageDroppedForDetonation": speedPercentageDroppedForDetonation = settingDouble; break;
        case "peakSpeed": peakSpeed = settingDouble; break;
        case "lastSpeed": lastSpeed = settingDouble; break;
        case "lastPlanetDistance": lastPlanetDistance = settingDouble; break;
        case "stopDistance": stopDistance = settingDouble; break;
        case "currentDescentAccuracy": currentDescentAccuracy = settingDouble; break;
        case "targetDistance": targetDistance = settingDouble; break;
        case "idleTicks": idleTicks = (int)settingDouble; break;
        case "planetVector": ConvertVector(out planetVector, settingValue); break;
        case "targetVector": ConvertVector(out targetVector, settingValue); break;
        case "planetaryWaypoint": ConvertVector(out planetaryWaypoint, settingValue); break;
        case "elevationWaypoint": ConvertVector(out elevationWaypoint, settingValue); break;
        case "launchVector": ConvertVector(out launchVector, settingValue); break;
        case "directLaunchVector": ConvertVector(out directLaunchVector, settingValue); break;
        case "directLaunchPosition": ConvertVector(out directLaunchPosition, settingValue); break;
        case "mergeFilter": mergeFilter = settingValue; break;
        case "forwardBlockReferenceFilter": forwardBlockReferenceFilter = settingValue; break;
        case "leavingPlanetTimerKeyword": leavingPlanetTimerKeyword = settingValue; break;
        case "elevatingTimerKeyword": elevatingTimerKeyword = settingValue; break;
        case "slowingDownForFinalApproachTimerKeyword": slowingDownForFinalApproachTimerKeyword = settingValue; break;
        case "closingInOnTargetTimerKeyword": closingInOnTargetTimerKeyword = settingValue; break;
        case "secondStageTimerKeyword": secondStageTimerKeyword = settingValue; break;
        case "secondStageKeyword": secondStageKeyword = settingValue; break;
        case "globalFilter": globalFilter = settingValue; break;
        case "exclusionKeyword": exclusionKeyword = settingValue; break;
        case "mergeBlockSeparation": mergeBlockSeparation = settingBool; break;
        case "connectorSeparation": connectorSeparation = settingBool; break;
        case "rotorSeparation": rotorSeparation = settingBool; break;
        case "pistonSeparation": pistonSeparation = settingBool; break;
        case "sameGridOnly": sameGridOnly = settingBool; break;
        case "adjustElevation": adjustElevation = settingBool; break;
        case "twoStageMissile": twoStageMissile = settingBool; break;
        case "targetEnemies": targetEnemies = settingBool; break;
        case "targetNeutrals": targetNeutrals = settingBool; break;
        case "targetAllies": targetAllies = settingBool; break;
        case "targetOwner": targetOwner = settingBool; break;
        case "targetUnowned": targetUnowned = settingBool; break;
        case "targetFaction": targetFaction = settingBool; break;
        case "determineMaxSpeed": determineMaxSpeed = settingBool; break;
        case "rememberLaunchDetails": rememberLaunchDetails = settingBool; break;
        case "active": active = settingBool; break;
        case "connected": connected = settingBool; break;
        case "currentlyOnPlanet": currentlyOnPlanet = settingBool; break;
        case "elevating": elevating = settingBool; break;
        case "closingInOnTarget": closingInOnTarget = settingBool; break;
        case "planetaryTarget": planetaryTarget = settingBool; break;
        case "leavingPlanet": leavingPlanet = settingBool; break;
        case "slowingDownForFinalApproach": slowingDownForFinalApproach = settingBool; break;
        case "movingTowardsPlanet": movingTowardsPlanet = settingBool; break;
        case "risingSpeed": risingSpeed = settingBool; break;
        case "usePlanetaryWaypoint": usePlanetaryWaypoint = settingBool; break;
        case "decelerating": decelerating = settingBool; break;
        case "ranLeavingPlanetTimer": ranLeavingPlanetTimer = settingBool; break;
        case "ranElevatingTimer": ranElevatingTimer = settingBool; break;
        case "ranSlowingDownForFinalApproachTimer": ranSlowingDownForFinalApproachTimer = settingBool; break;
        case "ranClosingInOnTargetTimer": ranClosingInOnTargetTimer = settingBool; break;
        case "thrustersEnabled": thrustersEnabled = settingBool; break;
        case "abandondedFirstStage": abandondedFirstStage = settingBool; break;
        case "direct": direct = settingBool; break;
        case "directPosition": directPosition = settingBool; break;
        case "script": if (settingValue != script) correctScript = false; break;
        case "version": if (version != settingDouble) correctVersion = false; break;
    }
}

// ════════════════════════════════════════════════════════════
// AGM SUPPORT CLASSES
// ════════════════════════════════════════════════════════════

class AgmPdController
{
    readonly double kP, kD;
    double lastError = 0; bool firstRun = true;
    public AgmPdController(double kP, double kD) { this.kP = kP; this.kD = kD; }
    public double Update(double error, int ticks = 1)
    {
        if (firstRun) { lastError = error; firstRun = false; return kP * error; }
        double deriv = (error - lastError) * ticks; lastError = error;
        return (kP * error) + (kD * deriv);
    }
    public void Reset() { firstRun = true; lastError = 0; }
}

class AgmGyroManager
{
    List<IMyGyro> gyros;
    byte[] pitchMap, yawMap;
    static readonly Action<IMyGyro, float>[] AxisSet =
    {
        (g,v) => { g.Yaw=-v; }, (g,v) => { g.Yaw=v; }, (g,v) => { g.Pitch=-v; },
        (g,v) => { g.Pitch=v; }, (g,v) => { g.Roll=-v; }, (g,v) => { g.Roll=v; },
    };
    public AgmGyroManager(List<IMyGyro> gyroList, IMyTerminalBlock fwdRef)
    {
        gyros = gyroList; pitchMap = new byte[gyros.Count]; yawMap = new byte[gyros.Count];
        MatrixD fwd = fwdRef.WorldMatrix;
        for (int i = 0; i < gyros.Count; i++)
        {
            pitchMap[i] = DirIdx(gyros[i].WorldMatrix.GetClosestDirection(fwd.Up));
            yawMap[i]   = DirIdx(gyros[i].WorldMatrix.GetClosestDirection(fwd.Left));
        }
    }
    static byte DirIdx(Base6Directions.Direction d)
    {
        switch (d)
        {
            case Base6Directions.Direction.Up:       return 1;
            case Base6Directions.Direction.Down:     return 0;
            case Base6Directions.Direction.Left:     return 2;
            case Base6Directions.Direction.Right:    return 3;
            case Base6Directions.Direction.Forward:  return 4;
            default:                                  return 5;
        }
    }
    public void Apply(Vector3D aim, AgmPdController pitchPD, AgmPdController yawPD, double limit)
    {
        float p = (float)pitchPD.Update(Clamp(aim.X, -limit, limit));
        float y = (float)yawPD  .Update(Clamp(aim.Y, -limit, limit));
        for (int i = 0; i < gyros.Count; i++)
        {
            if (!gyros[i].IsFunctional) continue;
            gyros[i].GyroOverride = true;
            AxisSet[pitchMap[i]](gyros[i], p);
            AxisSet[yawMap[i]]  (gyros[i], y);
        }
    }
    public void Zero() { foreach (var g in gyros) { g.GyroOverride = false; g.Pitch = g.Yaw = g.Roll = 0f; } }
    static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}

class AgmHomingTracker
{
    public enum TrackState { NoLock, Tracking, Slipped }
    public TrackState State { get; private set; } = TrackState.NoLock;
    public bool ExcludeFriendly, ExcludeNeutral;
    double coneLimit, coneStep;
    List<IMyCameraBlock> allCameras;
    List<CameraGroup> groups;
    CameraGroup lastGroup;
    float chargeTimeFactor;
    double coneTangent;
    bool omnidirectional;
    MyDetectedEntityInfo locked;
    int tickOfLock, nextScanTick, tick;
    Vector3D hitOffsetLocal;
    bool useLocalOffset;
    double boundingRadius, rangeFactor = 1.0;
    int nextAvailUpdate, computedInterval, extraBudget, relockFails;
    const int MIN_INTERVAL = 6, FALLBACK_INTERVAL = 12, AVAIL_INTERVAL = 30, LOST_TIMEOUT = 120, MAX_RELOCK = 5;
    const float DT = 1f / 60f;
    static readonly int[,] Spiral = { {1,0},{-1,0},{0,1},{0,-1},{-1,-1},{1,-1},{-1,1},{1,1} };

    public AgmHomingTracker(List<IMyCameraBlock> cams, bool exFriendly, bool exNeutral, double coneLimitDeg = 30.0, double coneStepDeg = 30.0)
    {
        allCameras = cams; ExcludeFriendly = exFriendly; ExcludeNeutral = exNeutral;
        coneLimit = coneLimitDeg; coneStep = coneStepDeg;
        if (cams.Count > 0)
        {
            float camCone = cams[0].RaycastConeLimit;
            if (camCone <= 0f || camCone >= 180f) { omnidirectional = true; coneTangent = 1.0; }
            else { omnidirectional = false; coneTangent = Math.Tan(MathHelper.ToRadians(90 - camCone)); }
            chargeTimeFactor = cams[0].TimeUntilScan(cams[0].AvailableScanRange + 1000) * 0.00006f;
        }
        else { chargeTimeFactor = 0.03f; coneTangent = 1.0; omnidirectional = true; }
        BuildGroups(); EnableCameras();
    }

    void EnableCameras() { foreach (var c in allCameras) { c.Enabled = true; c.EnableRaycast = true; } }

    void BuildGroups()
    {
        var dict = new Dictionary<string, CameraGroup>();
        foreach (var cam in allCameras)
        {
            string key = cam.CubeGrid.EntityId + "-" + (int)cam.Orientation.Forward;
            if (!dict.ContainsKey(key)) dict[key] = new CameraGroup(coneTangent, omnidirectional);
            dict[key].Add(cam);
        }
        groups = dict.Values.ToList();
        foreach (var g in groups) g.FinishGeometry();
    }

    public void AttemptInitialLock(Vector3D seedPos, double maxRange)
    {
        locked = new MyDetectedEntityInfo(); tickOfLock = tick; nextScanTick = tick;
        State = TrackState.Slipped;
        TryScan(ref seedPos, 1.0, true);
    }

    public void Tick(int externalTick)
    {
        tick = externalTick;
        if (State == TrackState.NoLock || tick < nextScanTick) return;
        Vector3D predicted = PredictPos();
        UpdateSchedule(ref predicted, rangeFactor);
        IMyCameraBlock cam; Vector3D scanAt;
        if (!FindCamera(ref predicted, rangeFactor, out cam, out scanAt))
        { State = TrackState.Slipped; if (tick > tickOfLock + LOST_TIMEOUT) State = TrackState.NoLock; return; }
        var hit = DoRaycast(cam, ref scanAt);
        if (!hit.IsEmpty() && hit.EntityId == locked.EntityId) ConfirmLock(ref hit, useLocalOffset);
        else if (State == TrackState.Tracking) Relock(ref hit, ref predicted, rangeFactor);
        if (State == TrackState.Slipped && tick > tickOfLock + LOST_TIMEOUT) State = TrackState.NoLock;
    }

    public bool GetTarget(out Vector3D pos, out Vector3D vel, out long id)
    {
        if (State == TrackState.NoLock || locked.IsEmpty()) { pos = vel = Vector3D.Zero; id = 0; return false; }
        vel = locked.Velocity; pos = locked.Position + vel * DT * (tick - tickOfLock);
        if (useLocalOffset) pos += Vector3D.TransformNormal(hitOffsetLocal, locked.Orientation);
        id = locked.EntityId; return true;
    }

    public bool ConfirmHit(double warheadRange)
    {
        foreach (var cam in allCameras)
        {
            if (!cam.IsWorking) continue;
            try
            {
                var hit = cam.Raycast(1f, 0f, 0f);
                if (hit.HitPosition.HasValue && IsValid(ref hit)) return true;
                for (double az = -coneLimit; az <= coneLimit; az += coneStep)
                    for (double el = -coneLimit; el <= coneLimit; el += coneStep)
                    {
                        if (az == 0 && el == 0) continue;
                        hit = cam.Raycast((float)(warheadRange * 1.5), (float)az, (float)el);
                        if (hit.HitPosition.HasValue && IsValid(ref hit)) return true;
                    }
            }
            catch { }
        }
        return false;
    }

    public string StateString() => State.ToString();

    bool TryScan(ref Vector3D target, double rFactor, bool force = false)
    {
        if (tick < nextScanTick && !force) return false;
        UpdateSchedule(ref target, rFactor);
        IMyCameraBlock cam; Vector3D scanAt;
        if (!FindCamera(ref target, rFactor, out cam, out scanAt)) return false;
        var hit = DoRaycast(cam, ref scanAt);
        if (hit.IsEmpty() || !IsValid(ref hit)) return false;
        locked = hit; tickOfLock = tick; boundingRadius = hit.BoundingBox.HalfExtents.Length();
        hitOffsetLocal = HitOffset(ref hit); useLocalOffset = false; relockFails = 0;
        State = TrackState.Tracking; return true;
    }

    void Relock(ref MyDetectedEntityInfo lastScan, ref Vector3D center, double rFactor)
    {
        if (extraBudget <= 0) { State = TrackState.Slipped; return; }
        Vector3D tryPos = PredictPos();
        IMyCameraBlock cam; Vector3D scanAt;
        if (FindCamera(ref tryPos, rFactor, out cam, out scanAt))
        {
            var hit = DoRaycast(cam, ref scanAt); extraBudget--;
            if (!hit.IsEmpty() && hit.EntityId == locked.EntityId) { ConfirmLock(ref hit, useLocalOffset); return; }
        }
        double orb = Math.Max(locked.Velocity.Length(), boundingRadius);
        int tries = Math.Min(MAX_RELOCK, extraBudget);
        for (int i = 0; i < tries; i++)
        {
            int si = i % Spiral.GetLength(0);
            Vector3D offset = (Vector3D)locked.Velocity * (1.0 / Math.Max(tries, 1)) + new Vector3D(Spiral[si,0], Spiral[si,1], 0) * orb * 0.5;
            Vector3D search = tryPos + offset;
            if (!FindCamera(ref search, rFactor, out cam, out scanAt)) continue;
            var hit = DoRaycast(cam, ref scanAt); extraBudget--;
            if (!hit.IsEmpty() && hit.EntityId == locked.EntityId) { ConfirmLock(ref hit, useLocalOffset); return; }
            if (!hit.IsEmpty() && IsValid(ref hit)) { ConfirmLock(ref hit, false); return; }
        }
        relockFails++; State = TrackState.Slipped;
    }

    void ConfirmLock(ref MyDetectedEntityInfo det, bool calcOffset)
    {
        locked = det; tickOfLock = tick;
        if (calcOffset) hitOffsetLocal = HitOffset(ref det);
        State = TrackState.Tracking;
    }

    void UpdateSchedule(ref Vector3D target, double rFactor, int passes = 1)
    {
        if (tick < nextAvailUpdate) return;
        nextAvailUpdate = tick + AVAIL_INTERVAL;
        int available = 0, chargeSum = 0; double maxRange = 0;
        foreach (var grp in groups)
        {
            if (!grp.Contains(ref target)) continue;
            double tRange = grp.CameraCount > 0 ? (target - grp.FirstCamPos()).Length() * rFactor : 0;
            double rRecip = tRange > 0 ? 1.0 / tRange : 0;
            foreach (var cam in grp.Cams)
                if (cam.IsWorking && cam.AvailableScanRange >= tRange)
                { maxRange = Math.Max(maxRange, tRange); available++; chargeSum += (int)Math.Floor(cam.AvailableScanRange * rRecip); }
        }
        if (maxRange > 0 && available > 0)
        { float active = available * 0.9f; computedInterval = (int)Math.Ceiling(maxRange * (chargeTimeFactor / active)); extraBudget = (int)Math.Floor((chargeSum - active) * 0.5f); }
        else { computedInterval = FALLBACK_INTERVAL; extraBudget = 0; }
        nextScanTick = tick + Math.Max(MIN_INTERVAL, computedInterval * passes);
    }

    bool FindCamera(ref Vector3D target, double rFactor, out IMyCameraBlock found, out Vector3D adjusted)
    {
        if (lastGroup != null && lastGroup.Contains(ref target) && TryGroup(lastGroup, ref target, rFactor, out found, out adjusted)) return true;
        foreach (var grp in groups)
        {
            if (grp == lastGroup) continue;
            if (!grp.Contains(ref target)) continue;
            if (TryGroup(grp, ref target, rFactor, out found, out adjusted)) { lastGroup = grp; return true; }
        }
        found = null; adjusted = target; return false;
    }

    bool TryGroup(CameraGroup grp, ref Vector3D target, double rFactor, out IMyCameraBlock found, out Vector3D adjusted)
    {
        for (int i = 0; i < grp.CameraCount; i++)
        {
            var cam = grp.Next();
            if (!cam.IsWorking) continue;
            Vector3D adj = (target - cam.GetPosition()) * rFactor + cam.GetPosition();
            if (CamReady(cam, ref adj)) { found = cam; adjusted = adj; return true; }
        }
        found = null; adjusted = target; return false;
    }

    bool CamReady(IMyCameraBlock cam, ref Vector3D target)
    {
        MatrixD cm = cam.WorldMatrix;
        Vector3D d = target - cm.Translation;
        if (!(cam.AvailableScanRange * cam.AvailableScanRange >= d.LengthSquared())) return false;
        if (omnidirectional) return true;
        Vector3D f = cm.Forward, l = cm.Left, u = cm.Up;
        return d.Dot(f+l) >= 0 && d.Dot(f-l) >= 0 && d.Dot(f+u) >= 0 && d.Dot(f-u) >= 0;
    }

    MyDetectedEntityInfo DoRaycast(IMyCameraBlock cam, ref Vector3D target)
    {
        MatrixD cm = cam.WorldMatrix;
        Vector3D delta = target - cm.Translation;
        Vector3D deltaLoc = Vector3D.TransformNormal(delta, MatrixD.Transpose(cm));
        double dist = deltaLoc.Length();
        if (dist < 0.001) return new MyDetectedEntityInfo();
        deltaLoc /= dist;
        Vector3D flat = Vector3D.Normalize(new Vector3D(deltaLoc.X, 0, deltaLoc.Z));
        double az = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(flat.Dot(Vector3D.Forward),-1,1)) * Math.Sign(deltaLoc.X));
        double el = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(flat.Dot(deltaLoc),-1,1)) * Math.Sign(deltaLoc.Y));
        return cam.Raycast((float)dist, (float)az, (float)el);
    }

    Vector3D PredictPos()
    {
        if (locked.IsEmpty()) return Vector3D.Zero;
        Vector3D pos = locked.Position + (Vector3D)locked.Velocity * DT * (tick - tickOfLock);
        if (useLocalOffset) pos += Vector3D.TransformNormal(hitOffsetLocal, locked.Orientation);
        return pos;
    }

    Vector3D HitOffset(ref MyDetectedEntityInfo det)
    {
        if (det.HitPosition.HasValue) return Vector3D.TransformNormal(det.HitPosition.Value - det.Position, MatrixD.Transpose(det.Orientation));
        return Vector3D.Zero;
    }

    bool IsValid(ref MyDetectedEntityInfo info)
    {
        if (info.Type != MyDetectedEntityType.LargeGrid && info.Type != MyDetectedEntityType.SmallGrid) return false;
        switch (info.Relationship)
        {
            case MyRelationsBetweenPlayerAndBlock.Owner:
            case MyRelationsBetweenPlayerAndBlock.FactionShare: return !ExcludeFriendly;
            case MyRelationsBetweenPlayerAndBlock.Neutral:
            case MyRelationsBetweenPlayerAndBlock.NoOwnership:  return !ExcludeNeutral;
            default: return true;
        }
    }
}

class CameraGroup
{
    public List<IMyCameraBlock> Cams = new List<IMyCameraBlock>();
    IMyCubeGrid grid;
    double coneTangent;
    bool omnidirectional;
    bool geometryReady = false;

    // Pre-computed Vector3D directions stored at geometry-build time.
    // Avoids Func<IMyCubeGrid,Vector3D> lambdas which caused Vector3 vs
    // Vector3D ambiguity when SE returned Matrix instead of MatrixD.
    Vector3D dirFwd, dirLeft, dirUp;

    // Boundary points in world space (computed once, static after build)
    Vector3D ptRight, ptLeft, ptDown, ptUp;

    public int CameraCount => Cams.Count;
    int camIndex;
    public IMyCameraBlock Next() { if (camIndex >= Cams.Count) camIndex = 0; return Cams[camIndex++]; }
    public void Add(IMyCameraBlock cam) => Cams.Add(cam);
    public Vector3D FirstCamPos() => Cams.Count > 0 ? Cams[0].GetPosition() : Vector3D.Zero;
    public CameraGroup(double tangent, bool omni) { coneTangent = tangent; omnidirectional = omni; }

    public void FinishGeometry()
    {
        if (Cams.Count == 0) return;
        grid = Cams[0].CubeGrid;

        // Compute bounding box in grid integer coords
        int minX=int.MaxValue,maxX=int.MinValue,minY=int.MaxValue,maxY=int.MinValue,minZ=int.MaxValue,maxZ=int.MinValue;
        foreach (var c in Cams)
        { minX=Math.Min(minX,c.Position.X);maxX=Math.Max(maxX,c.Position.X);minY=Math.Min(minY,c.Position.Y);maxY=Math.Max(maxY,c.Position.Y);minZ=Math.Min(minZ,c.Position.Z);maxZ=Math.Max(maxZ,c.Position.Z); }

        var first = Cams[0];

        // Explicitly type as MatrixD so .Forward/.Left/.Up return Vector3D not Vector3
        MatrixD firstMat = first.WorldMatrix;
        dirFwd  = firstMat.Forward;
        dirLeft = firstMat.Left;
        dirUp   = firstMat.Up;

        // Compute boundary corner world positions directly from grid integer coords
        int mx=(minX+maxX)/2, my=(minY+maxY)/2, mz=(minZ+maxZ)/2;
        ptRight = new Vector3D(grid.GridIntegerToWorld(new Vector3I(maxX, my,   mz)));
        ptLeft  = new Vector3D(grid.GridIntegerToWorld(new Vector3I(minX, my,   mz)));
        ptUp    = new Vector3D(grid.GridIntegerToWorld(new Vector3I(mx,   maxY, mz)));
        ptDown  = new Vector3D(grid.GridIntegerToWorld(new Vector3I(mx,   minY, mz)));

        geometryReady = true;
    }

    public bool Contains(ref Vector3D worldPos)
    {
        if (!geometryReady || Cams.Count == 0) return true; // fail open

        Vector3D fwd  = omnidirectional ? Vector3D.Zero : dirFwd;
        Vector3D left = dirLeft * coneTangent;  // Vector3D * double — always valid
        Vector3D up   = dirUp   * coneTangent;  // Vector3D * double — always valid

        Vector3D toRight = worldPos - ptRight;
        Vector3D toLeft  = worldPos - ptLeft;
        Vector3D toDown  = worldPos - ptDown;
        Vector3D toUp    = worldPos - ptUp;

        if (coneTangent >= 0)
            return toRight.Dot(fwd+left)>=0 && toLeft.Dot(fwd-left)>=0
                && toDown.Dot(fwd+up)  >=0 && toUp.Dot(fwd-up)    >=0;
        else
            return toRight.Dot(fwd+left)>=0 || toLeft.Dot(fwd-left)>=0
                || toDown.Dot(fwd+up)  >=0 || toUp.Dot(fwd-up)    >=0;
    }
}