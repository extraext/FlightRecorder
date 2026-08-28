using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;
using UnityEngine.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using KSP.Localization;
using ModuleWheels;

namespace FlightRecorder
{
    public enum RecorderState
    {
        Idle,
        Recording,
        Playback,
        Paused
    }

    #region Harmony Patches

    [HarmonyPatch(typeof(BaseEvent), "Invoke")]
    public static class Patch_BaseEvent_Invoke
    {
        public static void Prefix(BaseEvent __instance)
        {
            if (FlightRecorder.Instance != null && FlightRecorder.Instance.CurrentState == RecorderState.Recording)
            {
                FlightRecorder.Instance.OnBaseEventInvoked(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(BaseAction), "Invoke", new Type[] { typeof(KSPActionParam) })]
    public static class Patch_BaseAction_Invoke
    {
        public static void Prefix(BaseAction __instance, KSPActionParam param)
        {
            if (FlightRecorder.Instance != null && FlightRecorder.Instance.CurrentState == RecorderState.Recording)
            {
                FlightRecorder.Instance.OnBaseActionInvoked(__instance, param);
            }
        }
    }

    [HarmonyPatch(typeof(Vessel), "obt_speed", MethodType.Getter)]
    public static class Patch_Vessel_obt_speed
    {
        public static bool Prefix(Vessel __instance, ref double __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused) &&
                __instance == FlightGlobals.ActiveVessel)
            {
                __result = FlightRecorder.Instance.GetActiveObtSpeed();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(FlightGlobals), "ship_srfSpeed", MethodType.Getter)]
    public static class Patch_FlightGlobals_ship_srfSpeed
    {
        public static bool Prefix(ref double __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused))
            {
                __result = FlightRecorder.Instance.GetActiveSrfSpeed();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(FlightGlobals), "ship_obtSpeed", MethodType.Getter)]
    public static class Patch_FlightGlobals_ship_obtSpeed
    {
        public static bool Prefix(ref double __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused))
            {
                __result = FlightRecorder.Instance.GetActiveObtSpeed();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(FlightGlobals), "ship_srfVelocity", MethodType.Getter)]
    public static class Patch_FlightGlobals_ship_srfVelocity
    {
        public static bool Prefix(ref Vector3d __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused))
            {
                __result = FlightRecorder.Instance.GetActiveSrfVelocity();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(FlightGlobals), "ship_obtVelocity", MethodType.Getter)]
    public static class Patch_FlightGlobals_ship_obtVelocity
    {
        public static bool Prefix(ref Vector3d __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused))
            {
                __result = FlightRecorder.Instance.GetActiveObtVelocity();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Vessel), "GetSrfVelocity")]
    public static class Patch_Vessel_GetSrfVelocity
    {
        public static bool Prefix(Vessel __instance, ref Vector3d __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused) &&
                __instance == FlightGlobals.ActiveVessel)
            {
                __result = FlightRecorder.Instance.GetActiveSrfVelocity();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Vessel), "GetObtVelocity")]
    public static class Patch_Vessel_GetObtVelocity
    {
        public static bool Prefix(Vessel __instance, ref Vector3d __result)
        {
            if (FlightRecorder.Instance != null &&
                (FlightRecorder.Instance.CurrentState == RecorderState.Playback || FlightRecorder.Instance.CurrentState == RecorderState.Paused) &&
                __instance == FlightGlobals.ActiveVessel)
            {
                __result = FlightRecorder.Instance.GetActiveObtVelocity();
                return false;
            }
            return true;
        }
    }

    #endregion

    [Serializable]
    public struct WheelFrameData
    {
        public uint partPersistentId;
        public uint craftID;
        public int partIndex;
        public float suspensionY;
        public float wheelRotationX;
    }

    public class WheelTransformCache
    {
        public class CachedWheel
        {
            public uint partPersistentId;
            public uint craftID;
            public int partIndex;
            public Transform suspensionTransform;
            public Transform wheelTransform;
        }

        public Dictionary<uint, CachedWheel> cacheByPersistentId = new Dictionary<uint, CachedWheel>();
        public Dictionary<uint, CachedWheel> cacheByCraftId = new Dictionary<uint, CachedWheel>();
        public Dictionary<int, CachedWheel> cacheByIndex = new Dictionary<int, CachedWheel>();

        public void Rebuild(Vessel vessel)
        {
            cacheByPersistentId.Clear();
            cacheByCraftId.Clear();
            cacheByIndex.Clear();

            if (vessel == null || vessel.parts == null) return;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null) continue;

                Transform susp = p.FindModelTransform("suspension");
                Transform wheel = p.FindModelTransform("wheel");

                if (susp != null || wheel != null)
                {
                    CachedWheel cw = new CachedWheel
                    {
                        partPersistentId = p.persistentId,
                        craftID = p.craftID,
                        partIndex = i,
                        suspensionTransform = susp,
                        wheelTransform = wheel
                    };

                    cacheByPersistentId[p.persistentId] = cw;
                    if (p.craftID != 0) cacheByCraftId[p.craftID] = cw;
                    cacheByIndex[i] = cw;
                }
            }
        }

        public CachedWheel FindWheel(uint persistentId, uint craftID, int partIndex)
        {
            if (persistentId != 0 && cacheByPersistentId.TryGetValue(persistentId, out CachedWheel w1)) return w1;
            if (partIndex >= 0 && cacheByIndex.TryGetValue(partIndex, out CachedWheel w3)) return w3;
            if (craftID != 0 && cacheByCraftId.TryGetValue(craftID, out CachedWheel w2)) return w2;
            return null;
        }
    }

    [Serializable]
    public class FlightInputFrame
    {
        public double timeOffset;

        // kinematic transform data
        public double posX, posY, posZ;
        public double rotX, rotY, rotZ, rotW;

        // state vectors
        public double srfVelX, srfVelY, srfVelZ;
        public double obtVelX, obtVelY, obtVelZ;

        // analog control axes
        public float pitch;
        public float roll;
        public float yaw;
        public float mainThrottle;
        public float X;
        public float Y;
        public float Z;
        public float wheelSteer;
        public float wheelThrottle;

        // action groups & flight modes
        public bool sas;
        public bool rcs;
        public bool gear;
        public bool light;
        public bool brakes;
        public bool precisionMode;

        // custom action groups
        public bool[] customActionGroups = new bool[10];

        // telemetry metrics
        public float altitude;
        public float speed;
        public float obtSpeed;
        public float gForce;

        public WheelFrameData[] wheelData;
    }

    [Serializable]
    public class FlightEventFrame
    {
        public double timeOffset;
        public uint partPersistentId;
        public uint craftID;
        public int partIndex = -1;
        public string moduleName;
        public string eventName;
        public string eventType = "EVENT"; // "EVENT", "ACTION", "STAGING", "FIELD"
        public string fieldName;
        public string fieldValue;
        public bool executed;
    }

    [Serializable]
    public class RecordedSessionData
    {
        public string craftName = "Unknown Craft";
        public double startUT;
        public double duration;
        public string bodyName = "Kerbin";

        public double startPosX, startPosY, startPosZ;
        public double startRotX, startRotY, startRotZ, startRotW;

        public List<FlightInputFrame> frames = new List<FlightInputFrame>();
        public List<FlightEventFrame> events = new List<FlightEventFrame>();
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightRecorder : MonoBehaviour
    {
        public static FlightRecorder Instance { get; private set; }

        private const string MOD_VERSION = "v1.0.0";
        private const string CAM_LOCK_ID = "FlightRecorder_CamLock";
        private readonly int windowID = 948201;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // state
        public RecorderState CurrentState { get; private set; } = RecorderState.Idle;
        public RecordedSessionData CurrentSession { get; private set; }
        private Vessel recordedVessel = null;

        // settings & GUI state
        private bool showGUI = false;
        private bool showFileList = false;
        private bool lastShowFileList = false;
        private bool enableF7Hotkey = true;
        private bool revertInGameTimeOnPlayback = true;
        private bool isCamLocked = false;

        // file management
        private List<string> savedFilePaths = new List<string>();
        private Vector2 fileListScrollPos = Vector2.zero;
        private string confirmDeleteFilePath = null;
        private float confirmDeleteExpireTime = 0f;
        private string renamingFilePath = null;
        private string renameTextBuffer = "";

        private WheelTransformCache wheelCache = new WheelTransformCache();

        private double playbackProgressUT = 0;
        private int nextEventIndex = 0;
        private double lastPlaybackProgressUT = 0;
        private int currentPlaybackIndex = 0;
        private bool[] lastRecordedAGState = new bool[15];
        private bool agStateInitialized = false;

        private OrbitDriver.UpdateMode savedOrbitDriverMode = OrbitDriver.UpdateMode.UPDATE;
        private bool savedLandedState = false;
        private bool savedSplashedState = false;
        private Vessel.Situations savedSituation = Vessel.Situations.FLYING;

        private Vector3d currentSrfVelocity = Vector3d.zero;
        private Vector3d lastFrameWorldPos = Vector3d.zero;
        private bool lastFrameWorldPosValid = false;
        private Vector3d lastFrameWorldVelocity = Vector3d.zero;
        private Vector3d currentObtVelocity = Vector3d.zero;
        private float currentDisplaySpeed = 0f;
        private float currentDisplayObtSpeed = 0f;
        private float currentDisplayGForce = 1f;

        // navball ui direct bindings
        private SpeedDisplay cachedSpeedDisplay = null;
        private NavBall cachedNavBall = null;
        private VesselAutopilotUI cachedAutopilotUI = null;

        private readonly Dictionary<(uint partId, int moduleIndex, string fieldName), string> lastFieldCache = new Dictionary<(uint, int, string), string>();
        private static readonly Dictionary<Type, PropertyInfo[]> cachedTypeProperties = new Dictionary<Type, PropertyInfo[]>();
        private double lastFieldScanUT = 0;

        private static readonly HashSet<string> internalStateFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deployState", "panelState", "StateName", "isDecoupled", "staged", "EngineIgnited",
            "currentPosition", "animTime", "animSwitch", "deploymentState", "stateString", "isDeployed",
            "IsActivated", "isActivated", "status", "lastUpdateTime", "currentThrottle", "requestedThrottle",
            "flameout", "finalThrust", "fuelFlowGui", "engineSpool", "propellantReqMet", "normalizedOutput", "thrust"
        };

        // toolbar button
        private ApplicationLauncherButton toolbarButton;
        private Texture2D toolbarIconTexture;

        private readonly HashSet<Button> hookedPAWButtons = new HashSet<Button>();

        private readonly Dictionary<(uint partId, int moduleIndex, string eventName), bool> genericEventActiveState = new Dictionary<(uint, int, string), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex, string propName), string> genericModPropertyCache = new Dictionary<(uint, int, string), string>();

        private readonly Dictionary<(uint partId, int moduleIndex), bool> engineIgnitedCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), ModuleDeployablePart.DeployState> deployStateCache = new Dictionary<(uint, int), ModuleDeployablePart.DeployState>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> lightStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), ModuleParachute.deploymentStates> chuteStateCache = new Dictionary<(uint, int), ModuleParachute.deploymentStates>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> decoupleStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), string> dockStateCache = new Dictionary<(uint, int), string>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> scienceStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> converterStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), string> ladderStateCache = new Dictionary<(uint, int), string>();
        private readonly Dictionary<(uint partId, int moduleIndex), string> wheelDepStateCache = new Dictionary<(uint, int), string>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> colorChangerStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> animGroupStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> harvesterStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> radiatorStateCache = new Dictionary<(uint, int), bool>();
        private readonly Dictionary<(uint partId, int moduleIndex), string> grappleStateCache = new Dictionary<(uint, int), string>();
        private readonly Dictionary<(uint partId, int moduleIndex), bool> genericAnimSwitchCache = new Dictionary<(uint, int), bool>();

        // GUI & graph
        private Rect windowRect = new Rect(100, 100, 480, 0);
        private Texture2D graphTexture;
        private Texture2D whiteTexture;
        private GUIStyle headerStyle;
        private GUIStyle labelStyle;
        private GUIStyle saveFileStyle;
        private bool stylesInitialized = false;

        private readonly int graphTexWidth = 460;
        private readonly int graphTexHeight = 85;
        private Harmony harmonyInstance;

        #region double-precision math and nan preventions

        private static QuaternionD NormalizeQuaternionD(QuaternionD q)
        {
            double magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (magSq < 1e-12) return QuaternionD.identity;
            double invMag = 1.0 / Math.Sqrt(magSq);
            return new QuaternionD(q.x * invMag, q.y * invMag, q.z * invMag, q.w * invMag);
        }

        private static QuaternionD SafeInverse(QuaternionD q)
        {
            double magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (magSq < 1e-12) return QuaternionD.identity;
            return new QuaternionD(-q.x / magSq, -q.y / magSq, -q.z / magSq, q.w / magSq);
        }

        private static QuaternionD SafeSlerp(QuaternionD q1, QuaternionD q2, double t)
        {
            double dot = q1.x * q2.x + q1.y * q2.y + q1.z * q2.z + q1.w * q2.w;
            QuaternionD q2Mod = q2;

            if (dot < 0.0)
            {
                dot = -dot;
                q2Mod = new QuaternionD(-q2.x, -q2.y, -q2.z, -q2.w);
            }

            if (dot > 0.9995)
            {
                double rx = q1.x + t * (q2Mod.x - q1.x);
                double ry = q1.y + t * (q2Mod.y - q1.y);
                double rz = q1.z + t * (q2Mod.z - q1.z);
                double rw = q1.w + t * (q2Mod.w - q1.w);
                return NormalizeQuaternionD(new QuaternionD(rx, ry, rz, rw));
            }

            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            double theta0 = Math.Acos(dot);
            double theta = theta0 * t;
            double sinTheta = Math.Sin(theta);
            double sinTheta0 = Math.Sin(theta0);

            if (Math.Abs(sinTheta0) < 1e-12)
            {
                return NormalizeQuaternionD(q1);
            }

            double s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
            double s1 = sinTheta / sinTheta0;

            double x = s0 * q1.x + s1 * q2Mod.x;
            double y = s0 * q1.y + s1 * q2Mod.y;
            double z = s0 * q1.z + s1 * q2Mod.z;
            double w = s0 * q1.w + s1 * q2Mod.w;

            return NormalizeQuaternionD(new QuaternionD(x, y, z, w));
        }

        private static Vector3d SafeVectorSlerp(Vector3d v1, Vector3d v2, double alpha)
        {
            double mag1 = v1.magnitude;
            double mag2 = v2.magnitude;
            if (mag1 < 1e-6 || mag2 < 1e-6)
            {
                return Vector3d.Lerp(v1, v2, alpha);
            }

            Vector3d dir1 = v1 / mag1;
            Vector3d dir2 = v2 / mag2;

            double dot = Vector3d.Dot(dir1, dir2);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));

            double targetMag = (1.0 - alpha) * mag1 + alpha * mag2;

            if (dot > 0.99999)
            {
                Vector3d lerpedDir = Vector3d.Lerp(dir1, dir2, alpha).normalized;
                return lerpedDir * targetMag;
            }

            if (dot < -0.99999)
            {
                return Vector3d.Lerp(v1, v2, alpha);
            }

            double theta = Math.Acos(dot) * alpha;
            Vector3d relativeVec = (dir2 - dir1 * dot).normalized;
            Vector3d slerpedDir = dir1 * Math.Cos(theta) + relativeVec * Math.Sin(theta);

            return slerpedDir.normalized * targetMag;
        }

        private static QuaternionD ToQuaternionD(Quaternion q)
        {
            return new QuaternionD(q.x, q.y, q.z, q.w);
        }

        private static Quaternion ToQuaternion(QuaternionD q)
        {
            return new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w);
        }

        public static string FormatNavballSpeed(double spd)
        {
            return spd.ToString("F1", Inv) + "m/s";
        }

        #endregion

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            whiteTexture = new Texture2D(1, 1);
            whiteTexture.SetPixel(0, 0, Color.white);
            whiteTexture.Apply();

            toolbarIconTexture = CreateDefaultToolbarIcon();

            graphTexture = new Texture2D(graphTexWidth, graphTexHeight, TextureFormat.RGBA32, false);
            ClearGraphTexture();

            RefreshSavedFilesList();

            try
            {
                harmonyInstance = new Harmony("com.flightrecorder.mod");
                harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlightRecorder] Harmony initialization notice: {ex.Message}");
            }
        }

        private void Start()
        {
            if (toolbarButton == null && ApplicationLauncher.Ready && ApplicationLauncher.Instance != null)
            {
                OnGUIApplicationLauncherReady();
            }
        }

        private string GetPluginDataFolder()
        {
            try
            {
                string dataFolder = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "FlightRecorder", "PluginData");
                if (!Directory.Exists(dataFolder))
                {
                    Directory.CreateDirectory(dataFolder);
                }
                return dataFolder;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlightRecorder] Failed resolving PluginData folder: {ex.Message}");
                string fallback = Path.Combine(Application.dataPath, "PluginData");
                if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        private void OnEnable()
        {
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onGUIApplicationLauncherReady.Add(OnGUIApplicationLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(OnGUIApplicationLauncherDestroyed);
            GameEvents.onStageActivate.Add(OnStageActivate);
            GameEvents.onPartDie.Add(OnPartDie);
            GameEvents.onPartDeCouple.Add(OnPartDeCouple);
            GameEvents.onPartUndock.Add(OnPartUndock);

            if (ApplicationLauncher.Ready && ApplicationLauncher.Instance != null && toolbarButton == null)
            {
                OnGUIApplicationLauncherReady();
            }
        }

        private void OnDisable()
        {
            RestoreNavBallElements();

            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onGUIApplicationLauncherReady.Remove(OnGUIApplicationLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(OnGUIApplicationLauncherDestroyed);
            GameEvents.onStageActivate.Remove(OnStageActivate);
            GameEvents.onPartDie.Remove(OnPartDie);
            GameEvents.onPartDeCouple.Remove(OnPartDeCouple);
            GameEvents.onPartUndock.Remove(OnPartUndock);

            OnGUIApplicationLauncherDestroyed();
            DetachFlyByWire();
            ReleaseCameraLock();
        }

        private void RestoreNavBallElements()
        {
            if (cachedAutopilotUI != null)
            {
                cachedAutopilotUI.gameObject.SetActive(true);
                cachedAutopilotUI = null;
            }

            if (cachedNavBall != null)
            {
                if (cachedNavBall.progradeVector != null) cachedNavBall.progradeVector.gameObject.SetActive(true);
                if (cachedNavBall.retrogradeVector != null) cachedNavBall.retrogradeVector.gameObject.SetActive(true);
                if (cachedNavBall.normalVector != null) cachedNavBall.normalVector.gameObject.SetActive(true);
                if (cachedNavBall.antiNormalVector != null) cachedNavBall.antiNormalVector.gameObject.SetActive(true);
                if (cachedNavBall.radialInVector != null) cachedNavBall.radialInVector.gameObject.SetActive(true);
                if (cachedNavBall.radialOutVector != null) cachedNavBall.radialOutVector.gameObject.SetActive(true);
                cachedNavBall = null;
            }
        }

        private void OnPartDie(Part p)
        {
            if (CurrentState == RecorderState.Recording)
            {
                string partName = (p != null && p.partInfo != null) ? Localizer.Format(p.partInfo.title) : "Part";

                ScreenMessages.PostScreenMessage(
                    $"<color=#FF5555><b>[FlightRecorder]</b> Structural damage detected on <b>{partName}</b>.</color>",
                    4.0f,
                    ScreenMessageStyle.UPPER_CENTER
                );
            }
        }

        private void OnPartDeCouple(Part p)
        {
            if (CurrentState == RecorderState.Recording && CurrentSession != null && p != null)
            {
                RecordPartEvent(p, "ModuleDecouple", "Decouple", "EVENT");
            }
        }

        private void OnPartUndock(Part p)
        {
            if (CurrentState == RecorderState.Recording && CurrentSession != null && p != null)
            {
                RecordPartEvent(p, "ModuleDockingNode", "Undock", "EVENT");
            }
        }

        private void OnDestroy()
        {
            ReleaseCameraLock();
            RestoreNavBallElements();
            harmonyInstance?.UnpatchAll(harmonyInstance.Id);

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnVesselChange(Vessel v)
        {
            if (CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused)
            {
                StopRecordingOrPlayback();
            }
            else if (CurrentState == RecorderState.Recording)
            {
                if (FlightGlobals.ActiveVessel != null && (recordedVessel == null || FlightGlobals.ActiveVessel == recordedVessel))
                {
                    wheelCache.Rebuild(FlightGlobals.ActiveVessel);
                }
                else
                {
                    StopRecordingOrPlayback();
                }
            }
            ReleaseCameraLock();
        }

        private void Update()
        {
            if (toolbarButton == null && ApplicationLauncher.Ready && ApplicationLauncher.Instance != null)
            {
                OnGUIApplicationLauncherReady();
            }

            if (enableF7Hotkey && Input.GetKeyDown(KeyCode.F7))
            {
                ToggleGUI();
            }

            if (CurrentState == RecorderState.Recording)
            {
                ScanAndHookPAWindows();

                double currentUT = Planetarium.GetUniversalTime();
                if (currentUT - lastFieldScanUT > 0.1)
                {
                    lastFieldScanUT = currentUT;
                    ScanAndRecordFieldChanges(FlightGlobals.ActiveVessel);
                }
            }
            else if (CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused)
            {
                SyncVesselVelocities();
                UpdateNavballSpeedDisplay();

                Vessel activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null)
                {
                    FlightInputFrame curFrame = GetCurrentInterpolatedFrame();
                    if (curFrame != null)
                    {
                        activeVessel.ctrlState.mainThrottle = curFrame.mainThrottle;
                        activeVessel.ctrlState.pitch = curFrame.pitch;
                        activeVessel.ctrlState.yaw = curFrame.yaw;
                        activeVessel.ctrlState.roll = curFrame.roll;
                        if (FlightInputHandler.fetch != null)
                        {
                            FlightInputHandler.state.mainThrottle = curFrame.mainThrottle;
                        }

                        float dt = Time.deltaTime;
                        for (int i = 0; i < activeVessel.parts.Count; i++)
                        {
                            Part p = activeVessel.parts[i];
                            if (p == null || p.Modules == null) continue;
                            for (int m = 0; m < p.Modules.Count; m++)
                            {
                                if (p.Modules[m] is ModuleEngines engine && engine.EngineIgnited)
                                {
                                    float targetT = curFrame.mainThrottle;
                                    engine.requestedThrottle = targetT;
                                    if (engine.useEngineResponseTime)
                                    {
                                        float speed = (targetT > engine.currentThrottle) ? engine.engineAccelerationSpeed : engine.engineDecelerationSpeed;
                                        engine.currentThrottle = Mathf.MoveTowards(engine.currentThrottle, targetT, (speed > 0 ? speed : 1f) * dt);
                                    }
                                    else
                                    {
                                        engine.currentThrottle = targetT;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void LateUpdate()
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;

            if ((CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused) && activeVessel != null)
            {
                FlightInputFrame curFrame = GetCurrentInterpolatedFrame();
                if (curFrame != null && activeVessel.mainBody != null)
                {
                    CelestialBody body = activeVessel.mainBody;
                    QuaternionD bodyRotD = ToQuaternionD(body.bodyTransform.rotation);

                    Vector3d curBodyLocalPos = new Vector3d(curFrame.posX, curFrame.posY, curFrame.posZ);
                    QuaternionD curBodyLocalRotD = NormalizeQuaternionD(new QuaternionD(curFrame.rotX, curFrame.rotY, curFrame.rotZ, curFrame.rotW));

                    Vector3d renderWorldPos = body.position + (bodyRotD * curBodyLocalPos);
                    QuaternionD renderWorldRotD = bodyRotD * curBodyLocalRotD;

                    activeVessel.SetPosition(renderWorldPos, false);
                    activeVessel.SetRotation(ToQuaternion(renderWorldRotD), true);
                    activeVessel.ctrlState.mainThrottle = curFrame.mainThrottle;

                    float dt = Time.deltaTime;
                    for (int i = 0; i < activeVessel.parts.Count; i++)
                    {
                        Part p = activeVessel.parts[i];
                        if (p == null || p.Modules == null) continue;
                        for (int m = 0; m < p.Modules.Count; m++)
                        {
                            if (p.Modules[m] is ModuleEngines engine && engine.EngineIgnited)
                            {
                                float targetT = curFrame.mainThrottle;
                                engine.requestedThrottle = targetT;
                                if (engine.useEngineResponseTime)
                                {
                                    float speed = (targetT > engine.currentThrottle) ? engine.engineAccelerationSpeed : engine.engineDecelerationSpeed;
                                    engine.currentThrottle = Mathf.MoveTowards(engine.currentThrottle, targetT, (speed > 0 ? speed : 1f) * dt);
                                }
                                else
                                {
                                    engine.currentThrottle = targetT;
                                }
                            }
                        }
                    }
                }

                SyncVesselVelocities();
                UpdateNavballSpeedDisplay();

                // intentionally hide navball markers during playback
                if (cachedNavBall == null)
                {
                    cachedNavBall = UnityEngine.Object.FindObjectOfType<NavBall>();
                }
                if (cachedNavBall != null)
                {
                    if (cachedNavBall.progradeVector != null && cachedNavBall.progradeVector.gameObject.activeSelf)
                        cachedNavBall.progradeVector.gameObject.SetActive(false);
                    if (cachedNavBall.retrogradeVector != null && cachedNavBall.retrogradeVector.gameObject.activeSelf)
                        cachedNavBall.retrogradeVector.gameObject.SetActive(false);
                    if (cachedNavBall.normalVector != null && cachedNavBall.normalVector.gameObject.activeSelf)
                        cachedNavBall.normalVector.gameObject.SetActive(false);
                    if (cachedNavBall.antiNormalVector != null && cachedNavBall.antiNormalVector.gameObject.activeSelf)
                        cachedNavBall.antiNormalVector.gameObject.SetActive(false);
                    if (cachedNavBall.radialInVector != null && cachedNavBall.radialInVector.gameObject.activeSelf)
                        cachedNavBall.radialInVector.gameObject.SetActive(false);
                    if (cachedNavBall.radialOutVector != null && cachedNavBall.radialOutVector.gameObject.activeSelf)
                        cachedNavBall.radialOutVector.gameObject.SetActive(false);
                }

                // intentionally hide the sas panel at the left of the navball (to prevent the player from trying to select any of them during playback)
                if (cachedAutopilotUI == null)
                {
                    cachedAutopilotUI = UnityEngine.Object.FindObjectOfType<VesselAutopilotUI>();
                }
                if (cachedAutopilotUI != null && cachedAutopilotUI.gameObject.activeSelf)
                {
                    cachedAutopilotUI.gameObject.SetActive(false);
                }
            }
        }

        private void FixedUpdate()
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null) return;

            if (CurrentState == RecorderState.Recording)
            {
                RecordFrame(activeVessel);
                ScanAllPartModuleStateChanges(activeVessel);
            }
            else if (CurrentState == RecorderState.Playback)
            {
                double effectiveDt = TimeWarp.WarpMode == TimeWarp.Modes.HIGH ? TimeWarp.deltaTime : TimeWarp.fixedDeltaTime;

                lastPlaybackProgressUT = playbackProgressUT;
                playbackProgressUT += effectiveDt;

                if (revertInGameTimeOnPlayback && CurrentSession != null)
                {
                    double targetUT = CurrentSession.startUT + playbackProgressUT;
                    Planetarium.SetUniversalTime(targetUT);
                    if (activeVessel.mainBody != null)
                    {
                        activeVessel.mainBody.CBUpdate();
                    }
                }

                ApplyPlaybackFrame(activeVessel, playbackProgressUT, effectiveDt);
                ProcessDiscreteEventsForTime(lastPlaybackProgressUT, playbackProgressUT);

                double maxTime = (CurrentSession.frames != null && CurrentSession.frames.Count > 0)
                    ? CurrentSession.frames[CurrentSession.frames.Count - 1].timeOffset
                    : CurrentSession.duration;

                if (CurrentSession != null && playbackProgressUT >= maxTime - 0.0001)
                {
                    StopRecordingOrPlayback();
                }
            }
        }

        private void ApplyPlaybackFrame(Vessel activeVessel, double progressTime, double dt)
        {
            if (activeVessel == null || CurrentSession == null) return;

            FlightInputFrame curFrame = GetInterpolatedFrame(progressTime);
            if (curFrame == null) return;

            CelestialBody body = activeVessel.mainBody;
            if (body == null) return;

            Vector3d curBodyLocalPos = new Vector3d(curFrame.posX, curFrame.posY, curFrame.posZ);
            QuaternionD curBodyLocalRotD = NormalizeQuaternionD(new QuaternionD(curFrame.rotX, curFrame.rotY, curFrame.rotZ, curFrame.rotW));

            QuaternionD bodyRotD = ToQuaternionD(body.bodyTransform.rotation);

            Vector3d targetWorldPos = body.position + (bodyRotD * curBodyLocalPos);
            QuaternionD targetWorldRotD = bodyRotD * curBodyLocalRotD;

            activeVessel.SetPosition(targetWorldPos, false);
            activeVessel.SetRotation(ToQuaternion(targetWorldRotD), true);

            if (lastFrameWorldPosValid && dt > 0.0001)
            {
                lastFrameWorldVelocity = (targetWorldPos - lastFrameWorldPos) / dt;
            }
            lastFrameWorldPos = targetWorldPos;
            lastFrameWorldPosValid = true;

            if (curFrame.wheelData != null)
            {
                for (int w = 0; w < curFrame.wheelData.Length; w++)
                {
                    WheelFrameData wd = curFrame.wheelData[w];
                    WheelTransformCache.CachedWheel cached = wheelCache.FindWheel(wd.partPersistentId, wd.craftID, wd.partIndex);
                    if (cached != null)
                    {
                        if (cached.suspensionTransform != null)
                        {
                            Vector3 lp = cached.suspensionTransform.localPosition;
                            lp.y = wd.suspensionY;
                            cached.suspensionTransform.localPosition = lp;
                        }
                        if (cached.wheelTransform != null)
                        {
                            Vector3 rot = cached.wheelTransform.localEulerAngles;
                            rot.x = wd.wheelRotationX;
                            cached.wheelTransform.localEulerAngles = rot;
                        }
                    }
                }
            }

            currentSrfVelocity = new Vector3d(curFrame.srfVelX, curFrame.srfVelY, curFrame.srfVelZ);
            currentDisplaySpeed = curFrame.speed;
            currentDisplayGForce = curFrame.gForce;

            Vector3d bodyRotVel = body.getRFrmVel(targetWorldPos);
            if (curFrame.obtVelX != 0 || curFrame.obtVelY != 0 || curFrame.obtVelZ != 0)
            {
                currentObtVelocity = new Vector3d(curFrame.obtVelX, curFrame.obtVelY, curFrame.obtVelZ);
            }
            else
            {
                currentObtVelocity = currentSrfVelocity + bodyRotVel;
            }

            currentDisplayObtSpeed = curFrame.obtSpeed > 0.001f ? curFrame.obtSpeed : (float)currentObtVelocity.magnitude;

            Vector3d up = (targetWorldPos - body.position).normalized;
            activeVessel.srf_velocity = currentSrfVelocity;
            activeVessel.obt_velocity = currentObtVelocity;
            activeVessel.srf_vel_direction = currentSrfVelocity.sqrMagnitude > 0.01 ? (Vector3)currentSrfVelocity.normalized : activeVessel.vesselTransform.up;
            activeVessel.srfSpeed = (double)currentDisplaySpeed;
            activeVessel.verticalSpeed = Vector3d.Dot(currentSrfVelocity, up);
            activeVessel.horizontalSrfSpeed = (currentSrfVelocity - Vector3d.Project(currentSrfVelocity, up)).magnitude;
            activeVessel.geeForce = curFrame.gForce;
            activeVessel.geeForce_immediate = curFrame.gForce;
        }

        public void SyncVesselVelocities()
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null || activeVessel.mainBody == null) return;

            Vector3d up = (activeVessel.vesselTransform.position - activeVessel.mainBody.position).normalized;

            activeVessel.srf_velocity = currentSrfVelocity;
            activeVessel.obt_velocity = currentObtVelocity;
            activeVessel.srf_vel_direction = currentSrfVelocity.sqrMagnitude > 0.01 ? (Vector3)currentSrfVelocity.normalized : activeVessel.vesselTransform.up;
            activeVessel.srfSpeed = (double)currentDisplaySpeed;
            activeVessel.verticalSpeed = Vector3d.Dot(currentSrfVelocity, up);
            activeVessel.horizontalSrfSpeed = (currentSrfVelocity - Vector3d.Project(currentSrfVelocity, up)).magnitude;
        }

        private void UpdateNavballSpeedDisplay()
        {
            if (cachedSpeedDisplay == null)
            {
                cachedSpeedDisplay = UnityEngine.Object.FindObjectOfType<SpeedDisplay>();
            }

            if (cachedSpeedDisplay != null)
            {
                double activeSpeed = GetActiveNavballSpeed();
                string formattedSpeed = FormatNavballSpeed(activeSpeed);

                if (cachedSpeedDisplay.textSpeed != null)
                {
                    cachedSpeedDisplay.textSpeed.text = formattedSpeed;
                }
            }
        }

        public double GetActiveNavballSpeed()
        {
            if (FlightGlobals.speedDisplayMode == FlightGlobals.SpeedDisplayModes.Orbit)
            {
                return (double)currentDisplayObtSpeed;
            }
            else if (FlightGlobals.speedDisplayMode == FlightGlobals.SpeedDisplayModes.Target)
            {
                return FlightGlobals.ship_tgtSpeed;
            }
            else
            {
                return (double)currentDisplaySpeed;
            }
        }

        public double GetActiveSrfSpeed() => (double)currentDisplaySpeed;
        public double GetActiveObtSpeed() => (double)currentDisplayObtSpeed;
        public Vector3d GetActiveSrfVelocity() => currentSrfVelocity;
        public Vector3d GetActiveObtVelocity() => currentObtVelocity;

        private void HandOffToRealPhysics(Vessel v)
        {
            if (v == null || v.parts == null) return;

            CelestialBody body = v.mainBody;
            double alt = v.altitude;
            bool hasAtmosphere = body != null && body.atmosphere;
            double atmLimit = hasAtmosphere ? body.atmosphereDepth : 1000.0;
            bool isInSpace = alt > atmLimit;

            if (isInSpace)
            {
                v.Landed = false;
                v.Splashed = false;
                if (v.orbit != null && v.orbit.eccentricity >= 1.0)
                {
                    v.situation = Vessel.Situations.ESCAPING;
                }
                else if (v.orbit != null && v.orbit.PeA > (hasAtmosphere ? body.atmosphereDepth : 0.0))
                {
                    v.situation = Vessel.Situations.ORBITING;
                }
                else
                {
                    v.situation = Vessel.Situations.SUB_ORBITAL;
                }
            }
            else if (hasAtmosphere && alt > 50.0 && currentDisplaySpeed > 5.0)
            {
                v.situation = Vessel.Situations.FLYING;
                v.Landed = false;
                v.Splashed = false;
            }
            else
            {
                v.Landed = !v.Splashed;
                v.situation = v.Splashed ? Vessel.Situations.SPLASHED : Vessel.Situations.LANDED;
            }

            Vector3 targetPhysicsVel;

            if (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)
            {
                targetPhysicsVel = Vector3.zero;
            }
            else if (isInSpace)
            {
                Vector3 frameVel = Krakensbane.GetFrameVelocityV3f();
                targetPhysicsVel = (Vector3)currentObtVelocity - frameVel;
            }
            else
            {
                targetPhysicsVel = lastFrameWorldPosValid ? (Vector3)lastFrameWorldVelocity : (Vector3)currentSrfVelocity;
            }

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.rb == null) continue;

                p.rb.isKinematic = false;
                p.rb.detectCollisions = true;
                p.rb.velocity = targetPhysicsVel;
                p.rb.angularVelocity = Vector3.zero;
            }
        }

        private void SetVesselKinematic(Vessel v, bool enableKinematic)
        {
            if (v == null || v.parts == null) return;

            if (enableKinematic)
            {
                v.Landed = false;
                v.Splashed = false;
                v.situation = Vessel.Situations.FLYING;
            }

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.rb == null) continue;

                p.rb.isKinematic = enableKinematic;
                p.rb.detectCollisions = !enableKinematic;

                if (enableKinematic)
                {
                    p.rb.velocity = Vector3.zero;
                    p.rb.angularVelocity = Vector3.zero;
                }
            }
        }

        #region Universal Part State Engine & Multi-Tier Resolution

        public Part FindPartInVessel(Vessel vessel, uint persistentId, uint craftID, int partIndex)
        {
            if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return null;

            if (persistentId != 0)
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part p = vessel.parts[i];
                    if (p != null && p.persistentId == persistentId) return p;
                }
            }

            if (partIndex >= 0 && partIndex < vessel.parts.Count)
            {
                Part p = vessel.parts[partIndex];
                if (p != null) return p;
            }

            if (craftID != 0)
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part p = vessel.parts[i];
                    if (p != null && p.craftID == craftID) return p;
                }
            }

            return null;
        }

        public void OnBaseEventInvoked(BaseEvent kspEvent)
        {
            if (kspEvent == null || CurrentState != RecorderState.Recording || CurrentSession == null) return;

            BaseEventList eventList = kspEvent.listParent;
            if (eventList == null) return;

            PartModule mod = eventList.module;
            Part part = eventList.part ?? mod?.part;
            if (part == null) return;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || part.vessel != v) return;

            string moduleName = mod != null ? mod.ClassName : "Part";
            RecordPartEvent(part, moduleName, kspEvent.name, "EVENT");
        }

        public void OnBaseActionInvoked(BaseAction kspAction, KSPActionParam param)
        {
            if (kspAction == null || CurrentState != RecorderState.Recording || CurrentSession == null) return;

            BaseActionList actionList = kspAction.listParent;
            if (actionList == null) return;

            PartModule mod = actionList.module;
            Part part = actionList.part ?? mod?.part;
            if (part == null) return;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || part.vessel != v) return;

            string moduleName = mod != null ? mod.ClassName : "Part";
            RecordPartEvent(part, moduleName, kspAction.name, "ACTION");
        }

        private void ScanAndHookPAWindows()
        {
            if (UIPartActionController.Instance == null || UIPartActionController.Instance.windows == null) return;

            List<UIPartActionWindow> windows = UIPartActionController.Instance.windows;
            for (int w = 0; w < windows.Count; w++)
            {
                UIPartActionWindow window = windows[w];
                if (window == null || window.part == null) continue;

                Button[] buttons = window.GetComponentsInChildren<Button>(true);
                if (buttons == null) continue;

                for (int b = 0; b < buttons.Length; b++)
                {
                    Button btn = buttons[b];
                    if (btn == null || hookedPAWButtons.Contains(btn)) continue;

                    hookedPAWButtons.Add(btn);
                    Part targetPart = window.part;
                    btn.onClick.AddListener(() => OnPAWButtonClicked(targetPart, btn));
                }
            }
        }

        private void ScanAndRecordFieldChanges(Vessel v)
        {
            if (v == null || v.parts == null || CurrentSession == null) return;
            double currentUT = Planetarium.GetUniversalTime();

            for (int p = 0; p < v.parts.Count; p++)
            {
                Part part = v.parts[p];
                if (part == null || part.Modules == null) continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    PartModule mod = part.Modules[m];
                    if (mod == null || mod.Fields == null) continue;

                    if (mod is ModuleResourceConverter || mod is ModuleResourceHarvester || mod is ModuleCoreHeat)
                    {
                        continue;
                    }

                    for (int f = 0; f < mod.Fields.Count; f++)
                    {
                        BaseField field = mod.Fields[f];
                        if (field == null || internalStateFields.Contains(field.name)) continue;

                        object val = field.GetValue(mod);
                        if (val == null) continue;

                        string valStr = val.ToString();
                        var key = (part.persistentId, m, field.name);

                        if (lastFieldCache.TryGetValue(key, out string oldVal))
                        {
                            if (oldVal != valStr)
                            {
                                bool changed = true;
                                if (val is float fVal && float.TryParse(oldVal, NumberStyles.Float, Inv, out float fOldVal))
                                {
                                    if (Mathf.Abs(fVal - fOldVal) < 0.001f) changed = false;
                                }

                                if (changed)
                                {
                                    lastFieldCache[key] = valStr;
                                    CurrentSession.events.Add(new FlightEventFrame
                                    {
                                        timeOffset = currentUT - CurrentSession.startUT,
                                        partPersistentId = part.persistentId,
                                        craftID = part.craftID,
                                        partIndex = p,
                                        moduleName = mod.ClassName,
                                        eventName = field.name,
                                        eventType = "FIELD",
                                        fieldName = field.name,
                                        fieldValue = valStr,
                                        executed = false
                                    });
                                }
                            }
                        }
                        else
                        {
                            lastFieldCache[key] = valStr;
                        }
                    }
                }
            }
        }

        private void ScanAllPartModuleStateChanges(Vessel v)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part part = v.parts[i];
                if (part == null || part.Modules == null) continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    PartModule mod = part.Modules[m];
                    if (mod == null) continue;
                    var key = (part.persistentId, m);

                    if (mod is RetractableLadder ladderMod)
                    {
                        string lState = ladderMod.StateName;
                        if (ladderStateCache.TryGetValue(key, out string prevLState) && prevLState != lState && !string.IsNullOrEmpty(lState))
                        {
                            if (lState.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0 || lState.IndexOf("Moving", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Extend", "EVENT");
                            }
                            else if (lState.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Retract", "EVENT");
                            }
                        }
                        ladderStateCache[key] = lState;
                    }
                    else if (mod is ModuleAnimateGeneric animMod)
                    {
                        bool isSwitch = animMod.animSwitch;
                        if (genericAnimSwitchCache.TryGetValue(key, out bool prevSwitch) && prevSwitch != isSwitch)
                        {
                            RecordPartEvent(part, mod.ClassName, isSwitch ? "Close" : "Open", "EVENT");
                        }
                        genericAnimSwitchCache[key] = isSwitch;
                    }
                    else if (mod is ModuleGrappleNode grappleMod)
                    {
                        string gState = grappleMod.state;
                        if (grappleStateCache.TryGetValue(key, out string prevGState) && prevGState != gState && !string.IsNullOrEmpty(gState))
                        {
                            if (gState.IndexOf("Ready", StringComparison.OrdinalIgnoreCase) >= 0 || gState.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Arm", "EVENT");
                            }
                            else if (gState.IndexOf("Disabled", StringComparison.OrdinalIgnoreCase) >= 0 || gState.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Disarm", "EVENT");
                            }
                            else if (gState.IndexOf("Release", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Release", "EVENT");
                            }
                        }
                        grappleStateCache[key] = gState;
                    }
                    else if (mod is ModuleWheelDeployment wheelDep)
                    {
                        string state = wheelDep.stateString;
                        if (wheelDepStateCache.TryGetValue(key, out string prevState) && prevState != state && !string.IsNullOrEmpty(state))
                        {
                            if (state.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Deploy", "EVENT");
                            }
                            else if (state.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                RecordPartEvent(part, mod.ClassName, "Retract", "EVENT");
                            }
                        }
                        wheelDepStateCache[key] = state;
                    }
                    else if (mod is ModuleDeployablePart deployMod)
                    {
                        ModuleDeployablePart.DeployState state = deployMod.deployState;
                        if (deployStateCache.TryGetValue(key, out ModuleDeployablePart.DeployState prevState) && prevState != state)
                        {
                            if (state == ModuleDeployablePart.DeployState.EXTENDING || (state == ModuleDeployablePart.DeployState.EXTENDED && prevState == ModuleDeployablePart.DeployState.RETRACTED))
                            {
                                RecordPartEvent(part, mod.ClassName, "Extend", "EVENT");
                            }
                            else if (state == ModuleDeployablePart.DeployState.RETRACTING || (state == ModuleDeployablePart.DeployState.RETRACTED && prevState == ModuleDeployablePart.DeployState.EXTENDED))
                            {
                                RecordPartEvent(part, mod.ClassName, "Retract", "EVENT");
                            }
                        }
                        deployStateCache[key] = state;
                    }
                    else if (mod is ModuleAnimationGroup animGroup)
                    {
                        bool isDep = animGroup.isDeployed;
                        if (animGroupStateCache.TryGetValue(key, out bool prevDep) && prevDep != isDep)
                        {
                            RecordPartEvent(part, mod.ClassName, isDep ? "Deploy" : "Retract", "EVENT");
                        }
                        animGroupStateCache[key] = isDep;
                    }
                    else if (mod is ModuleEngines engineMod)
                    {
                        bool ignited = engineMod.EngineIgnited;
                        if (engineIgnitedCache.TryGetValue(key, out bool prevIgnited) && prevIgnited != ignited)
                        {
                            RecordPartEvent(part, mod.ClassName, ignited ? "Activate" : "Shutdown", "EVENT");
                        }
                        engineIgnitedCache[key] = ignited;
                    }
                    else if (mod is ModuleLight lightMod)
                    {
                        bool isOn = lightMod.isOn;
                        if (lightStateCache.TryGetValue(key, out bool prevLight) && prevLight != isOn)
                        {
                            RecordPartEvent(part, mod.ClassName, isOn ? "LightsOn" : "LightsOff", "EVENT");
                        }
                        lightStateCache[key] = isOn;
                    }
                    else if (mod is ModuleParachute chuteMod)
                    {
                        ModuleParachute.deploymentStates cState = chuteMod.deploymentState;
                        if (chuteStateCache.TryGetValue(key, out ModuleParachute.deploymentStates prevChute) && prevChute != cState)
                        {
                            if (cState == ModuleParachute.deploymentStates.CUT)
                            {
                                RecordPartEvent(part, mod.ClassName, "CutParachute", "EVENT");
                            }
                            else if (cState != ModuleParachute.deploymentStates.STOWED)
                            {
                                RecordPartEvent(part, mod.ClassName, "Deploy", "EVENT");
                            }
                        }
                        chuteStateCache[key] = cState;
                    }
                    else if (mod is ModuleDecouple decoupleMod)
                    {
                        bool isDecoupled = decoupleMod.isDecoupled;
                        if (decoupleStateCache.TryGetValue(key, out bool prevDecoupled) && prevDecoupled != isDecoupled && isDecoupled)
                        {
                            RecordPartEvent(part, mod.ClassName, "Decouple", "EVENT");
                        }
                        decoupleStateCache[key] = isDecoupled;
                    }
                    else if (mod is ModuleAnchoredDecoupler anchoredMod)
                    {
                        bool isDecoupled = anchoredMod.isDecoupled;
                        if (decoupleStateCache.TryGetValue(key, out bool prevDecoupled) && prevDecoupled != isDecoupled && isDecoupled)
                        {
                            RecordPartEvent(part, mod.ClassName, "Decouple", "EVENT");
                        }
                        decoupleStateCache[key] = isDecoupled;
                    }
                    else if (mod is ModuleDockingNode dockMod)
                    {
                        string dState = dockMod.state;
                        if (dockStateCache.TryGetValue(key, out string prevDockState) && prevDockState != dState && (dState == "Disengage" || dState == "Undocked"))
                        {
                            RecordPartEvent(part, mod.ClassName, "Undock", "EVENT");
                        }
                        dockStateCache[key] = dState;
                    }
                    else if (mod is ModuleScienceExperiment sciMod)
                    {
                        bool deployed = sciMod.Deployed;
                        if (scienceStateCache.TryGetValue(key, out bool prevSci) && prevSci != deployed && deployed)
                        {
                            RecordPartEvent(part, mod.ClassName, "DeployExperiment", "EVENT");
                        }
                        scienceStateCache[key] = deployed;
                    }
                    else if (mod is ModuleResourceHarvester harvesterMod)
                    {
                        bool active = harvesterMod.IsActivated;
                        if (harvesterStateCache.TryGetValue(key, out bool prevActive) && prevActive != active)
                        {
                            RecordPartEvent(part, mod.ClassName, active ? "StartResourceConverter" : "StopResourceConverter", "EVENT");
                        }
                        harvesterStateCache[key] = active;
                    }
                    else if (mod is ModuleResourceConverter converterMod)
                    {
                        bool active = converterMod.IsActivated;
                        if (converterStateCache.TryGetValue(key, out bool prevActive) && prevActive != active)
                        {
                            string actionName = (active ? "Start:" : "Stop:") + m;
                            RecordPartEvent(part, mod.ClassName, actionName, "EVENT");
                        }
                        converterStateCache[key] = active;
                    }
                    else if (mod is ModuleActiveRadiator radMod)
                    {
                        bool cooling = radMod.IsCooling;
                        if (radiatorStateCache.TryGetValue(key, out bool prevCooling) && prevCooling != cooling)
                        {
                            RecordPartEvent(part, mod.ClassName, cooling ? "Activate" : "Shutdown", "EVENT");
                        }
                        radiatorStateCache[key] = cooling;
                    }

                    if (mod.Events != null)
                    {
                        for (int e = 0; e < mod.Events.Count; e++)
                        {
                            BaseEvent baseEvt = mod.Events[e];
                            if (baseEvt == null || string.IsNullOrEmpty(baseEvt.name)) continue;

                            var eventKey = (part.persistentId, m, baseEvt.name);
                            bool active = baseEvt.active;
                            if (genericEventActiveState.TryGetValue(eventKey, out bool prevActive) && prevActive && !active)
                            {
                                RecordPartEvent(part, mod.ClassName, baseEvt.name, "EVENT");
                            }
                            genericEventActiveState[eventKey] = active;
                        }
                    }

                    Type t = mod.GetType();
                    if (!cachedTypeProperties.TryGetValue(t, out PropertyInfo[] props))
                    {
                        props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                        cachedTypeProperties[t] = props;
                    }

                    if (props != null)
                    {
                        for (int p = 0; p < props.Length; p++)
                        {
                            PropertyInfo prop = props[p];
                            if (prop == null || !prop.CanRead) continue;

                            Type pType = prop.PropertyType;
                            if (pType == typeof(bool) || pType.IsEnum)
                            {
                                try
                                {
                                    object val = prop.GetValue(mod, null);
                                    if (val != null)
                                    {
                                        string valStr = val.ToString();
                                        var propKey = (part.persistentId, m, prop.Name);
                                        if (genericModPropertyCache.TryGetValue(propKey, out string prevVal) && prevVal != valStr)
                                        {
                                            RecordPartEvent(part, mod.ClassName, prop.Name, "FIELD", prop.Name, valStr);
                                        }
                                        genericModPropertyCache[propKey] = valStr;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }

        private void CaptureBaselineStateSnapshot(Vessel v)
        {
            if (v == null || v.parts == null || CurrentSession == null) return;

            for (int p = 0; p < v.parts.Count; p++)
            {
                Part part = v.parts[p];
                if (part == null || part.Modules == null) continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    PartModule mod = part.Modules[m];
                    if (mod == null || mod.Fields == null) continue;

                    if (mod is ModuleResourceConverter || mod is ModuleResourceHarvester || mod is ModuleCoreHeat)
                    {
                        continue;
                    }

                    if (mod is RetractableLadder ladder)
                    {
                        RecordPartEvent(part, mod.ClassName, ladder.StateName.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0 ? "Extend" : "Retract", "EVENT");
                    }
                    else if (mod is ModuleDeployablePart deployable)
                    {
                        RecordPartEvent(part, mod.ClassName, deployable.deployState == ModuleDeployablePart.DeployState.EXTENDED ? "Extend" : "Retract", "EVENT");
                    }
                    else if (mod is ModuleWheelDeployment wheelDep)
                    {
                        bool isExt = wheelDep.stateString.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0;
                        RecordPartEvent(part, mod.ClassName, isExt ? "Deploy" : "Retract", "EVENT");
                    }
                    else if (mod is ModuleGrappleNode grappleMod)
                    {
                        if (grappleMod.state.IndexOf("Ready", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            RecordPartEvent(part, mod.ClassName, "Arm", "EVENT");
                        }
                    }

                    for (int f = 0; f < mod.Fields.Count; f++)
                    {
                        BaseField field = mod.Fields[f];
                        if (field == null || internalStateFields.Contains(field.name)) continue;

                        object val = field.GetValue(mod);
                        if (val == null) continue;

                        string valStr = val.ToString();
                        var key = (part.persistentId, m, field.name);
                        lastFieldCache[key] = valStr;

                        CurrentSession.events.Add(new FlightEventFrame
                        {
                            timeOffset = 0.0,
                            partPersistentId = part.persistentId,
                            craftID = part.craftID,
                            partIndex = p,
                            moduleName = mod.ClassName,
                            eventName = field.name,
                            eventType = "FIELD",
                            fieldName = field.name,
                            fieldValue = valStr,
                            executed = false
                        });
                    }
                }
            }
        }

        private void ApplyBaselineStateSnapshot(Vessel v)
        {
            if (v == null || v.parts == null || CurrentSession == null || CurrentSession.events == null) return;

            List<FlightEventFrame> events = CurrentSession.events;
            for (int i = 0; i < events.Count; i++)
            {
                FlightEventFrame evt = events[i];
                if (evt.timeOffset <= 0.0001)
                {
                    ExecutePartEvent(v, evt);
                    evt.executed = true;
                }
                else
                {
                    break;
                }
            }
        }

        private string FormatDuration(double totalSeconds)
        {
            if (totalSeconds < 60.0)
            {
                return $"{totalSeconds:F1}s";
            }

            int wholeSeconds = (int)totalSeconds;
            int hours = wholeSeconds / 3600;
            int minutes = (wholeSeconds % 3600) / 60;
            int seconds = wholeSeconds % 60;

            if (hours > 0)
            {
                return $"{hours}h {minutes}m {seconds}s";
            }
            return $"{minutes}m {seconds}s";
        }

        private string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return Regex.Replace(input, "<.*?>", string.Empty);
        }

        private string GetButtonText(Button btn)
        {
            Text uiText = btn.GetComponentInChildren<Text>(true);
            if (uiText != null && !string.IsNullOrEmpty(uiText.text))
                return StripRichText(uiText.text).Trim();

            Component[] comps = btn.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c.GetType().Name == "TextMeshProUGUI")
                {
                    PropertyInfo textProp = c.GetType().GetProperty("text");
                    if (textProp != null)
                    {
                        string val = textProp.GetValue(c, null) as string;
                        if (!string.IsNullOrEmpty(val))
                            return StripRichText(val).Trim();
                    }
                }
            }
            return string.Empty;
        }

        private void OnPAWButtonClicked(Part part, Button btn)
        {
            if (CurrentState != RecorderState.Recording || CurrentSession == null || part == null) return;

            string btnText = GetButtonText(btn);
            if (string.IsNullOrEmpty(btnText)) return;

            bool matched = false;
            string searchName = btnText;

            for (int m = 0; m < part.Modules.Count; m++)
            {
                PartModule mod = part.Modules[m];
                if (mod == null || mod.Events == null) continue;

                for (int e = 0; e < mod.Events.Count; e++)
                {
                    BaseEvent evt = mod.Events[e];
                    if (evt == null) continue;

                    string cName = StripRichText(evt.name).Trim();
                    string cGuiName = StripRichText(evt.guiName).Trim();
                    string cLocGuiName = StripRichText(Localizer.Format(evt.guiName)).Trim();

                    if (cName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                        cGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                        cLocGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                        searchName.IndexOf(cName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        searchName.IndexOf(cLocGuiName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        RecordPartEvent(part, mod.ClassName, evt.name, "EVENT");
                        matched = true;
                        break;
                    }
                }
                if (matched) break;
            }

            if (!matched)
            {
                RecordPartEvent(part, "GenericPAWEvent", btnText, "EVENT");
            }
        }

        private void OnStageActivate(int stage)
        {
            if (CurrentState == RecorderState.Recording && CurrentSession != null)
            {
                FlightEventFrame stagingEvent = new FlightEventFrame
                {
                    timeOffset = Planetarium.GetUniversalTime() - CurrentSession.startUT,
                    partPersistentId = 0,
                    craftID = 0,
                    partIndex = -1,
                    moduleName = "STAGING",
                    eventName = "STAGE",
                    eventType = "STAGING",
                    executed = false
                };

                CurrentSession.events.Add(stagingEvent);
            }
        }

        private void RecordPartEvent(Part part, string moduleName, string eventName, string type, string fieldName = null, string fieldValue = null)
        {
            if (CurrentState != RecorderState.Recording || CurrentSession == null || part == null) return;

            double nowOffset = Planetarium.GetUniversalTime() - CurrentSession.startUT;

            int count = CurrentSession.events.Count;
            for (int i = count - 1; i >= 0 && i >= count - 10; i--)
            {
                FlightEventFrame prev = CurrentSession.events[i];
                if (nowOffset - prev.timeOffset > 0.15) break;

                if (prev.partPersistentId == part.persistentId &&
                    prev.moduleName == moduleName &&
                    prev.eventName == eventName &&
                    prev.fieldName == fieldName)
                {
                    return;
                }
            }

            int pIndex = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.parts.IndexOf(part) : -1;

            FlightEventFrame eventFrame = new FlightEventFrame
            {
                timeOffset = nowOffset,
                partPersistentId = part.persistentId,
                craftID = part.craftID,
                partIndex = pIndex,
                moduleName = moduleName,
                eventName = eventName,
                eventType = type,
                fieldName = fieldName,
                fieldValue = fieldValue,
                executed = false
            };

            CurrentSession.events.Add(eventFrame);
        }

        private void ProcessDiscreteEventsForTime(double startTime, double endTime)
        {
            if (CurrentSession == null || CurrentSession.events == null) return;

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel == null) return;

            List<FlightEventFrame> events = CurrentSession.events;
            while (nextEventIndex < events.Count && events[nextEventIndex].timeOffset <= endTime)
            {
                FlightEventFrame evt = events[nextEventIndex];
                if (!evt.executed && evt.timeOffset >= startTime)
                {
                    ExecutePartEvent(activeVessel, evt);
                    evt.executed = true;
                }
                nextEventIndex++;
            }
        }

        private void ExecutePartEvent(Vessel vessel, FlightEventFrame evt)
        {
            if (evt == null || vessel == null) return;

            // staging events subA
            if (evt.moduleName == "STAGING" && evt.eventName == "STAGE")
            {
                StageManager.ActivateNextStage();
                for (int p = 0; p < vessel.parts.Count; p++)
                {
                    Part pItem = vessel.parts[p];
                    if (pItem == null || pItem.Modules == null) continue;
                    for (int m = 0; m < pItem.Modules.Count; m++)
                    {
                        if (pItem.Modules[m] is LaunchClamp clamp)
                        {
                            clamp.Release();
                        }
                        else if (pItem.Modules[m] is ModuleProceduralFairing fairing)
                        {
                            fairing.DeployFairing();
                            fairing.Events["Deploy"]?.Invoke();
                        }
                    }
                }
                return;
            }

            Part targetPart = FindPartInVessel(vessel, evt.partPersistentId, evt.craftID, evt.partIndex);
            if (targetPart == null || targetPart.Modules == null) return;

            if (evt.eventType == "FIELD" || !string.IsNullOrEmpty(evt.fieldName))
            {
                for (int i = 0; i < targetPart.Modules.Count; i++)
                {
                    PartModule mod = targetPart.Modules[i];
                    if (mod != null && SetModuleFieldValue(mod, evt.fieldName, evt.fieldValue)) return;
                }
                return;
            }

            if (evt.eventType == "ACTION")
            {
                for (int i = 0; i < targetPart.Modules.Count; i++)
                {
                    PartModule mod = targetPart.Modules[i];
                    if (mod == null || mod.Actions == null) continue;

                    for (int a = 0; a < mod.Actions.Count; a++)
                    {
                        BaseAction act = mod.Actions[a];
                        if (act != null && (act.name.Equals(evt.eventName, StringComparison.OrdinalIgnoreCase) ||
                                            act.guiName.Equals(evt.eventName, StringComparison.OrdinalIgnoreCase)))
                        {
                            act.Invoke(new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                            return;
                        }
                    }
                }
                return;
            }

            for (int i = 0; i < targetPart.Modules.Count; i++)
            {
                PartModule mod = targetPart.Modules[i];
                if (mod == null) continue;

                if (mod is RetractableLadder ladderMod)
                {
                    if (evt.eventName.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ladderMod.Extend();
                        ladderMod.Events["Extend"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ladderMod.Retract();
                        ladderMod.Events["Retract"]?.Invoke();
                        return;
                    }
                }

                // animations
                if (mod is ModuleAnimateGeneric animMod)
                {
                    if (evt.eventName.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.eventName.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (animMod.animTime < 0.95f)
                        {
                            animMod.Toggle();
                        }
                        return;
                    }
                    if (evt.eventName.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.eventName.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (animMod.animTime > 0.05f)
                        {
                            animMod.Toggle();
                        }
                        return;
                    }
                    animMod.Toggle();
                    return;
                }

                if (mod is ModuleGrappleNode grappleMod)
                {
                    if (evt.eventName.IndexOf("Arm", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        grappleMod.Events["Arm"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Disarm", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        grappleMod.Events["Disarm"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.eventName.IndexOf("Decouple", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.eventName.IndexOf("Disengage", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        grappleMod.Release();
                        grappleMod.Events["ReleaseCluster"]?.Invoke();
                        grappleMod.Events["Decouple"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Pivot", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        grappleMod.Events["TogglePivot"]?.Invoke();
                        return;
                    }
                }

                if (mod is ModuleWheelDeployment wheelDepMod)
                {
                    if (evt.eventName.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        evt.eventName.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (wheelDepMod.stateString != "Deployed" && wheelDepMod.stateString != "Deploying")
                        {
                            wheelDepMod.EventToggle();
                        }
                        wheelDepMod.Events["Deploy"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (wheelDepMod.stateString != "Retracted" && wheelDepMod.stateString != "Retracting")
                        {
                            wheelDepMod.EventToggle();
                        }
                        wheelDepMod.Events["Retract"]?.Invoke();
                        return;
                    }
                    wheelDepMod.EventToggle();
                    return;
                }

                if (mod is LaunchClamp clampMod)
                {
                    clampMod.Release();
                    return;
                }

                if (mod is ModuleProceduralFairing fairingMod)
                {
                    fairingMod.DeployFairing();
                    fairingMod.Events["Deploy"]?.Invoke();
                    return;
                }

                if (mod is ModuleDecouple decMod)
                {
                    decMod.Decouple();
                    return;
                }
                if (mod is ModuleAnchoredDecoupler anchMod)
                {
                    anchMod.Decouple();
                    return;
                }

                if (mod is ModuleDockingNode dockMod)
                {
                    dockMod.Undock();
                    return;
                }

                if (mod is ModuleDeployablePart deployMod)
                {
                    if (evt.eventName.IndexOf("Extend", StringComparison.OrdinalIgnoreCase) >= 0) { deployMod.Extend(); return; }
                    if (evt.eventName.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0) { deployMod.Retract(); return; }
                }

                if (mod is ModuleAnimationGroup animGroupMod)
                {
                    if (evt.eventName.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0) { animGroupMod.DeployModule(); return; }
                    if (evt.eventName.IndexOf("Retract", StringComparison.OrdinalIgnoreCase) >= 0) { animGroupMod.RetractModule(); return; }
                }

                if (mod is ModuleColorChanger colorChangerMod)
                {
                    colorChangerMod.ToggleEvent();
                    return;
                }

                if (mod is ModuleEngines engineMod)
                {
                    if (evt.eventName.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) { engineMod.Activate(); return; }
                    if (evt.eventName.IndexOf("Shutdown", StringComparison.OrdinalIgnoreCase) >= 0) { engineMod.Shutdown(); return; }
                }

                if (mod is ModuleLight lightMod)
                {
                    if (evt.eventName.IndexOf("LightsOn", StringComparison.OrdinalIgnoreCase) >= 0) { lightMod.LightsOn(); return; }
                    if (evt.eventName.IndexOf("LightsOff", StringComparison.OrdinalIgnoreCase) >= 0) { lightMod.LightsOff(); return; }
                }

                if (mod is ModuleParachute chuteMod)
                {
                    if (evt.eventName.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        chuteMod.Deploy();
                        chuteMod.Events["Deploy"]?.Invoke();
                        return;
                    }
                    if (evt.eventName.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        chuteMod.CutParachute();
                        chuteMod.Events["CutParachute"]?.Invoke();
                        return;
                    }
                }

                if (mod is ModuleScienceExperiment sciMod)
                {
                    if (evt.eventName.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sciMod.Deployed = true;
                        for (int a = 0; a < targetPart.Modules.Count; a++)
                        {
                            if (targetPart.Modules[a] is ModuleAnimateGeneric genericAnim)
                            {
                                genericAnim.Toggle();
                            }
                        }
                        return;
                    }
                    if (evt.eventName.IndexOf("Reset", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sciMod.Deployed = false;
                        for (int a = 0; a < targetPart.Modules.Count; a++)
                        {
                            if (targetPart.Modules[a] is ModuleAnimateGeneric genericAnim)
                            {
                                genericAnim.Toggle();
                            }
                        }
                        return;
                    }
                }

                if (mod is ModuleResourceHarvester harvesterMod)
                {
                    if (evt.eventName.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0) { harvesterMod.StartResourceConverter(); return; }
                    if (evt.eventName.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0) { harvesterMod.StopResourceConverter(); return; }
                }

                if (mod is ModuleResourceConverter converterMod)
                {
                    if (evt.eventName.StartsWith("Start:") || evt.eventName.StartsWith("Stop:"))
                    {
                        string[] tokens = evt.eventName.Split(':');
                        if (tokens.Length >= 2 && int.TryParse(tokens[1], out int targetModIdx))
                        {
                            if (i == targetModIdx)
                            {
                                if (tokens[0] == "Start") converterMod.StartResourceConverter();
                                else converterMod.StopResourceConverter();
                                return;
                            }
                            continue;
                        }
                    }
                }

                if (mod is ModuleActiveRadiator radMod)
                {
                    if (evt.eventName.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) { radMod.Activate(); return; }
                    if (evt.eventName.IndexOf("Shutdown", StringComparison.OrdinalIgnoreCase) >= 0) { radMod.Shutdown(); return; }
                }

                if (InvokeModuleEventOrAction(mod, evt.eventName)) return;
            }
        }

        private object ParseValueToType(string valStr, Type t)
        {
            if (t == typeof(float))
            {
                if (float.TryParse(valStr, NumberStyles.Float, Inv, out float fVal)) return fVal;
            }
            else if (t == typeof(double))
            {
                if (double.TryParse(valStr, NumberStyles.Float, Inv, out double dVal)) return dVal;
            }
            else if (t == typeof(bool))
            {
                return valStr == "1" || valStr.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (t == typeof(int))
            {
                if (int.TryParse(valStr, NumberStyles.Integer, Inv, out int iVal)) return iVal;
            }
            else if (t.IsEnum)
            {
                try { return Enum.Parse(t, valStr, true); } catch { }
            }
            else if (t == typeof(string))
            {
                return valStr;
            }
            return null;
        }

        private bool SetModuleFieldValue(PartModule mod, string fieldName, string fieldValue)
        {
            if (mod == null || string.IsNullOrEmpty(fieldName)) return false;

            if (mod.Fields != null && mod.Fields[fieldName] != null)
            {
                BaseField field = mod.Fields[fieldName];
                try
                {
                    Type t = field.FieldInfo.FieldType;
                    object parsedVal = ParseValueToType(fieldValue, t);

                    if (parsedVal != null)
                    {
                        object oldVal = field.GetValue(mod);
                        field.SetValue(parsedVal, mod);

                        if (field.uiControlFlight != null && field.uiControlFlight.onFieldChanged != null)
                        {
                            field.uiControlFlight.onFieldChanged.Invoke(field, oldVal);
                        }

                        if (UIPartActionController.Instance != null && UIPartActionController.Instance.windows != null && mod.part != null)
                        {
                            List<UIPartActionWindow> windows = UIPartActionController.Instance.windows;
                            for (int w = 0; w < windows.Count; w++)
                            {
                                UIPartActionWindow window = windows[w];
                                if (window != null && window.part == mod.part)
                                {
                                    window.UpdateWindow();
                                    break;
                                }
                            }
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FlightRecorder] Field assignment exception: {ex.Message}");
                }
            }

            PropertyInfo prop = mod.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    object parsedVal = ParseValueToType(fieldValue, prop.PropertyType);
                    if (parsedVal != null)
                    {
                        prop.SetValue(mod, parsedVal, null);
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private bool InvokeModuleEventOrAction(PartModule mod, string eventName)
        {
            if (mod == null || string.IsNullOrEmpty(eventName)) return false;

            if (mod is ModuleScienceExperiment || mod is ModuleScienceContainer)
            {
                if (eventName.IndexOf("Experiment", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eventName.IndexOf("Review", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    eventName.IndexOf("Collect", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            string searchName = StripRichText(eventName).Trim();

            if (mod.Events != null)
            {
                for (int e = 0; e < mod.Events.Count; e++)
                {
                    BaseEvent kspEvent = mod.Events[e];
                    if (kspEvent != null)
                    {
                        string cName = StripRichText(kspEvent.name).Trim();
                        string cGuiName = StripRichText(kspEvent.guiName).Trim();
                        string cLocGuiName = StripRichText(Localizer.Format(kspEvent.guiName)).Trim();

                        if (cName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                            cGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                            cLocGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                            searchName.IndexOf(cName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            searchName.IndexOf(cLocGuiName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            kspEvent.Invoke();
                            return true;
                        }
                    }
                }
            }

            if (mod.Actions != null)
            {
                for (int a = 0; a < mod.Actions.Count; a++)
                {
                    BaseAction kspAction = mod.Actions[a];
                    if (kspAction != null)
                    {
                        string cName = StripRichText(kspAction.name).Trim();
                        string cGuiName = StripRichText(kspAction.guiName).Trim();
                        string cLocGuiName = StripRichText(Localizer.Format(kspAction.guiName)).Trim();

                        if (cName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                            cGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
                            cLocGuiName.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                        {
                            kspAction.Invoke(new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void ResetEventExecutionFlags()
        {
            if (CurrentSession == null || CurrentSession.events == null) return;
            for (int i = 0; i < CurrentSession.events.Count; i++)
            {
                CurrentSession.events[i].executed = false;
            }
            agStateInitialized = false;
            nextEventIndex = 0;
        }

        #endregion

        #region recording & playback core

        public void StartRecording()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            recordedVessel = v;
            wheelCache.Rebuild(v);

            Vector3d worldRelPos = v.vesselTransform.position - v.mainBody.position;
            QuaternionD bodyRotD = ToQuaternionD(v.mainBody.bodyTransform.rotation);
            Vector3d bodyLocalPos = SafeInverse(bodyRotD) * worldRelPos;
            QuaternionD vesselRotD = ToQuaternionD(v.vesselTransform.rotation);
            QuaternionD bodyLocalRot = SafeInverse(bodyRotD) * vesselRotD;

            string cleanCraftName = Localizer.Format(v.vesselName);

            CurrentSession = new RecordedSessionData
            {
                craftName = cleanCraftName,
                startUT = Planetarium.GetUniversalTime(),
                bodyName = v.mainBody.bodyName,
                startPosX = bodyLocalPos.x,
                startPosY = bodyLocalPos.y,
                startPosZ = bodyLocalPos.z,
                startRotX = bodyLocalRot.x,
                startRotY = bodyLocalRot.y,
                startRotZ = bodyLocalRot.z,
                startRotW = bodyLocalRot.w
            };

            hookedPAWButtons.Clear();
            lastFieldCache.Clear();
            genericEventActiveState.Clear();
            genericModPropertyCache.Clear();
            engineIgnitedCache.Clear();
            deployStateCache.Clear();
            lightStateCache.Clear();
            chuteStateCache.Clear();
            decoupleStateCache.Clear();
            dockStateCache.Clear();
            scienceStateCache.Clear();
            converterStateCache.Clear();
            ladderStateCache.Clear();
            wheelDepStateCache.Clear();
            colorChangerStateCache.Clear();
            animGroupStateCache.Clear();
            harvesterStateCache.Clear();
            radiatorStateCache.Clear();
            grappleStateCache.Clear();
            genericAnimSwitchCache.Clear();

            CaptureBaselineStateSnapshot(v);

            CurrentState = RecorderState.Recording;
            ClearGraphTexture();
        }

        private void RecordFrame(Vessel v)
        {
            FlightCtrlState ctrl = v.ctrlState;
            double currentUT = Planetarium.GetUniversalTime();

            Vector3d worldRelPos = v.vesselTransform.position - v.mainBody.position;
            QuaternionD bodyRotD = ToQuaternionD(v.mainBody.bodyTransform.rotation);
            Vector3d bodyLocalPos = SafeInverse(bodyRotD) * worldRelPos;
            QuaternionD vesselRotD = ToQuaternionD(v.vesselTransform.rotation);
            QuaternionD bodyLocalRot = SafeInverse(bodyRotD) * vesselRotD;

            FlightInputFrame frame = new FlightInputFrame
            {
                timeOffset = currentUT - CurrentSession.startUT,
                posX = bodyLocalPos.x,
                posY = bodyLocalPos.y,
                posZ = bodyLocalPos.z,
                rotX = bodyLocalRot.x,
                rotY = bodyLocalRot.y,
                rotZ = bodyLocalRot.z,
                rotW = bodyLocalRot.w,
                srfVelX = v.srf_velocity.x,
                srfVelY = v.srf_velocity.y,
                srfVelZ = v.srf_velocity.z,
                obtVelX = v.obt_velocity.x,
                obtVelY = v.obt_velocity.y,
                obtVelZ = v.obt_velocity.z,
                pitch = ctrl.pitch,
                roll = ctrl.roll,
                yaw = ctrl.yaw,
                mainThrottle = ctrl.mainThrottle,
                X = ctrl.X,
                Y = ctrl.Y,
                Z = ctrl.Z,
                wheelSteer = ctrl.wheelSteer,
                wheelThrottle = ctrl.wheelThrottle,
                sas = v.ActionGroups[KSPActionGroup.SAS],
                rcs = v.ActionGroups[KSPActionGroup.RCS],
                gear = v.ActionGroups[KSPActionGroup.Gear],
                light = v.ActionGroups[KSPActionGroup.Light],
                brakes = v.ActionGroups[KSPActionGroup.Brakes],
                precisionMode = FlightInputHandler.fetch != null && FlightInputHandler.fetch.precisionMode,
                altitude = (float)v.altitude,
                speed = (float)v.srfSpeed,
                obtSpeed = (float)v.obt_speed,
                gForce = (float)v.geeForce
            };

            for (int i = 0; i < 10; i++)
            {
                KSPActionGroup group = (KSPActionGroup)(1 << (i + 7));
                frame.customActionGroups[i] = v.ActionGroups[group];
            }

            if (v != null && wheelCache.cacheByPersistentId.Count > 0)
            {
                frame.wheelData = new WheelFrameData[wheelCache.cacheByPersistentId.Count];
                int idx = 0;

                foreach (var kvp in wheelCache.cacheByPersistentId)
                {
                    WheelTransformCache.CachedWheel cached = kvp.Value;
                    frame.wheelData[idx++] = new WheelFrameData
                    {
                        partPersistentId = cached.partPersistentId,
                        craftID = cached.craftID,
                        partIndex = cached.partIndex,
                        suspensionY = cached.suspensionTransform != null ? cached.suspensionTransform.localPosition.y : 0f,
                        wheelRotationX = cached.wheelTransform != null ? cached.wheelTransform.localEulerAngles.x : 0f
                    };
                }
            }

            CurrentSession.frames.Add(frame);
            CurrentSession.duration = frame.timeOffset;
        }

        public void StartPlayback()
        {
            if (CurrentSession == null || CurrentSession.frames == null || CurrentSession.frames.Count == 0) return;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            if (FlightDriver.Pause)
            {
                FlightDriver.SetPause(false);
            }

            wheelCache.Rebuild(v);
            lastFrameWorldPosValid = false;

            if (v.orbitDriver != null)
            {
                savedOrbitDriverMode = v.orbitDriver.updateMode;
                v.orbitDriver.updateMode = OrbitDriver.UpdateMode.IDLE;
            }
            savedLandedState = v.Landed;
            savedSplashedState = v.Splashed;
            savedSituation = v.situation;

            if (revertInGameTimeOnPlayback)
            {
                Planetarium.SetUniversalTime(CurrentSession.startUT);
                if (v.mainBody != null)
                {
                    v.mainBody.CBUpdate();
                }
            }

            playbackProgressUT = 0;
            lastPlaybackProgressUT = 0;
            currentPlaybackIndex = 0;

            if (CurrentSession.events != null && CurrentSession.events.Count > 1)
            {
                CurrentSession.events.Sort((a, b) => a.timeOffset.CompareTo(b.timeOffset));
            }

            ResetEventExecutionFlags();
            ApplyBaselineStateSnapshot(v);
            AttachFlyByWire();
            SetVesselKinematic(v, true);

            ApplyPlaybackFrame(v, 0.0, 0.0);
            SyncVesselVelocities();

            CurrentState = RecorderState.Playback;
        }

        public void PausePlayback()
        {
            if (CurrentState == RecorderState.Playback)
            {
                CurrentState = RecorderState.Paused;
                DetachFlyByWire();
                FlightDriver.SetPause(true);
            }
            else if (CurrentState == RecorderState.Paused)
            {
                FlightDriver.SetPause(false);
                AttachFlyByWire();
                CurrentState = RecorderState.Playback;
            }
        }

        public void StopRecordingOrPlayback()
        {
            bool wasActivePlayback = CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused;

            if (FlightDriver.Pause)
            {
                FlightDriver.SetPause(false);
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != null)
            {
                if (wasActivePlayback && CurrentSession != null && CurrentSession.frames.Count > 0)
                {
                    HandOffToRealPhysics(activeVessel);

                    if (activeVessel.mainBody != null && currentObtVelocity.sqrMagnitude > 0.001)
                    {
                        Vector3d orbitalPos = (activeVessel.vesselTransform.position - activeVessel.mainBody.position).xzy;
                        Vector3d orbitalVel = currentObtVelocity.xzy;
                        double currentUT = Planetarium.GetUniversalTime();

                        activeVessel.orbit.UpdateFromStateVectors(
                            orbitalPos,
                            orbitalVel,
                            activeVessel.mainBody,
                            currentUT
                        );

                        if (activeVessel.orbitDriver != null)
                        {
                            bool inSpace = activeVessel.altitude > (activeVessel.mainBody.atmosphere ? activeVessel.mainBody.atmosphereDepth : 1000.0);
                            activeVessel.orbitDriver.updateMode = inSpace ? OrbitDriver.UpdateMode.UPDATE : OrbitDriver.UpdateMode.TRACK_Phys;
                            activeVessel.orbitDriver.pos = orbitalPos;
                            activeVessel.orbitDriver.vel = orbitalVel;
                        }
                    }
                    else if (activeVessel.orbitDriver != null)
                    {
                        activeVessel.orbitDriver.updateMode = savedOrbitDriverMode;
                    }
                }
                else
                {
                    SetVesselKinematic(activeVessel, false);
                }
            }

            RestoreNavBallElements();

            DetachFlyByWire();
            hookedPAWButtons.Clear();
            lastFieldCache.Clear();
            recordedVessel = null;
            lastFrameWorldPosValid = false;
            CurrentState = RecorderState.Idle;

            if (!wasActivePlayback && CurrentSession != null)
            {
                RebuildGraphTexture();
            }

            windowRect.height = 0;
        }

        private void AttachFlyByWire()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                v.OnFlyByWire -= OnFlyByWireCallback;
                v.OnFlyByWire += OnFlyByWireCallback;
            }
        }

        private void DetachFlyByWire()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                v.OnFlyByWire -= OnFlyByWireCallback;
            }
        }

        private void OnFlyByWireCallback(FlightCtrlState state)
        {
            if (CurrentState != RecorderState.Playback || CurrentSession == null) return;

            FlightInputFrame frame = GetInterpolatedFrame(playbackProgressUT);
            if (frame == null) return;

            state.pitch = frame.pitch;
            state.roll = frame.roll;
            state.yaw = frame.yaw;
            state.mainThrottle = frame.mainThrottle;
            state.X = frame.X;
            state.Y = frame.Y;
            state.Z = frame.Z;
            state.wheelSteer = frame.wheelSteer;
            state.wheelThrottle = frame.wheelThrottle;

            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                ApplyActionGroupStateEdge(v, KSPActionGroup.SAS, frame.sas, 0);
                ApplyActionGroupStateEdge(v, KSPActionGroup.RCS, frame.rcs, 1);
                ApplyActionGroupStateEdge(v, KSPActionGroup.Gear, frame.gear, 2);
                ApplyActionGroupStateEdge(v, KSPActionGroup.Light, frame.light, 3);
                ApplyActionGroupStateEdge(v, KSPActionGroup.Brakes, frame.brakes, 4);

                for (int i = 0; i < 10; i++)
                {
                    KSPActionGroup group = (KSPActionGroup)(1 << (i + 7));
                    bool val = frame.customActionGroups != null && frame.customActionGroups.Length > i && frame.customActionGroups[i];
                    ApplyActionGroupStateEdge(v, group, val, 5 + i);
                }

                if (FlightInputHandler.fetch != null && FlightInputHandler.fetch.precisionMode != frame.precisionMode)
                {
                    FlightInputHandler.fetch.precisionMode = frame.precisionMode;
                }
            }
        }

        private void ApplyActionGroupStateEdge(Vessel v, KSPActionGroup group, bool targetState, int stateIdx)
        {
            if (!agStateInitialized || lastRecordedAGState[stateIdx] != targetState)
            {
                v.ActionGroups.SetGroup(group, targetState);
                lastRecordedAGState[stateIdx] = targetState;
                if (stateIdx == 14) agStateInitialized = true;
            }
        }

        public FlightInputFrame GetCurrentInterpolatedFrame()
        {
            return GetInterpolatedFrame(playbackProgressUT);
        }

        private FlightInputFrame GetInterpolatedFrame(double timeSec)
        {
            if (CurrentSession == null || CurrentSession.frames == null || CurrentSession.frames.Count == 0) return null;

            List<FlightInputFrame> frames = CurrentSession.frames;
            int count = frames.Count;

            if (count == 1) return frames[0];
            if (timeSec <= frames[0].timeOffset) return frames[0];
            if (timeSec >= frames[count - 1].timeOffset) return frames[count - 1];

            if (currentPlaybackIndex >= count - 1 || frames[currentPlaybackIndex].timeOffset > timeSec)
            {
                currentPlaybackIndex = 0;
            }

            while (currentPlaybackIndex < count - 1 && frames[currentPlaybackIndex + 1].timeOffset <= timeSec)
            {
                currentPlaybackIndex++;
            }

            if (currentPlaybackIndex >= count - 1) return frames[count - 1];

            FlightInputFrame f1 = frames[currentPlaybackIndex];
            FlightInputFrame f2 = frames[currentPlaybackIndex + 1];

            double dt = f2.timeOffset - f1.timeOffset;
            double alpha = dt > 0.00001 ? ((timeSec - f1.timeOffset) / dt) : 0.0;
            alpha = Math.Max(0.0, Math.Min(1.0, alpha));
            float alphaF = (float)alpha;

            Vector3d pos1 = new Vector3d(f1.posX, f1.posY, f1.posZ);
            Vector3d pos2 = new Vector3d(f2.posX, f2.posY, f2.posZ);
            Vector3d blendedPos = SafeVectorSlerp(pos1, pos2, alpha);

            Vector3d srfVel1 = new Vector3d(f1.srfVelX, f1.srfVelY, f1.srfVelZ);
            Vector3d srfVel2 = new Vector3d(f2.srfVelX, f2.srfVelY, f2.srfVelZ);
            Vector3d blendedSrfVel = SafeVectorSlerp(srfVel1, srfVel2, alpha);

            Vector3d obtVel1 = new Vector3d(f1.obtVelX, f1.obtVelY, f1.obtVelZ);
            Vector3d obtVel2 = new Vector3d(f2.obtVelX, f2.obtVelY, f2.obtVelZ);
            Vector3d blendedObtVel = SafeVectorSlerp(obtVel1, obtVel2, alpha);

            QuaternionD rot1 = NormalizeQuaternionD(new QuaternionD(f1.rotX, f1.rotY, f1.rotZ, f1.rotW));
            QuaternionD rot2 = NormalizeQuaternionD(new QuaternionD(f2.rotX, f2.rotY, f2.rotZ, f2.rotW));

            QuaternionD blendedRot = SafeSlerp(rot1, rot2, alpha);

            FlightInputFrame blended = new FlightInputFrame
            {
                timeOffset = timeSec,
                posX = blendedPos.x,
                posY = blendedPos.y,
                posZ = blendedPos.z,
                rotX = blendedRot.x,
                rotY = blendedRot.y,
                rotZ = blendedRot.z,
                rotW = blendedRot.w,
                srfVelX = blendedSrfVel.x,
                srfVelY = blendedSrfVel.y,
                srfVelZ = blendedSrfVel.z,
                obtVelX = blendedObtVel.x,
                obtVelY = blendedObtVel.y,
                obtVelZ = blendedObtVel.z,
                pitch = Mathf.Lerp(f1.pitch, f2.pitch, alphaF),
                roll = Mathf.Lerp(f1.roll, f2.roll, alphaF),
                yaw = Mathf.Lerp(f1.yaw, f2.yaw, alphaF),
                mainThrottle = Mathf.Lerp(f1.mainThrottle, f2.mainThrottle, alphaF),
                X = Mathf.Lerp(f1.X, f2.X, alphaF),
                Y = Mathf.Lerp(f1.Y, f2.Y, alphaF),
                Z = Mathf.Lerp(f1.Z, f2.Z, alphaF),
                wheelSteer = Mathf.Lerp(f1.wheelSteer, f2.wheelSteer, alphaF),
                wheelThrottle = Mathf.Lerp(f1.wheelThrottle, f2.wheelThrottle, alphaF),
                sas = f1.sas,
                rcs = f1.rcs,
                gear = f1.gear,
                light = f1.light,
                brakes = f1.brakes,
                precisionMode = f1.precisionMode,
                altitude = Mathf.Lerp(f1.altitude, f2.altitude, alphaF),
                speed = Mathf.Lerp(f1.speed, f2.speed, alphaF),
                obtSpeed = Mathf.Lerp(f1.obtSpeed, f2.obtSpeed, alphaF),
                gForce = Mathf.Lerp(f1.gForce, f2.gForce, alphaF)
            };

            for (int i = 0; i < 10; i++)
            {
                blended.customActionGroups[i] = f1.customActionGroups[i];
            }

            if (f1.wheelData != null && f1.wheelData.Length > 0)
            {
                blended.wheelData = new WheelFrameData[f1.wheelData.Length];
                for (int i = 0; i < f1.wheelData.Length; i++)
                {
                    WheelFrameData w1 = f1.wheelData[i];
                    float suspY = w1.suspensionY;
                    float rotX = w1.wheelRotationX;

                    if (f2.wheelData != null)
                    {
                        for (int j = 0; j < f2.wheelData.Length; j++)
                        {
                            if (f2.wheelData[j].partPersistentId == w1.partPersistentId || (w1.craftID != 0 && f2.wheelData[j].craftID == w1.craftID))
                            {
                                suspY = Mathf.Lerp(w1.suspensionY, f2.wheelData[j].suspensionY, alphaF);
                                rotX = Mathf.LerpAngle(w1.wheelRotationX, f2.wheelData[j].wheelRotationX, alphaF);
                                break;
                            }
                        }
                    }

                    blended.wheelData[i] = new WheelFrameData
                    {
                        partPersistentId = w1.partPersistentId,
                        craftID = w1.craftID,
                        partIndex = w1.partIndex,
                        suspensionY = suspY,
                        wheelRotationX = rotX
                    };
                }
            }

            return blended;
        }

        #endregion

        #region File Persistence Logic

        public void RefreshSavedFilesList()
        {
            savedFilePaths.Clear();
            string dataFolder = GetPluginDataFolder();
            if (Directory.Exists(dataFolder))
            {
                savedFilePaths.AddRange(Directory.GetFiles(dataFolder, "*.cfg"));
            }
        }

        public void SaveCurrentSessionToDisk()
        {
            if (CurrentSession == null || CurrentSession.frames == null || CurrentSession.frames.Count == 0) return;

            try
            {
                string dataFolder = GetPluginDataFolder();

                string safeCraftName = CurrentSession.craftName;
                if (string.IsNullOrEmpty(safeCraftName)) safeCraftName = "Craft";

                foreach (char invalidChar in Path.GetInvalidFileNameChars())
                {
                    safeCraftName = safeCraftName.Replace(invalidChar, '_');
                }
                safeCraftName = safeCraftName.Replace(' ', '_');

                string timeStamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = $"{safeCraftName} - {timeStamp}.cfg";
                string fullFilePath = Path.Combine(dataFolder, fileName);

                ConfigNode root = SessionToConfigNode(CurrentSession);
                root.Save(fullFilePath);

                RefreshSavedFilesList();
                showFileList = true;
                windowRect.height = 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlightRecorder] Save Exception: {ex}");
            }
        }

        public void LoadSessionFromDisk(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                ConfigNode root = ConfigNode.Load(filePath);
                if (root == null) return;

                RecordedSessionData loadedData = ConfigNodeToSession(root);

                if (loadedData != null && loadedData.frames != null && loadedData.frames.Count > 0)
                {
                    CurrentSession = loadedData;
                    playbackProgressUT = 0;
                    lastPlaybackProgressUT = 0;
                    currentPlaybackIndex = 0;
                    StopRecordingOrPlayback();

                    if (FlightGlobals.ActiveVessel != null)
                    {
                        wheelCache.Rebuild(FlightGlobals.ActiveVessel);
                    }

                    RebuildGraphTexture();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlightRecorder] Failed to load session file: {ex.Message}");
            }
        }

        private ConfigNode SessionToConfigNode(RecordedSessionData session)
        {
            ConfigNode root = new ConfigNode("FLIGHT_RECORDING");
            root.AddValue("craftName", session.craftName);
            root.AddValue("startUT", session.startUT.ToString(Inv));
            root.AddValue("duration", session.duration.ToString(Inv));
            root.AddValue("bodyName", session.bodyName);
            root.AddValue("startPosX", session.startPosX.ToString(Inv));
            root.AddValue("startPosY", session.startPosY.ToString(Inv));
            root.AddValue("startPosZ", session.startPosZ.ToString(Inv));
            root.AddValue("startRotX", session.startRotX.ToString(Inv));
            root.AddValue("startRotY", session.startRotY.ToString(Inv));
            root.AddValue("startRotZ", session.startRotZ.ToString(Inv));
            root.AddValue("startRotW", session.startRotW.ToString(Inv));

            for (int i = 0; i < session.frames.Count; i++)
            {
                root.AddValue("F", FrameToString(session.frames[i]));
            }
            for (int i = 0; i < session.events.Count; i++)
            {
                root.AddValue("E", EventToString(session.events[i]));
            }

            return root;
        }

        private RecordedSessionData ConfigNodeToSession(ConfigNode root)
        {
            RecordedSessionData data = new RecordedSessionData
            {
                craftName = root.GetValue("craftName") ?? "Unknown Craft",
                bodyName = root.GetValue("bodyName") ?? "Kerbin"
            };

            double.TryParse(root.GetValue("startUT"), NumberStyles.Float, Inv, out data.startUT);
            double.TryParse(root.GetValue("duration"), NumberStyles.Float, Inv, out data.duration);
            double.TryParse(root.GetValue("startPosX"), NumberStyles.Float, Inv, out data.startPosX);
            double.TryParse(root.GetValue("startPosY"), NumberStyles.Float, Inv, out data.startPosY);
            double.TryParse(root.GetValue("startPosZ"), NumberStyles.Float, Inv, out data.startPosZ);
            double.TryParse(root.GetValue("startRotX"), NumberStyles.Float, Inv, out data.startRotX);
            double.TryParse(root.GetValue("startRotY"), NumberStyles.Float, Inv, out data.startRotY);
            double.TryParse(root.GetValue("startRotZ"), NumberStyles.Float, Inv, out data.startRotZ);
            double.TryParse(root.GetValue("startRotW"), NumberStyles.Float, Inv, out data.startRotW);

            foreach (string frameStr in root.GetValues("F"))
            {
                FlightInputFrame frame = StringToFrame(frameStr);
                if (frame != null) data.frames.Add(frame);
            }
            foreach (string eventStr in root.GetValues("E"))
            {
                FlightEventFrame evt = StringToEvent(eventStr);
                if (evt != null) data.events.Add(evt);
            }

            return data;
        }

        private string FrameToString(FlightInputFrame f)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(f.timeOffset.ToString(Inv)).Append(',');
            sb.Append(f.posX.ToString(Inv)).Append(',');
            sb.Append(f.posY.ToString(Inv)).Append(',');
            sb.Append(f.posZ.ToString(Inv)).Append(',');
            sb.Append(f.rotX.ToString(Inv)).Append(',');
            sb.Append(f.rotY.ToString(Inv)).Append(',');
            sb.Append(f.rotZ.ToString(Inv)).Append(',');
            sb.Append(f.rotW.ToString(Inv)).Append(',');
            sb.Append(f.pitch.ToString(Inv)).Append(',');
            sb.Append(f.roll.ToString(Inv)).Append(',');
            sb.Append(f.yaw.ToString(Inv)).Append(',');
            sb.Append(f.mainThrottle.ToString(Inv)).Append(',');
            sb.Append(f.X.ToString(Inv)).Append(',');
            sb.Append(f.Y.ToString(Inv)).Append(',');
            sb.Append(f.Z.ToString(Inv)).Append(',');
            sb.Append(f.wheelSteer.ToString(Inv)).Append(',');
            sb.Append(f.wheelThrottle.ToString(Inv)).Append(',');
            sb.Append(f.sas ? '1' : '0').Append(',');
            sb.Append(f.rcs ? '1' : '0').Append(',');
            sb.Append(f.gear ? '1' : '0').Append(',');
            sb.Append(f.light ? '1' : '0').Append(',');
            sb.Append(f.brakes ? '1' : '0').Append(',');
            sb.Append(f.precisionMode ? '1' : '0').Append(',');
            for (int i = 0; i < 10; i++)
            {
                sb.Append(f.customActionGroups != null && f.customActionGroups.Length > i && f.customActionGroups[i] ? '1' : '0');
            }
            sb.Append(',');
            sb.Append(f.altitude.ToString(Inv)).Append(',');
            sb.Append(f.speed.ToString(Inv)).Append(',');
            sb.Append(f.gForce.ToString(Inv)).Append(',');
            sb.Append(f.srfVelX.ToString(Inv)).Append(',');
            sb.Append(f.srfVelY.ToString(Inv)).Append(',');
            sb.Append(f.srfVelZ.ToString(Inv)).Append(',');
            sb.Append(f.obtVelX.ToString(Inv)).Append(',');
            sb.Append(f.obtVelY.ToString(Inv)).Append(',');
            sb.Append(f.obtVelZ.ToString(Inv)).Append(',');
            sb.Append(f.obtSpeed.ToString(Inv));

            StringBuilder wheelSb = new StringBuilder();
            if (f.wheelData != null)
            {
                for (int w = 0; w < f.wheelData.Length; w++)
                {
                    if (w > 0) wheelSb.Append(';');
                    wheelSb.Append(f.wheelData[w].partPersistentId).Append(':')
                           .Append(f.wheelData[w].suspensionY.ToString(Inv)).Append(':')
                           .Append(f.wheelData[w].wheelRotationX.ToString(Inv)).Append(':')
                           .Append(f.wheelData[w].craftID).Append(':')
                           .Append(f.wheelData[w].partIndex);
                }
            }
            sb.Append(',').Append(wheelSb.ToString());

            return sb.ToString();
        }

        private FlightInputFrame StringToFrame(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] p = s.Split(',');
            if (p.Length < 27) return null;

            try
            {
                FlightInputFrame f = new FlightInputFrame
                {
                    timeOffset = double.Parse(p[0], Inv),
                    posX = double.Parse(p[1], Inv),
                    posY = double.Parse(p[2], Inv),
                    posZ = double.Parse(p[3], Inv),
                    rotX = double.Parse(p[4], Inv),
                    rotY = double.Parse(p[5], Inv),
                    rotZ = double.Parse(p[6], Inv),
                    rotW = double.Parse(p[7], Inv),
                    pitch = float.Parse(p[8], Inv),
                    roll = float.Parse(p[9], Inv),
                    yaw = float.Parse(p[10], Inv),
                    mainThrottle = float.Parse(p[11], Inv),
                    X = float.Parse(p[12], Inv),
                    Y = float.Parse(p[13], Inv),
                    Z = float.Parse(p[14], Inv),
                    wheelSteer = float.Parse(p[15], Inv),
                    wheelThrottle = float.Parse(p[16], Inv),
                    sas = p[17] == "1",
                    rcs = p[18] == "1",
                    gear = p[19] == "1",
                    light = p[20] == "1",
                    brakes = p[21] == "1",
                    precisionMode = p[22] == "1",
                    altitude = float.Parse(p[24], Inv),
                    speed = float.Parse(p[25], Inv),
                    gForce = float.Parse(p[26], Inv)
                };

                string cag = p[23];
                for (int i = 0; i < 10 && i < cag.Length; i++)
                {
                    f.customActionGroups[i] = cag[i] == '1';
                }

                int wheelIndex = 27;

                if (p.Length >= 34)
                {
                    f.srfVelX = double.Parse(p[27], Inv);
                    f.srfVelY = double.Parse(p[28], Inv);
                    f.srfVelZ = double.Parse(p[29], Inv);
                    f.obtVelX = double.Parse(p[30], Inv);
                    f.obtVelY = double.Parse(p[31], Inv);
                    f.obtVelZ = double.Parse(p[32], Inv);
                    f.obtSpeed = float.Parse(p[33], Inv);
                    wheelIndex = 34;
                }

                if (p.Length > wheelIndex && !string.IsNullOrEmpty(p[wheelIndex]))
                {
                    string[] wheels = p[wheelIndex].Split(';');
                    f.wheelData = new WheelFrameData[wheels.Length];
                    for (int w = 0; w < wheels.Length; w++)
                    {
                        string[] wParts = wheels[w].Split(':');
                        if (wParts.Length >= 3)
                        {
                            uint.TryParse(wParts[0], NumberStyles.Integer, Inv, out f.wheelData[w].partPersistentId);
                            float.TryParse(wParts[1], NumberStyles.Float, Inv, out f.wheelData[w].suspensionY);
                            float.TryParse(wParts[2], NumberStyles.Float, Inv, out f.wheelData[w].wheelRotationX);
                            if (wParts.Length >= 5)
                            {
                                uint.TryParse(wParts[3], NumberStyles.Integer, Inv, out f.wheelData[w].craftID);
                                int.TryParse(wParts[4], NumberStyles.Integer, Inv, out f.wheelData[w].partIndex);
                            }
                        }
                    }
                }

                return f;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private string EventToString(FlightEventFrame e)
        {
            return string.Join("|", new[]
            {
                e.timeOffset.ToString(Inv),
                e.partPersistentId.ToString(Inv),
                e.moduleName ?? string.Empty,
                e.eventName ?? string.Empty,
                e.eventType ?? "EVENT",
                e.fieldName ?? string.Empty,
                e.fieldValue ?? string.Empty,
                e.craftID.ToString(Inv),
                e.partIndex.ToString(Inv)
            });
        }

        private FlightEventFrame StringToEvent(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] p = s.Split('|');
            if (p.Length < 4) return null;

            try
            {
                FlightEventFrame evt = new FlightEventFrame
                {
                    timeOffset = double.Parse(p[0], Inv),
                    partPersistentId = uint.Parse(p[1], Inv),
                    moduleName = p[2],
                    eventName = p[3],
                    executed = false
                };

                if (p.Length >= 7)
                {
                    evt.eventType = p[4];
                    evt.fieldName = p[5];
                    evt.fieldValue = p[6];
                }

                if (p.Length >= 9)
                {
                    uint.TryParse(p[7], NumberStyles.Integer, Inv, out evt.craftID);
                    int.TryParse(p[8], NumberStyles.Integer, Inv, out evt.partIndex);
                }

                return evt;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public void DeleteSessionFromDisk(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    RefreshSavedFilesList();
                    windowRect.height = 0;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FlightRecorder] Delete failed: {ex.Message}");
                }
            }
        }

        public void RenameSessionFile(string oldPath, string newName)
        {
            if (!File.Exists(oldPath)) return;

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                newName = newName.Replace(invalidChar, '_');
            }
            newName = newName.Trim();
            if (newName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            {
                newName = newName.Substring(0, newName.Length - 4);
            }
            if (string.IsNullOrEmpty(newName)) return;

            string dir = Path.GetDirectoryName(oldPath);
            string newPath = Path.Combine(dir, newName + ".cfg");
            if (string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase)) return;

            if (File.Exists(newPath))
            {
                ScreenMessages.PostScreenMessage("[FlightRecorder] Rename failed: a file with that name already exists.", 3.0f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            try
            {
                File.Move(oldPath, newPath);
                RefreshSavedFilesList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FlightRecorder] Rename failed: {ex.Message}");
            }
        }

        #endregion

        #region graph rendering

        private void ClearGraphTexture()
        {
            Color background = new Color(0.1f, 0.1f, 0.12f, 1f);
            Color[] pixels = new Color[graphTexWidth * graphTexHeight];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

            graphTexture.SetPixels(pixels);
            graphTexture.Apply();
        }

        private void RebuildGraphTexture()
        {
            if (CurrentSession == null || CurrentSession.frames == null || CurrentSession.frames.Count < 2)
            {
                ClearGraphTexture();
                return;
            }

            Color bg = new Color(0.12f, 0.12f, 0.14f, 1f);
            Color gridColor = new Color(0.2f, 0.2f, 0.22f, 1f);
            Color cyan = new Color(0f, 0.85f, 1f, 1f);
            Color green = new Color(0.2f, 0.9f, 0.3f, 1f);
            Color red = new Color(0.95f, 0.25f, 0.25f, 1f);

            Color[] pixels = new Color[graphTexWidth * graphTexHeight];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

            for (int x = 0; x < graphTexWidth; x += 55)
            {
                for (int y = 0; y < graphTexHeight; y++)
                    pixels[y * graphTexWidth + x] = gridColor;
            }
            for (int y = 0; y < graphTexHeight; y += 20)
            {
                for (int x = 0; x < graphTexWidth; x++)
                    pixels[y * graphTexWidth + x] = gridColor;
            }

            float maxSpeed = 10f, maxAlt = 10f, maxG = 2f;
            List<FlightInputFrame> frames = CurrentSession.frames;
            int totalFrames = frames.Count;

            for (int i = 0; i < totalFrames; i++)
            {
                if (frames[i].speed > maxSpeed) maxSpeed = frames[i].speed;
                if (frames[i].altitude > maxAlt) maxAlt = frames[i].altitude;
                if (frames[i].gForce > maxG) maxG = frames[i].gForce;
            }

            double totalTime = CurrentSession.duration;
            if (totalTime <= 0.001) totalTime = 1.0;

            for (int i = 0; i < totalFrames - 1; i++)
            {
                FlightInputFrame f1 = frames[i];
                FlightInputFrame f2 = frames[i + 1];

                int x1 = Mathf.Clamp((int)((f1.timeOffset / totalTime) * (graphTexWidth - 1)), 0, graphTexWidth - 1);
                int x2 = Mathf.Clamp((int)((f2.timeOffset / totalTime) * (graphTexWidth - 1)), 0, graphTexWidth - 1);

                int ySpd1 = Mathf.Clamp((int)((f1.speed / maxSpeed) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                int ySpd2 = Mathf.Clamp((int)((f2.speed / maxSpeed) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                DrawPixelLine(pixels, graphTexWidth, graphTexHeight, x1, ySpd1, x2, ySpd2, cyan);

                int yAlt1 = Mathf.Clamp((int)((f1.altitude / maxAlt) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                int yAlt2 = Mathf.Clamp((int)((f2.altitude / maxAlt) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                DrawPixelLine(pixels, graphTexWidth, graphTexHeight, x1, yAlt1, x2, yAlt2, green);

                int yG1 = Mathf.Clamp((int)(Mathf.Clamp01(f1.gForce / maxG) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                int yG2 = Mathf.Clamp((int)(Mathf.Clamp01(f2.gForce / maxG) * (graphTexHeight - 1)), 0, graphTexHeight - 1);
                DrawPixelLine(pixels, graphTexWidth, graphTexHeight, x1, yG1, x2, yG2, red);
            }

            graphTexture.SetPixels(pixels);
            graphTexture.Apply();
        }

        private void DrawPixelLine(Color[] pixels, int width, int height, int x0, int y0, int x1, int y1, Color col)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy, e2;

            while (true)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                {
                    pixels[y0 * width + x0] = col;
                }
                if (x0 == x1 && y0 == y1) break;
                e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        #endregion

        #region toolbar + camera lock + additional gui rendering

        private void OnGUIApplicationLauncherReady()
        {
            if (ApplicationLauncher.Ready && ApplicationLauncher.Instance != null && toolbarButton == null)
            {
                try
                {
                    toolbarIconTexture = GetOrCreateToolbarIcon();

                    toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                        () => { showGUI = true; },
                        () => { showGUI = false; ReleaseCameraLock(); },
                        null, null, null, null,
                        ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                        toolbarIconTexture
                    );

                    if (toolbarButton != null && showGUI)
                    {
                        toolbarButton.SetTrue(false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FlightRecorder] Failed to create toolbar button: {ex}");
                }
            }
        }

        private void OnGUIApplicationLauncherDestroyed()
        {
            if (toolbarButton != null)
            {
                try
                {
                    if (ApplicationLauncher.Instance != null)
                    {
                        ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FlightRecorder] Toolbar button destroy notice: {ex.Message}");
                }
                toolbarButton = null;
            }
        }

        public void ToggleGUI()
        {
            showGUI = !showGUI;
            if (!showGUI) ReleaseCameraLock();

            if (toolbarButton != null)
            {
                if (showGUI) toolbarButton.SetTrue(false);
                else toolbarButton.SetFalse(false);
            }
        }

        private void UpdateCameraLock()
        {
            bool mouseOverGUI = showGUI && HighLogic.LoadedSceneIsFlight && windowRect.Contains(Event.current.mousePosition);
            if (mouseOverGUI && !isCamLocked)
            {
                InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, CAM_LOCK_ID);
                isCamLocked = true;
            }
            else if (!mouseOverGUI && isCamLocked)
            {
                ReleaseCameraLock();
            }
        }

        private void ReleaseCameraLock()
        {
            if (isCamLocked)
            {
                InputLockManager.RemoveControlLock(CAM_LOCK_ID);
                isCamLocked = false;
            }
        }

        private Texture2D CreateDefaultToolbarIcon()
        {
            Texture2D tex = new Texture2D(38, 38, TextureFormat.RGBA32, false);
            Color transparent = new Color(0, 0, 0, 0);
            Color red = new Color(0.95f, 0.2f, 0.2f, 1f);
            Color white = new Color(1f, 1f, 1f, 1f);

            for (int y = 0; y < 38; y++)
            {
                for (int x = 0; x < 38; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(19, 19));
                    if (dist <= 8.5f) tex.SetPixel(x, y, red);
                    else if (dist <= 14.5f && dist >= 12f) tex.SetPixel(x, y, white);
                    else tex.SetPixel(x, y, transparent);
                }
            }
            tex.Apply();
            return tex;
        }

        private Texture2D GetOrCreateToolbarIcon()
        {
            if (GameDatabase.Instance != null)
            {
                Texture2D gdbTex = GameDatabase.Instance.GetTexture("FlightRecorder/Icons/icon", false)
                                ?? GameDatabase.Instance.GetTexture("FlightRecorder/icons/icon", false)
                                ?? GameDatabase.Instance.GetTexture("FlightRecorder/Icons/Icon", false);
                if (gdbTex != null) return gdbTex;
            }

            try
            {
                string[] possiblePaths = new[]
                {
                    Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "FlightRecorder", "Icons", "icon.png"),
                    Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "FlightRecorder", "icons", "icon.png"),
                    Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "flightrecorder", "icons", "icon.png")
                };

                for (int p = 0; p < possiblePaths.Length; p++)
                {
                    string diskPath = possiblePaths[p];
                    if (File.Exists(diskPath))
                    {
                        byte[] fileBytes = File.ReadAllBytes(diskPath);
                        Texture2D diskTex = new Texture2D(38, 38, TextureFormat.RGBA32, false);

                        Type imgConv = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")
                                       ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine");

                        if (imgConv != null)
                        {
                            MethodInfo loadMethod = imgConv.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                            if (loadMethod != null)
                            {
                                bool success = (bool)loadMethod.Invoke(null, new object[] { diskTex, fileBytes });
                                if (success)
                                {
                                    diskTex.Apply();
                                    return diskTex;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return toolbarIconTexture != null ? toolbarIconTexture : CreateDefaultToolbarIcon();
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };
            headerStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                richText = true
            };
            labelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            saveFileStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
            saveFileStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f);

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!showGUI || !HighLogic.LoadedSceneIsFlight)
            {
                ReleaseCameraLock();
                return;
            }

            UpdateCameraLock();
            InitializeStyles();

            if (showFileList != lastShowFileList)
            {
                windowRect.height = 0;
                lastShowFileList = showFileList;
            }

            windowRect = GUILayout.Window(windowID, windowRect, DrawHorizontalWindow, $"FlightRecorder {MOD_VERSION}", GUI.skin.window, GUILayout.Width(480));
        }

        private void DrawHorizontalWindow(int id)
        {
            GUILayout.BeginVertical();

            // TOP HEADER BAR
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.BeginVertical();

            string rawName = CurrentSession != null ? CurrentSession.craftName : (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : "None");
            string craftName = Localizer.Format(rawName);
            string recordedUTStr = CurrentSession != null ? KSPUtil.PrintDateCompact(CurrentSession.startUT, true, true) : "N/A";
            string durationStr = CurrentSession != null ? FormatDuration(CurrentSession.duration) : "0.0s";

            GUILayout.Label($"Craft: <b>{craftName}</b>", labelStyle);
            GUILayout.Label($"Start: <b>{recordedUTStr}</b>   |   Duration: <b>{durationStr}</b>", labelStyle);

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical();
            Color statusColor = CurrentState switch
            {
                RecorderState.Recording => Color.red,
                RecorderState.Playback => Color.green,
                RecorderState.Paused => Color.yellow,
                _ => Color.gray
            };
            GUI.color = statusColor;
            GUILayout.Label($"● {CurrentState}", headerStyle, GUILayout.Width(75));
            GUI.color = Color.white;
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            // CONTROL BUTTONS BAR
            GUILayout.BeginHorizontal();

            if (CurrentState == RecorderState.Idle)
            {
                if (GUILayout.Button("Record", GUILayout.Width(70), GUILayout.Height(22)))
                    StartRecording();

                GUI.enabled = CurrentSession != null && CurrentSession.frames.Count > 0;
                if (GUILayout.Button("Play", GUILayout.Width(55), GUILayout.Height(22)))
                    StartPlayback();

                if (GUILayout.Button("Save", GUILayout.Width(55), GUILayout.Height(22)))
                    SaveCurrentSessionToDisk();
                GUI.enabled = true;
            }
            else
            {
                if (GUILayout.Button("Stop", GUILayout.Width(60), GUILayout.Height(22)))
                    StopRecordingOrPlayback();

                if (CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused)
                {
                    string pauseLabel = CurrentState == RecorderState.Paused ? "Resume" : "Pause";
                    if (GUILayout.Button(pauseLabel, GUILayout.Width(65), GUILayout.Height(22)))
                        PausePlayback();
                }
            }

            string filesBtnText = showFileList ? "Hide Files" : "Files";
            if (GUILayout.Button(filesBtnText, GUILayout.Width(80), GUILayout.Height(22)))
            {
                showFileList = !showFileList;
                if (showFileList) RefreshSavedFilesList();
            }

            revertInGameTimeOnPlayback = GUILayout.Toggle(revertInGameTimeOnPlayback, "Revert Time", GUILayout.Height(22));
            enableF7Hotkey = GUILayout.Toggle(enableF7Hotkey, "Open with F7", GUILayout.Height(22));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (showFileList)
            {
                DrawSavedFilesPanel();
            }

            DrawTelemetryGraph();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawSavedFilesPanel()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("Saved Flight Recordings:", headerStyle);

            if (savedFilePaths.Count == 0)
            {
                GUILayout.Label("No recordings found.", labelStyle);
            }
            else
            {
                int itemHeight = 28;
                int contentHeight = savedFilePaths.Count * itemHeight + 2;
                int scrollHeight = Mathf.Min(contentHeight, 150);

                fileListScrollPos = GUILayout.BeginScrollView(fileListScrollPos, GUILayout.Height(scrollHeight));

                string fileToDelete = null;

                for (int i = 0; i < savedFilePaths.Count; i++)
                {
                    string path = savedFilePaths[i];
                    string fileName = Path.GetFileNameWithoutExtension(path);

                    GUILayout.BeginHorizontal();

                    bool isRenamingThis = renamingFilePath == path;

                    if (isRenamingThis)
                    {
                        Event e = Event.current;
                        if (e.type == EventType.KeyDown)
                        {
                            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                            {
                                RenameSessionFile(path, renameTextBuffer);
                                renamingFilePath = null;
                                isRenamingThis = false;
                                e.Use();
                            }
                            else if (e.keyCode == KeyCode.Escape)
                            {
                                renamingFilePath = null;
                                isRenamingThis = false;
                                e.Use();
                            }
                        }
                    }

                    if (isRenamingThis)
                    {
                        GUI.SetNextControlName("RenameField");
                        renameTextBuffer = GUILayout.TextField(renameTextBuffer, saveFileStyle, GUILayout.Width(210));
                        if (GUI.GetNameOfFocusedControl() != "RenameField")
                        {
                            GUI.FocusControl("RenameField");
                        }
                    }
                    else
                    {
                        GUILayout.Label(fileName, saveFileStyle, GUILayout.Width(210));
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Select", GUILayout.Width(50), GUILayout.Height(22)))
                    {
                        LoadSessionFromDisk(path);
                        confirmDeleteFilePath = null;
                        renamingFilePath = null;
                    }

                    if (GUILayout.Button("Rename", GUILayout.Width(65), GUILayout.Height(22)))
                    {
                        renamingFilePath = path;
                        renameTextBuffer = fileName;
                        confirmDeleteFilePath = null;
                    }

                    if (confirmDeleteFilePath == path && Time.realtimeSinceStartup > confirmDeleteExpireTime)
                    {
                        confirmDeleteFilePath = null;
                    }

                    if (confirmDeleteFilePath == path)
                    {
                        GUI.color = Color.red;
                        if (GUILayout.Button("Delete?", GUILayout.Width(55), GUILayout.Height(22)))
                        {
                            fileToDelete = path;
                            confirmDeleteFilePath = null;
                        }
                        GUI.color = Color.white;
                    }
                    else
                    {
                        if (GUILayout.Button("Delete", GUILayout.Width(55), GUILayout.Height(22)))
                        {
                            confirmDeleteFilePath = path;
                            confirmDeleteExpireTime = Time.realtimeSinceStartup + 5f;
                        }
                    }

                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();

                if (!string.IsNullOrEmpty(fileToDelete))
                {
                    DeleteSessionFromDisk(fileToDelete);
                }
            }

            GUILayout.EndVertical();
        }

        private void DrawTelemetryGraph()
        {
            Rect graphRect = GUILayoutUtility.GetRect(graphTexWidth, graphTexHeight, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(graphRect, graphTexture);

            if (CurrentState == RecorderState.Recording)
            {
                GUIStyle overlayStyle = new GUIStyle(labelStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    richText = true
                };
                GUI.Label(graphRect, "<b><size=15><color=#FF4444>● RECORDING...</color></size></b>\n<size=11><color=#CCCCCC>Capturing flight telemetry</color></size>", overlayStyle);
                return;
            }

            if (CurrentSession == null || CurrentSession.frames == null || CurrentSession.frames.Count < 2)
            {
                GUIStyle centerLabel = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(graphRect, "No flight telemetry loaded.", centerLabel);
                return;
            }

            Event e = Event.current;
            if (e.isMouse && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
            {
                if (graphRect.Contains(e.mousePosition))
                {
                    float scrubRatio = (e.mousePosition.x - graphRect.x) / graphRect.width;
                    float newProgress = Mathf.Clamp01(scrubRatio) * (float)CurrentSession.duration;

                    if (Mathf.Abs(newProgress - (float)playbackProgressUT) > 0.01f)
                    {
                        playbackProgressUT = newProgress;
                        lastPlaybackProgressUT = newProgress;
                        currentPlaybackIndex = 0;
                        ResetEventExecutionFlags();

                        if (CurrentState == RecorderState.Playback || CurrentState == RecorderState.Paused)
                        {
                            if (revertInGameTimeOnPlayback && CurrentSession != null)
                            {
                                double targetUT = CurrentSession.startUT + playbackProgressUT;
                                Planetarium.SetUniversalTime(targetUT);

                                if (FlightGlobals.currentMainBody != null)
                                {
                                    FlightGlobals.currentMainBody.CBUpdate();
                                }
                            }

                            if (FlightGlobals.ActiveVessel != null)
                            {
                                ApplyPlaybackFrame(FlightGlobals.ActiveVessel, playbackProgressUT, 0.0);
                                SyncVesselVelocities();
                                UpdateNavballSpeedDisplay();
                            }
                        }
                    }
                    e.Use();
                }
            }

            double totalDur = Math.Max(0.001, CurrentSession.duration);
            float ratio = Mathf.Clamp01((float)(playbackProgressUT / totalDur));
            float needleX = graphRect.x + ratio * graphRect.width;
            GUI.color = Color.yellow;
            GUI.DrawTexture(new Rect(needleX, graphRect.y, 1.5f, graphRect.height), whiteTexture);
            GUI.color = Color.white;

            Rect legendRect = new Rect(graphRect.x + 6, graphRect.y + 4, 240, 14);
            GUI.Label(legendRect, "<color=#00D9FF>― Speed</color>   <color=#33E64D>― Altitude</color>   <color=#F24040>― G-Force</color>", labelStyle);
        }

        #endregion
    }
}