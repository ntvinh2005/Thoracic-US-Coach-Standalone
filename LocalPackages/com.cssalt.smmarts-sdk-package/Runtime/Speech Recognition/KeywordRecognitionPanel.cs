using SMMARTS;
using UnityEngine;

/// <summary>
/// Panel for Advanced Users with Shift+K.
/// Runtime debugging interface for monitoring keyword recognition keyphrases.
/// Activated with Left Shift + K. Designed for developers who need to troubleshoot
/// keyword recognition functionality and see what keyphrases are currently loaded.
/// </summary>
public class KeywordRecognitionPanel : MonoBehaviour
{
    public bool showSettings = false;
    public float GUI_ScaleFactorAdjust = 1f;
    public float scaleFactor;

    // UI state management
    private Vector2 middleScrollPosition = Vector2.zero;
    private Vector2 rightScrollPosition = Vector2.zero;

    /// <summary>
    /// Handles keyboard input for toggling the keyphrase debugging interface.
    /// Left Shift + K activates/deactivates the debugging panel.
    /// </summary>
    void Update()
    {
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.K))
            showSettings = !showSettings;
    }

    /// <summary>
    /// Allows outside scripts to turn on and off the panel.
    /// </summary>
    public void ActivatePanel()
    {
        showSettings = true;
    }
    public void DeactivatePanel()
    {
        showSettings = false;
    }

    /// <summary>
    /// Renders the keyphrase debugging GUI showing currently loaded keyphrases.
    /// </summary>
    void OnGUI()
    {
        if (!showSettings) return;

        // Auto-calculate scale factor to fit all content
        scaleFactor = CalculateOptimalScaleFactor();
        GUI.matrix = Matrix4x4.Scale(new Vector3(scaleFactor, scaleFactor, 1));

        // DPI Scaling System
        scaleFactor = Screen.dpi / 96f * GUI_ScaleFactorAdjust;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scaleFactor, scaleFactor, 1));

        // Panel sizing
        float panelWidth = (Screen.width * 0.8f) / scaleFactor;
        float panelHeight = (Screen.height * 0.95f) / scaleFactor;

        // Center the panel
        float x = (Screen.width - panelWidth * scaleFactor) / 2 / scaleFactor;
        float y = (Screen.height - panelHeight * scaleFactor) / 2 / scaleFactor;

        // Create styled background
        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.90f));
        GUI.Box(new Rect(x, y, panelWidth, panelHeight), "", boxStyle);

        GUILayout.BeginArea(new Rect(x + 10, y + 10, panelWidth - 20, panelHeight - 20));

        // Header
        GUILayout.Label("KEYPHRASE PANEL - Keyword Recognition",
                       new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold });

        // Three-column layout
        GUILayout.BeginHorizontal();

        // Left Column - Reserved for future inputs
        DrawInputColumn(panelWidth);

        // Middle Column - First half of keyphrases
        DrawMiddleKeyphraseColumn(panelWidth);

        // Right Column - Second half of keyphrases (overflow)
        DrawRightKeyphraseColumn(panelWidth);

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }


    private float CalculateOptimalScaleFactor()
    {
        if (KeywordRecognitionManager.ME?.keywords == null)
            return Screen.dpi / 96f * GUI_ScaleFactorAdjust; // Fallback to normal scaling

        // Estimate space needed
        int keywordCount = KeywordRecognitionManager.ME.keywords.Length;
        int maxKeywordsPerColumn = Mathf.CeilToInt(keywordCount / 2f);

        // Estimate pixels needed (rough calculation)
        float headerHeight = 60f; // Panel header
        float columnHeaderHeight = 30f; // Column headers
        float lineHeight = 20f; // Per keyword line
        float padding = 40f; // General padding

        float neededHeight = headerHeight + columnHeaderHeight + (maxKeywordsPerColumn * lineHeight) + padding;
        float availableHeight = Screen.height * 0.95f;

        // Calculate scale that fits the content
        float contentScaleFactor = availableHeight / neededHeight;

        // Apply your GUI scale adjustment and clamp to reasonable bounds
        float finalScale = contentScaleFactor * GUI_ScaleFactorAdjust;
        return Mathf.Clamp(finalScale, 0.5f, 3.0f); // Don't go too tiny or huge
    }

    private void DrawInputColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));

        GUILayout.Label("--- Recognition Status ---", GetHeaderStyle());

        if (KeywordRecognitionManager.ME != null)
        {
            // Show basic status info for now
            GUILayout.Label($"Manager: Connected");
            GUILayout.Label($"Total Keyphrases: {(KeywordRecognitionManager.ME.keywords?.Length ?? 0)}");
        }
        else
        {
            GUILayout.Label("Manager: Not Found");
        }

        GUILayout.Space(10);
        GUILayout.Label("--- Future Controls ---", GetHeaderStyle());
        GUILayout.Label("(Reserved for inputs)");

        GUILayout.EndVertical();
    }

    private void DrawMiddleKeyphraseColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));

        GUILayout.Label("--- Keyphrases (1) ---", GetHeaderStyle());

        if (KeywordRecognitionManager.ME?.keywords != null)
        {
            // Calculate available height dynamically
            float availableHeight = (Screen.height * 0.95f) / scaleFactor - 120; // Subtract header space
            middleScrollPosition = GUILayout.BeginScrollView(middleScrollPosition, GUILayout.Height(availableHeight));

            string[] keywords = KeywordRecognitionManager.ME.keywords;
            int halfCount = Mathf.CeilToInt(keywords.Length / 2f);

            for (int i = 0; i < halfCount && i < keywords.Length; i++)
            {
                GUILayout.Label($"{i + 1}: {keywords[i]}");
            }

            GUILayout.EndScrollView();
        }
        else
        {
            GUILayout.Label("No keyphrases loaded");
        }

        GUILayout.EndVertical();
    }

    private void DrawRightKeyphraseColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));

        GUILayout.Label("--- Keyphrases (2) ---", GetHeaderStyle());

        if (KeywordRecognitionManager.ME?.keywords != null)
        {
            // Same dynamic height calculation
            float availableHeight = (Screen.height * 0.95f) / scaleFactor - 120;
            rightScrollPosition = GUILayout.BeginScrollView(rightScrollPosition, GUILayout.Height(availableHeight));

            string[] keywords = KeywordRecognitionManager.ME.keywords;
            int halfCount = Mathf.CeilToInt(keywords.Length / 2f);

            for (int i = halfCount; i < keywords.Length; i++)
            {
                GUILayout.Label($"{i + 1}: {keywords[i]}");
            }

            GUILayout.EndScrollView();
        }
        else
        {
            GUILayout.Label("Overflow area");
        }

        GUILayout.EndVertical();
    }
    private GUIStyle GetHeaderStyle()
    {
        return new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.yellow }
        };
    }

    /// <summary>
    /// Creates a solid color texture for GUI styling purposes.
    /// </summary>
    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; i++)
            pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }
}