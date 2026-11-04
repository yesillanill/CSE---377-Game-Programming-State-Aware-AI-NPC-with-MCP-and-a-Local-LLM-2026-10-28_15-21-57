#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

[InitializeOnLoad]
public static class TacticalSceneSetupEditor
{
    static TacticalSceneSetupEditor()
    {
        EditorApplication.delayCall += EnsureSceneAndPrefabsSetup;
    }

    [MenuItem("Tools/CSE-377/Setup Scene Walls & Prefabs")]
    public static void EnsureSceneAndPrefabsSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        bool sceneModified = false;

        // 1. Ensure the 4 symmetric rectangular arena walls and secondary cover exist directly in the Scene Hierarchy
        EnsureSceneCube("BackWall", new Vector3(0f, 0.6f, 8f), new Vector3(16.5f, 1.2f, 0.5f), ref sceneModified);
        EnsureSceneCube("FrontWall", new Vector3(0f, 0.6f, -8f), new Vector3(16.5f, 1.2f, 0.5f), ref sceneModified);
        EnsureSceneCube("LeftWall", new Vector3(-8f, 0.6f, 0f), new Vector3(0.5f, 1.2f, 15.5f), ref sceneModified);
        EnsureSceneCube("RightWall", new Vector3(8f, 0.6f, 0f), new Vector3(0.5f, 1.2f, 15.5f), ref sceneModified);
        GameObject eastCover = EnsureSceneCube("CoverCubeEast", new Vector3(3.0f, 0.5f, 1.5f), new Vector3(2.2f, 1.0f, 0.5f), ref sceneModified);

        // Align primary CoverCube symmetrically with CoverCubeEast
        GameObject westCover = GameObject.Find("CoverCube");
        if (westCover != null)
        {
            Vector3 desiredPos = new Vector3(-3.0f, 0.5f, 1.5f);
            Vector3 desiredScale = new Vector3(2.2f, 1.0f, 0.5f);
            if (Vector3.Distance(westCover.transform.position, desiredPos) > 0.01f ||
                Vector3.Distance(westCover.transform.localScale, desiredScale) > 0.01f)
            {
                westCover.transform.position = desiredPos;
                westCover.transform.rotation = Quaternion.identity;
                westCover.transform.localScale = desiredScale;
                sceneModified = true;
            }
        }

        // 2. Ensure the 2 permanent Ally NPCs exist directly in the Scene Hierarchy (no runtime ally spawning)
        GameObject sceneAlly = EnsureSceneCapsule("Ally", new Vector3(2.4f, 1f, -1.6f), ref sceneModified);
        EnsureSceneCapsule("Ally2", new Vector3(-2.4f, 1f, -1.6f), ref sceneModified);

        // 3. Link scene references on GuardNPCController
        GuardNPCController guard = Object.FindAnyObjectByType<GuardNPCController>();
        if (guard != null)
        {
            GameObject enemyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyUnit.prefab");
            GameObject allyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/AllyNPC.prefab");

            if (enemyPrefab != null && guard.enemyPrefab != enemyPrefab)
            {
                guard.enemyPrefab = enemyPrefab;
                EditorUtility.SetDirty(guard);
                sceneModified = true;
            }
            if (allyPrefab != null && guard.allyPrefab != allyPrefab)
            {
                guard.allyPrefab = allyPrefab;
                EditorUtility.SetDirty(guard);
                sceneModified = true;
            }
            if (eastCover != null && guard.eastCoverTransform != eastCover.transform)
            {
                guard.eastCoverTransform = eastCover.transform;
                EditorUtility.SetDirty(guard);
                sceneModified = true;
            }
            if (sceneAlly != null && guard.allyTransform != sceneAlly.transform)
            {
                guard.allyTransform = sceneAlly.transform;
                EditorUtility.SetDirty(guard);
                sceneModified = true;
            }
        }

        EnsureHud(guard, ref sceneModified);

        if (sceneModified && !Application.isPlaying)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
        }
    }

    private static void EnsureHud(GuardNPCController guard, ref bool modified)
    {
        GameObject canvasObject = GameObject.Find("GameHUD");
        if (canvasObject == null)
        {
            canvasObject = new GameObject("GameHUD", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(GameHUD));
            modified = true;
        }

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        GameObject panel = FindOrCreateChild(canvasObject.transform, "Panel", typeof(Image));
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 1f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(18f, -18f);
        panelRect.sizeDelta = new Vector2(620f, 920f);
        Image panelImage = panel.GetComponent<Image>();
        panelImage.color = new Color(0.035f, 0.05f, 0.08f, 0.94f);

        GameObject title = FindOrCreateChild(panel.transform, "Title", typeof(Text));
        RectTransform titleRect = title.GetComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.offsetMin = new Vector2(20f, -72f);
        titleRect.offsetMax = new Vector2(-20f, -20f);
        Text titleText = title.GetComponent<Text>();
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        titleText.fontSize = 28;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.UpperLeft;
        titleText.color = new Color(0.35f, 0.82f, 1f);
        titleText.text = "TACTICAL GUARD NPC";

        GameObject info = FindOrCreateChild(panel.transform, "Info", typeof(Text));
        RectTransform infoRect = info.GetComponent<RectTransform>();
        infoRect.anchorMin = new Vector2(0f, 1f);
        infoRect.anchorMax = new Vector2(1f, 1f);
        infoRect.pivot = new Vector2(0.5f, 1f);
        infoRect.offsetMin = new Vector2(20f, -565f);
        infoRect.offsetMax = new Vector2(-20f, -84f);
        Text infoText = info.GetComponent<Text>();
        infoText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        infoText.fontSize = 21;
        infoText.lineSpacing = 1.05f;
        infoText.supportRichText = true;
        infoText.color = new Color(0.93f, 0.96f, 1f);
        infoText.alignment = TextAnchor.UpperLeft;
        infoText.text = "AI SERVICE: Starting...\nSCENE: Tactical Arena\nNPC: Guard 01\n\nWaiting for the local AI agent...";

        GameObject buttons = FindOrCreateChild(panel.transform, "Buttons", typeof(RectTransform),
            typeof(VerticalLayoutGroup));
        RectTransform buttonsRect = buttons.GetComponent<RectTransform>();
        buttonsRect.anchorMin = new Vector2(0f, 0f);
        buttonsRect.anchorMax = new Vector2(1f, 0f);
        buttonsRect.pivot = new Vector2(0.5f, 0f);
        buttonsRect.offsetMin = new Vector2(20f, 18f);
        buttonsRect.offsetMax = new Vector2(-20f, 350f);
        VerticalLayoutGroup layout = buttons.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 7f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        RemoveLegacyButton(buttons.transform, "RoleButton");
        RemoveLegacyButton(buttons.transform, "DamageButton");
        RemoveLegacyButton(buttons.transform, "AllyHelpButton");
        RemoveLegacyButton(buttons.transform, "CoverButton");
        RemoveLegacyButton(buttons.transform, "ScenarioAButton");
        RemoveLegacyButton(buttons.transform, "ScenarioBButton");
        RemoveLegacyButton(buttons.transform, "ScenarioCButton");
        RemoveLegacyButton(buttons.transform, "ScenarioDButton");
        RemoveLegacyButton(buttons.transform, "ScenarioEButton");
        RemoveLegacyButton(buttons.transform, "ResetButton");
        RemoveLegacyButton(buttons.transform, "ControlsLabel");
        RemoveLegacyButton(buttons.transform, "ScenariosLabel");
        RemoveLegacyButton(buttons.transform, "ResetLabel");

        GameObject controlsRow = EnsureRow(buttons.transform, "ControlsRow");
        Button role = EnsureButton(controlsRow.transform, "RoleButton", new Color(0.10f, 0.38f, 0.58f));
        Button damage = EnsureButton(controlsRow.transform, "DamageButton", new Color(0.58f, 0.20f, 0.18f));
        Button allyHelp = EnsureButton(controlsRow.transform, "AllyHelpButton", new Color(0.48f, 0.27f, 0.12f));
        Button cover = EnsureButton(controlsRow.transform, "CoverButton", new Color(0.12f, 0.43f, 0.34f));

        GameObject scenariosRow = EnsureRow(buttons.transform, "ScenariosRow");
        Button scenarioA = EnsureButton(scenariosRow.transform, "ScenarioAButton", new Color(0.16f, 0.36f, 0.48f));
        Button scenarioB = EnsureButton(scenariosRow.transform, "ScenarioBButton", new Color(0.50f, 0.25f, 0.18f));
        Button scenarioC = EnsureButton(scenariosRow.transform, "ScenarioCButton", new Color(0.42f, 0.25f, 0.48f));
        Button scenarioD = EnsureButton(scenariosRow.transform, "ScenarioDButton", new Color(0.52f, 0.18f, 0.18f));
        Button scenarioE = EnsureButton(scenariosRow.transform, "ScenarioEButton", new Color(0.16f, 0.43f, 0.31f));

        GameObject resetRow = EnsureRow(buttons.transform, "ResetRow");
        Button reset = EnsureButton(resetRow.transform, "ResetButton", new Color(0.25f, 0.28f, 0.34f));

        GameHUD hud = canvasObject.GetComponent<GameHUD>();
        SerializedObject serializedHud = new SerializedObject(hud);
        SetReference(serializedHud, "guard", guard);
        SetReference(serializedHud, "infoText", infoText);
        SetReference(serializedHud, "roleButton", role);
        SetReference(serializedHud, "damageButton", damage);
        SetReference(serializedHud, "allyHelpButton", allyHelp);
        SetReference(serializedHud, "coverButton", cover);
        SetReference(serializedHud, "scenarioAButton", scenarioA);
        SetReference(serializedHud, "scenarioBButton", scenarioB);
        SetReference(serializedHud, "scenarioCButton", scenarioC);
        SetReference(serializedHud, "scenarioDButton", scenarioD);
        SetReference(serializedHud, "scenarioEButton", scenarioE);
        SetReference(serializedHud, "resetButton", reset);
        serializedHud.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hud);
        modified = true;
    }

    private static GameObject FindOrCreateChild(Transform parent, string name, params System.Type[] components)
    {
        Transform existing = parent.Find(name);
        if (existing != null)
        {
            return existing.gameObject;
        }

        GameObject child = new GameObject(name, components);
        child.transform.SetParent(parent, false);
        return child;
    }

    private static void RemoveLegacyButton(Transform parent, string name)
    {
        Transform legacyButton = parent.Find(name);
        if (legacyButton != null)
        {
            Object.DestroyImmediate(legacyButton.gameObject);
        }
    }

    private static GameObject EnsureRow(Transform parent, string name)
    {
        GameObject row = FindOrCreateChild(parent, name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
        RectTransform rect = row.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 42f);
        HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        LayoutElement element = row.GetComponent<LayoutElement>();
        if (element == null) element = row.AddComponent<LayoutElement>();
        element.minHeight = 42f;
        element.preferredHeight = 42f;
        return row;
    }

    private static void EnsureSectionLabel(Transform parent, string name, string text)
    {
        GameObject labelObject = FindOrCreateChild(parent, name, typeof(RectTransform), typeof(Text));
        LayoutElement element = labelObject.GetComponent<LayoutElement>();
        if (element == null) element = labelObject.AddComponent<LayoutElement>();
        element.minHeight = 24f;
        element.preferredHeight = 24f;
        Text label = labelObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 16;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleLeft;
        label.color = new Color(0.48f, 0.70f, 0.80f);
        label.text = text;
    }

    private static Button EnsureButton(Transform parent, string name, Color color)
    {
        GameObject buttonObject = FindOrCreateChild(parent, name, typeof(RectTransform), typeof(Image), typeof(Button));
        RectTransform rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0f, 38f);
        LayoutElement layoutElement = buttonObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = buttonObject.AddComponent<LayoutElement>();
        }
        layoutElement.minHeight = 38f;
        layoutElement.preferredHeight = 38f;
        layoutElement.flexibleHeight = 0f;
        Image image = buttonObject.GetComponent<Image>();
        image.color = color;

        GameObject labelObject = FindOrCreateChild(buttonObject.transform, "Label", typeof(Text));
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        Text label = labelObject.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 15;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.text = GetInitialButtonLabel(name);

        return buttonObject.GetComponent<Button>();
    }

    private static string GetInitialButtonLabel(string name)
    {
        switch (name)
        {
            case "RoleButton":
                return "Role: GUARD (R)";
            case "DamageButton":
                return "-20 HP (H)";
            case "AllyHelpButton":
                return "Ally Help (J)";
            case "CoverButton":
                return "Cover (C)";
            case "ScenarioAButton":
                return "1: Scen A";
            case "ScenarioBButton":
                return "2: Scen B";
            case "ScenarioCButton":
                return "3: Scen C";
            case "ScenarioDButton":
                return "4: Scen D";
            case "ScenarioEButton":
                return "5: Scen E";
            case "ResetButton":
                return "Reset (0 / Backspace)";
            default:
                return name;
        }
    }

    private static void SetReference(SerializedObject serializedObject, string propertyName, Object value)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property != null)
        {
            property.objectReferenceValue = value;
        }
    }

    private static GameObject EnsureSceneCube(string name, Vector3 position, Vector3 scale, ref bool modified)
    {
        GameObject obj = GameObject.Find(name);
        if (obj == null)
        {
            obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obj.name = name;
            modified = true;
        }

        if (Vector3.Distance(obj.transform.position, position) > 0.01f ||
            Vector3.Distance(obj.transform.localScale, scale) > 0.01f ||
            obj.transform.rotation != Quaternion.identity)
        {
            obj.transform.position = position;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = scale;
            modified = true;
        }

        return obj;
    }

    private static GameObject EnsureSceneCapsule(string name, Vector3 position, ref bool modified)
    {
        GameObject obj = GameObject.Find(name);
        if (obj == null)
        {
            obj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            obj.name = name;
            obj.transform.position = position;
            obj.transform.rotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            modified = true;
        }
        return obj;
    }
}
#endif
