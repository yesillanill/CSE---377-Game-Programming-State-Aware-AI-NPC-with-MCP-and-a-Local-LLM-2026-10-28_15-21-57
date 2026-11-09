using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

public enum NpcRole
{
    GUARD,
    SCOUT,
    ASSAULT
}

[Serializable]
public class NpcState
{
    public string npcId = "guard_01";
    public string role = "GUARD";
    public float health = 90f;
    public int ammo = 10;
    public float enemyDistance = 5f;
    public int enemyCount = 1;
    public int nearbyAllies = 0;
    public bool coverAvailable = true;
    public bool allyNeedsHelp = false;
    public string currentAction = "ATTACK";
    public int stateVersion = 1;
}

[Serializable]
public class EnvironmentContext
{
    public float nearest_cover_distance = 3.5f;
    public bool objective_under_threat = false;
}

[Serializable]
public class DecisionRequestPayload
{
    public int stateVersion;
    public NpcState npc;
    public List<string> legalActions;
    public EnvironmentContext environment;
}

[Serializable]
public class DecisionResponsePayload
{
    public int stateVersion;
    public string action;
    public float confidence;
    public string reason;
    public List<string> toolsCalled;
    public int latencyMs;
}

public class GuardNPCController : MonoBehaviour
{
    // Section 17: Multi-Agent Cooperation static event
    public static event Action<string, Vector3> OnCallForHelpTriggered;
    private static int lastManualHelpFrame = -1;

    [Header("Server Settings")]
    public string apiUrl = "http://127.0.0.1:8000/decide";
    public string warmupUrl = "http://127.0.0.1:8000/warmup";
    public float decisionInterval = 4.0f;
    [Tooltip("HTTP timeout in seconds (set to 25s by default so local 8B LLM tool-calling completes without premature timeout).")]
    public int requestTimeoutSeconds = 25;
    [Header("Local AI Startup")]
    [Tooltip("Starts the local Python agent and Ollama model automatically when the game begins.")]
    public bool autoStartLocalAI = true;
    [Tooltip("Model passed to Ollama. Keep this aligned with PythonAgent/agent.py.")]
    public string localAiModel = "llama3.1:8b";
    [Tooltip("Executable resolved from PATH. Use python on Windows/Linux and python3 on macOS if needed.")]
    public string pythonCommand = "python";
    [Tooltip("Optional project-relative or absolute PythonAgent directory. Empty resolves ../../PythonAgent from this Unity project.")]
    public string pythonAgentDirectory = "";
    [Tooltip("Maximum time allowed for the first Ollama model load or download.")]
    public int warmupTimeoutSeconds = 120;

    [Header("Role & Personality (Section 16)")]
    [Tooltip("Selects the NPC tactical personality (GUARD, SCOUT, ASSAULT) sent to the MCP server and local LLM.")]
    public NpcRole npcRole = NpcRole.GUARD;

    [Header("NPC Tactical State")]
    public NpcState state = new NpcState();
    public EnvironmentContext envContext = new EnvironmentContext();
    public int maxAmmo = 10;

    [Header("Combat, Cover Recovery & Cooperation Settings")]
    [Tooltip("Enables periodic incoming enemy fire damage when enemies have an unobstructed line of sight to the NPC.")]
    public bool enableEnemyCombatDamage = true;
    [Tooltip("Interval in seconds between incoming enemy ranged attacks.")]
    public float enemyAttackInterval = 2.8f;
    [Tooltip("Interval in seconds between Guard shots when engaging enemies.")]
    public float guardAttackFireInterval = 0.9f;
    [Tooltip("Health recovered per second while shielded behind cover.")]
    public float coverHealRatePerSecond = 12.0f;
    [Tooltip("Interval in seconds for the existing Ally in combat to autonomously request help from the Guard.")]
    public float allyAutoHelpRequestInterval = 12.0f;

    [Header("Unit Prefabs (Assets/Prefabs)")]
    public GameObject enemyPrefab;
    public GameObject allyPrefab;

    [Header("Scene References (Existing Scene Objects)")]
    public Transform enemyTransform;
    public Transform allyTransform;
    public Transform coverTransform;
    public Transform eastCoverTransform;
    public Transform retreatPoint;
    public float moveSpeed = 4.2f;

    [Header("Tactical Camera Settings")]
    [Tooltip("Applies fixed elevated top-down/isometric camera settings (55 deg pitch, 50 deg FOV) so all 4 arena walls and units are visible.")]
    public bool configureMainCamera = true;
    public Vector3 cameraPosition = new Vector3(0f, 16.5f, -12.0f);
    public Vector3 cameraEulerAngles = new Vector3(55f, 0f, 0f);
    public float cameraFieldOfView = 50f;

    [Header("HUD & Debug Display")]
    public bool showHud = true;
    public bool showWorldLabels = false;

    private int currentStateVersion = 1;
    private bool isWaitingForDecision = false;
    private bool pendingDecisionRequest = false;
    private float decisionTimer = 0f;
    private float proximityDamageTimer = 0f;
    private float combatFireDamageTimer = 0f;
    private float guardAttackFireTimer = 0f;
    private float autoReloadTimer = 0f;
    private float allyDistressTimer = 0f;
    private float allySupportMoveTimer = 0f;
    private float timeSpentInCover = 0f;
    private bool hasCalledHelpDuringCurrentCover = false;
    private Vector3 initialGuardPosition = new Vector3(0f, 1f, 0f);
    private Vector3 initialPrimaryEnemyPosition = new Vector3(0f, 1f, 5f);
    private Vector3 initialAllyPosition = new Vector3(2.4f, 1f, -1.6f);
    private readonly List<Vector3> initialAllyPositions = new List<Vector3>();
    private Vector3 distressedAllyPosition = new Vector3(2.4f, 1f, -1.6f);
    private Vector3 targetPosition;
    private Renderer npcRenderer;

    // Additional enemies for Scenarios B/D/E; Allies are never spawned dynamically (only existing scene Allies are used)
    private readonly List<GameObject> extraEnemyUnits = new List<GameObject>();
    private readonly List<Transform> existingSceneAllies = new List<Transform>();

    // Health and cover state tracking for existing Allies and active Enemies
    private readonly List<float> allyHealths = new List<float>();
    private readonly List<bool> allyInCoverFlags = new List<bool>();
    private readonly List<float> allyCoverTimers = new List<float>();
    private readonly List<float> enemyHealths = new List<float>();
    private readonly List<bool> enemyInCoverFlags = new List<bool>();
    private readonly List<float> enemyCoverTimers = new List<float>();
    private float allyCombatFireTimer = 0f;
    private int roleStrafeDirection = 1;

    // Cooperation tracking: each Ally help call gets an id so allyNeedsHelp stays true until the LLM has seen it
    private int allyHelpRequestId = 0;
    private int distressedAllyIndex = -1;
    private const float AllySupportDuration = 8.0f;
    private const float AllySupportVolleyInterval = 0.6f;
    private const float AllyNormalVolleyInterval = 1.25f;

    // Snapshot fields for event-driven decision triggers
    private NpcRole prevNpcRole;
    private float prevHealth;
    private int prevAmmo;
    private int prevEnemyCount;
    private int prevNearbyAllies;
    private bool prevCoverAvailable;
    private bool prevAllyNeedsHelp;
    private string prevRole;
    private bool prevObjectiveUnderThreat;
    private bool wasMovingToTarget = false;

    // HUD telemetry state
    private string aiServiceStatus = "initializing...";
    private string activeScenarioLabel = "Initial State (Scenario A)";
    private string lastSelectedAction = "NONE";
    private string lastExecutedAction = "ATTACK";
    private float lastConfidence = 0f;
    private string lastReason = "Waiting for first tactical decision...";
    private int lastLatencyMs = 0;
    private string lastValidationResult = "PENDING";
    private List<string> lastToolsCalled = new List<string>();
    private DiagnosticsProcess ollamaProcess;
    private DiagnosticsProcess agentProcess;
    private bool localAiStartupAttempted;

    private static readonly Vector3[] ExtraEnemySlots = new Vector3[]
    {
        new Vector3(-2.6f, 1f, 5.0f),
        new Vector3(2.6f, 1f, 5.0f),
        new Vector3(-4.8f, 1f, 4.4f),
        new Vector3(4.8f, 1f, 4.4f),
        new Vector3(0.0f, 1f, 6.4f)
    };

    private void OnValidate()
    {
        if (state != null)
        {
            state.role = npcRole.ToString();
        }
    }

    private void OnEnable()
    {
        OnCallForHelpTriggered += HandleAllyCallForHelp;
    }

    private void OnDisable()
    {
        OnCallForHelpTriggered -= HandleAllyCallForHelp;
    }

    private void Start()
    {
        npcRenderer = GetComponent<Renderer>();
        initialGuardPosition = transform.position;
        targetPosition = transform.position;

        if (requestTimeoutSeconds < 15)
        {
            requestTimeoutSeconds = 25;
        }
        if (decisionInterval < 2.0f)
        {
            decisionInterval = 4.0f;
        }
        if (moveSpeed < 3.5f)
        {
            moveSpeed = 4.2f;
        }

        // Upgrade legacy serialized camera positions so all 4 rectangular walls fit cleanly in view
        if (cameraPosition.y < 15.5f || cameraPosition.z > -11.0f)
        {
            cameraPosition = new Vector3(0f, 16.5f, -12.0f);
        }

        if (eastCoverTransform == null)
        {
            GameObject eastCoverObj = GameObject.Find("CoverCubeEast");
            if (eastCoverObj != null) eastCoverTransform = eastCoverObj.transform;
        }
        if (coverTransform == null)
        {
            GameObject coverObj = GameObject.Find("CoverCube");
            if (coverObj != null) coverTransform = coverObj.transform;
        }
        if (retreatPoint == null)
        {
            GameObject retObj = GameObject.Find("RetreatPoint");
            if (retObj != null) retreatPoint = retObj.transform;
        }
        if (enemyTransform == null)
        {
            GameObject existingEnemy = GameObject.Find("Enemy");
            if (existingEnemy != null)
            {
                enemyTransform = existingEnemy.transform;
            }
            else if (enemyPrefab != null)
            {
                GameObject spawned = Instantiate(enemyPrefab, new Vector3(0f, 1f, 5f), Quaternion.identity);
                spawned.name = "Enemy";
                enemyTransform = spawned.transform;
            }
        }

        if (enemyTransform != null)
        {
            initialPrimaryEnemyPosition = enemyTransform.position;
        }

        DiscoverExistingSceneAllies();
        // Scene 1 (Initial Scenario A) starts with 0 allies
        state.nearbyAllies = 0;

        state.role = npcRole.ToString();
        prevNpcRole = npcRole;
        prevRole = state.role;
        state.stateVersion = currentStateVersion;

        ApplyCameraConfiguration();
        ApplySceneVisualContrast();
        SyncSceneUnitsVisuals();
        UpdateDynamicDistances();
        SyncStateSnapshot();

        // Start in active combat posture if health and ammo are high
        if (state.health >= 60f && state.ammo > 0 && state.enemyCount > 0)
        {
            ExecuteAction("ATTACK");
        }

        // Preload local LLM into memory on startup and immediately request the first tactical decision
        StartCoroutine(WarmupAndStartDecision());
    }

    private void DiscoverExistingSceneAllies()
    {
        existingSceneAllies.Clear();
        initialAllyPositions.Clear();

        if (allyTransform == null)
        {
            GameObject allyObj = GameObject.Find("Ally");
            if (allyObj == null) allyObj = GameObject.Find("AllyNPC");
            if (allyObj == null) allyObj = GameObject.Find("Ally_Unit_1");
            if (allyObj != null) allyTransform = allyObj.transform;
        }

        if (allyTransform != null && !existingSceneAllies.Contains(allyTransform))
        {
            existingSceneAllies.Add(allyTransform);
            initialAllyPosition = allyTransform.position;
            initialAllyPositions.Add(allyTransform.position);
        }

        // Also collect any other Ally objects already placed in the scene (e.g., Ally2)
        GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < rootObjects.Length; i++)
        {
            Transform[] children = rootObjects[i].GetComponentsInChildren<Transform>(true);
            for (int j = 0; j < children.Length; j++)
            {
                Transform tr = children[j];
                // Skip UI elements (e.g., the HUD's "AllyHelpButton") so they are never treated as Ally units
                if (tr != null && tr.gameObject != gameObject &&
                    tr.name.StartsWith("Ally", StringComparison.OrdinalIgnoreCase) &&
                    !(tr is RectTransform) && tr.GetComponentInParent<Canvas>(true) == null &&
                    !existingSceneAllies.Contains(tr))
                {
                    existingSceneAllies.Add(tr);
                    initialAllyPositions.Add(tr.position);
                }
            }
        }

        EnsureUnitHealthListsSynced();
    }

    private void EnsureUnitHealthListsSynced()
    {
        while (allyHealths.Count < existingSceneAllies.Count)
        {
            allyHealths.Add(90f);
            allyInCoverFlags.Add(false);
            allyCoverTimers.Add(0f);
        }

        int totalEnemies = Mathf.Max(1, state.enemyCount);
        while (enemyHealths.Count < totalEnemies)
        {
            enemyHealths.Add(100f);
            enemyInCoverFlags.Add(false);
            enemyCoverTimers.Add(0f);
        }
    }

    private IEnumerator WarmupAndStartDecision()
    {
        if (autoStartLocalAI)
        {
            yield return StartCoroutine(EnsureLocalAIStarted());
        }

        yield return StartCoroutine(WarmupModel());
        RequestTacticalDecision();
    }

    private IEnumerator EnsureLocalAIStarted()
    {
        if (localAiStartupAttempted)
        {
            yield break;
        }

        localAiStartupAttempted = true;

        bool agentAvailable = false;
        using (UnityWebRequest probe = UnityWebRequest.Get(apiUrl.Replace("/decide", "/")))
        {
            probe.timeout = 2;
            yield return probe.SendWebRequest();
            agentAvailable = probe.result == UnityWebRequest.Result.Success;
        }

        if (!agentAvailable)
        {
            StartOllamaProcess();
            StartPythonAgentProcess();
        }

        // Give the agent process time to bind port 8000 before WarmupModel makes its request.
        float deadline = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < deadline)
        {
            using (UnityWebRequest probe = UnityWebRequest.Get(apiUrl.Replace("/decide", "/")))
            {
                probe.timeout = 2;
                yield return probe.SendWebRequest();
                if (probe.result == UnityWebRequest.Result.Success)
                {
                    yield break;
                }
            }

            yield return new WaitForSecondsRealtime(0.5f);
        }

        UnityEngine.Debug.LogWarning("[AI Startup] Local agent did not become available within 20 seconds. Warmup will report the exact connection error.");
    }

    private void StartOllamaProcess()
    {
        if (ollamaProcess != null && !ollamaProcess.HasExited)
        {
            return;
        }

        try
        {
            ProcessStartInfo startInfo = CreateProcessStartInfo(
                "ollama",
                $"run {localAiModel}",
                GetProjectRootDirectory());
            ollamaProcess = DiagnosticsProcess.Start(startInfo);
            UnityEngine.Debug.Log($"[AI Startup] Started Ollama model '{localAiModel}'.");
        }
        catch (System.Exception exception)
        {
            UnityEngine.Debug.LogError($"[AI Startup] Could not start Ollama from PATH: {exception.Message}");
        }
    }

    private void StartPythonAgentProcess()
    {
        if (agentProcess != null && !agentProcess.HasExited)
        {
            return;
        }

        string agentDirectory = ResolvePythonAgentDirectory();
        if (!File.Exists(Path.Combine(agentDirectory, "agent.py")))
        {
            UnityEngine.Debug.LogError($"[AI Startup] Python agent was not found at '{agentDirectory}'.");
            return;
        }

        try
        {
            ProcessStartInfo startInfo = CreateProcessStartInfo(
                pythonCommand,
                "-m uvicorn agent:app --host 127.0.0.1 --port 8000",
                agentDirectory);
            startInfo.EnvironmentVariables["OLLAMA_MODEL"] = localAiModel;
            agentProcess = DiagnosticsProcess.Start(startInfo);
            UnityEngine.Debug.Log($"[AI Startup] Started FastAPI agent from '{agentDirectory}'.");
        }
        catch (System.Exception exception)
        {
            if (pythonCommand == "python")
            {
                try
                {
                    ProcessStartInfo fallbackStartInfo = CreateProcessStartInfo(
                        "python3",
                        "-m uvicorn agent:app --host 127.0.0.1 --port 8000",
                        agentDirectory);
                    fallbackStartInfo.EnvironmentVariables["OLLAMA_MODEL"] = localAiModel;
                    agentProcess = DiagnosticsProcess.Start(fallbackStartInfo);
                    UnityEngine.Debug.Log("[AI Startup] Started FastAPI agent with fallback command 'python3'.");
                    return;
                }
                catch (System.Exception fallbackException)
                {
                    UnityEngine.Debug.LogError($"[AI Startup] Could not start Python agent with 'python' or 'python3': {fallbackException.Message}");
                    return;
                }
            }

            UnityEngine.Debug.LogError($"[AI Startup] Could not start Python agent with '{pythonCommand}': {exception.Message}");
        }
    }

    private ProcessStartInfo CreateProcessStartInfo(string fileName, string arguments, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
    }

    private string ResolvePythonAgentDirectory()
    {
        if (!string.IsNullOrWhiteSpace(pythonAgentDirectory))
        {
            return Path.GetFullPath(pythonAgentDirectory);
        }

        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "PythonAgent"));
    }

    private string GetProjectRootDirectory()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    }

    private void OnApplicationQuit()
    {
        StopOwnedProcess(agentProcess);
        StopOwnedProcess(ollamaProcess);
    }

    private static void StopOwnedProcess(DiagnosticsProcess process)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill();
            process.Dispose();
        }
        catch (System.Exception exception)
        {
            UnityEngine.Debug.LogWarning($"[AI Shutdown] Could not stop owned process: {exception.Message}");
        }
    }

    private void Update()
    {
        // Bidirectional synchronization between npcRole (Inspector enum) and state.role (Inspector string)
        SyncRoleBidirectional();

        // Keep existing scene units and cover objects synced
        SyncSceneUnitsVisuals();

        // Unity executes per-frame movement, distance measurement, combat firing, enemy damage, cover recovery, and ally/enemy cover behavior
        EnsureUnitHealthListsSynced();
        ExecuteContinuousMovement();
        UpdateExistingAllyMovements();
        UpdateActiveEnemyMovementsAndCover();
        UpdateDynamicDistances();
        UpdateGuardCombatFiring();
        UpdateEnemyDamage();
        UpdateCoverRecoveryAndCooperation();

        // Keyboard shortcuts (1-6, B, H, J, R, C, F1) for testing Scenarios A-E, damage, cooperation, role, and HUD
        HandleScenarioHotkeys();

        // Event-driven triggers (role change, ally help, cover changed, health change, enemy count, ammo empty, objective changed)
        CheckStateChangeEvents();

        // Periodic timer trigger (every 2-5 seconds, default 4.0s; never called every frame)
        decisionTimer += Time.deltaTime;
        if (decisionTimer >= decisionInterval && !isWaitingForDecision)
        {
            decisionTimer = 0f;
            RequestTacticalDecision();
        }
    }

    private void SyncRoleBidirectional()
    {
        if (npcRole != prevNpcRole)
        {
            state.role = npcRole.ToString();
            prevNpcRole = npcRole;
            ApplyRoleCombatPostureImmediate();
        }
        else if (!string.Equals(state.role, prevRole, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(state.role) &&
                Enum.TryParse<NpcRole>(state.role.Trim(), true, out NpcRole parsedRole))
            {
                npcRole = parsedRole;
                prevNpcRole = parsedRole;
                state.role = parsedRole.ToString();
                ApplyRoleCombatPostureImmediate();
            }
            else
            {
                state.role = npcRole.ToString();
            }
        }
    }

    public void SetRole(NpcRole newRole)
    {
        npcRole = newRole;
        prevNpcRole = newRole;
        state.role = newRole.ToString();
        activeScenarioLabel = $"Role Switched -> {state.role} ({GetRoleWeaponProfileLabel()})";
        ApplyRoleCombatPostureImmediate();
    }

    public void CycleRole()
    {
        NpcRole nextRole = (NpcRole)(((int)npcRole + 1) % 3);
        SetRole(nextRole);
    }

    // Role-specific combat parameters so GUARD, SCOUT, and ASSAULT have distinct attack positions, cadences, bullets, speeds, and cover thresholds
    private string GetRoleWeaponProfileLabel()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return "Long-Range Marksman | Strafe z=-2.6";
            case NpcRole.ASSAULT:
                return "Close-Range Twin Burst | Rush z=+3.1";
            default:
                return "Mid-Range Tactical Rifle | Hold z=+0.4";
        }
    }

    private float GetRoleFireInterval()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return 1.35f; // Deliberate high-damage marksman cadence
            case NpcRole.ASSAULT:
                return 0.55f; // Rapid close-quarters twin-burst cadence
            default:
                return Mathf.Max(0.4f, guardAttackFireInterval); // Balanced 0.90s rifle cadence
        }
    }

    private float GetRoleMoveSpeed()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return 5.8f; // Fast agile flanker
            case NpcRole.ASSAULT:
                return 4.9f; // Aggressive rusher
            default:
                return moveSpeed; // Standard 4.2f tactical guard speed
        }
    }

    private float GetRoleEnterCoverThreshold()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return 55f; // Cautious: retreats to cover early at <= 55 HP
            case NpcRole.ASSAULT:
                return 25f; // Fearless: stays in close combat until critical <= 25 HP
            default:
                return 40f; // Balanced: takes cover at <= 40 HP
        }
    }

    private float GetRoleExitCoverThreshold()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return 82f; // Waits until almost full health (>= 82 HP) before leaving cover
            case NpcRole.ASSAULT:
                return 60f; // Charges back out of cover early at >= 60 HP
            default:
                return 75f; // Balanced cover exit at >= 75 HP
        }
    }

    private Color GetRoleAttackColor()
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                return new Color(0.15f, 0.88f, 0.98f); // Electric cyan skirmisher
            case NpcRole.ASSAULT:
                return new Color(1.0f, 0.28f, 0.08f); // Fiery scarlet-orange rusher
            default:
                return new Color(0.95f, 0.55f, 0.15f); // Tactical amber-gold defender
        }
    }

    private Vector3 GetRoleAttackPosition(Vector3 enemyPos)
    {
        switch (npcRole)
        {
            case NpcRole.SCOUT:
                // Long-range rear vantage line (z = -2.6f, ~7.5m from Enemy), strafing left/right after every sniper shot
                return new Vector3(1.45f * roleStrafeDirection, transform.position.y, -2.6f);

            case NpcRole.ASSAULT:
                // Aggressive close-quarters rush position (z = +3.1f, past the cover line, right in front of the Enemy!)
                float targetX = Mathf.Clamp(enemyPos.x * 0.55f + (0.65f * roleStrafeDirection), -2.2f, 2.2f);
                return new Vector3(targetX, transform.position.y, 3.1f);

            default:
                // GUARD: Central defensive line (x = 0.0f, z = +0.4f) holding the middle corridor between the cover cubes
                return new Vector3(0.0f, transform.position.y, 0.4f);
        }
    }

    private void ApplyRoleCombatPostureImmediate()
    {
        if (!Application.isPlaying || state.enemyCount <= 0) return;

        // If the Guard is currently moving to cover or healing in cover, keep it heading to the cover shelter
        if (IsRecoveringInCover())
        {
            targetPosition = GetCoverShelterPosition();
            return;
        }

        // If the NPC is currently attacking (or in the open), immediately reposition to the new role's attack zone
        if (state.currentAction == "ATTACK" || !IsPhysicallyInCoverPocket())
        {
            SetNpcColor(GetRoleAttackColor());
            targetPosition = GetGuardAttackTargetPosition(GetNearestActiveEnemyPosition());
        }
    }

    // Spawns a visible 3D bullet sphere that travels from shooter to target with customizable scale and speed
    private void SpawnVisualBullet(Vector3 startPos, Vector3 endPos, Color bulletColor, float bulletScale = 0.32f, float bulletSpeed = 18.0f)
    {
        StartCoroutine(AnimateVisualBulletCoroutine(startPos, endPos, bulletColor, bulletScale, bulletSpeed));
    }

    private IEnumerator AnimateVisualBulletCoroutine(Vector3 startPos, Vector3 endPos, Color bulletColor, float bulletScale, float bulletSpeed)
    {
        GameObject bullet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bullet.name = "Projectile_Tracer";
        if (bullet.TryGetComponent<Collider>(out Collider col))
        {
            Destroy(col);
        }
        bullet.transform.position = startPos + new Vector3(0f, 0.3f, 0f);
        bullet.transform.localScale = new Vector3(bulletScale, bulletScale, bulletScale);
        if (bullet.TryGetComponent<Renderer>(out Renderer r))
        {
            r.material.color = bulletColor;
        }

        Vector3 target = endPos + new Vector3(0f, 0.3f, 0f);
        float elapsed = 0f;
        while (bullet != null && Vector3.Distance(bullet.transform.position, target) > 0.15f && elapsed < 1.5f)
        {
            bullet.transform.position = Vector3.MoveTowards(bullet.transform.position, target, bulletSpeed * Time.deltaTime);
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (bullet != null)
        {
            Destroy(bullet);
        }
    }

    // Guard fires visible bullets at the Enemy whenever it is in ATTACK or has a clear line of sight to an enemy
    private void UpdateGuardCombatFiring()
    {
        if (state.enemyCount <= 0 || state.health <= 0f)
        {
            guardAttackFireTimer = 0f;
            autoReloadTimer = 0f;
            return;
        }

        // Role-specific auto-reload speed (ASSAULT reloads faster in 0.65s; GUARD/SCOUT in 1.1s)
        if (state.ammo <= 0)
        {
            guardAttackFireTimer = 0f;
            if (!IsRecoveringInCover() && state.currentAction != "RETREAT" && state.currentAction != "RELOAD")
            {
                state.currentAction = "RELOAD";
                SetNpcColor(new Color(0.15f, 0.85f, 0.85f));
            }
            autoReloadTimer += Time.deltaTime;
            float reloadDelay = (npcRole == NpcRole.ASSAULT) ? 0.65f : 1.1f;
            if (autoReloadTimer >= reloadDelay)
            {
                autoReloadTimer = 0f;
                ExecuteAction("RELOAD");
            }
            return;
        }

        autoReloadTimer = 0f;

        // Fire when executing ATTACK, or when in the open with a clear line of sight to the enemy
        bool canFireNow = (state.currentAction == "ATTACK") ||
                          (!IsPhysicallyInCoverPocket() && state.currentAction != "RETREAT" && CountEnemiesWithClearLineOfSight() > 0);

        if (!canFireNow)
        {
            guardAttackFireTimer = 0f;
            return;
        }

        guardAttackFireTimer += Time.deltaTime;
        float interval = GetRoleFireInterval();
        if (guardAttackFireTimer >= interval)
        {
            guardAttackFireTimer -= interval;
            FireGuardShotAtEnemy();
        }
    }

    private void FireGuardShotAtEnemy()
    {
        if (state.ammo <= 0 || state.enemyCount <= 0) return;

        // If the Guard just finished reloading, return to ATTACK posture when firing resumes
        if (state.currentAction == "RELOAD" && !IsRecoveringInCover())
        {
            state.currentAction = "ATTACK";
            SetNpcColor(GetRoleAttackColor());
            targetPosition = GetGuardAttackTargetPosition(GetNearestActiveEnemyPosition());
        }

        bool hadAmmo = state.ammo > 0;
        state.ammo--;

        int targetEnemyIdx = GetBestTargetEnemyIndex();
        Transform targetEnemy = GetEnemyTransformByIndex(targetEnemyIdx);
        Vector3 enemyPos = targetEnemy != null ? targetEnemy.position : GetNearestActiveEnemyPosition();
        bool enemyShielded = IsEnemyPhysicallyInCover(targetEnemyIdx);
        Vector3 impactPos = enemyShielded ? GetEnemyCoverCubePosition(targetEnemyIdx) : enemyPos;

        if (npcRole == NpcRole.SCOUT)
        {
            // SCOUT: High-velocity cyan precision sniper shot (24 DMG, speed 28, sleek 0.26 scale) + hit-and-run lateral strafe!
            SpawnVisualBullet(transform.position, impactPos, new Color(0.15f, 0.95f, 1.0f), 0.26f, 28.0f);
            if (!enemyShielded)
            {
                DamageEnemy(targetEnemyIdx, 24f, $"{state.npcId} [SCOUT SNIPER]");
            }
            roleStrafeDirection = -roleStrafeDirection;
            if (state.currentAction == "ATTACK")
            {
                targetPosition = GetGuardAttackTargetPosition(enemyPos);
            }
        }
        else if (npcRole == NpcRole.ASSAULT)
        {
            // ASSAULT: Close-range twin heavy fiery-orange burst (2 projectiles side-by-side, 22 total DMG, larger 0.38 scale)!
            Vector3 leftOffset = new Vector3(-0.22f, 0f, 0f);
            Vector3 rightOffset = new Vector3(0.22f, 0f, 0f);
            SpawnVisualBullet(transform.position + leftOffset, impactPos + leftOffset * 0.5f, new Color(1.0f, 0.36f, 0.08f), 0.38f, 21.0f);
            SpawnVisualBullet(transform.position + rightOffset, impactPos + rightOffset * 0.5f, new Color(1.0f, 0.52f, 0.12f), 0.35f, 19.5f);
            if (!enemyShielded)
            {
                DamageEnemy(targetEnemyIdx, 22f, $"{state.npcId} [ASSAULT BURST]");
            }
            roleStrafeDirection = -roleStrafeDirection;
            if (state.currentAction == "ATTACK")
            {
                targetPosition = GetGuardAttackTargetPosition(enemyPos);
            }
        }
        else
        {
            // GUARD: Balanced mid-range controlled gold rifle shot (16 DMG, speed 18, 0.32 scale) from central defense line
            SpawnVisualBullet(transform.position, impactPos, new Color(1.0f, 0.92f, 0.15f), 0.32f, 18.0f);
            if (!enemyShielded)
            {
                DamageEnemy(targetEnemyIdx, 16f, $"{state.npcId} [GUARD RIFLE]");
            }
        }

        Debug.Log($"[Combat] {state.npcId} ({state.role}) fired ({GetRoleWeaponProfileLabel()}) at Enemy #{targetEnemyIdx + 1}! Remaining Ammo: {state.ammo}/{maxAmmo}");

        // Immediately exit ATTACK and enter RELOAD posture the moment the last round is fired
        if (state.ammo <= 0)
        {
            guardAttackFireTimer = 0f;
            autoReloadTimer = 0f;
            if (!IsRecoveringInCover() && state.currentAction != "RETREAT")
            {
                state.currentAction = "RELOAD";
                SetNpcColor(new Color(0.15f, 0.85f, 0.85f));
            }
        }

        // Keep prevAmmo synced unless ammo just hit 0 (so the ammo-depleted event trigger fires immediately at 0)
        if (!(hadAmmo && state.ammo == 0))
        {
            prevAmmo = state.ammo;
        }
    }

    private int GetBestTargetEnemyIndex()
    {
        EnsureUnitHealthListsSynced();
        int count = Mathf.Max(0, state.enemyCount);

        // Prefer firing at an active enemy that is NOT currently shielded in cover
        for (int i = 0; i < count; i++)
        {
            Transform tr = GetEnemyTransformByIndex(i);
            if (tr != null && tr.gameObject.activeInHierarchy && !IsEnemyPhysicallyInCover(i))
            {
                return i;
            }
        }
        return 0;
    }

    private Transform GetEnemyTransformByIndex(int index)
    {
        if (index == 0) return enemyTransform;
        int extraIdx = index - 1;
        if (extraIdx >= 0 && extraIdx < extraEnemyUnits.Count && extraEnemyUnits[extraIdx] != null)
        {
            return extraEnemyUnits[extraIdx].transform;
        }
        return enemyTransform;
    }

    private Vector3 GetEnemyOpenCombatPosition(int index)
    {
        if (index <= 0)
        {
            return new Vector3(0.0f, 1.0f, 4.8f);
        }
        int slotIdx = index - 1;
        if (slotIdx < ExtraEnemySlots.Length)
        {
            return ExtraEnemySlots[slotIdx];
        }
        return new Vector3(-4.5f + (slotIdx % 5) * 2.2f, 1.0f, 6.0f);
    }

    private Vector3 GetEnemyCoverCubePosition(int index)
    {
        // Even-indexed enemies use CoverCube (west), odd-indexed enemies use CoverCubeEast (east)
        bool useEast = (index % 2 == 1);
        if (useEast && eastCoverTransform != null && eastCoverTransform.gameObject.activeInHierarchy)
        {
            return eastCoverTransform.position;
        }
        if (coverTransform != null && coverTransform.gameObject.activeInHierarchy)
        {
            return coverTransform.position;
        }
        if (eastCoverTransform != null && eastCoverTransform.gameObject.activeInHierarchy)
        {
            return eastCoverTransform.position;
        }
        return new Vector3(-3.0f, 0.5f, 1.5f);
    }

    private Vector3 GetEnemyCoverShelterPosition(int index)
    {
        Vector3 cubePos = GetEnemyCoverCubePosition(index);
        float xOffset = (index >= 2) ? ((index % 2 == 0) ? -0.65f : 0.65f) : 0f;
        // North side of the cover cube (z + 1.05f) shields the Enemy from Guard/Ally on the south side
        return new Vector3(cubePos.x + xOffset, 1.0f, cubePos.z + 1.05f);
    }

    private bool IsEnemyPhysicallyInCover(int index)
    {
        EnsureUnitHealthListsSynced();
        if (!state.coverAvailable || index < 0 || index >= enemyInCoverFlags.Count || !enemyInCoverFlags[index])
        {
            return false;
        }
        Transform tr = GetEnemyTransformByIndex(index);
        if (tr == null || !tr.gameObject.activeInHierarchy) return false;
        Vector3 shelterPos = GetEnemyCoverShelterPosition(index);
        return Vector2.Distance(new Vector2(tr.position.x, tr.position.z), new Vector2(shelterPos.x, shelterPos.z)) <= 1.25f;
    }

    private void DamageEnemy(int index, float amount, string attackerId)
    {
        EnsureUnitHealthListsSynced();
        if (index < 0 || index >= enemyHealths.Count || amount <= 0f) return;
        if (IsEnemyPhysicallyInCover(index)) return;

        float oldHp = enemyHealths[index];
        enemyHealths[index] = Mathf.Max(10f, enemyHealths[index] - amount);
        Debug.Log($"[Enemy Damage] Enemy #{index + 1} hit by {attackerId} (-{amount:F0} HP): {oldHp:F0} -> {enemyHealths[index]:F0} HP");

        // When Enemy health drops low (<= 35 HP) and cover is available, Enemy moves into cover!
        if (enemyHealths[index] <= 35f && state.coverAvailable && !enemyInCoverFlags[index])
        {
            enemyInCoverFlags[index] = true;
            enemyCoverTimers[index] = 0f;
            Debug.Log($"[Enemy Tactical Cover] Enemy #{index + 1} health is low ({enemyHealths[index]:F0} HP) -> moving behind cover to recover!");
        }
    }

    private Vector3 GetAllyCoverShelterPosition(int index)
    {
        // Ally #1 (index 0) takes cover behind CoverCubeEast; Ally #2 (index 1) takes cover behind CoverCube (or offset behind CoverCubeEast)
        if (index % 2 == 0 && eastCoverTransform != null && eastCoverTransform.gameObject.activeInHierarchy)
        {
            return new Vector3(eastCoverTransform.position.x, 1.0f, eastCoverTransform.position.z - 1.05f);
        }
        if (index % 2 == 1 && coverTransform != null && coverTransform.gameObject.activeInHierarchy)
        {
            return new Vector3(coverTransform.position.x + 0.85f, 1.0f, coverTransform.position.z - 1.05f);
        }
        if (eastCoverTransform != null && eastCoverTransform.gameObject.activeInHierarchy)
        {
            return new Vector3(eastCoverTransform.position.x + index * 0.7f, 1.0f, eastCoverTransform.position.z - 1.05f);
        }
        return new Vector3(index % 2 == 0 ? 3.0f : -2.15f, 1.0f, 0.45f);
    }

    private Vector3 GetAllyOpenCombatPosition(int index)
    {
        // Ally #1 fights on the right flank (+1.55, 0.8), Ally #2 fights on the left flank (-1.55, 0.8)
        if (index == 0) return new Vector3(1.55f, 1.0f, 0.8f);
        if (index == 1) return new Vector3(-1.55f, 1.0f, 0.8f);
        return new Vector3(1.55f + (index - 1) * 1.0f, 1.0f, 0.8f);
    }

    private bool IsAllyPhysicallyInCover(int index)
    {
        EnsureUnitHealthListsSynced();
        if (!state.coverAvailable || index < 0 || index >= allyInCoverFlags.Count || !allyInCoverFlags[index])
        {
            return false;
        }
        if (index >= existingSceneAllies.Count || existingSceneAllies[index] == null) return false;
        Vector3 allyPos = existingSceneAllies[index].position;
        Vector3 shelterPos = GetAllyCoverShelterPosition(index);
        return Vector2.Distance(new Vector2(allyPos.x, allyPos.z), new Vector2(shelterPos.x, shelterPos.z)) <= 1.25f;
    }

    private void DamageAlly(int index, float amount)
    {
        EnsureUnitHealthListsSynced();
        if (index < 0 || index >= allyHealths.Count || amount <= 0f) return;
        if (IsAllyPhysicallyInCover(index)) return;

        float oldHp = allyHealths[index];
        allyHealths[index] = Mathf.Max(10f, allyHealths[index] - amount);
        Debug.Log($"[Ally Damage] Ally #{index + 1} took {amount:F0} damage: {oldHp:F0} -> {allyHealths[index]:F0} HP");

        // When Ally health drops low (<= 40 HP), Ally moves into cover (if available) and calls Guard for help!
        if (allyHealths[index] <= 40f)
        {
            if (state.coverAvailable && !allyInCoverFlags[index])
            {
                allyInCoverFlags[index] = true;
                allyCoverTimers[index] = 0f;
                Vector3 allyPos = (index < existingSceneAllies.Count && existingSceneAllies[index] != null)
                    ? existingSceneAllies[index].position
                    : initialAllyPosition;
                Debug.Log($"[Ally Tactical Cover] Ally #{index + 1} health is low ({allyHealths[index]:F0} HP) -> moving behind cover and calling Guard for help!");
                OnCallForHelpTriggered?.Invoke($"ally_0{index + 1}", allyPos);
            }
            else if (!state.coverAvailable && oldHp > 40f)
            {
                Vector3 allyPos = (index < existingSceneAllies.Count && existingSceneAllies[index] != null)
                    ? existingSceneAllies[index].position
                    : initialAllyPosition;
                Debug.Log($"[Ally Distress] Ally #{index + 1} health dropped low ({allyHealths[index]:F0} HP) with no cover available -> calling Guard for help!");
                OnCallForHelpTriggered?.Invoke($"ally_0{index + 1}", allyPos);
            }
        }
    }

    private Vector3 GetNearestActiveEnemyPosition()
    {
        if (enemyTransform != null && enemyTransform.gameObject.activeInHierarchy)
        {
            return enemyTransform.position;
        }
        for (int i = 0; i < extraEnemyUnits.Count; i++)
        {
            if (extraEnemyUnits[i] != null && extraEnemyUnits[i].activeInHierarchy)
            {
                return extraEnemyUnits[i].transform.position;
            }
        }
        return new Vector3(0f, 1f, 5f);
    }

    // Damage System: reduces health (clamped >= 0) and triggers an immediate tactical decision on critical/major health change
    public void TakeDamage(float amount)
    {
        if (amount <= 0f) return;

        float oldHealth = state.health;
        state.health = Mathf.Max(0f, state.health - amount);
        Debug.Log($"[Damage System] {state.npcId} ({state.role}) took {amount:F1} damage. Health: {oldHealth:F0} -> {state.health:F0}");

        // Role-specific cover threshold (SCOUT=55 HP, GUARD=40 HP, ASSAULT=25 HP)
        float roleCoverThreshold = GetRoleEnterCoverThreshold();
        bool crossedLowThreshold = (oldHealth > roleCoverThreshold && state.health <= roleCoverThreshold) ||
                                   (oldHealth > 25f && state.health <= 25f);
        bool manualBurstDrop = amount >= 15f;

        // Always move to cover when health is at/below the role's cover threshold and cover is available
        if (state.health <= roleCoverThreshold && state.coverAvailable)
        {
            ExecuteAction("TAKE_COVER");
        }

        if (crossedLowThreshold || manualBurstDrop)
        {
            SyncStateSnapshot();
            IncrementStateVersion($"Damage taken (Role={state.role}, Health={state.health:F0}, lowHealth={state.health <= roleCoverThreshold})");
            decisionTimer = 0f;
            RequestTacticalDecision();
        }
    }

    // Returns the sheltered cover position directly behind the nearest cover cube
    private Vector3 GetCoverShelterPosition()
    {
        if (coverTransform != null && coverTransform.gameObject.activeInHierarchy)
        {
            return new Vector3(coverTransform.position.x, transform.position.y, coverTransform.position.z - 1.0f);
        }
        if (eastCoverTransform != null && eastCoverTransform.gameObject.activeInHierarchy)
        {
            return new Vector3(eastCoverTransform.position.x, transform.position.y, eastCoverTransform.position.z - 1.0f);
        }
        return new Vector3(-3.0f, transform.position.y, 0.5f);
    }

    // Checks whether a wall or cover object blocks enemy line of sight or shields the NPC
    public bool IsProtectedFromEnemy(Vector3 enemyPos)
    {
        // 1. Check if NPC is positioned behind an active Cover object relative to the enemy
        if (state.coverAvailable)
        {
            if (IsBehindCoverTransform(coverTransform, enemyPos) || IsBehindCoverTransform(eastCoverTransform, enemyPos))
            {
                return true;
            }
        }

        // 2. Physical 3D Linecast at torso height (y = 0.6f) to check if any Wall or Cover collider blocks the shot
        Vector3 rayStart = new Vector3(enemyPos.x, 0.6f, enemyPos.z);
        Vector3 rayEnd = new Vector3(transform.position.x, 0.6f, transform.position.z);

        RaycastHit[] hits = Physics.RaycastAll(rayStart, (rayEnd - rayStart).normalized, Vector3.Distance(rayStart, rayEnd));
        for (int i = 0; i < hits.Length; i++)
        {
            Transform hitTransform = hits[i].transform;
            if (hitTransform == null || hitTransform == transform) continue;
            if (enemyTransform != null && hitTransform == enemyTransform) continue;

            string hitName = hitTransform.name;
            if (hitName.StartsWith("Enemy", StringComparison.OrdinalIgnoreCase) ||
                hitName.StartsWith("Ally", StringComparison.OrdinalIgnoreCase) ||
                hitName.StartsWith("Projectile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Hit a CoverCube or Arena Wall -> shot is physically blocked!
            return true;
        }

        return false;
    }

    private bool IsBehindCoverTransform(Transform cover, Vector3 enemyPos)
    {
        if (cover == null || !cover.gameObject.activeInHierarchy) return false;

        Vector3 npcPos = transform.position;
        Vector3 covPos = cover.position;

        // Check if NPC is within the sheltered zone behind the cover cube (within 1.5m of the rear face of the cover cube)
        float distToRearCover = Vector2.Distance(new Vector2(npcPos.x, npcPos.z), new Vector2(covPos.x, covPos.z - 1.0f));
        bool inRearPocket = distToRearCover <= 1.35f && npcPos.z <= covPos.z + 0.1f;
        if (!inRearPocket) return false;

        float enemyToNpcDistSq = (new Vector2(npcPos.x - enemyPos.x, npcPos.z - enemyPos.z)).sqrMagnitude;
        float enemyToCoverDistSq = (new Vector2(covPos.x - enemyPos.x, covPos.z - enemyPos.z)).sqrMagnitude;
        return enemyToNpcDistSq >= enemyToCoverDistSq * 0.85f;
    }

    private int CountEnemiesWithClearLineOfSight()
    {
        if (state.enemyCount <= 0) return 0;

        int clearSightCount = 0;
        if (enemyTransform != null && enemyTransform.gameObject.activeInHierarchy)
        {
            if (!IsEnemyPhysicallyInCover(0) && !IsProtectedFromEnemy(enemyTransform.position))
            {
                clearSightCount++;
            }
        }

        for (int i = 0; i < extraEnemyUnits.Count; i++)
        {
            GameObject unit = extraEnemyUnits[i];
            if (unit != null && unit.activeInHierarchy)
            {
                if (!IsEnemyPhysicallyInCover(i + 1) && !IsProtectedFromEnemy(unit.transform.position))
                {
                    clearSightCount++;
                }
            }
        }

        return clearSightCount;
    }

    public bool IsPhysicallyInCoverPocket()
    {
        if (!state.coverAvailable) return false;
        Vector3 shelterPos = GetCoverShelterPosition();
        return Vector2.Distance(new Vector2(transform.position.x, transform.position.z), new Vector2(shelterPos.x, shelterPos.z)) <= 1.15f;
    }

    // True while the Guard has taken cover (or is physically in cover) and is still healing toward its role's exit-cover threshold
    private bool IsRecoveringInCover()
    {
        if (!state.coverAvailable) return false;
        if (state.health >= GetRoleExitCoverThreshold()) return false;
        bool inCoverPosture = state.currentAction == "TAKE_COVER" ||
                              (state.currentAction == "CALL_FOR_HELP" && IsPhysicallyInCoverPocket());
        return inCoverPosture || IsPhysicallyInCoverPocket();
    }

    private void UpdateEnemyDamage()
    {
        if (state.health <= 0f || state.enemyCount <= 0)
        {
            proximityDamageTimer = 0f;
            combatFireDamageTimer = 0f;
            return;
        }

        // Find an active Enemy that is NOT currently hiding in cover
        int shooterEnemyIdx = -1;
        int totalEnemies = Mathf.Max(0, state.enemyCount);
        for (int i = 0; i < totalEnemies; i++)
        {
            Transform tr = GetEnemyTransformByIndex(i);
            if (tr != null && tr.gameObject.activeInHierarchy && !IsEnemyPhysicallyInCover(i))
            {
                shooterEnemyIdx = i;
                break;
            }
        }

        // If all active enemies are currently hiding in cover, no enemy shots are fired
        if (shooterEnemyIdx < 0)
        {
            proximityDamageTimer = 0f;
            combatFireDamageTimer = 0f;
            return;
        }

        Transform shooterTr = GetEnemyTransformByIndex(shooterEnemyIdx);
        Vector3 enemyPos = shooterTr != null ? shooterTr.position : GetNearestActiveEnemyPosition();
        float distanceToEnemy = Vector3.Distance(transform.position, enemyPos);

        // Check how many active enemies have an unobstructed line of sight to the Guard
        int exposedToEnemies = CountEnemiesWithClearLineOfSight();

        // 1. Close-quarters proximity damage: 5 HP/sec when an exposed enemy is within 2 meters of the Guard
        if (exposedToEnemies > 0 && distanceToEnemy < 2.0f)
        {
            proximityDamageTimer += Time.deltaTime;
            if (proximityDamageTimer >= 1.0f)
            {
                proximityDamageTimer -= 1.0f;
                SpawnVisualBullet(enemyPos, transform.position, new Color(0.95f, 0.25f, 0.20f));
                TakeDamage(5f);
            }
        }
        else
        {
            proximityDamageTimer = 0f;
        }

        // 2. Periodic enemy combat fire against BOTH Guard and existing Scene Allies
        if (enableEnemyCombatDamage && distanceToEnemy <= 15.0f)
        {
            combatFireDamageTimer += Time.deltaTime;
            float interval = Mathf.Max(1.5f, enemyAttackInterval);
            if (combatFireDamageTimer >= interval)
            {
                combatFireDamageTimer -= interval;

                // Fire at Guard (or hit CoverCube harmlessly if Guard is behind cover)
                if (exposedToEnemies > 0)
                {
                    SpawnVisualBullet(enemyPos, transform.position, new Color(0.95f, 0.25f, 0.20f));
                    float damage = 8f + Mathf.Max(0, exposedToEnemies - 1) * 2f;
                    TakeDamage(damage);
                }
                else if (coverTransform != null && coverTransform.gameObject.activeInHierarchy)
                {
                    SpawnVisualBullet(enemyPos, coverTransform.position, new Color(0.95f, 0.25f, 0.20f));
                }

                // Also fire at existing Allies in the scene (if Ally is in cover, shot hits CoverCubeEast harmlessly)
                for (int i = 0; i < existingSceneAllies.Count; i++)
                {
                    Transform ally = existingSceneAllies[i];
                    if (ally == null || !ally.gameObject.activeInHierarchy) continue;

                    if (IsAllyPhysicallyInCover(i))
                    {
                        // Shot hits the cover cube this Ally is hiding behind (Ally #1 -> CoverCubeEast, Ally #2 -> CoverCube)
                        Vector3 shelter = GetAllyCoverShelterPosition(i);
                        SpawnVisualBullet(enemyPos, new Vector3(shelter.x, 0.5f, shelter.z + 1.05f), new Color(0.95f, 0.25f, 0.20f));
                    }
                    else
                    {
                        SpawnVisualBullet(enemyPos, ally.position, new Color(0.95f, 0.25f, 0.20f));
                        DamageAlly(i, 14f);
                    }
                }
            }
        }
        else
        {
            combatFireDamageTimer = 0f;
        }
    }

    // Handles health recovery while in cover, calling the existing Ally for help, leaving cover once healed, and Ally distress calls
    private void UpdateCoverRecoveryAndCooperation()
    {
        bool inCoverPocket = IsPhysicallyInCoverPocket();

        if (inCoverPocket)
        {
            timeSpentInCover += Time.deltaTime;

            // 1. If the Guard arrived in cover with low/moderate health and hasn't called the Ally for help yet, call the existing Ally!
            if (!hasCalledHelpDuringCurrentCover && state.nearbyAllies > 0 && state.enemyCount > 0 && timeSpentInCover >= 0.8f)
            {
                hasCalledHelpDuringCurrentCover = true;
                CallForHelp();
            }

            // 2. Recover health while sheltered behind cover (including if health was reduced all the way to 0 via H)
            if (state.health < 90f)
            {
                float healRate = Mathf.Max(12.0f, coverHealRatePerSecond);
                state.health = Mathf.Min(90f, state.health + healRate * Time.deltaTime);
                prevHealth = state.health;
            }

            // 3. Role-specific cover exit threshold (ASSAULT exits at >= 60 HP after 1.6s, GUARD at >= 75 HP, SCOUT at >= 82 HP)
            float exitThreshold = GetRoleExitCoverThreshold();
            float minCoverStay = (npcRole == NpcRole.ASSAULT) ? 1.6f : 2.3f;
            if (state.health >= exitThreshold && timeSpentInCover >= minCoverStay && state.enemyCount > 0)
            {
                timeSpentInCover = 0f;
                hasCalledHelpDuringCurrentCover = false;
                if (state.ammo <= 0)
                {
                    state.ammo = maxAmmo;
                    prevAmmo = state.ammo;
                }
                Debug.Log($"[Tactical Cover] {state.npcId} ({state.role}) recovered to {state.health:F0} HP in cover -> stepping out to {GetRoleWeaponProfileLabel()}!");
                ExecuteAction("ATTACK");
                SyncStateSnapshot();
                IncrementStateVersion($"Guard ({state.role}) recovered in cover ({state.health:F0} HP) and stepped out to ATTACK");
                decisionTimer = 0f;
                RequestTacticalDecision();
            }
        }
        else
        {
            timeSpentInCover = 0f;
        }

        // 4. Autonomous Ally Call for Help: active Allies in the scene periodically request help from the Guard
        bool guardWalkingToCover = state.currentAction == "TAKE_COVER" && !inCoverPocket;
        if (state.nearbyAllies > 0 && existingSceneAllies.Count > 0 && state.enemyCount > 0 && !state.allyNeedsHelp && !guardWalkingToCover && allyAutoHelpRequestInterval > 0f)
        {
            allyDistressTimer += Time.deltaTime;
            if (allyDistressTimer >= allyAutoHelpRequestInterval)
            {
                allyDistressTimer = 0f;
                int callerIdx = GetMostWoundedActiveAllyIndex();
                Vector3 allyPos = GetAllyPositionOrDefault(callerIdx);
                Debug.Log($"[AI Cooperation] Ally #{callerIdx + 1} is under enemy fire at {allyPos} and calling Guard '{state.npcId}' for help!");
                OnCallForHelpTriggered?.Invoke($"ally_0{callerIdx + 1}", allyPos);
            }
        }
    }

    // Returns the active Ally with the lowest health (defaults to index 0)
    private int GetMostWoundedActiveAllyIndex()
    {
        EnsureUnitHealthListsSynced();
        int best = 0;
        float bestHp = float.MaxValue;
        int activeCount = Mathf.Clamp(state.nearbyAllies, 0, Mathf.Min(existingSceneAllies.Count, allyHealths.Count));
        for (int i = 0; i < activeCount; i++)
        {
            Transform ally = existingSceneAllies[i];
            if (ally == null || !ally.gameObject.activeInHierarchy) continue;
            if (allyHealths[i] < bestHp)
            {
                bestHp = allyHealths[i];
                best = i;
            }
        }
        return best;
    }

    private Vector3 GetAllyPositionOrDefault(int index)
    {
        if (index >= 0 && index < existingSceneAllies.Count && existingSceneAllies[index] != null)
        {
            return existingSceneAllies[index].position;
        }
        return GetActiveAllyPosition();
    }

    // Parses "ally_02" -> 1; returns -1 for non-Ally callers (e.g., other Guard NPCs)
    private static int ParseAllyIndex(string callerId)
    {
        if (!string.IsNullOrEmpty(callerId) &&
            callerId.StartsWith("ally_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(callerId.Substring(5), out int number) && number > 0)
        {
            return number - 1;
        }
        return -1;
    }

    // Live position of the Ally that requested help (falls back to the position captured when the call was made)
    private Vector3 GetDistressedAllyPosition()
    {
        if (distressedAllyIndex >= 0 && distressedAllyIndex < existingSceneAllies.Count &&
            existingSceneAllies[distressedAllyIndex] != null &&
            existingSceneAllies[distressedAllyIndex].gameObject.activeInHierarchy)
        {
            return existingSceneAllies[distressedAllyIndex].position;
        }
        return distressedAllyPosition;
    }

    // Forward support position used while CALL_FOR_HELP support is active:
    // Allies advance through the central corridor (between CoverCube x<=-1.9 and CoverCubeEast x>=1.9) to z=+2.35 to suppress Enemies
    private Vector3 GetAllySupportPosition(int index)
    {
        if (index == 0) return new Vector3(1.45f, 1.0f, 2.35f);
        if (index == 1) return new Vector3(-1.45f, 1.0f, 2.35f);
        float side = (index % 2 == 0) ? 1.45f : -1.45f;
        return new Vector3(Mathf.Clamp(side, -1.75f, 1.75f), 1.0f, 2.35f);
    }

    // Clears the cooperation flag (used once the LLM has received the request, or on scenario/reset)
    private void ClearAllyHelpRequest(string reason)
    {
        if (!state.allyNeedsHelp && distressedAllyIndex < 0) return;
        state.allyNeedsHelp = false;
        prevAllyNeedsHelp = false;
        distressedAllyIndex = -1;
        SyncSceneUnitsVisuals();
        Debug.Log($"[AI Cooperation] Ally help request cleared ({reason}).");
    }

    // Clears allyNeedsHelp only if the request that just completed carried this exact help call to the LLM
    private void AcknowledgeAllyHelpRequest(int helpIdAtRequest)
    {
        if (helpIdAtRequest >= 0 && helpIdAtRequest == allyHelpRequestId && state.allyNeedsHelp)
        {
            ClearAllyHelpRequest($"help request #{helpIdAtRequest} was delivered to the AI decision");
        }
    }

    private Vector3 GetActiveAllyPosition()
    {
        for (int i = 0; i < existingSceneAllies.Count; i++)
        {
            if (existingSceneAllies[i] != null && existingSceneAllies[i].gameObject.activeInHierarchy)
            {
                return existingSceneAllies[i].position;
            }
        }
        return initialAllyPosition;
    }

    // Moves only the existing Ally in the scene (never creates new Allies):
    // When Ally health is low (<= 40 HP), Ally moves behind CoverCubeEast, heals, and steps back out to fight when >= 75 HP!
    private void UpdateExistingAllyMovements()
    {
        EnsureUnitHealthListsSynced();

        if (allySupportMoveTimer > 0f)
        {
            allySupportMoveTimer = Mathf.Max(0f, allySupportMoveTimer - Time.deltaTime);
        }

        // CALL_FOR_HELP support: Allies advance next to the Guard and fire at a faster cadence while the timer is active
        bool supportActive = allySupportMoveTimer > 0f;
        float volleyInterval = supportActive ? AllySupportVolleyInterval : AllyNormalVolleyInterval;

        bool fireVolleyNow = false;
        if (state.enemyCount > 0)
        {
            allyCombatFireTimer += Time.deltaTime;
            if (allyCombatFireTimer >= volleyInterval)
            {
                allyCombatFireTimer -= volleyInterval;
                fireVolleyNow = true;
            }
        }
        else
        {
            allyCombatFireTimer = 0f;
        }

        int targetEnemyIdx = GetBestTargetEnemyIndex();
        Transform targetEnemy = GetEnemyTransformByIndex(targetEnemyIdx);
        Vector3 enemyPos = targetEnemy != null ? targetEnemy.position : GetNearestActiveEnemyPosition();

        for (int i = 0; i < existingSceneAllies.Count; i++)
        {
            Transform ally = existingSceneAllies[i];
            if (ally == null || !ally.gameObject.activeInHierarchy) continue;

            // If cover is disabled, Ally cannot stay in cover
            if (!state.coverAvailable && allyInCoverFlags[i])
            {
                allyInCoverFlags[i] = false;
                allyCoverTimers[i] = 0f;
            }

            // Enter cover if Ally health is low (<= 40 HP) and cover is available
            if (allyHealths[i] <= 40f && state.coverAvailable && !allyInCoverFlags[i])
            {
                allyInCoverFlags[i] = true;
                allyCoverTimers[i] = 0f;
            }

            Vector3 desiredAllyPos;

            if (allyInCoverFlags[i] && state.coverAvailable)
            {
                desiredAllyPos = GetAllyCoverShelterPosition(i);

                // Once physically behind CoverCubeEast, heal up and then step back out of cover!
                if (IsAllyPhysicallyInCover(i))
                {
                    allyCoverTimers[i] += Time.deltaTime;
                    allyHealths[i] = Mathf.Min(90f, allyHealths[i] + coverHealRatePerSecond * Time.deltaTime);

                    if (allyHealths[i] >= 75f && allyCoverTimers[i] >= 2.2f)
                    {
                        allyInCoverFlags[i] = false;
                        allyCoverTimers[i] = 0f;
                        desiredAllyPos = GetAllyOpenCombatPosition(i);
                        Debug.Log($"[Ally Tactical Cover] Ally #{i + 1} recovered to {allyHealths[i]:F0} HP -> stepping out of cover to attack!");
                    }
                }
            }
            else if (state.enemyCount > 0)
            {
                // Active combat position in the open firing lane (or forward next to the Guard while supporting it)
                desiredAllyPos = supportActive ? GetAllySupportPosition(i) : GetAllyOpenCombatPosition(i);

                if (fireVolleyNow)
                {
                    if (IsEnemyPhysicallyInCover(targetEnemyIdx))
                    {
                        SpawnVisualBullet(ally.position, GetEnemyCoverCubePosition(targetEnemyIdx), new Color(0.20f, 0.95f, 0.65f));
                    }
                    else
                    {
                        SpawnVisualBullet(ally.position, enemyPos, new Color(0.20f, 0.95f, 0.65f));
                        DamageEnemy(targetEnemyIdx, 10f, $"ally_0{i + 1}");
                    }
                }
            }
            else
            {
                desiredAllyPos = (i < initialAllyPositions.Count) ? initialAllyPositions[i] : initialAllyPosition;
            }

            ally.position = Vector3.MoveTowards(ally.position, desiredAllyPos, (moveSpeed * 0.92f) * Time.deltaTime);
        }
    }

    // Moves active Enemies: when an Enemy's health drops low (<= 35 HP), it moves behind cover (north side of CoverCube/CoverCubeEast),
    // regenerates health while shielded, and steps back out to its open combat position once health reaches >= 80 HP!
    private void UpdateActiveEnemyMovementsAndCover()
    {
        EnsureUnitHealthListsSynced();
        int activeCount = Mathf.Max(0, state.enemyCount);

        for (int i = 0; i < activeCount; i++)
        {
            Transform enemyTr = GetEnemyTransformByIndex(i);
            if (enemyTr == null || !enemyTr.gameObject.activeInHierarchy) continue;

            if (!state.coverAvailable && enemyInCoverFlags[i])
            {
                enemyInCoverFlags[i] = false;
                enemyCoverTimers[i] = 0f;
            }

            if (enemyHealths[i] <= 35f && state.coverAvailable && !enemyInCoverFlags[i])
            {
                enemyInCoverFlags[i] = true;
                enemyCoverTimers[i] = 0f;
            }

            Vector3 desiredEnemyPos;
            if (enemyInCoverFlags[i] && state.coverAvailable)
            {
                desiredEnemyPos = GetEnemyCoverShelterPosition(i);

                if (IsEnemyPhysicallyInCover(i))
                {
                    enemyCoverTimers[i] += Time.deltaTime;
                    enemyHealths[i] = Mathf.Min(100f, enemyHealths[i] + coverHealRatePerSecond * Time.deltaTime);

                    if (enemyHealths[i] >= 80f && enemyCoverTimers[i] >= 2.2f)
                    {
                        enemyInCoverFlags[i] = false;
                        enemyCoverTimers[i] = 0f;
                        desiredEnemyPos = GetEnemyOpenCombatPosition(i);
                        Debug.Log($"[Enemy Tactical Cover] Enemy #{i + 1} recovered to {enemyHealths[i]:F0} HP -> stepping out of cover to attack!");
                    }
                }
            }
            else
            {
                desiredEnemyPos = GetEnemyOpenCombatPosition(i);
            }

            enemyTr.position = Vector3.MoveTowards(enemyTr.position, desiredEnemyPos, (moveSpeed * 0.90f) * Time.deltaTime);
        }
    }

    // Section 17: Listener for static OnCallForHelpTriggered event from allied NPCs
    private void HandleAllyCallForHelp(string callerId, Vector3 callerPosition)
    {
        if (callerId == state.npcId) return;

        // New help call id: allyNeedsHelp stays true until a decision request carrying it has completed
        allyHelpRequestId++;
        state.allyNeedsHelp = true;
        prevAllyNeedsHelp = true;
        distressedAllyIndex = ParseAllyIndex(callerId);
        distressedAllyPosition = callerPosition;
        SyncSceneUnitsVisuals();
        activeScenarioLabel = $"Cooperation Alert (Ally '{callerId}' Needs Help)";

        // A wounded Guard (health at/below its role's cover threshold or still healing in cover) does NOT run into the open
        float roleCoverThreshold = GetRoleEnterCoverThreshold();
        if (state.health <= roleCoverThreshold || IsRecoveringInCover())
        {
            if (state.coverAvailable)
            {
                targetPosition = GetCoverShelterPosition();
            }
            Debug.Log($"[AI Cooperation] {state.npcId} ({state.role}) is recovering in cover ({state.health:F0} HP) -> staying in cover and asking the AI.");
        }
        else if (state.ammo > 0 && state.enemyCount > 0)
        {
            // Healthy and armed: immediately move out to rescue the Ally and fire at the Enemy
            ExecuteAction("ATTACK");
        }

        IncrementStateVersion($"Ally '{callerId}' triggered CALL_FOR_HELP at {callerPosition}");
        decisionTimer = 0f;
        RequestTacticalDecision();
    }

    public void TriggerManualAllyHelpSignal()
    {
        if (lastManualHelpFrame == Time.frameCount) return;
        lastManualHelpFrame = Time.frameCount;
        int callerIdx = GetMostWoundedActiveAllyIndex();
        Vector3 helpPos = GetAllyPositionOrDefault(callerIdx);
        string callerId = $"ally_0{callerIdx + 1}";
        Debug.Log($"[Cooperation Test] Broadcasting OnCallForHelpTriggered from '{callerId}' at {helpPos}.");
        OnCallForHelpTriggered?.Invoke(callerId, helpPos);
    }

    public void ApplyCameraConfiguration()
    {
        if (!configureMainCamera) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        cam.transform.position = cameraPosition;
        cam.transform.rotation = Quaternion.Euler(cameraEulerAngles);
        cam.orthographic = false;
        cam.fieldOfView = cameraFieldOfView;
        cam.backgroundColor = new Color(0.12f, 0.16f, 0.22f, 1f);
    }

    private void ApplySceneVisualContrast()
    {
        // Guard NPC starts with a distinct tactical blue tint
        SetNpcColor(new Color(0.18f, 0.65f, 0.95f));

        // Enemy #1 in bright crimson red
        if (enemyTransform != null && enemyTransform.TryGetComponent<Renderer>(out Renderer er))
        {
            er.material.color = new Color(0.90f, 0.22f, 0.20f);
        }

        // Cover objects in slate blue-grey
        Color coverColor = new Color(0.28f, 0.38f, 0.48f);
        if (coverTransform != null && coverTransform.TryGetComponent<Renderer>(out Renderer cr))
        {
            cr.material.color = coverColor;
        }
        if (eastCoverTransform != null && eastCoverTransform.TryGetComponent<Renderer>(out Renderer ecr))
        {
            ecr.material.color = coverColor;
        }

        // Color Scene Arena Walls (placed directly in SampleScene.unity)
        Color wallColor = new Color(0.46f, 0.45f, 0.42f);
        ColorSceneObject("BackWall", wallColor);
        ColorSceneObject("FrontWall", wallColor);
        ColorSceneObject("LeftWall", wallColor);
        ColorSceneObject("RightWall", wallColor);

        // Color Ground for clear visual separation
        ColorSceneObject("Ground", new Color(0.22f, 0.26f, 0.22f));
    }

    private void ColorSceneObject(string objName, Color color)
    {
        GameObject obj = GameObject.Find(objName);
        if (obj != null && obj.TryGetComponent<Renderer>(out Renderer r))
        {
            r.material.color = color;
        }
    }

    private GameObject InstantiateUnitFromPrefab(GameObject prefab, string instanceName, Vector3 position, Color color)
    {
        GameObject instance;
        if (prefab != null)
        {
            instance = Instantiate(prefab, position, Quaternion.identity);
        }
        else
        {
            instance = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            instance.transform.position = position;
            instance.transform.rotation = Quaternion.identity;
        }

        instance.name = instanceName;
        instance.transform.localScale = Vector3.one;
        if (instance.TryGetComponent<Renderer>(out Renderer r))
        {
            r.material.color = color;
        }
        return instance;
    }

    // Updates Enemy visuals and colors ONLY the existing Ally in the scene (never spawns new Allies)
    private void SyncSceneUnitsVisuals()
    {
        int targetEnemies = Mathf.Max(0, state.enemyCount);

        // Enemy #1 is enemyTransform in the scene
        if (enemyTransform != null)
        {
            bool showPrimaryEnemy = targetEnemies >= 1;
            if (enemyTransform.gameObject.activeSelf != showPrimaryEnemy)
            {
                enemyTransform.gameObject.SetActive(showPrimaryEnemy);
            }
        }

        // Enemies #2..N are instantiated from enemyPrefab when scenario enemyCount > 1
        int neededExtraEnemies = Mathf.Max(0, targetEnemies - 1);
        while (extraEnemyUnits.Count < neededExtraEnemies)
        {
            int idx = extraEnemyUnits.Count;
            Vector3 slot = idx < ExtraEnemySlots.Length
                ? ExtraEnemySlots[idx]
                : new Vector3(-4.5f + (idx % 5) * 2.2f, 1f, 6.4f);
            GameObject enemyObj = InstantiateUnitFromPrefab(
                enemyPrefab,
                $"Enemy_Unit_{idx + 2}",
                slot,
                new Color(0.90f, 0.22f, 0.20f)
            );
            extraEnemyUnits.Add(enemyObj);
        }

        for (int i = 0; i < extraEnemyUnits.Count; i++)
        {
            if (extraEnemyUnits[i] != null)
            {
                bool shouldBeActive = i < neededExtraEnemies;
                if (extraEnemyUnits[i].activeSelf != shouldBeActive)
                {
                    extraEnemyUnits[i].SetActive(shouldBeActive);
                }
            }
        }

        // Color active Enemies: crimson red in open combat, purple-crimson when taking cover & healing
        EnsureUnitHealthListsSynced();
        for (int i = 0; i < targetEnemies; i++)
        {
            Transform enemyTr = GetEnemyTransformByIndex(i);
            if (enemyTr != null && enemyTr.TryGetComponent<Renderer>(out Renderer er))
            {
                bool inCov = i < enemyInCoverFlags.Count && enemyInCoverFlags[i] && state.coverAvailable;
                er.material.color = inCov
                    ? new Color(0.72f, 0.32f, 0.58f) // Taking cover & healing
                    : new Color(0.90f, 0.22f, 0.20f); // Active combat
            }
        }

        // Existing Scene Allies: NEVER spawn new Ally objects; toggle active state of pre-placed scene Allies based on state.nearbyAllies
        int targetAllies = Mathf.Clamp(state.nearbyAllies, 0, existingSceneAllies.Count);
        for (int i = 0; i < existingSceneAllies.Count; i++)
        {
            Transform ally = existingSceneAllies[i];
            if (ally == null) continue;

            bool shouldBeActive = i < targetAllies;
            if (ally.gameObject.activeSelf != shouldBeActive)
            {
                ally.gameObject.SetActive(shouldBeActive);
            }

            if (shouldBeActive && ally.TryGetComponent<Renderer>(out Renderer r))
            {
                bool allyInCov = i < allyInCoverFlags.Count && allyInCoverFlags[i] && state.coverAvailable;
                if (allyInCov)
                {
                    r.material.color = new Color(0.22f, 0.68f, 0.92f); // Cyan-blue when Ally is in cover & healing
                }
                else if (state.allyNeedsHelp && (distressedAllyIndex < 0 || distressedAllyIndex == i))
                {
                    r.material.color = new Color(1.0f, 0.55f, 0.12f); // Bright amber-orange on the Ally calling for help
                }
                else
                {
                    r.material.color = new Color(0.15f, 0.85f, 0.55f); // Bright emerald-teal in open combat
                }
            }
        }

        // Toggle cover cubes visibility when coverAvailable changes
        if (coverTransform != null && coverTransform.gameObject.activeSelf != state.coverAvailable)
        {
            coverTransform.gameObject.SetActive(state.coverAvailable);
        }
        if (eastCoverTransform != null && eastCoverTransform.gameObject.activeSelf != state.coverAvailable)
        {
            eastCoverTransform.gameObject.SetActive(state.coverAvailable);
        }
    }

    private void UpdateDynamicDistances()
    {
        if (enemyTransform != null && state.enemyCount > 0)
        {
            state.enemyDistance = Mathf.Round(Vector3.Distance(transform.position, enemyTransform.position) * 10f) / 10f;
        }
        if (coverTransform != null)
        {
            envContext.nearest_cover_distance = Mathf.Round(Vector3.Distance(transform.position, coverTransform.position) * 10f) / 10f;
        }
    }

    private void SyncStateSnapshot()
    {
        prevNpcRole = npcRole;
        prevHealth = state.health;
        prevAmmo = state.ammo;
        prevEnemyCount = state.enemyCount;
        prevNearbyAllies = state.nearbyAllies;
        prevCoverAvailable = state.coverAvailable;
        prevAllyNeedsHelp = state.allyNeedsHelp;
        prevRole = state.role;
        prevObjectiveUnderThreat = envContext.objective_under_threat;
    }

    private void CheckStateChangeEvents()
    {
        string triggerReason = null;

        if (state.role != prevRole)
        {
            triggerReason = $"NPC role changed ({prevRole} -> {state.role})";
        }
        else if (state.allyNeedsHelp != prevAllyNeedsHelp)
        {
            if (state.allyNeedsHelp)
            {
                allyHelpRequestId++;
            }
            triggerReason = $"Ally help status changed ({prevAllyNeedsHelp} -> {state.allyNeedsHelp})";
        }
        else if (state.coverAvailable != prevCoverAvailable)
        {
            triggerReason = $"Cover availability changed ({prevCoverAvailable} -> {state.coverAvailable})";
        }
        else if ((prevHealth > GetRoleEnterCoverThreshold() && state.health <= GetRoleEnterCoverThreshold()) || (prevHealth > 25f && state.health <= 25f))
        {
            triggerReason = $"Health dropped to critical/low threshold ({prevHealth:F0} -> {state.health:F0})";
        }
        else if (state.enemyCount != prevEnemyCount)
        {
            triggerReason = $"Enemy count changed ({prevEnemyCount} -> {state.enemyCount})";
        }
        else if (prevAmmo > 0 && state.ammo == 0)
        {
            triggerReason = "Ammo depleted (0 remaining)";
        }
        else if (state.nearbyAllies != prevNearbyAllies)
        {
            triggerReason = $"Nearby allies changed ({prevNearbyAllies} -> {state.nearbyAllies})";
        }
        else if (envContext.objective_under_threat != prevObjectiveUnderThreat)
        {
            triggerReason = $"Objective threat changed ({prevObjectiveUnderThreat} -> {envContext.objective_under_threat})";
        }

        if (triggerReason != null)
        {
            SyncStateSnapshot();
            IncrementStateVersion(triggerReason);
            decisionTimer = 0f;
            RequestTacticalDecision();
        }
    }

    private void IncrementStateVersion(string reason)
    {
        currentStateVersion++;
        state.stateVersion = currentStateVersion;
        Debug.Log($"[AI State Event] {reason} -> stateVersion incremented to {currentStateVersion}.");
    }

    // Legal Action Generator: filters valid actions deterministically based on current game rules
    public List<string> GenerateLegalActions()
    {
        List<string> legal = new List<string>();

        // Can only attack if ammo > 0 and at least one enemy is present
        if (state.ammo > 0 && state.enemyCount > 0)
        {
            legal.Add("ATTACK");
        }

        // Can reload whenever magazine is not full
        if (state.ammo < maxAmmo)
        {
            legal.Add("RELOAD");
        }

        // Can only take cover if cover is available AND the Guard is not already healed up inside the cover pocket
        float roleExitCoverThreshold = (npcRole == NpcRole.SCOUT) ? GetRoleExitCoverThreshold() : 70f;
        bool alreadyHealedInCover = Application.isPlaying && IsPhysicallyInCoverPocket() && state.health >= roleExitCoverThreshold;
        if (state.coverAvailable && !alreadyHealedInCover)
        {
            legal.Add("TAKE_COVER");
        }

        // Can retreat or call for help when enemies are present (CALL_FOR_HELP requires at least 1 ally in the current scene)
        if (state.enemyCount > 0)
        {
            legal.Add("RETREAT");
            if (state.nearbyAllies > 0)
            {
                legal.Add("CALL_FOR_HELP");
            }
        }

        // HOLD_POSITION is always available so the legal action list is never empty
        legal.Add("HOLD_POSITION");
        return legal;
    }

    public string GetLegalFallbackAction(List<string> legalActions)
    {
        if (legalActions != null)
        {
            if (legalActions.Contains("TAKE_COVER")) return "TAKE_COVER";
            if (legalActions.Contains("RETREAT")) return "RETREAT";
            if (legalActions.Contains("HOLD_POSITION")) return "HOLD_POSITION";
            if (legalActions.Count > 0) return legalActions[0];
        }
        return state.coverAvailable ? "TAKE_COVER" : "HOLD_POSITION";
    }

    public void RequestTacticalDecision()
    {
        if (isWaitingForDecision)
        {
            // Queue a follow-up request so the latest stateVersion is evaluated as soon as the in-flight call finishes
            pendingDecisionRequest = true;
            return;
        }
        StartCoroutine(SendDecisionRequestCoroutine());
    }

    private IEnumerator WarmupModel()
    {
        using (UnityWebRequest req = UnityWebRequest.Get(warmupUrl))
        {
            req.timeout = Mathf.Max(30, warmupTimeoutSeconds);
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                aiServiceStatus = "online";
                Debug.Log("[AI Warmup] Local LLM loaded into memory and ready.");
            }
            else
            {
                aiServiceStatus = "offline";
                Debug.LogWarning($"[AI Warmup] Could not reach AI service at {warmupUrl} ({req.error}). Running in fallback-ready mode.");
            }
        }
    }

    private IEnumerator SendDecisionRequestCoroutine()
    {
        isWaitingForDecision = true;
        pendingDecisionRequest = false;
        int requestVersion = currentStateVersion;
        state.role = npcRole.ToString();
        state.stateVersion = requestVersion;
        List<string> legalActionsAtRequest = GenerateLegalActions();
        int ammoAtRequest = state.ammo;
        // Remember which Ally help call (if any) this request delivers to the LLM
        int helpIdAtRequest = state.allyNeedsHelp ? allyHelpRequestId : -1;

        DecisionRequestPayload payload = new DecisionRequestPayload
        {
            stateVersion = requestVersion,
            npc = state,
            legalActions = legalActionsAtRequest,
            environment = envContext
        };

        string jsonBody = JsonUtility.ToJson(payload);
        float startTime = Time.realtimeSinceStartup;

        using (UnityWebRequest req = new UnityWebRequest(apiUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = requestTimeoutSeconds;

            yield return req.SendWebRequest();

            int measuredRTTMs = Mathf.RoundToInt((Time.realtimeSinceStartup - startTime) * 1000f);

            // Handle Latency, Timeouts, and Offline AI Service
            if (req.result != UnityWebRequest.Result.Success)
            {
                bool timedOut = req.error != null && req.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                aiServiceStatus = timedOut ? "timeout" : "offline";

                List<string> currentLegalOnError = GenerateLegalActions();
                string fallbackAction = GetLegalFallbackAction(currentLegalOnError);
                LogDecision(
                    requestVersion,
                    currentLegalOnError,
                    new List<string> { "NONE (Error/Timeout)" },
                    fallbackAction,
                    0f,
                    $"Request failed or timed out ({req.error}). Executing deterministic legal fallback.",
                    measuredRTTMs,
                    "FALLBACK",
                    fallbackAction
                );
                ExecuteAction(fallbackAction);
                AcknowledgeAllyHelpRequest(helpIdAtRequest);
                isWaitingForDecision = false;
                if (pendingDecisionRequest)
                {
                    RequestTacticalDecision();
                }
                yield break;
            }

            DecisionResponsePayload response = JsonUtility.FromJson<DecisionResponsePayload>(req.downloadHandler.text);
            if (response == null)
            {
                aiServiceStatus = "offline";
                List<string> currentLegalOnNull = GenerateLegalActions();
                string fallbackAction = GetLegalFallbackAction(currentLegalOnNull);
                LogDecision(
                    requestVersion,
                    currentLegalOnNull,
                    new List<string> { "NONE (Malformed JSON)" },
                    fallbackAction,
                    0f,
                    "Malformed JSON response from AI service. Executing deterministic legal fallback.",
                    measuredRTTMs,
                    "FALLBACK",
                    fallbackAction
                );
                ExecuteAction(fallbackAction);
                AcknowledgeAllyHelpRequest(helpIdAtRequest);
                isWaitingForDecision = false;
                if (pendingDecisionRequest)
                {
                    RequestTacticalDecision();
                }
                yield break;
            }

            aiServiceStatus = (response.reason != null && response.reason.StartsWith("Agent error", StringComparison.OrdinalIgnoreCase))
                ? "offline (server fallback)"
                : "online";

            // Validate Decision & Handle Stale StateVersions
            List<string> currentLegalActions = GenerateLegalActions();
            bool isStale = (response.stateVersion != currentStateVersion);
            bool isLegalNow = !string.IsNullOrEmpty(response.action) &&
                              (currentLegalActions.Contains(response.action) ||
                               (response.action == "RELOAD" && ammoAtRequest <= 0));
            // If a stale decision arrives while the Guard is wounded and recovering in cover, do not let a stale ATTACK/RETREAT/HOLD pull it out of cover
            if (isStale && IsRecoveringInCover() &&
                (response.action == "ATTACK" || response.action == "RETREAT" || response.action == "HOLD_POSITION"))
            {
                isLegalNow = false;
            }

            if (isStale && !isLegalNow)
            {
                string fallback = GetLegalFallbackAction(currentLegalActions);
                LogDecision(
                    response.stateVersion,
                    currentLegalActions,
                    response.toolsCalled,
                    response.action,
                    response.confidence,
                    response.reason + $" [Rejected: Stale stateVersion {response.stateVersion} != {currentStateVersion} and action is no longer legal]",
                    response.latencyMs,
                    "STALE & REJECTED",
                    fallback
                );
                ExecuteAction(fallback);
                pendingDecisionRequest = true;
            }
            else if (isStale)
            {
                LogDecision(
                    response.stateVersion,
                    currentLegalActions,
                    response.toolsCalled,
                    response.action,
                    response.confidence,
                    response.reason + $" [Revalidated against current legal actions (stateVersion {response.stateVersion} -> {currentStateVersion})]",
                    response.latencyMs,
                    "REVALIDATED",
                    response.action
                );
                ExecuteAction(response.action);
                pendingDecisionRequest = true;
            }
            else if (!isLegalNow)
            {
                string fallback = GetLegalFallbackAction(currentLegalActions);
                LogDecision(
                    response.stateVersion,
                    currentLegalActions,
                    response.toolsCalled,
                    response.action,
                    response.confidence,
                    response.reason + " [Rejected: Action not in current legal actions]",
                    response.latencyMs,
                    "REJECTED",
                    fallback
                );
                ExecuteAction(fallback);
            }
            else
            {
                LogDecision(
                    response.stateVersion,
                    currentLegalActions,
                    response.toolsCalled,
                    response.action,
                    response.confidence,
                    response.reason,
                    response.latencyMs,
                    "PASS",
                    response.action
                );
                ExecuteAction(response.action);
            }
        }

        AcknowledgeAllyHelpRequest(helpIdAtRequest);
        isWaitingForDecision = false;
        if (pendingDecisionRequest)
        {
            RequestTacticalDecision();
        }
    }

    // Action Executor: maps validated high-level tactical actions to standard Unity gameplay behaviors
    private void ExecuteAction(string action)
    {
        // Prevent staying stuck in TAKE_COVER if the Guard is already at high health (>= 70 HP) with ammo and not a SCOUT
        if (action == "TAKE_COVER" && state.health >= 70f && state.ammo > 0 && state.enemyCount > 0 && npcRole != NpcRole.SCOUT)
        {
            action = "ATTACK";
        }
        // Keep the Guard in cover until health regenerates to the role's exit threshold (prevents premature ATTACK/RETREAT from cancelling cover healing)
        else if (IsRecoveringInCover() &&
                 (action == "ATTACK" || action == "RETREAT" || action == "HOLD_POSITION"))
        {
            action = "TAKE_COVER";
        }

        state.currentAction = action;

        switch (action)
        {
            case "ATTACK":
                Attack();
                break;
            case "TAKE_COVER":
                TakeCover();
                break;
            case "RETREAT":
                Retreat();
                break;
            case "CALL_FOR_HELP":
                CallForHelp();
                break;
            case "RELOAD":
                Reload();
                break;
            case "HOLD_POSITION":
                HoldPosition();
                break;
        }
    }

    private void Attack()
    {
        hasCalledHelpDuringCurrentCover = false;
        timeSpentInCover = 0f;
        SetNpcColor(GetRoleAttackColor());

        // Immediately fire an opening shot at the enemy (and continue firing at role-specific cadence in UpdateGuardCombatFiring)
        FireGuardShotAtEnemy();
        guardAttackFireTimer = 0f;

        Vector3 enemyPos = GetNearestActiveEnemyPosition();

        // While an Ally needs help, move to a rescue spot between the distressed Ally and the role's combat zone.
        // allyNeedsHelp is NOT cleared here; it stays true until the LLM has received it (see AcknowledgeAllyHelpRequest).
        targetPosition = GetGuardAttackTargetPosition(enemyPos);
        if (state.allyNeedsHelp)
        {
            string allyLabel = distressedAllyIndex >= 0 ? $"Ally #{distressedAllyIndex + 1}" : "Ally";
            Debug.Log($"[AI Cooperation] {state.npcId} ({state.role}) is moving to rescue {allyLabel} at {GetDistressedAllyPosition()} ({GetRoleWeaponProfileLabel()})!");
        }
    }

    // Role attack zone, or a rescue spot toward the distressed Ally while allyNeedsHelp is active
    private Vector3 GetGuardAttackTargetPosition(Vector3 enemyPos)
    {
        Vector3 roleCombatPos = GetRoleAttackPosition(enemyPos);
        if (!state.allyNeedsHelp)
        {
            return roleCombatPos;
        }
        Vector3 rescueSpot = Vector3.Lerp(GetDistressedAllyPosition(), roleCombatPos, 0.55f);
        return new Vector3(rescueSpot.x, transform.position.y, rescueSpot.z);
    }

    private void TakeCover()
    {
        SetNpcColor(new Color(0.18f, 0.55f, 0.95f));
        targetPosition = GetCoverShelterPosition();
    }

    private void Retreat()
    {
        hasCalledHelpDuringCurrentCover = false;
        timeSpentInCover = 0f;
        SetNpcColor(new Color(0.95f, 0.80f, 0.15f));
        if (retreatPoint != null)
            targetPosition = new Vector3(retreatPoint.position.x, transform.position.y, retreatPoint.position.z);
        else if (enemyTransform != null)
            targetPosition = transform.position + (transform.position - enemyTransform.position).normalized * 4f;
    }

    private void CallForHelp()
    {
        SetNpcColor(new Color(0.85f, 0.25f, 0.85f));

        // Do NOT spawn new Allies! Command the existing scene Allies to advance next to the Guard and fire faster for 8 seconds
        // (see supportActive in UpdateExistingAllyMovements). A pending Ally help request is left for the LLM to see.
        allySupportMoveTimer = AllySupportDuration;
        allyCombatFireTimer = AllySupportVolleyInterval; // first support volley fires immediately

        SyncSceneUnitsVisuals();

        // Keep Guard in cover while calling the existing Ally for help
        if (state.coverAvailable && (IsRecoveringInCover() || IsPhysicallyInCoverPocket() || Vector3.Distance(targetPosition, GetCoverShelterPosition()) < 0.5f))
        {
            targetPosition = GetCoverShelterPosition();
        }

        Vector3 rallyPos = GetActiveAllyPosition();
        Debug.Log($"[AI Cooperation] {state.npcId} ({state.role}) executed CALL_FOR_HELP! {state.nearbyAllies} existing Ally unit(s) advancing next to the Guard (rally from {rallyPos}) with rapid support fire for {AllySupportDuration:F0}s!");
        OnCallForHelpTriggered?.Invoke(state.npcId, transform.position);
    }

    private void Reload()
    {
        SetNpcColor(new Color(0.15f, 0.85f, 0.85f));
        state.ammo = maxAmmo;
        prevAmmo = state.ammo;
        guardAttackFireTimer = 0f;
        if (IsRecoveringInCover())
        {
            targetPosition = GetCoverShelterPosition();
        }
        Debug.Log($"[Combat] {state.npcId} ({state.role}) reloaded magazine: {state.ammo}/{maxAmmo}");
    }

    private void HoldPosition()
    {
        SetNpcColor(new Color(0.20f, 0.80f, 0.35f));
        targetPosition = transform.position;
    }

    private void ExecuteContinuousMovement()
    {
        // Clamp targetPosition inside the 4-walled arena boundaries [-7.0, +7.0]
        Vector3 desired = new Vector3(
            Mathf.Clamp(targetPosition.x, -7.0f, 7.0f),
            transform.position.y,
            Mathf.Clamp(targetPosition.z, -7.0f, 7.0f)
        );
        float distBefore = Vector3.Distance(transform.position, desired);
        bool isMovingNow = distBefore > 0.04f;

        if (isMovingNow)
        {
            transform.position = Vector3.MoveTowards(transform.position, desired, GetRoleMoveSpeed() * Time.deltaTime);
        }

        float distAfter = Vector3.Distance(transform.position, desired);
        if (wasMovingToTarget && distAfter <= 0.04f)
        {
            wasMovingToTarget = false;
            UpdateDynamicDistances();
        }
        else
        {
            wasMovingToTarget = distAfter > 0.04f;
        }
    }

    private void SetNpcColor(Color c)
    {
        if (npcRenderer != null) npcRenderer.material.color = c;
    }

    // Keyboard shortcuts using UnityEngine.InputSystem (Keyboard.current)
    private void HandleScenarioHotkeys()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.f1Key.wasPressedThisFrame)
        {
            showHud = !showHud;
        }
        else if (kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame)
        {
            // Scene 1: 0 allies
            ApplyScenario("Scenario A - Strong Position", 90f, 10, 1, 5f, 0, true, false);
        }
        else if (kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame)
        {
            ApplyScenario("Scenario B - Critical Health (Brief: ammo=7)", 15f, 7, 2, 4f, 1, true, true);
        }
        else if (kb.digit6Key.wasPressedThisFrame || kb.numpad6Key.wasPressedThisFrame || kb.bKey.wasPressedThisFrame)
        {
            ApplyScenario("Scenario B - Critical Health (Infographic: ammo=8)", 15f, 8, 2, 4f, 1, true, true);
        }
        else if (kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame)
        {
            ApplyScenario("Scenario C - No Ammunition", 70f, 0, 1, 7f, 1, true, false);
        }
        else if (kb.digit4Key.wasPressedThisFrame || kb.numpad4Key.wasPressedThisFrame)
        {
            // Scene 4: 2 allies
            ApplyScenario("Scenario D - Outnumbered", 40f, 6, 4, 8f, 2, false, true);
        }
        else if (kb.digit5Key.wasPressedThisFrame || kb.numpad5Key.wasPressedThisFrame)
        {
            // Scene 5: 2 allies
            ApplyScenario("Scenario E - Supported Defense", 55f, 4, 2, 12f, 2, true, false);
        }
        else if (kb.digit0Key.wasPressedThisFrame || kb.numpad0Key.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame)
        {
            ResetPositionsHealthAndAmmo();
        }
        else if (kb.hKey.wasPressedThisFrame)
        {
            // Damage hotkey: deals 20 damage to the active NPC
            activeScenarioLabel = $"Damaged (-20 HP -> {Mathf.Max(0f, state.health - 20f):F0} HP)";
            TakeDamage(20f);
        }
        else if (kb.jKey.wasPressedThisFrame)
        {
            // Manual cooperation test hotkey: fires OnCallForHelpTriggered so listening NPCs set allyNeedsHelp=true
            TriggerManualAllyHelpSignal();
        }
        else if (kb.rKey.wasPressedThisFrame)
        {
            // Cycle NPC personality role (GUARD -> SCOUT -> ASSAULT)
            CycleRole();
        }
        else if (kb.cKey.wasPressedThisFrame)
        {
            // Toggle coverAvailable directly so CheckStateChangeEvents detects the "cover changed" trigger
            state.coverAvailable = !state.coverAvailable;
            activeScenarioLabel = $"Custom (Cover Toggled -> {state.coverAvailable})";
        }
    }

    private void ApplyScenario(string scenarioName, float hp, int ammo, int enemies, float dist, int allies, bool cover, bool objectiveThreat)
    {
        activeScenarioLabel = scenarioName;
        currentStateVersion++;
        state.stateVersion = currentStateVersion;
        state.role = npcRole.ToString();
        state.health = hp;
        state.ammo = ammo;
        state.enemyCount = enemies;
        state.enemyDistance = dist;
        state.nearbyAllies = Mathf.Clamp(allies, 0, existingSceneAllies.Count);
        state.coverAvailable = cover;
        state.allyNeedsHelp = false;
        distressedAllyIndex = -1;
        envContext.objective_under_threat = objectiveThreat;

        combatFireDamageTimer = 0f;
        proximityDamageTimer = 0f;
        guardAttackFireTimer = 0f;
        autoReloadTimer = 0f;
        allyDistressTimer = 0f;
        allySupportMoveTimer = 0f;
        allyCombatFireTimer = 0f;
        timeSpentInCover = 0f;
        hasCalledHelpDuringCurrentCover = false;

        EnsureUnitHealthListsSynced();
        for (int i = 0; i < allyHealths.Count; i++)
        {
            allyHealths[i] = 90f;
            allyInCoverFlags[i] = false;
            allyCoverTimers[i] = 0f;
        }
        for (int i = 0; i < enemyHealths.Count; i++)
        {
            enemyHealths[i] = 100f;
            enemyInCoverFlags[i] = false;
            enemyCoverTimers[i] = 0f;
        }

        SyncSceneUnitsVisuals();
        UpdateDynamicDistances();
        SyncStateSnapshot();

        if (hp <= 35f && cover)
        {
            ExecuteAction("TAKE_COVER");
        }
        else if (ammo == 0)
        {
            // Enter RELOAD posture immediately with 0 ammo so HUD shows RELOAD while the LLM also receives ammo=0
            state.currentAction = "RELOAD";
            SetNpcColor(new Color(0.15f, 0.85f, 0.85f));
        }
        else if (!cover && enemies >= 3)
        {
            ExecuteAction("RETREAT");
        }
        else
        {
            ExecuteAction("ATTACK");
        }

        Debug.Log($"<b>=== [{scenarioName} Loaded (Role: {state.role}, stateVersion: {currentStateVersion})] ===</b>");
        decisionTimer = 0f;
        RequestTacticalDecision();
    }

    // Resets ONLY unit positions, healths, and ammunition to their initial values WITHOUT resetting the selected Guard role or active Scene/Scenario
    public void ResetPositionsHealthAndAmmo()
    {
        currentStateVersion++;
        state.stateVersion = currentStateVersion;
        state.role = npcRole.ToString();

        // 1. Reset Guard Health & Ammo to initial values (90 HP, maxAmmo)
        state.health = 90f;
        state.ammo = maxAmmo;
        prevAmmo = maxAmmo;
        state.allyNeedsHelp = false;
        prevAllyNeedsHelp = false;
        distressedAllyIndex = -1;

        // 2. Reset Guard Position to initial starting position
        transform.position = initialGuardPosition;
        targetPosition = initialGuardPosition;

        // 3. Reset Existing Scene Allies' Positions & Healths (90 HP, out of cover)
        EnsureUnitHealthListsSynced();
        for (int i = 0; i < existingSceneAllies.Count; i++)
        {
            if (existingSceneAllies[i] != null)
            {
                Vector3 startPos = (i < initialAllyPositions.Count)
                    ? initialAllyPositions[i]
                    : new Vector3(i % 2 == 0 ? 2.4f : -2.4f, 1f, -1.6f);
                existingSceneAllies[i].position = startPos;
            }
            if (i < allyHealths.Count)
            {
                allyHealths[i] = 90f;
                allyInCoverFlags[i] = false;
                allyCoverTimers[i] = 0f;
            }
        }

        // 4. Reset Active Enemies' Positions & Healths (100 HP, out of cover)
        if (enemyTransform != null)
        {
            enemyTransform.position = initialPrimaryEnemyPosition;
        }
        for (int i = 0; i < extraEnemyUnits.Count; i++)
        {
            if (extraEnemyUnits[i] != null)
            {
                Vector3 slot = i < ExtraEnemySlots.Length
                    ? ExtraEnemySlots[i]
                    : new Vector3(-4.5f + (i % 5) * 2.2f, 1f, 6.4f);
                extraEnemyUnits[i].transform.position = slot;
            }
        }
        for (int i = 0; i < enemyHealths.Count; i++)
        {
            enemyHealths[i] = 100f;
            enemyInCoverFlags[i] = false;
            enemyCoverTimers[i] = 0f;
        }

        // 5. Reset combat/cover timers
        combatFireDamageTimer = 0f;
        proximityDamageTimer = 0f;
        guardAttackFireTimer = 0f;
        autoReloadTimer = 0f;
        allyDistressTimer = 0f;
        allySupportMoveTimer = 0f;
        allyCombatFireTimer = 0f;
        timeSpentInCover = 0f;
        hasCalledHelpDuringCurrentCover = false;

        SyncSceneUnitsVisuals();
        UpdateDynamicDistances();
        SyncStateSnapshot();

        if (state.enemyCount > 0)
        {
            ExecuteAction("ATTACK");
        }
        else
        {
            ExecuteAction("HOLD_POSITION");
        }

        Debug.Log($"[Scene Reset] Positions, healths, and ammo reset to initial values (Preserved Role: {state.role}, Scene: {activeScenarioLabel}).");
        decisionTimer = 0f;
        RequestTacticalDecision();
    }

    // Logging and Observability (all messages in English)
    private void LogDecision(
        int responseStateVersion,
        List<string> legalActions,
        List<string> tools,
        string selectedAction,
        float confidence,
        string reason,
        int latencyMs,
        string validationResult,
        string executedAction)
    {
        lastSelectedAction = selectedAction;
        lastExecutedAction = executedAction;
        lastConfidence = confidence;
        lastReason = reason;
        lastLatencyMs = latencyMs;
        lastValidationResult = validationResult;
        lastToolsCalled = tools != null ? new List<string>(tools) : new List<string>();

        string toolsText = (tools != null && tools.Count > 0) ? string.Join(", ", tools) : "None";
        string log =
            $"<b>[AI DECISION LOG - {DateTime.Now:HH:mm:ss}]</b>\n" +
            $"NPC: {state.npcId} | Role: {state.role} (RequestStateVersion: {responseStateVersion}, CurrentStateVersion: {currentStateVersion})\n" +
            $"State: Health={state.health:F0}, Ammo={state.ammo}, Enemies={state.enemyCount}, Dist={state.enemyDistance}m, Allies={state.nearbyAllies}, Cover={state.coverAvailable}, AllyNeedsHelp={state.allyNeedsHelp}, CurrentAction={state.currentAction}\n" +
            $"Legal Actions: {string.Join(", ", legalActions)}\n" +
            $"MCP Tools Called: {toolsText}\n" +
            $"Selected Action: {selectedAction}\n" +
            $"Confidence: {confidence:F2}\n" +
            $"Reason: {reason}\n" +
            $"Latency: {latencyMs} ms\n" +
            $"Validation Result: {validationResult}\n" +
            $"Executed Action: {executedAction}";

        Debug.Log(log);
    }

    public string GetHudDisplayText()
    {
        List<string> currentLegal = GenerateLegalActions();
        string toolsStr = (lastToolsCalled != null && lastToolsCalled.Count > 0)
            ? string.Join(", ", lastToolsCalled)
            : "None";

        string statusDisplay = isWaitingForDecision ? $"{aiServiceStatus} (request in flight...)" : aiServiceStatus;
        bool isProtectedNow = state.enemyCount > 0 && (IsPhysicallyInCoverPocket() || CountEnemiesWithClearLineOfSight() == 0);
        string coverShieldText = isProtectedNow ? " [SHIELDED +HEALING]" : "";

        EnsureUnitHealthListsSynced();

        // One HUD line per active scene Ally (Scenarios D/E activate 2 Allies)
        int activeAllies = Mathf.Clamp(state.nearbyAllies, 0, Mathf.Min(existingSceneAllies.Count, allyHealths.Count));
        StringBuilder allyLines = new StringBuilder();
        if (activeAllies <= 0)
        {
            allyLines.Append("<b>Ally HP:</b> - [NONE]\n");
        }
        else
        {
            for (int i = 0; i < activeAllies; i++)
            {
                allyLines.Append($"<b>Ally #{i + 1} HP:</b> {allyHealths[i]:F0} [{GetAllyHudStatus(i)}]\n");
            }
        }

        // All active Enemies on a single line, separated by " | "
        int activeEnemies = Mathf.Clamp(state.enemyCount, 0, enemyHealths.Count);
        List<string> enemyParts = new List<string>();
        for (int i = 0; i < activeEnemies; i++)
        {
            enemyParts.Add($"#{i + 1} {enemyHealths[i]:F0}{GetEnemyHudStatus(i, activeEnemies)}");
        }
        string enemyLine = enemyParts.Count > 0 ? string.Join(" | ", enemyParts) : "-";
        string supportText = allySupportMoveTimer > 0f ? $" | <b>Ally Support:</b> {allySupportMoveTimer:F1}s" : "";

        return
            "<b>TACTICAL GUARD NPC - DEBUG HUD</b>\n" +
            $"<b>AI Service:</b> {statusDisplay}\n" +
            $"<b>Scenario:</b> {activeScenarioLabel}\n" +
            $"<b>NPC ID:</b> {state.npcId} | <b>Role:</b> {state.role} ({GetRoleWeaponProfileLabel()})\n" +
            $"<b>Guard HP:</b> {state.health:F0}{coverShieldText} | <b>Ammo:</b> {state.ammo}/{maxAmmo} | <b>StateVer:</b> {currentStateVersion}\n" +
            allyLines.ToString() +
            $"<b>Enemy HP:</b> {enemyLine}\n" +
            $"<b>Enemies:</b> {state.enemyCount} (Dist: {state.enemyDistance:F1}m) | <b>Allies:</b> {state.nearbyAllies}{supportText}\n" +
            $"<b>Cover Available:</b> {state.coverAvailable} (Nearest: {envContext.nearest_cover_distance:F1}m)\n" +
            $"<b>Objective Threat:</b> {envContext.objective_under_threat} | <b>Ally Needs Help:</b> {state.allyNeedsHelp}\n" +
            $"<b>Current Action:</b> {state.currentAction}\n\n" +
            $"<b>Legal Actions:</b>\n{string.Join(", ", currentLegal)}\n\n" +
            $"<b>Last AI Decision:</b> {lastSelectedAction} (Conf: {lastConfidence:F2}, {lastLatencyMs} ms)\n" +
            $"<b>MCP Tools:</b> {toolsStr}\n" +
            $"<b>Reason:</b> {lastReason}\n" +
            $"<b>Validation:</b> {lastValidationResult} -> <b>Executed:</b> {lastExecutedAction}";
    }

    private string GetAllyHudStatus(int index)
    {
        bool flaggedForCover = index < allyInCoverFlags.Count && allyInCoverFlags[index] && state.coverAvailable;
        string status;
        if (flaggedForCover)
        {
            status = IsAllyPhysicallyInCover(index) ? "IN COVER +HEALING" : "MOVING TO COVER";
        }
        else
        {
            status = (allySupportMoveTimer > 0f && state.enemyCount > 0) ? "SUPPORTING GUARD" : "ATTACK";
        }

        if (state.allyNeedsHelp && index == distressedAllyIndex)
        {
            status += ", NEEDS HELP";
        }
        return status;
    }

    // Enemy status for the single-line HUD: full labels when <= 2 enemies, compact labels when >= 3 so 4 enemies stay on 1 line
    private string GetEnemyHudStatus(int index, int totalActiveEnemies)
    {
        bool flaggedForCover = index < enemyInCoverFlags.Count && enemyInCoverFlags[index] && state.coverAvailable;
        bool compact = totalActiveEnemies > 2;
        if (!flaggedForCover)
        {
            return compact ? "" : " [ATTACK]";
        }
        if (IsEnemyPhysicallyInCover(index))
        {
            return compact ? " [HEAL]" : " [IN COVER +HEALING]";
        }
        return compact ? " [COVER]" : " [MOVING TO COVER]";
    }

    public string GetCurrentRoleLabel()
    {
        return state != null ? state.role : npcRole.ToString();
    }

    public void TriggerHudDamage()
    {
        activeScenarioLabel = $"Damaged (-20 HP -> {Mathf.Max(0f, state.health - 20f):F0} HP)";
        TakeDamage(20f);
    }

    public void ToggleHudCover()
    {
        state.coverAvailable = !state.coverAvailable;
        activeScenarioLabel = $"Custom (Cover Toggled -> {state.coverAvailable})";
    }

    public void ApplyHudScenarioA() => ApplyScenario("Scenario A - Strong Position", 90f, 10, 1, 5f, 0, true, false);
    public void ApplyHudScenarioB() => ApplyScenario("Scenario B - Critical Health (Brief: ammo=7)", 15f, 7, 2, 4f, 1, true, true);
    public void ApplyHudScenarioC() => ApplyScenario("Scenario C - No Ammunition", 70f, 0, 1, 7f, 1, true, false);
    public void ApplyHudScenarioD() => ApplyScenario("Scenario D - Outnumbered", 40f, 6, 4, 8f, 2, false, true);
    public void ApplyHudScenarioE() => ApplyScenario("Scenario E - Supported Defense", 55f, 4, 2, 12f, 2, true, false);
}
