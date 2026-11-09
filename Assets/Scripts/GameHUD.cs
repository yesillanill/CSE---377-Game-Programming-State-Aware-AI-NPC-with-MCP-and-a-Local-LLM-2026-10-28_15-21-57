using UnityEngine;
using UnityEngine.UI;

public class GameHUD : MonoBehaviour
{
    [SerializeField] private GuardNPCController guard;
    [SerializeField] private Text infoText;
    [SerializeField] private Button roleButton;
    [SerializeField] private Button damageButton;
    [SerializeField] private Button allyHelpButton;
    [SerializeField] private Button coverButton;
    [SerializeField] private Button scenarioAButton;
    [SerializeField] private Button scenarioBButton;
    [SerializeField] private Button scenarioCButton;
    [SerializeField] private Button scenarioDButton;
    [SerializeField] private Button scenarioEButton;
    [SerializeField] private Button resetButton;

    private void Awake()
    {
        if (guard == null)
        {
            guard = FindAnyObjectByType<GuardNPCController>();
        }

        BindButtons();

        // Scenarios D/E add an extra Ally line; shrink the font automatically instead of truncating the bottom lines
        if (infoText != null)
        {
            infoText.resizeTextForBestFit = true;
            infoText.resizeTextMinSize = 14;
            infoText.resizeTextMaxSize = Mathf.Max(14, infoText.fontSize);
        }
    }

    private void Update()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (guard != null && canvas != null)
        {
            canvas.enabled = guard.showHud;
        }

        if (guard == null || infoText == null || (canvas != null && !canvas.enabled))
        {
            return;
        }

        infoText.text = guard.GetHudDisplayText();
        UpdateButtonLabels();
    }

    public void SetGuard(GuardNPCController controller)
    {
        guard = controller;
    }

    private void BindButtons()
    {
        AddListener(roleButton, guard != null ? guard.CycleRole : null);
        AddListener(damageButton, guard != null ? guard.TriggerHudDamage : null);
        AddListener(allyHelpButton, guard != null ? guard.TriggerManualAllyHelpSignal : null);
        AddListener(coverButton, guard != null ? guard.ToggleHudCover : null);
        AddListener(scenarioAButton, guard != null ? guard.ApplyHudScenarioA : null);
        AddListener(scenarioBButton, guard != null ? guard.ApplyHudScenarioB : null);
        AddListener(scenarioCButton, guard != null ? guard.ApplyHudScenarioC : null);
        AddListener(scenarioDButton, guard != null ? guard.ApplyHudScenarioD : null);
        AddListener(scenarioEButton, guard != null ? guard.ApplyHudScenarioE : null);
        AddListener(resetButton, guard != null ? guard.ResetPositionsHealthAndAmmo : null);
    }

    private static void AddListener(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button == null || action == null)
        {
            return;
        }

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);
    }

    private void UpdateButtonLabels()
    {
        if (guard == null)
        {
            return;
        }

        SetButtonText(roleButton, $"Role: {guard.GetCurrentRoleLabel()} (R)");
        SetButtonText(damageButton, "-20 HP (H)");
        SetButtonText(allyHelpButton, "Ally Help (J)");
        SetButtonText(coverButton, "Cover (C)");
        SetButtonText(scenarioAButton, "1: Scen A");
        SetButtonText(scenarioBButton, "2: Scen B");
        SetButtonText(scenarioCButton, "3: Scen C");
        SetButtonText(scenarioDButton, "4: Scen D");
        SetButtonText(scenarioEButton, "5: Scen E");
        SetButtonText(resetButton, "Reset (0 / Backspace)");
    }

    private static void SetButtonText(Button button, string value)
    {
        if (button == null)
        {
            return;
        }

        Text label = button.GetComponentInChildren<Text>();
        if (label != null)
        {
            label.text = value;
        }
    }
}
