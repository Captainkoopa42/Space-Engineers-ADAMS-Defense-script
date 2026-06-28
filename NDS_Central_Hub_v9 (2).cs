// =====================================================
// NDS CENTRAL HUB v9.0 - Command & Control + Nav UI + LCD Packages
// =====================================================
//
// NAVIGATION ARGUMENTS (wire to button panel or timer blocks):
//   up | down        move cursor
//   left | right     adjust value on Settings screen
//   enter            select / confirm
//   back             go up one level
//
// SAVED TARGETS — edit in CustomData under [Targets] section:
//   [Targets]
//   Alpha Base=GPS:Alpha Base:1000:2000:3000:
//   Bravo Fleet=GPS:Bravo Fleet:4000:5000:6000:
//
// STRIKE PACKAGES — two sources, merged automatically:
//   1. Built-in defaults (edit STRIKE_TEMPLATES below)
//   2. LCD-defined packages — name any text panel:
//        Package: <name you want>
//      then write the steps on the panel itself, one per line:
//        DEC:3
//        NUKE:2
//        AIR:1
//      An LCD package with the same name as a built-in overrides it.
//      Run "refresh" (or "reload") after adding/editing a panel to
//      pick up the change — the Hub doesn't watch panels continuously.
//
// DEFAULT TYPE / DEFAULT SALVO — one shared setting, used for ANY
// bare target with no explicit type or count attached: a plain typed
// GPS:... command, or a satellite auto-paint. There is intentionally
// only one default. The satellite never sets type or count itself —
// it only ever sends raw coordinates. If you want a specific package
// fired at a specific target, use the Strike Packages menu (or the
// strike:NAME:GPS:... argument) instead of relying on the default.
//
// DIRECT ARGUMENTS (bypass menu):
//   GPS:Name:X:Y:Z:|3|Nuke
//   GPS:Name:X:Y:Z:|DEC:2|NUKE:1
//   strike:TEMPLATE_NAME:GPS:...
//   salvo:3 | type:Nuke | limit:2 | clear | status | push:key:val
//   reload | refresh   — rescan saved targets AND LCD packages
// =====================================================


// ── TYPE SYSTEM ───────────────────────────────────────
enum MissileType { Normal, Nuke, Decoy, AirTurret, PillarTurret }

static readonly MissileType[] ALL_TYPES = {
    MissileType.Normal, MissileType.Nuke, MissileType.Decoy,
    MissileType.AirTurret, MissileType.PillarTurret
};

static readonly Dictionary<MissileType, string> TYPE_CODE = new Dictionary<MissileType, string>
{
    { MissileType.Normal,       "NORM"   },
    { MissileType.Nuke,         "NUKE"   },
    { MissileType.Decoy,        "DEC"    },
    { MissileType.AirTurret,    "AIR"    },
    { MissileType.PillarTurret, "PILLAR" },
};


// ── STRIKE TEMPLATES (built-in defaults) ─────────────
// These always exist, even before any LCD packages are placed.
// An LCD named "Package: SingleNuke" would override this entry's
// steps with whatever is written on that panel.
class StrikeStep { public MissileType Type; public int Count; }

static readonly Dictionary<string, List<StrikeStep>> STRIKE_TEMPLATES
    = new Dictionary<string, List<StrikeStep>>(StringComparer.OrdinalIgnoreCase)
{
    ["SingleNuke"] = new List<StrikeStep>
        { new StrikeStep { Type = MissileType.Nuke, Count = 1 } },

    ["SuppressAndStrike"] = new List<StrikeStep>
    {
        new StrikeStep { Type = MissileType.Decoy, Count = 2 },
        new StrikeStep { Type = MissileType.Nuke,  Count = 1 },
    },

    ["ArmorBreaker"] = new List<StrikeStep>
    {
        new StrikeStep { Type = MissileType.Decoy,     Count = 3 },
        new StrikeStep { Type = MissileType.Nuke,      Count = 2 },
        new StrikeStep { Type = MissileType.AirTurret, Count = 2 },
    },

    ["GroundControl"] = new List<StrikeStep>
    {
        new StrikeStep { Type = MissileType.AirTurret,    Count = 3 },
        new StrikeStep { Type = MissileType.PillarTurret, Count = 3 },
    },

    ["SaturationRun"] = new List<StrikeStep>
    {
        new StrikeStep { Type = MissileType.Decoy,        Count = 4 },
        new StrikeStep { Type = MissileType.AirTurret,    Count = 3 },
        new StrikeStep { Type = MissileType.PillarTurret, Count = 3 },
        new StrikeStep { Type = MissileType.Nuke,         Count = 2 },
    },

    ["AllTypes"] = new List<StrikeStep>
    {
        new StrikeStep { Type = MissileType.Normal,       Count = 1 },
        new StrikeStep { Type = MissileType.Nuke,         Count = 1 },
        new StrikeStep { Type = MissileType.Decoy,        Count = 1 },
        new StrikeStep { Type = MissileType.AirTurret,    Count = 1 },
        new StrikeStep { Type = MissileType.PillarTurret, Count = 1 },
    },
};

// LCD panels named with this prefix (case-insensitive) define additional
// strike packages. Everything after the prefix, trimmed, is the package name.
const string PACKAGE_LCD_PREFIX = "Package:";

// Runtime package set — built-ins seeded first, then LCD-defined packages
// merged in by ScanForPackages(). This is what the menu actually reads from.
Dictionary<string, List<StrikeStep>> allPackages = new Dictionary<string, List<StrikeStep>>(StringComparer.OrdinalIgnoreCase);
List<string> packageNames = new List<string>();


// ── DEFAULTS ─────────────────────────────────────────
// One shared default. Used for any bare target — typed manually or
// painted by the satellite — that doesn't specify its own type/count.
int    defaultSalvoSize      = 1;
string defaultType           = "Normal";
int    dispatchParallelLimit = 2;


// ── IGC ───────────────────────────────────────────────
string hubId             = "CENTRAL_COMMAND";
string sharedSecret      = "YourSecretKey123";
string hubAddressChannel = "HUB_ADDRESS";
string ndsToHubChannel   = "NDS_TO_HUB";
string targetsTag        = "HUB_TO_NDS_TARGETS";
string satConfigChannel  = "SAT_CONFIG_CHANNEL";
IMyUnicastListener ndsListener;
int tickCounter      = 0;
int messageIdCounter = 0;
const int BROADCAST_INTERVAL = 300;
const int STALE_TIMEOUT      = 1800;


// ── MISSION MODEL ─────────────────────────────────────
class MissionStep { public MissileType Type; public int Count; }

class TargetMission
{
    public string            GPS;
    public List<MissionStep> Steps = new List<MissionStep>();
}

Queue<TargetMission> missionQueue = new Queue<TargetMission>();


// ── SILO REGISTRY ─────────────────────────────────────
class NdsUnitStatus
{
    public long   Address;
    public string Name;
    public int    LastSeenTick;
    public bool   IsReady;
    public int    EmptyMounts;
    public int    BuildingMounts;
    public int    ReadyMounts;
    public int    PendingAcks;
    public bool   Confirmed;  // true once a Status message arrives — distinguishes real silos from satellites, which only ever send Target pings
}
Dictionary<long, NdsUnitStatus> activeNdsUnits = new Dictionary<long, NdsUnitStatus>();


// ── SAVED TARGETS ─────────────────────────────────────
// Populated from [Targets] section of CustomData
class SavedTarget { public string Name; public string GPS; }
List<SavedTarget> savedTargets = new List<SavedTarget>();


// ── MENU STATE ────────────────────────────────────────
enum Screen
{
    Main,
    StrikePackages,   // browse templates (built-in + LCD-defined)
    TargetSelect,     // browse saved targets to assign a template to
    QueueStatus,      // scrollable mission queue
    SiloStatus,       // scrollable silo registry
    Settings,         // left/right to adjust values
    ClearConfirm,     // enter to confirm clear
}

Screen currentScreen   = Screen.Main;
int    cursorRow       = 0;
int    scrollOffset    = 0;
string pendingTemplate = null;  // template selected, waiting for target

// Settings cursor tracks which setting is selected (0-2)
int settingsCursor     = 0;

// LCD display block names
const string MENU_LCD_NAME   = "Hub Menu";   // navigation display
const string STATUS_LCD_NAME = "Hub Display"; // always-on status (unchanged)

// Rows visible on menu LCD at once
const int LCD_VISIBLE_ROWS = 10;


// Main menu items — fixed order
static readonly string[] MAIN_MENU_ITEMS = {
    "Strike Packages",
    "Queue Status",
    "Silo Status",
    "Settings",
    "Clear Queue",
};


// ── PROGRAM ───────────────────────────────────────────

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    ndsListener = IGC.UnicastListener;
    LoadSavedTargets();
    ScanForPackages();
    Echo($"Hub {hubId} online. {savedTargets.Count} saved targets, {packageNames.Count} strike packages.");
}

public void Main(string argument, UpdateType updateSource)
{
    tickCounter += 10;

    if (!string.IsNullOrEmpty(argument))
    {
        if (!HandleNavInput(argument))
            ProcessArgument(argument);
    }

    if ((updateSource & UpdateType.Update10) != 0)
    {
        if (tickCounter % BROADCAST_INTERVAL == 0)
        {
            IGC.SendBroadcastMessage(hubAddressChannel, Me.CubeGrid.EntityId.ToString());
            CleanStaleUnits();
        }
        ProcessIncomingMessages();
        DispatchMissions();
        DrawMenuLCD();
        DrawStatusLCD();
    }
}


// ── SAVED TARGET LOADER ───────────────────────────────

void LoadSavedTargets()
{
    savedTargets.Clear();
    bool inSection = false;
    foreach (string rawLine in Me.CustomData.Split('\n'))
    {
        string line = rawLine.Trim();
        if (line.Equals("[Targets]", StringComparison.OrdinalIgnoreCase))
        { inSection = true; continue; }
        if (line.StartsWith("[") && inSection) break; // next section
        if (!inSection || string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;

        int eq = line.IndexOf('=');
        if (eq > 0)
        {
            string name = line.Substring(0, eq).Trim();
            string gps  = line.Substring(eq + 1).Trim();
            if (!string.IsNullOrEmpty(name) && gps.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase))
                savedTargets.Add(new SavedTarget { Name = name, GPS = gps });
        }
    }
}


// ── PACKAGE SCANNER ───────────────────────────────────
// Builds the live package list: built-in STRIKE_TEMPLATES first (in their
// declared order), then any LCD panel named "Package: <name>" — parsed
// one TYPE:COUNT step per line. An LCD sharing a built-in's name overrides
// that built-in's steps without changing its position in the list.

void ScanForPackages()
{
    allPackages.Clear();
    packageNames.Clear();

    foreach (var kv in STRIKE_TEMPLATES)
    {
        allPackages[kv.Key] = kv.Value;
        packageNames.Add(kv.Key);
    }

    var panels = new List<IMyTextPanel>();
    GridTerminalSystem.GetBlocksOfType(panels, p =>
        p.CustomName.ToLower().StartsWith(PACKAGE_LCD_PREFIX.ToLower()));

    var newLcdNames = new List<string>();

    foreach (var panel in panels)
    {
        string pkgName = panel.CustomName.Substring(PACKAGE_LCD_PREFIX.Length).Trim();
        if (string.IsNullOrEmpty(pkgName)) continue;

        var steps = new List<StrikeStep>();
        foreach (string rawLine in panel.GetText().Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;

            var sub = line.Split(':');
            if (sub.Length < 2) continue;

            MissileType type; int count;
            if (TryParseType(sub[0].Trim(), out type) && int.TryParse(sub[1].Trim(), out count) && count > 0)
                steps.Add(new StrikeStep { Type = type, Count = count });
        }

        if (steps.Count == 0) continue; // unreadable or empty panel — skip rather than register a blank package

        bool isNewName = !allPackages.ContainsKey(pkgName);
        allPackages[pkgName] = steps;
        if (isNewName) newLcdNames.Add(pkgName);
    }

    newLcdNames.Sort(StringComparer.OrdinalIgnoreCase);
    packageNames.AddRange(newLcdNames);
}


// ── NAVIGATION INPUT ──────────────────────────────────
// Returns true if the argument was consumed as a nav command.

bool HandleNavInput(string arg)
{
    string l = arg.ToLower().Trim();
    switch (l)
    {
        case "up":    NavUp();    DrawMenuLCD(); return true;
        case "down":  NavDown();  DrawMenuLCD(); return true;
        case "left":  NavLeft();  DrawMenuLCD(); return true;
        case "right": NavRight(); DrawMenuLCD(); return true;
        case "enter": NavEnter(); DrawMenuLCD(); return true;
        case "back":  NavBack();  DrawMenuLCD(); return true;
        default: return false;
    }
}

void NavUp()
{
    cursorRow = Math.Max(0, cursorRow - 1);
    if (cursorRow < scrollOffset) scrollOffset = cursorRow;
}

void NavDown()
{
    int maxRow = GetCurrentListCount() - 1;
    cursorRow  = Math.Min(maxRow, cursorRow + 1);
    if (cursorRow >= scrollOffset + LCD_VISIBLE_ROWS)
        scrollOffset = cursorRow - LCD_VISIBLE_ROWS + 1;
}

void NavLeft()
{
    if (currentScreen != Screen.Settings) return;
    switch (settingsCursor)
    {
        case 0: // Default Type — cycle backward
            int ti = Array.IndexOf(ALL_TYPES, ParseTypeOrDefault(defaultType));
            ti = (ti - 1 + ALL_TYPES.Length) % ALL_TYPES.Length;
            defaultType = TYPE_CODE[ALL_TYPES[ti]];
            break;
        case 1: // Default Salvo — decrement
            if (defaultSalvoSize > 1) defaultSalvoSize--;
            break;
        case 2: // Parallel Limit — decrement
            if (dispatchParallelLimit > 1) dispatchParallelLimit--;
            break;
    }
}

void NavRight()
{
    if (currentScreen != Screen.Settings) return;
    switch (settingsCursor)
    {
        case 0: // Default Type — cycle forward
            int ti = Array.IndexOf(ALL_TYPES, ParseTypeOrDefault(defaultType));
            ti = (ti + 1) % ALL_TYPES.Length;
            defaultType = TYPE_CODE[ALL_TYPES[ti]];
            break;
        case 1: defaultSalvoSize++;         break;
        case 2: dispatchParallelLimit++;    break;
    }
}

void NavEnter()
{
    switch (currentScreen)
    {
        case Screen.Main:
            switch (cursorRow)
            {
                case 0: GoTo(Screen.StrikePackages); break;
                case 1: GoTo(Screen.QueueStatus);    break;
                case 2: GoTo(Screen.SiloStatus);     break;
                case 3: GoTo(Screen.Settings);       break;
                case 4: GoTo(Screen.ClearConfirm);   break;
            }
            break;

        case Screen.StrikePackages:
            if (cursorRow < packageNames.Count)
            {
                pendingTemplate = packageNames[cursorRow];
                GoTo(Screen.TargetSelect);
            }
            break;

        case Screen.TargetSelect:
            if (pendingTemplate != null && cursorRow < savedTargets.Count)
            {
                FireTemplate(pendingTemplate, savedTargets[cursorRow].GPS);
                pendingTemplate = null;
                GoTo(Screen.Main);
            }
            break;

        case Screen.Settings:
            // Enter on settings cycles the cursor between settings rows
            settingsCursor = (settingsCursor + 1) % 3;
            break;

        case Screen.ClearConfirm:
            missionQueue.Clear();
            Echo("Queue cleared via menu.");
            GoTo(Screen.Main);
            break;

        case Screen.QueueStatus:
        case Screen.SiloStatus:
            // Enter does nothing on read-only screens
            break;
    }
}

void NavBack()
{
    if (currentScreen == Screen.TargetSelect && pendingTemplate != null)
    { pendingTemplate = null; GoTo(Screen.StrikePackages); return; }
    GoTo(Screen.Main);
}

void GoTo(Screen screen)
{
    currentScreen = screen;
    cursorRow     = 0;
    scrollOffset  = 0;
    if (screen == Screen.Settings) settingsCursor = 0;
}

int GetCurrentListCount()
{
    switch (currentScreen)
    {
        case Screen.Main:           return MAIN_MENU_ITEMS.Length;
        case Screen.StrikePackages: return Math.Max(1, packageNames.Count);
        case Screen.TargetSelect:   return Math.Max(1, savedTargets.Count);
        case Screen.QueueStatus:    return Math.Max(1, missionQueue.Count);
        case Screen.SiloStatus:     return Math.Max(1, activeNdsUnits.Values.Count(u => u.Confirmed));
        case Screen.Settings:       return 3;
        case Screen.ClearConfirm:   return 1;
        default: return 1;
    }
}


// ── TEMPLATE FIRE ─────────────────────────────────────

void FireTemplate(string templateName, string gps)
{
    List<StrikeStep> template;
    if (!allPackages.TryGetValue(templateName, out template)) return;

    var mission = new TargetMission { GPS = gps };
    foreach (var step in template)
        mission.Steps.Add(new MissionStep { Type = step.Type, Count = step.Count });

    EnqueueMission(mission);
}


// ── MENU LCD RENDERER ─────────────────────────────────

void DrawMenuLCD()
{
    var lcd = GridTerminalSystem.GetBlockWithName(MENU_LCD_NAME) as IMyTextPanel;
    if (lcd == null) return;
    lcd.ContentType = ContentType.TEXT_AND_IMAGE;

    var sb = new System.Text.StringBuilder();

    switch (currentScreen)
    {
        case Screen.Main:
            sb.AppendLine("╔═ NDS CENTRAL HUB ═╗");
            sb.AppendLine($"  Silos: {activeNdsUnits.Values.Count(u => u.Confirmed)}  Queue: {missionQueue.Count}");
            sb.AppendLine("─────────────────────");
            for (int i = 0; i < MAIN_MENU_ITEMS.Length; i++)
                sb.AppendLine((i == cursorRow ? "► " : "  ") + MAIN_MENU_ITEMS[i]);
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ▲▼ navigate  ✓ select");
            break;

        case Screen.StrikePackages:
            sb.AppendLine("╔═ STRIKE PACKAGES ══╗");
            sb.AppendLine("  Select package:");
            sb.AppendLine("─────────────────────");
            if (packageNames.Count == 0)
            {
                sb.AppendLine("  No packages found.");
            }
            else
            {
                for (int i = scrollOffset; i < Math.Min(packageNames.Count, scrollOffset + LCD_VISIBLE_ROWS); i++)
                {
                    string tname = packageNames[i];
                    string steps = string.Join("+", allPackages[tname]
                                    .Select(s => $"{TYPE_CODE[s.Type]}x{s.Count}"));
                    sb.AppendLine($"{(i == cursorRow ? "►" : " ")} {tname}");
                    sb.AppendLine($"    {steps}");
                }
            }
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ✓ select  ✗ back");
            break;

        case Screen.TargetSelect:
            sb.AppendLine("╔═ SELECT TARGET ═════╗");
            sb.AppendLine($"  Package: {pendingTemplate}");
            sb.AppendLine("─────────────────────");
            if (savedTargets.Count == 0)
            {
                sb.AppendLine("  No saved targets.");
                sb.AppendLine("  Edit CustomData:");
                sb.AppendLine("  [Targets]");
                sb.AppendLine("  Name=GPS:...");
            }
            else
            {
                for (int i = scrollOffset; i < Math.Min(savedTargets.Count, scrollOffset + LCD_VISIBLE_ROWS); i++)
                    sb.AppendLine($"{(i == cursorRow ? "► " : "  ")}{savedTargets[i].Name}");
            }
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ✓ fire  ✗ back");
            break;

        case Screen.QueueStatus:
            sb.AppendLine("╔═ MISSION QUEUE ════╗");
            sb.AppendLine($"  {missionQueue.Count} mission(s) pending");
            sb.AppendLine("─────────────────────");
            if (missionQueue.Count == 0)
            {
                sb.AppendLine("  Queue is empty.");
            }
            else
            {
                var qlist = missionQueue.ToArray();
                for (int i = scrollOffset; i < Math.Min(qlist.Length, scrollOffset + LCD_VISIBLE_ROWS); i++)
                {
                    string steps = string.Join("→", qlist[i].Steps.Select(s => $"{TYPE_CODE[s.Type]}x{s.Count}"));
                    sb.AppendLine($"{(i == cursorRow ? "►" : " ")} {ParseGPSName(qlist[i].GPS)}");
                    sb.AppendLine($"    {steps}");
                }
            }
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ▲▼ scroll  ✗ back");
            break;

        case Screen.SiloStatus:
            var ulist = activeNdsUnits.Values.Where(u => u.Confirmed).ToList();
            sb.AppendLine("╔═ SILO STATUS ══════╗");
            sb.AppendLine($"  {ulist.Count} registered silo(s)");
            sb.AppendLine("─────────────────────");
            if (ulist.Count == 0)
            {
                sb.AppendLine("  No silos registered.");
                sb.AppendLine("  Waiting for check-in...");
            }
            else
            {
                for (int i = scrollOffset; i < Math.Min(ulist.Count, scrollOffset + LCD_VISIBLE_ROWS); i++)
                {
                    var u = ulist[i];
                    string rdy = u.IsReady ? "RDY" : "BSY";
                    sb.AppendLine($"{(i == cursorRow ? "►" : " ")} [{rdy}] {u.Name}");
                    sb.AppendLine($"    E:{u.EmptyMounts} B:{u.BuildingMounts} R:{u.ReadyMounts}");
                }
            }
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ▲▼ scroll  ✗ back");
            break;

        case Screen.Settings:
            sb.AppendLine("╔═ SETTINGS ══════════╗");
            sb.AppendLine("  ◄► adjust  ▲▼/✓ move");
            sb.AppendLine("─────────────────────");
            sb.AppendLine($"{(settingsCursor == 0 ? "► " : "  ")}Default Type:  [{defaultType}]");
            sb.AppendLine($"{(settingsCursor == 1 ? "► " : "  ")}Default Salvo: [{defaultSalvoSize}]");
            sb.AppendLine($"{(settingsCursor == 2 ? "► " : "  ")}Parallel Limit:[{dispatchParallelLimit}]");
            sb.AppendLine("─────────────────────");
            sb.AppendLine("  ✗ back (saves on exit)");
            break;

        case Screen.ClearConfirm:
            sb.AppendLine("╔═ CONFIRM CLEAR ════╗");
            sb.AppendLine();
            sb.AppendLine($"  Clear all {missionQueue.Count} mission(s)?");
            sb.AppendLine();
            sb.AppendLine("  ► Press ENTER to confirm");
            sb.AppendLine("    Press BACK to cancel");
            break;
    }

    lcd.WriteText(sb.ToString());
}


// ── STATUS LCD (always-on, no cursor) ─────────────────

void DrawStatusLCD()
{
    var lcd = GridTerminalSystem.GetBlockWithName(STATUS_LCD_NAME) as IMyTextPanel;
    if (lcd == null) return;
    lcd.ContentType = ContentType.TEXT_AND_IMAGE;
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("=== CENTRAL HUB ===");
    sb.AppendLine($"Default: [{defaultType}] x{defaultSalvoSize}");
    sb.AppendLine($"Limit:   {dispatchParallelLimit} parallel types");
    sb.AppendLine($"Queue:   {missionQueue.Count} mission(s)");
    sb.AppendLine($"Silos:   {activeNdsUnits.Values.Count(u => u.Confirmed)}");
    sb.AppendLine($"Packages:{packageNames.Count}");
    sb.AppendLine();
    sb.AppendLine("── Silos ──");
    foreach (var u in activeNdsUnits.Values.Where(u => u.Confirmed))
        sb.AppendLine($"{(u.IsReady ? "[RDY]" : "[BSY]")} {u.Name}  " +
                      $"E:{u.EmptyMounts} B:{u.BuildingMounts} R:{u.ReadyMounts}");
    if (missionQueue.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("── Next Mission ──");
        var next  = missionQueue.Peek();
        string steps = string.Join(" → ", next.Steps.Select(s => $"{TYPE_CODE[s.Type]} x{s.Count}"));
        sb.AppendLine(ParseGPSName(next.GPS));
        sb.AppendLine(steps);
        if (missionQueue.Count > 1)
            sb.AppendLine($"+{missionQueue.Count - 1} more queued");
    }
    lcd.WriteText(sb.ToString());
}


// ── ARGUMENT PROCESSING (direct, bypasses menu) ───────

void ProcessArgument(string arg)
{
    if (arg.StartsWith("salvo:", StringComparison.OrdinalIgnoreCase))
    {
        int s;
        if (int.TryParse(arg.Substring(6).Trim(), out s) && s > 0)
        { defaultSalvoSize = s; Echo($"Default salvo: x{defaultSalvoSize}"); }
        return;
    }
    if (arg.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
    { defaultType = arg.Substring(5).Trim(); Echo($"Default type: [{defaultType}]"); return; }

    if (arg.StartsWith("limit:", StringComparison.OrdinalIgnoreCase))
    {
        int n;
        if (int.TryParse(arg.Substring(6).Trim(), out n) && n >= 1)
        { dispatchParallelLimit = n; Echo($"Parallel limit: {n}"); }
        return;
    }
    if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase))
    { missionQueue.Clear(); Echo("Queue cleared."); return; }

    if (arg.Equals("reload", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("refresh", StringComparison.OrdinalIgnoreCase))
    {
        LoadSavedTargets();
        ScanForPackages();
        Echo($"Refreshed: {savedTargets.Count} targets, {packageNames.Count} packages.");
        return;
    }

    if (arg.Equals("status", StringComparison.OrdinalIgnoreCase))
    {
        Echo($"Queue: {missionQueue.Count}  Silos: {activeNdsUnits.Values.Count(u => u.Confirmed)}");
        Echo($"Targets loaded: {savedTargets.Count}  Packages: {packageNames.Count}");
        return;
    }
    if (arg.StartsWith("push:", StringComparison.OrdinalIgnoreCase))
    {
        var parts = arg.Split(':');
        if (parts.Length >= 3)
            IGC.SendBroadcastMessage(satConfigChannel, $"{sharedSecret}:Config:{parts[1]}:{parts[2]}");
        return;
    }
    if (arg.StartsWith("strike:", StringComparison.OrdinalIgnoreCase))
    { ProcessStrikeCommand(arg); return; }

    if (arg.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase))
    {
        var mission = ParseMissionArgument(arg);
        if (mission != null) EnqueueMission(mission);
        return;
    }
}

void ProcessStrikeCommand(string arg)
{
    int first  = arg.IndexOf(':');
    int second = arg.IndexOf(':', first + 1);
    if (first < 0 || second < 0) return;
    string templateName = arg.Substring(first + 1, second - first - 1).Trim();
    string gps          = arg.Substring(second + 1).Trim();
    FireTemplate(templateName, gps);
}


// ── MISSION PARSING ───────────────────────────────────

TargetMission ParseMissionArgument(string raw)
{
    int pipeIdx = raw.IndexOf('|');
    string gps  = pipeIdx >= 0 ? raw.Substring(0, pipeIdx).Trim() : raw.Trim();
    string rest = pipeIdx >= 0 ? raw.Substring(pipeIdx + 1).Trim() : "";

    var mission = new TargetMission { GPS = gps };

    if (string.IsNullOrEmpty(rest))
    {
        // Bare GPS — no type/count attached. This is the path both a plain
        // typed "GPS:..." command AND every satellite paint take. Both use
        // the one shared default below; the satellite never overrides it.
        mission.Steps.Add(new MissionStep { Type = ParseTypeOrDefault(defaultType), Count = defaultSalvoSize });
        return mission;
    }

    var segments   = rest.Split('|');
    bool multiStep = segments.Any(s => s.Contains(":"));

    if (multiStep)
    {
        foreach (var seg in segments)
        {
            var sub = seg.Split(':');
            if (sub.Length < 2) continue;
            MissileType type; int count;
            if (TryParseType(sub[0].Trim(), out type) && int.TryParse(sub[1].Trim(), out count) && count > 0)
                mission.Steps.Add(new MissionStep { Type = type, Count = count });
        }
    }
    else
    {
        int count    = defaultSalvoSize;
        string typeS = defaultType;
        if (segments.Length >= 2) { int.TryParse(segments[0], out count); typeS = segments[1].Trim(); }
        else if (!int.TryParse(segments[0], out count)) { typeS = segments[0]; count = defaultSalvoSize; }

        if (typeS.Equals("All", StringComparison.OrdinalIgnoreCase))
            foreach (MissileType t in ALL_TYPES)
                mission.Steps.Add(new MissionStep { Type = t, Count = count });
        else
        {
            MissileType type;
            if (!TryParseType(typeS, out type)) type = MissileType.Normal;
            mission.Steps.Add(new MissionStep { Type = type, Count = count });
        }
    }

    return mission.Steps.Count > 0 ? mission : null;
}

void EnqueueMission(TargetMission mission)
{
    if (mission == null) return;
    missionQueue.Enqueue(mission);
    string summary = string.Join(", ", mission.Steps.Select(s => $"{TYPE_CODE[s.Type]} x{s.Count}"));
    Echo($"Queued: {ParseGPSName(mission.GPS)} [{summary}]");
}


// ── INCOMING MESSAGES ─────────────────────────────────

void ProcessIncomingMessages()
{
    while (ndsListener.HasPendingMessage)
    {
        var msg = ndsListener.AcceptMessage();

        // IGC.UnicastListener receives every unicast message addressed to
        // this PB, regardless of channel. Filter to the ones actually meant
        // for this inbox — same pattern the silo uses for its own listener.
        if (msg.Tag != ndsToHubChannel) continue;

        string raw   = msg.Data.ToString();

        // Limited split: secret : unitId : msgId : msgType : <everything else>
        // The 5th slot keeps its own colons intact — critical for Target
        // messages, whose payload IS a colon-delimited GPS string. An
        // unlimited Split(':') here would shatter "GPS:Name:X:Y:Z:" into
        // garbage the moment a satellite pings in a real target.
        var parts = raw.Split(new[] { ':' }, 5);
        if (parts.Length < 5 || parts[0] != sharedSecret) continue;

        string unitId    = parts[1];
        string msgType   = parts[3];
        string remainder = parts[4];

        if (!activeNdsUnits.ContainsKey(msg.Source))
            activeNdsUnits[msg.Source] = new NdsUnitStatus { Address = msg.Source, Name = unitId };

        var unit = activeNdsUnits[msg.Source];
        unit.LastSeenTick = tickCounter;

        switch (msgType)
        {
            case "Status":
            {
                // Status payloads have no embedded colon-delimited data of
                // their own (just simple fields), so a further sub-split is safe.
                var sub = remainder.Split(':');
                unit.IsReady   = sub.Length > 0 && sub[0] == "Ready";
                unit.Confirmed = true; // only real silos send Status — marks this as a true silo, not a satellite
                if (sub.Length >= 4)
                {
                    int.TryParse(sub[1], out unit.EmptyMounts);
                    int.TryParse(sub[2], out unit.BuildingMounts);
                    int.TryParse(sub[3], out unit.ReadyMounts);
                }
                break;
            }
            case "Ack":
                unit.PendingAcks = Math.Max(0, unit.PendingAcks - 1);
                break;
            case "Target":
                // remainder is the untouched GPS payload, exactly as the
                // satellite sent it — bare coordinates, nothing else. The
                // satellite never attaches a type or count; that's the
                // Hub's job via the shared default, or via a strike package
                // the operator picks manually.
                var satMission = ParseMissionArgument(remainder);
                if (satMission != null) EnqueueMission(satMission);
                break;
        }
    }
}


// ── MISSION DISPATCH ──────────────────────────────────

void DispatchMissions()
{
    if (missionQueue.Count == 0) return;
    foreach (var unit in activeNdsUnits.Values)
    {
        if (unit.PendingAcks >= 2) continue;
        bool hasCap = unit.Confirmed && (unit.IsReady || unit.EmptyMounts > 0 || unit.BuildingMounts > 0);
        if (!hasCap) continue;
        if (missionQueue.Count == 0) break;

        var mission  = missionQueue.Dequeue();
        string stepStr = string.Join("|", mission.Steps.Select(s => $"{TYPE_CODE[s.Type]}:{s.Count}"));
        string payload = $"{sharedSecret}:Mission:{mission.GPS}|{stepStr}";
        IGC.SendUnicastMessage(unit.Address, targetsTag, payload);
        unit.PendingAcks++;
        messageIdCounter++;
        Echo($"Dispatched → {unit.Name}: {ParseGPSName(mission.GPS)}");
        break;
    }
}


// ── CLEANUP ───────────────────────────────────────────

void CleanStaleUnits()
{
    var stale = new List<long>();
    foreach (var kv in activeNdsUnits)
        if (tickCounter - kv.Value.LastSeenTick > STALE_TIMEOUT)
            stale.Add(kv.Key);
    foreach (var key in stale) activeNdsUnits.Remove(key);
}


// ── HELPERS ───────────────────────────────────────────

bool TryParseType(string code, out MissileType type)
{
    string c = code.Trim().ToUpper();
    foreach (var kv in TYPE_CODE)
        if (kv.Value == c) { type = kv.Key; return true; }
    return Enum.TryParse(code.Trim(), true, out type);
}

MissileType ParseTypeOrDefault(string code)
{
    MissileType t;
    if (!TryParseType(code, out t)) t = MissileType.Normal;
    return t;
}

string ParseGPSName(string gps)
{
    var p = gps.Split(':');
    return p.Length > 1 ? p[1] : gps;
}
