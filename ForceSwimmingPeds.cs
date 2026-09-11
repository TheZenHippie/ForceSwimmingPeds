using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Control = GTA.Control;
using Hash = GTA.Native.Hash;
using Notification = GTA.UI.Notification;
using Screen = GTA.UI.Screen;

public class ForceSwimmingPeds : Script
{
    private const string IniFileName = "ForceSwimmingPeds.ini";
    private const float DegToRad = 0.0174532925f;

    // Cached visual aid drawing assets
    private static readonly Color CylinderColor = Color.FromArgb(80, 46, 204, 113);
    private static readonly Color ConeColor = Color.FromArgb(230, 241, 196, 15);
    private static readonly Vector3 ConeRotation = new Vector3(0.0f, 180.0f, 0.0f);
    private static readonly Vector3 ConeScale = new Vector3(0.5f, 0.5f, 0.7f);

    // ============================================================
    // Location Profile Model
    // ============================================================
    public class SwimmingLocation
    {
        public string Name { get; set; } = "Default Location";
        public Vector3 Center { get; set; } = Vector3.Zero;
        public float Radius { get; set; } = 25.0f;
        public int SwimChance { get; set; } = 10;
        public int RestDuration { get; set; } = 30;
        public int SwimDuration { get; set; } = 45;

        public SwimmingLocation() { }

        public SwimmingLocation(string name, Vector3 center, float radius, int chance, int restDuration = 30, int swimDuration = 45)
        {
            Name = name;
            Center = center;
            Radius = radius;
            SwimChance = chance;
            RestDuration = restDuration;
            SwimDuration = swimDuration;
        }

        public override string ToString() => Name;
    }

    // ============================================================
    // Configuration & State Variables
    // ============================================================
    private Keys _menuKey = Keys.F5;
    private readonly List<SwimmingLocation> _locations = new List<SwimmingLocation>();
    private SwimmingLocation _activeLocation = null;

    private Vector3 _targetLocation = Vector3.Zero;
    private float _radius = 25.0f;
    private int _swimChancePercent = 10;
    private int _restDurationSeconds = 30;
    private int _swimDurationSeconds = 45;
    private bool _enabled = false;
    private string _cachedIniPath = null;

    // ============================================================
    // LemonUI Menu Elements
    // ============================================================
    private readonly ObjectPool _pool;
    private readonly NativeMenu _mainMenu;
    private readonly NativeMenu _advancedMenu;
    private readonly NativeMenu _manageLocationsMenu;
    private readonly NativeMenu _coordsMenu;

    private readonly NativeCheckboxItem _enableCheckbox;
    private readonly NativeItem _recallSwimmersItem;
    private readonly NativeListItem<string> _locationListItem;
    private readonly NativeItem _saveNewLocationItem;
    private readonly NativeItem _pickLocationItem;
    private readonly NativeSliderItem _radiusSlider;
    private readonly NativeSliderItem _chanceSlider;
    private readonly NativeSliderItem _restSlider;
    private readonly NativeSliderItem _swimDurationSlider;

    // Submenu items
    private readonly NativeItem _teleportItem;
    private readonly NativeSliderItem _sliderX;
    private readonly NativeSliderItem _sliderY;
    private readonly NativeSliderItem _sliderZ;
    private readonly NativeItem _updateCurrentLocationItem;

    private readonly NativeListItem<string> _teleportSubItem;
    private readonly NativeListItem<string> _deleteSubItem;
    private readonly NativeItem _renameSubItem;

    private int _lastSliderX = 100;
    private int _lastSliderY = 100;
    private int _lastSliderZ = 100;
    private bool _isUpdatingUI = false;

    private bool IsAnyMenuOpen => _mainMenu.Visible || _advancedMenu.Visible || _coordsMenu.Visible || _manageLocationsMenu.Visible;

    // ============================================================
    // Location Picking State
    // ============================================================
    private bool _isPickingLocation = false;
    private float _lastCursorX = 0.5f;
    private float _lastCursorY = 0.5f;

    // ============================================================
    // APS Profile & Curated Scenario Catalog
    // ============================================================
    private class ApsPedProfile
    {
        public string Name;
        public int NameHash;
        public string Model;
        public string MovementStyle;
        public string AnimationType; // "Animation", "Scenario", "None"
        public string AnimDict;
        public string AnimClip;
        public string Scenario;
    }

    private readonly Dictionary<int, ApsPedProfile> _apsProfiles = new Dictionary<int, ApsPedProfile>();

    private static readonly string[] KnownScenarios = new[]
    {
        "WORLD_HUMAN_SUNBATHE",
        "WORLD_HUMAN_SUNBATHE_BACK",
        "PROP_HUMAN_SEAT_SUNLOUNGER",
        "PROP_HUMAN_SEAT_CHAIR",
        "PROP_HUMAN_SEAT_BENCH",
        "PROP_HUMAN_SEAT_DECKCHAIR",
        "WORLD_HUMAN_PARTYING",
        "WORLD_HUMAN_DRINKING",
        "WORLD_HUMAN_SMOKING",
        "WORLD_HUMAN_SMOKING_POT",
        "WORLD_HUMAN_CHEERING",
        "WORLD_HUMAN_YOGA",
        "WORLD_HUMAN_STAND_MOBILE",
        "WORLD_HUMAN_MUSCLE_FLEX",
        "WORLD_HUMAN_PUSH_UPS",
        "WORLD_HUMAN_SIT_UPS",
        "WORLD_HUMAN_LEANING",
        "WORLD_HUMAN_HANG_OUT_STREET",
        "WORLD_HUMAN_STRIP_WATCH_STAND",
        "WORLD_HUMAN_GUARD_STAND",
        "WORLD_HUMAN_COP_IDLES",
        "WORLD_HUMAN_PROSTITUTE_HIGH_CLASS",
        "WORLD_HUMAN_PROSTITUTE_LOW_CLASS",
        "WORLD_HUMAN_BINOCULARS",
        "WORLD_HUMAN_TOURIST_MAP",
        "WORLD_HUMAN_JOG_STANDING"
    };

    // ============================================================
    // Swimming State Machine
    // ============================================================
    private enum SwimmerState
    {
        SwimmingToCenter, // Moving towards water center
        SwimmingInPool,   // Actively swimming/treading inside pool radius
        ReturningToEntry, // Returning to original entry point
        Resting           // Rest/immune state: resumes prior task loop or relaxes in nearest scenario
    }

    private class ActiveSwimmer
    {
        public Ped Ped;
        public Vector3 EntryPosition;
        public float EntryHeading;
        public int StartTime;
        public SwimmerState State;
        public int StateStartTime;
        public Vector3 CurrentSubTarget;
        public int LastTaskTime;

        // Task loop & scenario restoration tracking
        public bool HadTaskLoop;
        public int TaskType; // 1 = Anim, 2 = Scenario, 0 = None
        public string SavedAnimDict;
        public string SavedAnimClip;
        public string SavedScenario;

        // Periodic swimming & immunity cooldown
        public int SwimDurationMs;
        public int ImmunityUntil;
        public bool ScenarioAttempted;
    }

    private readonly List<ActiveSwimmer> _activeSwimmers = new List<ActiveSwimmer>();
    private readonly List<Ped> _candidatePeds = new List<Ped>(16);
    private readonly Random _random = new Random();
    private int _lastCheckTime = 0;

    public ForceSwimmingPeds()
    {
        LoadSettings();
        LoadApsProfiles();

        // Initialize LemonUI Menus
        _pool = new ObjectPool();
        _mainMenu = new NativeMenu("Force Swimming Peds", "Swimming Location & Behavior");
        _advancedMenu = new NativeMenu("Advanced Settings", "Coordinates & Locations");
        _manageLocationsMenu = new NativeMenu("Manage Locations", "Saved Swimming Locations");
        _coordsMenu = new NativeMenu("Coordinates", "Fine-Tune Position Sliders");
        _pool.Add(_mainMenu);
        _pool.Add(_advancedMenu);
        _pool.Add(_manageLocationsMenu);
        _pool.Add(_coordsMenu);

        foreach (var menu in new[] { _mainMenu, _advancedMenu, _manageLocationsMenu, _coordsMenu })
        {
            menu.CloseOnInvalidClick = false;
            menu.RotateCamera = true;
            menu.DisableControls = false;
        }

        _mainMenu.Closed += (sender, e) =>
        {
            SaveSettings();
        };

        // 1. Enable Swimming Behavior Checkbox (Row 1 - top of menu)
        _enableCheckbox = new NativeCheckboxItem("Enable Swimming Behavior", "Enables forced swimming for models in this area. Unchecking recalls all swimmers back to entry points.", _enabled);
        _enableCheckbox.CheckboxChanged += (sender, e) =>
        {
            SetSwimmingEnabled(_enableCheckbox.Checked);
        };
        _mainMenu.Add(_enableCheckbox);

        // 2. Recall All Swimmers Button (Row 2 - prominent and highlighted in yellow)
        _recallSwimmersItem = new NativeItem("~y~Recall All Swimmers~s~", "Calls all active swimmers back to their entry points and restores their activities.");
        _recallSwimmersItem.Activated += (sender, e) =>
        {
            RecallAllSwimmers();
        };
        _mainMenu.Add(_recallSwimmersItem);

        // 3. Saved Location Switcher List Item (Row 3)
        var locationNames = _locations.Select(l => l.Name).ToArray();
        _locationListItem = new NativeListItem<string>("Location", "Select a saved swimming location.", locationNames);
        _locationListItem.ItemChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            int idx = _locationListItem.SelectedIndex;
            if (idx >= 0 && idx < _locations.Count)
            {
                SetActiveLocation(_locations[idx], true);
            }
        };
        _locationListItem.Activated += (sender, e) =>
        {
            int idx = _locationListItem.SelectedIndex;
            if (idx >= 0 && idx < _locations.Count)
            {
                SetActiveLocation(_locations[idx], true);
            }
        };
        _mainMenu.Add(_locationListItem);

        // 4. Save Location As... Item (Row 4)
        _saveNewLocationItem = new NativeItem("Save Location As...", "Save current coordinates, radius, and timing under a custom friendly name.");
        _saveNewLocationItem.Activated += (sender, e) =>
        {
            string newName = Game.GetUserInput(WindowTitle.EnterMessage60, "My Pool", 30);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                newName = newName.Trim();
                var existing = _locations.FirstOrDefault(l => l.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Center = _targetLocation;
                    existing.Radius = _radius;
                    existing.SwimChance = _swimChancePercent;
                    existing.RestDuration = _restDurationSeconds;
                    existing.SwimDuration = _swimDurationSeconds;
                    SetActiveLocation(existing, true);
                    Notification.Show($"~g~Updated existing location:~s~ {newName}");
                }
                else
                {
                    var newLoc = new SwimmingLocation(newName, _targetLocation, _radius, _swimChancePercent, _restDurationSeconds, _swimDurationSeconds);
                    _locations.Add(newLoc);
                    SetActiveLocation(newLoc, true);
                    Notification.Show($"~g~Saved new location:~s~ {newName}");
                }
            }
        };
        _mainMenu.Add(_saveNewLocationItem);

        // 5. Pick Location on Screen (Row 5)
        _pickLocationItem = new NativeItem("Pick on Screen", "Shows a crosshair to place center point and preview green radius zone. Left-Click to accept.");
        _pickLocationItem.Activated += (sender, e) =>
        {
            _mainMenu.Visible = false;
            _isPickingLocation = true;
            _lastCursorX = 0.5f;
            _lastCursorY = 0.5f;
            Notification.Show("~b~Location Picker Active~w~: Move mouse, scroll to resize radius, and ~g~Left Click~w~ to accept.");
        };
        _mainMenu.Add(_pickLocationItem);

        // 6. Area Radius Slider (Row 6)
        _radiusSlider = new NativeSliderItem("Area Radius: 25m", "Adjust area radius covering the pool/water body (5m-200m). Press Enter to type exact radius.", 200, (int)Math.Max(5, Math.Min(200, _radius)));
        _radiusSlider.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            _radius = Math.Max(5, _radiusSlider.Value);
            if (_activeLocation != null) _activeLocation.Radius = _radius;
            _radiusSlider.Title = $"Area Radius: {_radius:F0}m";
        };
        _radiusSlider.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _radius.ToString("F0", CultureInfo.InvariantCulture), 10);
            if (!string.IsNullOrEmpty(input) && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) && val >= 1.0f)
            {
                _radius = val;
                if (_activeLocation != null) _activeLocation.Radius = _radius;
                _isUpdatingUI = true;
                _radiusSlider.Value = (int)Math.Min(200, Math.Max(5, val));
                _radiusSlider.Title = $"Area Radius: {_radius:F0}m";
                _isUpdatingUI = false;
                SaveSettings();
                Notification.Show($"~g~Area Radius set to: {_radius:F0}m");
            }
        };
        _mainMenu.Add(_radiusSlider);

        // 7. Swim Chance Slider (Row 7)
        _chanceSlider = new NativeSliderItem("Swim Chance: 10%", "Adjust percentage chance for eligible peds in radius to swim (1-100%). Press Enter to type exact %.", 100, _swimChancePercent);
        _chanceSlider.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            _swimChancePercent = Math.Max(1, Math.Min(100, _chanceSlider.Value));
            if (_activeLocation != null) _activeLocation.SwimChance = _swimChancePercent;
            _chanceSlider.Title = $"Swim Chance: {_swimChancePercent}%";
        };
        _chanceSlider.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _swimChancePercent.ToString(), 5);
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out int val) && val >= 1 && val <= 100)
            {
                _swimChancePercent = val;
                if (_activeLocation != null) _activeLocation.SwimChance = _swimChancePercent;
                _isUpdatingUI = true;
                _chanceSlider.Value = val;
                _chanceSlider.Title = $"Swim Chance: {_swimChancePercent}%";
                _isUpdatingUI = false;
                SaveSettings();
                Notification.Show($"~g~Swim Chance set to: {_swimChancePercent}%");
            }
        };
        _mainMenu.Add(_chanceSlider);

        // 8. Rest / Immune Time Slider (Row 8)
        _restSlider = new NativeSliderItem($"Rest / Immune Time: {_restDurationSeconds}s", "Time in seconds models rest (sunbathing or original task loop) and stay immune to swim triggers (10s-300s). Press Enter to type.", 300, _restDurationSeconds);
        _restSlider.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            _restDurationSeconds = Math.Max(10, Math.Min(300, _restSlider.Value));
            if (_activeLocation != null) _activeLocation.RestDuration = _restDurationSeconds;
            _restSlider.Title = $"Rest / Immune Time: {_restDurationSeconds}s";
        };
        _restSlider.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _restDurationSeconds.ToString(), 5);
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out int val) && val >= 5 && val <= 600)
            {
                _restDurationSeconds = val;
                if (_activeLocation != null) _activeLocation.RestDuration = _restDurationSeconds;
                _isUpdatingUI = true;
                _restSlider.Value = Math.Min(300, val);
                _restSlider.Title = $"Rest / Immune Time: {_restDurationSeconds}s";
                _isUpdatingUI = false;
                SaveSettings();
                Notification.Show($"~g~Rest / Immune Time set to: {_restDurationSeconds}s");
            }
        };
        _mainMenu.Add(_restSlider);

        // 9. Swim Duration Slider (Row 9)
        _swimDurationSlider = new NativeSliderItem($"Swim Duration: {_swimDurationSeconds}s", "Time in seconds models swim before automatically returning to their starting spot (15s-300s). Press Enter to type.", 300, _swimDurationSeconds);
        _swimDurationSlider.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            _swimDurationSeconds = Math.Max(15, Math.Min(300, _swimDurationSlider.Value));
            if (_activeLocation != null) _activeLocation.SwimDuration = _swimDurationSeconds;
            _swimDurationSlider.Title = $"Swim Duration: {_swimDurationSeconds}s";
        };
        _swimDurationSlider.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _swimDurationSeconds.ToString(), 5);
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out int val) && val >= 10 && val <= 600)
            {
                _swimDurationSeconds = val;
                if (_activeLocation != null) _activeLocation.SwimDuration = _swimDurationSeconds;
                _isUpdatingUI = true;
                _swimDurationSlider.Value = Math.Min(300, val);
                _swimDurationSlider.Title = $"Swim Duration: {_swimDurationSeconds}s";
                _isUpdatingUI = false;
                SaveSettings();
                Notification.Show($"~g~Swim Duration set to: {_swimDurationSeconds}s");
            }
        };
        _mainMenu.Add(_swimDurationSlider);

        // 10. Submenu: Advanced Settings (Row 10 - strictly 10 items on main screen)
        _mainMenu.AddSubMenu(_advancedMenu);

        // Inside Advanced Settings Submenu:
        // Teleport to Center
        _teleportItem = new NativeItem("Teleport to Center", "Teleports player character to active location center point.");
        _teleportItem.Activated += (sender, e) =>
        {
            Ped player = Game.Player.Character;
            if (player != null && player.Exists())
            {
                if (_targetLocation == Vector3.Zero)
                {
                    Notification.Show("~y~No target location set yet. Use 'Pick on Screen' first.");
                }
                else
                {
                    player.Position = new Vector3(_targetLocation.X, _targetLocation.Y, _targetLocation.Z + 1.0f);
                    Notification.Show($"~g~Teleported to '{_activeLocation?.Name}' center.");
                }
            }
        };
        _advancedMenu.Add(_teleportItem);

        // Coordinates Submenu (nested inside Advanced Settings)
        _sliderX = new NativeSliderItem("X Coordinate: 0.00", "Use Left/Right to nudge X (±0.5m). Press Enter to type exact map coordinate.", 200, 100);
        _sliderX.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            int diff = _sliderX.Value - _lastSliderX;
            if (diff != 0)
            {
                _targetLocation = new Vector3(_targetLocation.X + (diff * 0.5f), _targetLocation.Y, _targetLocation.Z);
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                _lastSliderX = _sliderX.Value;
                UpdateSliderLabels();
                if (_sliderX.Value <= 10 || _sliderX.Value >= 190)
                {
                    ResetSliderCenters();
                }
            }
        };
        _sliderX.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _targetLocation.X.ToString("F2", CultureInfo.InvariantCulture), 30);
            if (!string.IsNullOrEmpty(input) && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                _targetLocation = new Vector3(val, _targetLocation.Y, _targetLocation.Z);
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                UpdateSlidersFromLocation();
                SaveSettings();
                Notification.Show($"~g~X Coordinate set to: {val:F2}");
            }
        };
        _coordsMenu.Add(_sliderX);

        _sliderY = new NativeSliderItem("Y Coordinate: 0.00", "Use Left/Right to nudge Y (±0.5m). Press Enter to type exact map coordinate.", 200, 100);
        _sliderY.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            int diff = _sliderY.Value - _lastSliderY;
            if (diff != 0)
            {
                _targetLocation = new Vector3(_targetLocation.X, _targetLocation.Y + (diff * 0.5f), _targetLocation.Z);
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                _lastSliderY = _sliderY.Value;
                UpdateSliderLabels();
                if (_sliderY.Value <= 10 || _sliderY.Value >= 190)
                {
                    ResetSliderCenters();
                }
            }
        };
        _sliderY.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _targetLocation.Y.ToString("F2", CultureInfo.InvariantCulture), 30);
            if (!string.IsNullOrEmpty(input) && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                _targetLocation = new Vector3(val, _targetLocation.Y, _targetLocation.Z);
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                UpdateSlidersFromLocation();
                SaveSettings();
                Notification.Show($"~g~Y Coordinate set to: {val:F2}");
            }
        };
        _coordsMenu.Add(_sliderY);

        _sliderZ = new NativeSliderItem("Z Coordinate: 0.00", "Use Left/Right to nudge Z (±0.5m). Press Enter to type exact map coordinate.", 200, 100);
        _sliderZ.ValueChanged += (sender, e) =>
        {
            if (_isUpdatingUI) return;
            int diff = _sliderZ.Value - _lastSliderZ;
            if (diff != 0)
            {
                _targetLocation = new Vector3(_targetLocation.X, _targetLocation.Y, _targetLocation.Z + (diff * 0.5f));
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                _lastSliderZ = _sliderZ.Value;
                UpdateSliderLabels();
                if (_sliderZ.Value <= 10 || _sliderZ.Value >= 190)
                {
                    ResetSliderCenters();
                }
            }
        };
        _sliderZ.Activated += (sender, e) =>
        {
            string input = Game.GetUserInput(WindowTitle.EnterMessage60, _targetLocation.Z.ToString("F2", CultureInfo.InvariantCulture), 30);
            if (!string.IsNullOrEmpty(input) && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
            {
                _targetLocation = new Vector3(_targetLocation.X, _targetLocation.Y, val);
                if (_activeLocation != null) _activeLocation.Center = _targetLocation;
                UpdateSlidersFromLocation();
                SaveSettings();
                Notification.Show($"~g~Z Coordinate set to: {val:F2}");
            }
        };
        _coordsMenu.Add(_sliderZ);

        _updateCurrentLocationItem = new NativeItem("Update Location", "Saves current coordinates, radius, and chance back to the active location profile.");
        _updateCurrentLocationItem.Activated += (sender, e) =>
        {
            if (_activeLocation != null)
            {
                _activeLocation.Center = _targetLocation;
                _activeLocation.Radius = _radius;
                _activeLocation.SwimChance = _swimChancePercent;
                _activeLocation.RestDuration = _restDurationSeconds;
                _activeLocation.SwimDuration = _swimDurationSeconds;
                SaveSettings();
                Notification.Show($"~g~Location '{_activeLocation.Name}' updated and saved.");
            }
        };
        _coordsMenu.Add(_updateCurrentLocationItem);

        _advancedMenu.AddSubMenu(_coordsMenu);

        // Manage Saved Locations Submenu (nested inside Advanced Settings)
        _teleportSubItem = new NativeListItem<string>("Teleport To", "Select a saved location and press Enter to teleport to its center point.", locationNames);
        _teleportSubItem.Activated += (sender, e) =>
        {
            int idx = _teleportSubItem.SelectedIndex;
            if (idx >= 0 && idx < _locations.Count)
            {
                var loc = _locations[idx];
                SetActiveLocation(loc, true);
                Ped player = Game.Player.Character;
                if (player != null && player.Exists() && loc.Center != Vector3.Zero)
                {
                    player.Position = new Vector3(loc.Center.X, loc.Center.Y, loc.Center.Z + 1.0f);
                    Notification.Show($"~g~Teleported to '{loc.Name}' center.");
                }
            }
        };
        _manageLocationsMenu.Add(_teleportSubItem);

        _renameSubItem = new NativeItem("Rename Current", "Rename the currently active swimming location.");
        _renameSubItem.Activated += (sender, e) =>
        {
            if (_activeLocation != null)
            {
                string ren = Game.GetUserInput(WindowTitle.EnterMessage60, _activeLocation.Name, 30);
                if (!string.IsNullOrWhiteSpace(ren))
                {
                    string oldName = _activeLocation.Name;
                    _activeLocation.Name = ren.Trim();
                    UpdateLocationMenuLists();
                    SaveSettings();
                    Notification.Show($"~g~Renamed '{oldName}' to '{_activeLocation.Name}'");
                }
            }
        };
        _manageLocationsMenu.Add(_renameSubItem);

        _deleteSubItem = new NativeListItem<string>("Delete Location", "Select a saved location and press Enter to delete it.", locationNames);
        _deleteSubItem.Activated += (sender, e) =>
        {
            int idx = _deleteSubItem.SelectedIndex;
            if (_locations.Count <= 1)
            {
                Notification.Show("~r~Cannot delete the only remaining location.");
                return;
            }

            if (idx >= 0 && idx < _locations.Count)
            {
                var locToDelete = _locations[idx];
                _locations.RemoveAt(idx);
                if (_activeLocation == locToDelete)
                {
                    _activeLocation = _locations[0];
                    _targetLocation = _activeLocation.Center;
                    _radius = _activeLocation.Radius;
                    _swimChancePercent = _activeLocation.SwimChance;
                    _restDurationSeconds = _activeLocation.RestDuration;
                    _swimDurationSeconds = _activeLocation.SwimDuration;
                    UpdateSlidersFromLocation();
                }
                UpdateLocationMenuLists();
                SaveSettings();
                Notification.Show($"~r~Deleted location:~s~ {locToDelete.Name}");
            }
        };
        _manageLocationsMenu.Add(_deleteSubItem);

        _advancedMenu.AddSubMenu(_manageLocationsMenu);

        UpdateSlidersFromLocation();
        UpdateLocationMenuLists();

        // Register script events
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Aborted += OnAborted;
    }

    private void SetActiveLocation(SwimmingLocation loc, bool save = true)
    {
        if (loc == null) return;
        _activeLocation = loc;
        _targetLocation = loc.Center;
        _radius = loc.Radius;
        _swimChancePercent = loc.SwimChance;
        _restDurationSeconds = loc.RestDuration > 0 ? loc.RestDuration : 30;
        _swimDurationSeconds = loc.SwimDuration > 0 ? loc.SwimDuration : 45;

        UpdateSlidersFromLocation();
        UpdateLocationMenuLists();

        if (save) SaveSettings();
        Notification.Show($"~g~Active Location:~s~ {loc.Name}");
    }

    private void UpdateLocationMenuLists()
    {
        _isUpdatingUI = true;
        var names = _locations.Select(l => l.Name).ToList();

        _locationListItem.Items.Clear();
        foreach (var n in names) _locationListItem.Items.Add(n);
        int idx = _locations.IndexOf(_activeLocation);
        _locationListItem.SelectedIndex = idx >= 0 ? idx : 0;

        if (_teleportSubItem != null)
        {
            _teleportSubItem.Items.Clear();
            foreach (var n in names) _teleportSubItem.Items.Add(n);
            _teleportSubItem.SelectedIndex = idx >= 0 ? idx : 0;
        }

        if (_deleteSubItem != null)
        {
            _deleteSubItem.Items.Clear();
            foreach (var n in names) _deleteSubItem.Items.Add(n);
            _deleteSubItem.SelectedIndex = idx >= 0 ? idx : 0;
        }

        _isUpdatingUI = false;
    }

    private void UpdateSlidersFromLocation()
    {
        _isUpdatingUI = true;
        _sliderX.Title = $"X Coordinate: {_targetLocation.X:F2}";
        _sliderY.Title = $"Y Coordinate: {_targetLocation.Y:F2}";
        _sliderZ.Title = $"Z Coordinate: {_targetLocation.Z:F2}";
        _sliderX.Value = 100;
        _sliderY.Value = 100;
        _sliderZ.Value = 100;
        _lastSliderX = 100;
        _lastSliderY = 100;
        _lastSliderZ = 100;

        _radiusSlider.Title = $"Area Radius: {_radius:F0}m";
        _radiusSlider.Value = (int)Math.Max(5, Math.Min(200, _radius));

        _chanceSlider.Title = $"Swim Chance: {_swimChancePercent}%";
        _chanceSlider.Value = Math.Max(1, Math.Min(100, _swimChancePercent));

        _restSlider.Title = $"Rest / Immune Time: {_restDurationSeconds}s";
        _restSlider.Value = Math.Max(10, Math.Min(300, _restDurationSeconds));

        _swimDurationSlider.Title = $"Swim Duration: {_swimDurationSeconds}s";
        _swimDurationSlider.Value = Math.Max(15, Math.Min(300, _swimDurationSeconds));
        _isUpdatingUI = false;
    }

    private void UpdateSliderLabels()
    {
        _sliderX.Title = $"X Coordinate: {_targetLocation.X:F2}";
        _sliderY.Title = $"Y Coordinate: {_targetLocation.Y:F2}";
        _sliderZ.Title = $"Z Coordinate: {_targetLocation.Z:F2}";
    }

    private void ResetSliderCenters()
    {
        _isUpdatingUI = true;
        _sliderX.Value = 100;
        _sliderY.Value = 100;
        _sliderZ.Value = 100;
        _lastSliderX = 100;
        _lastSliderY = 100;
        _lastSliderZ = 100;
        _isUpdatingUI = false;
    }

    private void RecallAllSwimmers()
    {
        int count = 0;
        int now = Game.GameTime;

        for (int i = 0; i < _activeSwimmers.Count; i++)
        {
            var swimmer = _activeSwimmers[i];
            if (swimmer.Ped != null && swimmer.Ped.Exists() && swimmer.Ped.IsAlive)
            {
                if (swimmer.State == SwimmerState.SwimmingToCenter || swimmer.State == SwimmerState.SwimmingInPool)
                {
                    swimmer.State = SwimmerState.ReturningToEntry;
                    swimmer.StateStartTime = now;
                    swimmer.LastTaskTime = 0;

                    // Pre-load saved animation dictionary early if applicable
                    if (swimmer.HadTaskLoop && swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict))
                    {
                        Function.Call(Hash.REQUEST_ANIM_DICT, swimmer.SavedAnimDict);
                    }

                    count++;
                }
            }
        }

        if (count > 0)
        {
            Notification.Show($"~y~Recalling {count} swimmer(s) back to entry point(s)...");
        }
        else
        {
            Notification.Show("~y~No active swimmers to recall.");
        }
    }

    private void SetSwimmingEnabled(bool enable)
    {
        _enabled = enable;
        SaveSettings();

        if (_enabled)
        {
            if (_targetLocation == Vector3.Zero)
            {
                Notification.Show("~y~Warning: Center coordinates are (0,0,0). Use 'Pick on Screen' or adjust sliders.");
            }
            else
            {
                Notification.Show($"~g~Swimming behavior enabled for '{_activeLocation?.Name}' (Radius: {_radius:F0}m, Chance: {_swimChancePercent}%).");
            }
        }
        else
        {
            RecallAllSwimmers();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == _menuKey)
        {
            if (_isPickingLocation)
            {
                // Cancel picking and reopen menu
                _isPickingLocation = false;
                _mainMenu.Visible = true;
            }
            else
            {
                if (IsAnyMenuOpen)
                {
                    _mainMenu.Visible = false;
                    _advancedMenu.Visible = false;
                    _coordsMenu.Visible = false;
                    _manageLocationsMenu.Visible = false;
                    SaveSettings();
                }
                else
                {
                    _mainMenu.Visible = true;
                }
            }
        }
        else if (e.KeyCode == Keys.Back)
        {
            // If the root main menu is visible (no submenu open), close it on Backspace
            if (_mainMenu.Visible && !_advancedMenu.Visible && !_coordsMenu.Visible && !_manageLocationsMenu.Visible)
            {
                _mainMenu.Visible = false;
                SaveSettings();
            }
        }
        else if (_isPickingLocation)
        {
            if (e.KeyCode == Keys.Escape)
            {
                _isPickingLocation = false;
                _mainMenu.Visible = true;
            }
            else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
            {
                _radius = Math.Min(200.0f, _radius + 2.0f);
                UpdateSlidersFromLocation();
            }
            else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
            {
                _radius = Math.Max(5.0f, _radius - 2.0f);
                UpdateSlidersFromLocation();
            }
        }
    }

    private void OnTick(object sender, EventArgs e)
    {
        _pool.Process();

        if (IsAnyMenuOpen)
        {
            // Suppress attack/firing controls while menus are open so player can look and move around freely without punching or shooting
            Game.DisableControlThisFrame(Control.Attack);
            Game.DisableControlThisFrame(Control.Attack2);
            Game.DisableControlThisFrame(Control.MeleeAttack1);
            Game.DisableControlThisFrame(Control.MeleeAttack2);
            Game.DisableControlThisFrame(Control.SelectWeapon);

            // Render visual aid while main menu or coordinate/profile submenus are open so user can adjust location and radius sliders
            if (_targetLocation != Vector3.Zero)
            {
                DrawRadiusVisualAid(_targetLocation, _radius);
            }
        }

        if (_isPickingLocation)
        {
            ProcessLocationPicker();
        }

        ProcessSwimmerStateMachine();
    }

    // ============================================================
    // Location Picker & Screen Raycasting
    // ============================================================
    private void ProcessLocationPicker()
    {
        // Disable attack/fire controls to prevent firing weapon while clicking
        Game.DisableControlThisFrame(Control.Attack);
        Game.DisableControlThisFrame(Control.Attack2);
        Game.DisableControlThisFrame(Control.Aim);
        Game.DisableControlThisFrame(Control.MeleeAttack1);
        Game.DisableControlThisFrame(Control.MeleeAttack2);
        Game.DisableControlThisFrame(Control.SelectWeapon);
        Game.DisableControlThisFrame(Control.NextCamera);

        // Activate mouse cursor
        Function.Call(Hash.SET_MOUSE_CURSOR_THIS_FRAME);

        float curX = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.CursorX);
        float curY = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.CursorY);

        if (curX > 0.001f || curY > 0.001f)
        {
            _lastCursorX = curX;
            _lastCursorY = curY;
        }

        float screenX = _lastCursorX;
        float screenY = _lastCursorY;

        // Draw crosshair on screen at cursor position
        DrawCrosshair(screenX, screenY);

        // Raycast from camera through screen coordinate into world
        Vector3 camPos = GameplayCamera.Position;
        Vector3 camRot = GameplayCamera.Rotation;
        Vector3 worldRayTarget = ScreenRelToWorld(camPos, camRot, new Vector2(screenX, screenY));
        Vector3 rayDir = (worldRayTarget - camPos).Normalized;
        Vector3 rayEnd = camPos + (rayDir * 1000.0f);

        // IntersectFlags: -1 (all geometry, map, objects, vehicles, peds, water)
        RaycastResult ray = World.Raycast(camPos, rayEnd, (IntersectFlags)(-1), Game.Player.Character);
        Vector3 hitPoint = ray.DidHit ? ray.HitPosition : (camPos + (rayDir * 40.0f));

        // Probe water height at the hit coordinates
        using (var waterHeightArg = new OutputArgument())
        {
            if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, hitPoint.X, hitPoint.Y, hitPoint.Z, waterHeightArg))
            {
                float wh = waterHeightArg.GetResult<float>();
                if (wh > hitPoint.Z - 6.0f && wh < hitPoint.Z + 6.0f)
                {
                    hitPoint = new Vector3(hitPoint.X, hitPoint.Y, wh);
                }
            }
        }

        // Render real-time green dome visual aid indicating radius from prospective center point
        DrawRadiusVisualAid(hitPoint, _radius);

        // Allow mouse wheel to nudge radius dynamically while aiming
        if (Game.IsControlJustPressed(Control.CursorScrollUp) || Game.IsControlJustPressed(Control.SelectNextWeapon))
        {
            _radius = Math.Min(200.0f, _radius + 2.0f);
            UpdateSlidersFromLocation();
        }
        else if (Game.IsControlJustPressed(Control.CursorScrollDown) || Game.IsControlJustPressed(Control.SelectPrevWeapon))
        {
            _radius = Math.Max(5.0f, _radius - 2.0f);
            UpdateSlidersFromLocation();
        }

        // Draw on-screen instruction banner
        Screen.ShowHelpTextThisFrame($"~INPUT_ATTACK~ Left Click: Accept Location | Radius: ~g~{_radius:F0}m~s~ (Scroll/PgUp/PgDn) | ~INPUT_CELLPHONE_CANCEL~ Cancel");

        // Handle Left Click: Commit location
        if (Game.IsControlJustPressed(Control.Attack) || Game.IsControlJustPressed(Control.CursorAccept))
        {
            _targetLocation = hitPoint;
            if (_activeLocation != null)
            {
                _activeLocation.Center = _targetLocation;
                _activeLocation.Radius = _radius;
            }
            _isPickingLocation = false;
            Audio.PlaySoundFrontend("SELECT", "HUD_FRONTEND_DEFAULT_SOUNDSET");
            UpdateSlidersFromLocation();
            SaveSettings();
            _mainMenu.Visible = true;
            Notification.Show($"~g~Center Location Set:~s~ X:{_targetLocation.X:F2} Y:{_targetLocation.Y:F2} Z:{_targetLocation.Z:F2}");
        }
        // Handle Right Click / Cancel
        else if (Game.IsControlJustPressed(Control.Attack2) || Game.IsControlJustPressed(Control.CursorCancel) || Game.IsControlJustPressed(Control.PhoneCancel))
        {
            _isPickingLocation = false;
            _mainMenu.Visible = true;
        }
    }

    private void DrawCrosshair(float screenX, float screenY)
    {
        // Vertical line
        Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.0020f, 0.038f, 255, 255, 255, 230);
        // Horizontal line
        Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.022f, 0.0035f, 255, 255, 255, 230);
        // Center focal dot
        Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.0045f, 0.0075f, 0, 200, 255, 255);
    }

    /// <summary>
    /// Renders a single semi-transparent green boundary cylinder to visually indicate the swimming activation area radius.
    /// Only the actual activation area is drawn as a single boundary ring, with a center indicator pin.
    /// </summary>
    private void DrawRadiusVisualAid(Vector3 center, float radius)
    {
        if (center == Vector3.Zero || radius <= 0.1f) return;

        // 1. Single semi-transparent green vertical cylinder representing the exact activation boundary (only 1 ring)
        World.DrawMarker(
            MarkerType.VerticalCylinder,
            new Vector3(center.X, center.Y, center.Z - 0.5f),
            Vector3.Zero,
            Vector3.Zero,
            new Vector3(radius * 2.0f, radius * 2.0f, 2.5f),
            CylinderColor
        );

        // 2. Center point focal indicator (downward-pointing golden arrow/cone)
        World.DrawMarker(
            MarkerType.UpsideDownCone,
            new Vector3(center.X, center.Y, center.Z + 1.2f),
            Vector3.Zero,
            ConeRotation,
            ConeScale,
            ConeColor
        );
    }

    private static Vector3 ScreenRelToWorld(Vector3 camPos, Vector3 camRot, Vector2 screenCoord)
    {
        Vector3 camForward = RotationToDirection(camRot);
        Vector3 rotUp = camRot + new Vector3(10.0f, 0.0f, 0.0f);
        Vector3 rotDown = camRot + new Vector3(-10.0f, 0.0f, 0.0f);
        Vector3 rotLeft = camRot + new Vector3(0.0f, 0.0f, -10.0f);
        Vector3 rotRight = camRot + new Vector3(0.0f, 0.0f, 10.0f);

        Vector3 camRight = RotationToDirection(rotRight) - RotationToDirection(rotLeft);
        Vector3 camUp = RotationToDirection(rotUp) - RotationToDirection(rotDown);

        float rollRad = -camRot.Y * DegToRad;
        float cosRoll = (float)Math.Cos(rollRad);
        float sinRoll = (float)Math.Sin(rollRad);
        Vector3 camRightRoll = (camRight * cosRoll) - (camUp * sinRoll);
        Vector3 camUpRoll = (camRight * sinRoll) + (camUp * cosRoll);

        Vector3 point3D = camPos + (camForward * 10.0f);
        Vector3 point3DTest = point3D + camRightRoll + camUpRoll;

        if (!WorldToScreen(point3DTest, out Vector2 screenPointTest) || !WorldToScreen(point3D, out Vector2 screenPoint))
        {
            return camPos + (camForward * 10.0f);
        }

        if (Math.Abs(screenPointTest.X - screenPoint.X) < 0.0001f || Math.Abs(screenPointTest.Y - screenPoint.Y) < 0.0001f)
        {
            return camPos + (camForward * 10.0f);
        }

        float scaleX = (screenCoord.X - screenPoint.X) / (screenPointTest.X - screenPoint.X);
        float scaleY = (screenCoord.Y - screenPoint.Y) / (screenPointTest.Y - screenPoint.Y);

        return point3D + (camRightRoll * scaleX) + (camUpRoll * scaleY);
    }

    private static bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
    {
        using (var outX = new OutputArgument())
        using (var outY = new OutputArgument())
        {
            bool success = Function.Call<bool>(Hash.GET_SCREEN_COORD_FROM_WORLD_COORD, worldPos.X, worldPos.Y, worldPos.Z, outX, outY);
            screenPos = new Vector2(outX.GetResult<float>(), outY.GetResult<float>());
            return success;
        }
    }

    private static Vector3 RotationToDirection(Vector3 rot)
    {
        float z = rot.Z * DegToRad;
        float x = rot.X * DegToRad;
        float num = (float)Math.Abs(Math.Cos(x));
        return new Vector3(-(float)(Math.Sin(z) * num), (float)(Math.Cos(z) * num), (float)Math.Sin(x));
    }

    private bool IsPedTracked(Ped ped)
    {
        for (int i = 0; i < _activeSwimmers.Count; i++)
        {
            if (_activeSwimmers[i].Ped == ped)
                return true;
        }
        return false;
    }

    // ============================================================
    // Swimming Ped AI State Machine
    // ============================================================
    private void ProcessSwimmerStateMachine()
    {
        int now = Game.GameTime;

        // Process all active, returning, and anchored swimmers
        for (int i = _activeSwimmers.Count - 1; i >= 0; i--)
        {
            var swimmer = _activeSwimmers[i];
            Ped ped = swimmer.Ped;

            // 1. Guard against dead, deleted, or despawned peds
            if (ped == null || !ped.Exists() || !ped.IsAlive)
            {
                _activeSwimmers.RemoveAt(i);
                continue;
            }

            int stateElapsed = now - swimmer.StateStartTime;

            // Lazy in-water evaluation helper
            bool inWaterChecked = false;
            bool inWater = false;
            bool CheckInWater()
            {
                if (!inWaterChecked)
                {
                    inWater = Function.Call<bool>(Hash.IS_PED_SWIMMING, ped.Handle) || Function.Call<bool>(Hash.IS_ENTITY_IN_WATER, ped.Handle);
                    inWaterChecked = true;
                }
                return inWater;
            }

            switch (swimmer.State)
            {
                case SwimmerState.SwimmingToCenter:
                    // If behavior disabled or recalled, transition immediately to returning
                    if (!_enabled)
                    {
                        swimmer.State = SwimmerState.ReturningToEntry;
                        swimmer.StateStartTime = now;
                        swimmer.LastTaskTime = 0;

                        if (swimmer.HadTaskLoop && swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict))
                        {
                            Function.Call(Hash.REQUEST_ANIM_DICT, swimmer.SavedAnimDict);
                        }
                        break;
                    }

                    Vector3 posToCenter = ped.Position;
                    float distToCenterSq = posToCenter.DistanceToSquared(_targetLocation);
                    if (distToCenterSq <= 16.0f || (distToCenterSq <= 36.0f && stateElapsed > 4000 && CheckInWater()))
                    {
                        // Arrived in pool center, transition to active pool swimming
                        swimmer.State = SwimmerState.SwimmingInPool;
                        swimmer.StateStartTime = now;
                        swimmer.CurrentSubTarget = GetRandomPointInRadius(_targetLocation, _radius * 0.65f);
                        swimmer.LastTaskTime = now;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, swimmer.CurrentSubTarget.X, swimmer.CurrentSubTarget.Y, swimmer.CurrentSubTarget.Z, 1.2f, -1, 0.0f, 0.0f);
                    }
                    else if (now - swimmer.LastTaskTime > 3500)
                    {
                        // Periodically reinforce movement task
                        swimmer.LastTaskTime = now;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, _targetLocation.X, _targetLocation.Y, _targetLocation.Z, 1.4f, -1, 0.0f, 0.0f);
                    }
                    break;

                case SwimmerState.SwimmingInPool:
                    if (!_enabled)
                    {
                        swimmer.State = SwimmerState.ReturningToEntry;
                        swimmer.StateStartTime = now;
                        swimmer.LastTaskTime = 0;

                        if (swimmer.HadTaskLoop && swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict))
                        {
                            Function.Call(Hash.REQUEST_ANIM_DICT, swimmer.SavedAnimDict);
                        }
                        break;
                    }

                    // Periodic swim limit check: When swim duration expires, return to entry point
                    if (stateElapsed >= swimmer.SwimDurationMs)
                    {
                        swimmer.State = SwimmerState.ReturningToEntry;
                        swimmer.StateStartTime = now;
                        swimmer.LastTaskTime = 0;

                        if (swimmer.HadTaskLoop && swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict))
                        {
                            Function.Call(Hash.REQUEST_ANIM_DICT, swimmer.SavedAnimDict);
                        }
                        break;
                    }

                    // Keep ped within pool bounds using squared distances
                    Vector3 posInPool = ped.Position;
                    float distFromCenterSq = posInPool.DistanceToSquared(_targetLocation);
                    float maxBound = _radius * 0.88f;
                    float maxBoundSq = maxBound * maxBound;

                    if (distFromCenterSq > maxBoundSq)
                    {
                        // Ped drifting near boundary; navigate back toward center
                        swimmer.CurrentSubTarget = _targetLocation;
                        swimmer.LastTaskTime = now;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, _targetLocation.X, _targetLocation.Y, _targetLocation.Z, 1.3f, -1, 0.0f, 0.0f);
                    }
                    else if (posInPool.DistanceToSquared(swimmer.CurrentSubTarget) < 6.25f || (now - swimmer.LastTaskTime > 7000))
                    {
                        // Reached current lap waypoint; choose next waypoint inside pool
                        swimmer.CurrentSubTarget = GetRandomPointInRadius(_targetLocation, _radius * 0.65f);
                        swimmer.LastTaskTime = now;
                        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, swimmer.CurrentSubTarget.X, swimmer.CurrentSubTarget.Y, swimmer.CurrentSubTarget.Z, 1.2f, -1, 0.0f, 0.0f);
                    }
                    break;

                case SwimmerState.ReturningToEntry:
                    Vector3 posReturning = ped.Position;
                    float distToEntrySq = posReturning.DistanceToSquared(swimmer.EntryPosition);

                    // Check if ped has arrived back at their entry spot or watchdog (1.5m -> 2.25m^2, 2.2m -> 4.84m^2)
                    if (distToEntrySq <= 2.25f || (distToEntrySq <= 4.84f && stateElapsed > 6000 && !CheckInWater()) || stateElapsed > 35000)
                    {
                        // Safely arrived back at entry position!
                        swimmer.State = SwimmerState.Resting;
                        swimmer.StateStartTime = now;
                        swimmer.LastTaskTime = now;
                        swimmer.ImmunityUntil = now + (_restDurationSeconds * 1000);

                        Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
                        Function.Call(Hash.SET_PED_COORDS_KEEP_VEHICLE, ped.Handle, swimmer.EntryPosition.X, swimmer.EntryPosition.Y, swimmer.EntryPosition.Z);
                        Function.Call(Hash.SET_ENTITY_HEADING, ped.Handle, swimmer.EntryHeading);
                        Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);

                        if (swimmer.HadTaskLoop)
                        {
                            // Model was already in a task loop when activated: return to previously set task loop!
                            if (swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict) && !string.IsNullOrEmpty(swimmer.SavedAnimClip))
                            {
                                PlayAnimationOnPed(ped, swimmer.SavedAnimDict, swimmer.SavedAnimClip);
                            }
                            else if (swimmer.TaskType == 2 && !string.IsNullOrEmpty(swimmer.SavedScenario))
                            {
                                Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, ped.Handle, swimmer.SavedScenario, 0, true);
                            }
                            else
                            {
                                Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped.Handle, swimmer.EntryPosition.X, swimmer.EntryPosition.Y, swimmer.EntryPosition.Z, 15.0f, -1);
                            }
                        }
                        else
                        {
                            // Model had no active task loop when activated:
                            // Exit water and perform nearest scenario action (lying on a towel, beach chair, sitting in a chair) for rest duration
                            int restMs = _restDurationSeconds * 1000;
                            Function.Call(Hash.TASK_USE_NEAREST_SCENARIO_TO_COORD, ped.Handle, swimmer.EntryPosition.X, swimmer.EntryPosition.Y, swimmer.EntryPosition.Z, 25.0f, restMs);
                            swimmer.ScenarioAttempted = true;
                        }
                        break;
                    }

                    // Navigation while returning
                    if (now - swimmer.LastTaskTime > 2500)
                    {
                        swimmer.LastTaskTime = now;
                        if (CheckInWater())
                        {
                            // In water: swim straight towards entry point
                            Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, swimmer.EntryPosition.X, swimmer.EntryPosition.Y, swimmer.EntryPosition.Z, 1.4f, -1, 0.0f, 0.0f);
                        }
                        else
                        {
                            // On land: use navmesh navigation to pathfind around loungers, steps, and obstacles
                            Function.Call(Hash.TASK_FOLLOW_NAV_MESH_TO_COORD, ped.Handle, swimmer.EntryPosition.X, swimmer.EntryPosition.Y, swimmer.EntryPosition.Z, 1.3f, -1, 0.25f, 0, 0.0f);
                        }
                    }
                    break;

                case SwimmerState.Resting:
                    // If ped had no prior task loop, verify after 3.5s if they began a scenario
                    // If no native map scenario node was within range, fallback to towel sunbathing in place!
                    if (!swimmer.HadTaskLoop && swimmer.ScenarioAttempted && (stateElapsed > 3500) && (stateElapsed < 6500))
                    {
                        bool inScenario = Function.Call<bool>(Hash.IS_PED_USING_ANY_SCENARIO, ped.Handle);
                        if (!inScenario)
                        {
                            swimmer.ScenarioAttempted = false;
                            string fallbackScenario = (_random.Next(0, 2) == 0) ? "WORLD_HUMAN_SUNBATHE" : "WORLD_HUMAN_SUNBATHE_BACK";
                            Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, ped.Handle, fallbackScenario, 0, true);
                        }
                    }

                    // Check if immunity / rest duration has expired
                    if (now >= swimmer.ImmunityUntil)
                    {
                        // Immunity expired! Model can now be triggered to swim again
                        if (!_enabled)
                        {
                            break;
                        }

                        // If ped had no original task loop, clear temporary scenario and let them stand
                        if (!swimmer.HadTaskLoop)
                        {
                            Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
                            Function.Call(Hash.TASK_STAND_STILL, ped.Handle, -1);
                        }

                        // Remove from active swimmers: makes ped eligible again for TryStartPedSwim()
                        _activeSwimmers.RemoveAt(i);
                        continue;
                    }
                    break;
            }
        }

        // Periodic candidate search when swimming behavior is enabled
        if (_enabled && _targetLocation != Vector3.Zero)
        {
            if (now - _lastCheckTime > 2000)
            {
                _lastCheckTime = now;

                // Max 6 concurrent swimming peds (no LINQ allocation)
                int activeCount = 0;
                for (int j = 0; j < _activeSwimmers.Count; j++)
                {
                    var st = _activeSwimmers[j].State;
                    if (st == SwimmerState.SwimmingToCenter || st == SwimmerState.SwimmingInPool)
                    {
                        activeCount++;
                    }
                }

                if (activeCount < 6)
                {
                    if (_random.Next(0, 100) < _swimChancePercent)
                    {
                        TryStartPedSwim();
                    }
                }
            }
        }
    }

    private void TryStartPedSwim()
    {
        Ped player = Game.Player.Character;
        Ped[] nearbyPeds = World.GetNearbyPeds(_targetLocation, _radius);
        if (nearbyPeds == null || nearbyPeds.Length == 0) return;

        _candidatePeds.Clear();
        for (int i = 0; i < nearbyPeds.Length; i++)
        {
            Ped p = nearbyPeds[i];
            if (p == null || !p.Exists() || !p.IsAlive || p == player || p.IsInVehicle())
                continue;

            // Don't select if currently tracked (swimming, returning, or resting/immune)
            if (IsPedTracked(p))
                continue;

            // Don't interrupt peds in combat or ragdolling
            if (p.IsInCombat || p.IsRagdoll)
                continue;

            _candidatePeds.Add(p);
        }

        if (_candidatePeds.Count > 0)
        {
            Ped selected = _candidatePeds[_random.Next(_candidatePeds.Count)];
            StartSwimRoutine(selected);
        }
    }

    private void StartSwimRoutine(Ped ped)
    {
        Vector3 pedPos = ped.Position;
        float pedHeading = ped.Heading;

        bool hadTaskLoop = false;
        int taskType = 0; // 1 = Animation, 2 = Scenario
        string animDict = "";
        string animClip = "";
        string scenarioName = "";

        // 1. Detect if ped was spawned with APS decorators
        try
        {
            if (Function.Call<bool>(Hash.DECOR_EXIST_ON, ped.Handle, "APS_ProfileHash"))
            {
                int pHash = Function.Call<int>(Hash.DECOR_GET_INT, ped.Handle, "APS_ProfileHash");
                if (_apsProfiles.TryGetValue(pHash, out var prof))
                {
                    if (prof.AnimationType == "Animation" && !string.IsNullOrEmpty(prof.AnimDict) && !string.IsNullOrEmpty(prof.AnimClip))
                    {
                        hadTaskLoop = true;
                        taskType = 1;
                        animDict = prof.AnimDict;
                        animClip = prof.AnimClip;
                    }
                    else if (prof.AnimationType == "Scenario" && !string.IsNullOrEmpty(prof.Scenario))
                    {
                        hadTaskLoop = true;
                        taskType = 2;
                        scenarioName = prof.Scenario;
                    }
                }
            }
        }
        catch { }

        // 2. If not detected via decorator, check native scenario states
        if (!hadTaskLoop)
        {
            try
            {
                if (Function.Call<bool>(Hash.IS_PED_USING_ANY_SCENARIO, ped.Handle))
                {
                    hadTaskLoop = true;
                    taskType = 2;
                    for (int sIdx = 0; sIdx < KnownScenarios.Length; sIdx++)
                    {
                        string s = KnownScenarios[sIdx];
                        if (Function.Call<bool>(Hash.IS_PED_USING_SCENARIO, ped.Handle, s))
                        {
                            scenarioName = s;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // 3. If still not detected, check if playing animation from known APS profiles
        if (!hadTaskLoop && _apsProfiles.Count > 0)
        {
            try
            {
                foreach (var prof in _apsProfiles.Values)
                {
                    if (prof.AnimationType == "Animation" && !string.IsNullOrEmpty(prof.AnimDict) && !string.IsNullOrEmpty(prof.AnimClip))
                    {
                        if (Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, ped.Handle, prof.AnimDict, prof.AnimClip, 3))
                        {
                            hadTaskLoop = true;
                            taskType = 1;
                            animDict = prof.AnimDict;
                            animClip = prof.AnimClip;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // Configure ped immunity & swimming flags
        Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
        Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
        Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 17, false); // BF_AlwaysFlee = false
        Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, ped.Handle, 0, false);
        Function.Call(Hash.SET_PED_DIES_IN_WATER, ped.Handle, false);
        Function.Call(Hash.SET_PED_CONFIG_FLAG, ped.Handle, 64, true); // CPED_CONFIG_FLAG_DrownsInWater = false
        Function.Call(Hash.SET_PED_MAX_TIME_UNDERWATER, ped.Handle, 600.0f);

        // Task: Go straight to center point
        Function.Call(Hash.TASK_GO_STRAIGHT_TO_COORD, ped.Handle, _targetLocation.X, _targetLocation.Y, _targetLocation.Z, 1.4f, -1, 0.0f, 0.0f);

        // Calculate personalized swim duration (base +/- 5s variance)
        int baseSwimMs = _swimDurationSeconds * 1000;
        int variance = _random.Next(-5000, 5001);
        int swimMs = Math.Max(10000, baseSwimMs + variance);

        _activeSwimmers.Add(new ActiveSwimmer
        {
            Ped = ped,
            EntryPosition = pedPos,
            EntryHeading = pedHeading,
            StartTime = Game.GameTime,
            State = SwimmerState.SwimmingToCenter,
            StateStartTime = Game.GameTime,
            CurrentSubTarget = _targetLocation,
            LastTaskTime = Game.GameTime,
            HadTaskLoop = hadTaskLoop,
            TaskType = taskType,
            SavedAnimDict = animDict,
            SavedAnimClip = animClip,
            SavedScenario = scenarioName,
            SwimDurationMs = swimMs,
            ImmunityUntil = 0,
            ScenarioAttempted = false
        });
    }

    private void PlayAnimationOnPed(Ped ped, string animDict, string animClip)
    {
        if (ped == null || !ped.Exists() || !ped.IsAlive) return;
        if (string.IsNullOrWhiteSpace(animDict) || string.IsNullOrWhiteSpace(animClip)) return;

        try
        {
            if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, animDict)) return;

            Function.Call(Hash.REQUEST_ANIM_DICT, animDict);
            int start = Game.GameTime;
            while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, animDict) && (Game.GameTime - start) < 1500)
            {
                Script.Wait(0);
            }

            if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, animDict))
            {
                Function.Call(Hash.TASK_PLAY_ANIM, ped.Handle, animDict, animClip, 8.0f, -8.0f, -1, 1, 0.0f, false, false, false);
                Function.Call(Hash.REMOVE_ANIM_DICT, animDict);
            }
        }
        catch { }
    }

    private void LoadApsProfiles()
    {
        _apsProfiles.Clear();
        try
        {
            string scriptsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            string iniPath = Path.Combine(scriptsFolder, "customizedpeds.ini");
            if (!File.Exists(iniPath))
            {
                iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "customizedpeds.ini");
            }
            if (!File.Exists(iniPath)) return;

            ApsPedProfile cur = null;
            foreach (var rawLine in File.ReadAllLines(iniPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string sec = line.Substring(1, line.Length - 2).Trim();
                    cur = new ApsPedProfile { Name = sec, NameHash = Game.GenerateHash(sec) };
                    _apsProfiles[cur.NameHash] = cur;
                    continue;
                }

                if (cur == null) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();

                if (k.Equals("FriendlyName", StringComparison.OrdinalIgnoreCase))
                {
                    cur.Name = v;
                    cur.NameHash = Game.GenerateHash(v);
                    _apsProfiles[cur.NameHash] = cur;
                }
                else if (k.Equals("Model", StringComparison.OrdinalIgnoreCase)) cur.Model = v;
                else if (k.Equals("MovementStyle", StringComparison.OrdinalIgnoreCase)) cur.MovementStyle = v;
                else if (k.Equals("AnimationType", StringComparison.OrdinalIgnoreCase)) cur.AnimationType = v;
                else if (k.Equals("AnimDict", StringComparison.OrdinalIgnoreCase)) cur.AnimDict = v;
                else if (k.Equals("AnimClip", StringComparison.OrdinalIgnoreCase)) cur.AnimClip = v;
                else if (k.Equals("Scenario", StringComparison.OrdinalIgnoreCase)) cur.Scenario = v;
            }
        }
        catch { }
    }

    private Vector3 GetRandomPointInRadius(Vector3 center, float radius)
    {
        double angle = _random.NextDouble() * Math.PI * 2.0;
        double dist = Math.Sqrt(_random.NextDouble()) * radius;
        float x = center.X + (float)(Math.Cos(angle) * dist);
        float y = center.Y + (float)(Math.Sin(angle) * dist);
        float z = center.Z;

        using (var waterHeightArg = new OutputArgument())
        {
            if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, x, y, z, waterHeightArg))
            {
                float wh = waterHeightArg.GetResult<float>();
                if (Math.Abs(wh - z) < 4.0f)
                {
                    z = wh;
                }
            }
        }

        return new Vector3(x, y, z);
    }

    private void OnAborted(object sender, EventArgs e)
    {
        for (int i = 0; i < _activeSwimmers.Count; i++)
        {
            var swimmer = _activeSwimmers[i];
            if (swimmer.Ped != null && swimmer.Ped.Exists() && swimmer.Ped.IsAlive)
            {
                Function.Call(Hash.CLEAR_PED_TASKS, swimmer.Ped.Handle);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, swimmer.Ped.Handle, false);
                Function.Call(Hash.SET_PED_CONFIG_FLAG, swimmer.Ped.Handle, 64, false);
                Function.Call(Hash.SET_PED_DIES_IN_WATER, swimmer.Ped.Handle, true);
                Function.Call(Hash.SET_PED_MAX_TIME_UNDERWATER, swimmer.Ped.Handle, 15.0f);

                if (swimmer.HadTaskLoop)
                {
                    if (swimmer.TaskType == 1 && !string.IsNullOrEmpty(swimmer.SavedAnimDict) && !string.IsNullOrEmpty(swimmer.SavedAnimClip))
                    {
                        PlayAnimationOnPed(swimmer.Ped, swimmer.SavedAnimDict, swimmer.SavedAnimClip);
                    }
                    else if (swimmer.TaskType == 2 && !string.IsNullOrEmpty(swimmer.SavedScenario))
                    {
                        Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, swimmer.Ped.Handle, swimmer.SavedScenario, 0, true);
                    }
                }
            }
        }
        _activeSwimmers.Clear();
    }

    // ============================================================
    // INI Multi-Location Persistence
    // ============================================================
    private string GetIniPath()
    {
        if (_cachedIniPath != null) return _cachedIniPath;

        string scriptsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
        if (Directory.Exists(scriptsFolder))
        {
            _cachedIniPath = Path.Combine(scriptsFolder, IniFileName);
        }
        else
        {
            _cachedIniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IniFileName);
        }
        return _cachedIniPath;
    }

    private void LoadSettings()
    {
        _locations.Clear();
        string iniPath = GetIniPath();
        if (!File.Exists(iniPath))
        {
            var def = new SwimmingLocation("Default Location", Vector3.Zero, 25.0f, 10);
            _locations.Add(def);
            _activeLocation = def;
            return;
        }

        try
        {
            string currentSection = "";
            SwimmingLocation currentLoc = null;
            string activeLocName = "";

            Vector3 legacyCenter = Vector3.Zero;
            float legacyRadius = 25.0f;
            int legacyChance = 10;
            bool hasLegacyCoords = false;

            foreach (var rawLine in File.ReadAllLines(iniPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (currentSection.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
                    {
                        string locName = currentSection.Substring("Location:".Length).Trim();
                        if (!string.IsNullOrEmpty(locName))
                        {
                            currentLoc = new SwimmingLocation(locName, Vector3.Zero, 25.0f, 10, 30, 45);
                            _locations.Add(currentLoc);
                        }
                    }
                    else
                    {
                        currentLoc = null;
                    }
                    continue;
                }

                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = line.Substring(0, eqIdx).Trim();
                string val = line.Substring(eqIdx + 1).Trim();

                if (currentLoc != null)
                {
                    if (key.Equals("CenterX", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                            currentLoc.Center = new Vector3(x, currentLoc.Center.Y, currentLoc.Center.Z);
                    }
                    else if (key.Equals("CenterY", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                            currentLoc.Center = new Vector3(currentLoc.Center.X, y, currentLoc.Center.Z);
                    }
                    else if (key.Equals("CenterZ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                            currentLoc.Center = new Vector3(currentLoc.Center.X, currentLoc.Center.Y, z);
                    }
                    else if (key.Equals("Radius", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                            currentLoc.Radius = Math.Max(5.0f, r);
                    }
                    else if (key.Equals("SwimChance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int sc))
                            currentLoc.SwimChance = Math.Max(1, Math.Min(100, sc));
                    }
                    else if (key.Equals("RestDuration", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int rd))
                            currentLoc.RestDuration = Math.Max(10, Math.Min(300, rd));
                    }
                    else if (key.Equals("SwimDuration", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int sd))
                            currentLoc.SwimDuration = Math.Max(15, Math.Min(300, sd));
                    }
                }
                else if (currentSection.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("MenuKey", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse(val, true, out Keys k)) _menuKey = k;
                    }
                    else if (key.Equals("ActiveLocation", StringComparison.OrdinalIgnoreCase))
                    {
                        activeLocName = val;
                    }
                    else if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bool.TryParse(val, out bool en)) _enabled = en;
                    }
                    else if (key.Equals("RestDuration", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int rd)) _restDurationSeconds = Math.Max(10, Math.Min(300, rd));
                    }
                    else if (key.Equals("SwimDuration", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int sd)) _swimDurationSeconds = Math.Max(15, Math.Min(300, sd));
                    }
                    else if (key.Equals("CenterX", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                        {
                            legacyCenter = new Vector3(x, legacyCenter.Y, legacyCenter.Z);
                            hasLegacyCoords = true;
                        }
                    }
                    else if (key.Equals("CenterY", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                        {
                            legacyCenter = new Vector3(legacyCenter.X, y, legacyCenter.Z);
                            hasLegacyCoords = true;
                        }
                    }
                    else if (key.Equals("CenterZ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        {
                            legacyCenter = new Vector3(legacyCenter.X, legacyCenter.Y, z);
                            hasLegacyCoords = true;
                        }
                    }
                    else if (key.Equals("Radius", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                            legacyRadius = Math.Max(5.0f, r);
                    }
                    else if (key.Equals("SwimChance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out int sc))
                            legacyChance = Math.Max(1, Math.Min(100, sc));
                    }
                }
            }

            if (_locations.Count == 0 && hasLegacyCoords)
            {
                var legacyLoc = new SwimmingLocation("Default Location", legacyCenter, legacyRadius, legacyChance, _restDurationSeconds, _swimDurationSeconds);
                _locations.Add(legacyLoc);
            }
            else if (_locations.Count == 0)
            {
                var defLoc = new SwimmingLocation("Default Location", Vector3.Zero, 25.0f, 10, 30, 45);
                _locations.Add(defLoc);
            }

            _activeLocation = _locations.FirstOrDefault(l => l.Name.Equals(activeLocName, StringComparison.OrdinalIgnoreCase))
                              ?? _locations[0];

            _targetLocation = _activeLocation.Center;
            _radius = _activeLocation.Radius;
            _swimChancePercent = _activeLocation.SwimChance;
            _restDurationSeconds = _activeLocation.RestDuration > 0 ? _activeLocation.RestDuration : 30;
            _swimDurationSeconds = _activeLocation.SwimDuration > 0 ? _activeLocation.SwimDuration : 45;
        }
        catch (Exception ex)
        {
            Notification.Show($"~r~Error loading settings: {ex.Message}");
            if (_locations.Count == 0)
            {
                var fallback = new SwimmingLocation("Default Location", Vector3.Zero, 25.0f, 10, 30, 45);
                _locations.Add(fallback);
                _activeLocation = fallback;
            }
        }
    }

    private void SaveSettings()
    {
        string iniPath = GetIniPath();
        try
        {
            if (_activeLocation != null)
            {
                _activeLocation.Center = _targetLocation;
                _activeLocation.Radius = _radius;
                _activeLocation.SwimChance = _swimChancePercent;
                _activeLocation.RestDuration = _restDurationSeconds;
                _activeLocation.SwimDuration = _swimDurationSeconds;
            }

            var sb = new System.Text.StringBuilder(1024);
            sb.AppendLine("[Settings]");
            sb.AppendLine("; Key to open/close menu (Default: F5)");
            sb.AppendLine($"MenuKey={_menuKey}");
            sb.AppendLine($"ActiveLocation={_activeLocation?.Name ?? "Default Location"}");
            sb.AppendLine("; Enable or disable swimming behavior (Default: false)");
            sb.AppendLine($"Enabled={_enabled}");
            sb.AppendLine($"RestDuration={_restDurationSeconds}");
            sb.AppendLine($"SwimDuration={_swimDurationSeconds}");
            sb.AppendLine();

            for (int i = 0; i < _locations.Count; i++)
            {
                var loc = _locations[i];
                sb.AppendLine($"[Location:{loc.Name}]");
                sb.AppendLine($"CenterX={loc.Center.X.ToString("F3", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"CenterY={loc.Center.Y.ToString("F3", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"CenterZ={loc.Center.Z.ToString("F3", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"Radius={loc.Radius.ToString("F1", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"SwimChance={loc.SwimChance}");
                sb.AppendLine($"RestDuration={loc.RestDuration}");
                sb.AppendLine($"SwimDuration={loc.SwimDuration}");
                sb.AppendLine();
            }

            File.WriteAllText(iniPath, sb.ToString());
        }
        catch { }
    }
}
