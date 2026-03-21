// ============================================================
//  VelocityOverlay.cs
//  Unity 2017  |  C# 4.0  |  ModTool.Interface / ModBehaviour
// ============================================================

using System.Collections.Generic;
using UnityEngine;
using ModTool.Interface;

public enum VelocityPanelCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public class VelocityOverlay : ModBehaviour
{
    [Header("References")]
    public RoadPathGenerator roadGenerator;

    [Header("Settings")]
    public string playerObjectName = "Player_Human";

    [Range(10f, 2000f)]
    public float visibilityRadius = 500f;

    [Range(5f, 100f)]
    public float innerFadeRadius = 20f;

    [Header("Panel Position")]
    public VelocityPanelCorner panelAnchor = VelocityPanelCorner.TopLeft;
    public Vector2 panelMargin = new Vector2(10f, 10f);
    public float panelWidth = 220f;

    // ── Private state ─────────────────────────────────────────
    private Transform _playerTransform;
    private int _frameCount = 0;
    private bool _playerFoundLogged = false;

    private List<VelocityMarker> _nearbyMarkers = new List<VelocityMarker>();
    private List<float> _fadeValues = new List<float>();

    // GUI textures
    private Texture2D _bgTex;
    private Texture2D _barTex;

    void Start()
    {
        TryFindPlayer();
        BuildTextures();
        if (roadGenerator == null)
        {
            Debug.LogError("[VelocityOverlay] roadGenerator is NULL at Start.");
            return;
        }

        Debug.Log("[VelocityOverlay] showRuntimeVelocityOverlay = " +
                  roadGenerator.showRuntimeVelocityOverlay);

        if (roadGenerator.bakedMarkerPositions == null)
        {
            Debug.LogError("[VelocityOverlay] bakedMarkerPositions is NULL.");
            return;
        }

        Debug.Log("[VelocityOverlay] bakedMarkerPositions count = " +
                  roadGenerator.bakedMarkerPositions.Length);

        if (roadGenerator.bakedMarkerPositions.Length > 0)
        {
            Debug.Log("[VelocityOverlay] First marker — position: " +
                      roadGenerator.bakedMarkerPositions[0] +
                      "  kmh: " + roadGenerator.bakedMarkerKmh[0] +
                      "  velScale: " + roadGenerator.bakedMarkerVelScale[0]);
        }
    }

    void Update()
    {
        _frameCount++;
        if (_frameCount % 30 == 0) TryFindPlayer();

        if (_playerTransform == null || roadGenerator == null) return;

        _nearbyMarkers.Clear();
        _fadeValues.Clear();

        Vector3 playerPos = _playerTransform.position;

        Vector3[] positions = roadGenerator.bakedMarkerPositions;
        float[] kmhArr = roadGenerator.bakedMarkerKmh;
        float[] vsArr = roadGenerator.bakedMarkerVelScale;

        if (positions == null || positions.Length == 0) return;

        for (int i = 0; i < positions.Length; i++)
        {
            float dist = Vector3.Distance(playerPos, positions[i]);
            if (dist > visibilityRadius) continue;

            float fade = dist <= innerFadeRadius ? 1f
                : 1f - Mathf.Clamp01((dist - innerFadeRadius) /
                  (visibilityRadius - innerFadeRadius));

            if (fade < 0.01f) continue;

            //int[] typeArr = roadGenerator.bakedMarkerType;
            _nearbyMarkers.Add(new VelocityMarker
            {
                position = positions[i],
                kmh = kmhArr[i],
                velScale = vsArr[i]
            });
            _fadeValues.Add(fade);
        }
    }

    void OnGUI()
    {
        if (roadGenerator == null) return;
        if (!roadGenerator.showRuntimeVelocityOverlay) return;

        // Always draw the panel — even when no markers nearby
        // so we can confirm the overlay is working.
        float rowH = 22f;
        float padding = 8f;

        int rowCount = _nearbyMarkers.Count > 0 ? _nearbyMarkers.Count : 1;
        float panelH = padding + 20f + rowCount * rowH + padding;

        Rect panelRect = ComputePanelRect(panelH);

        // Background
        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(panelRect, _bgTex);
        GUI.color = Color.white;

        // Header
        GUIStyle header = new GUIStyle(GUI.skin.label);
        header.fontStyle = FontStyle.Bold;
        header.fontSize = 12;
        header.normal.textColor = Color.white;
        header.alignment = TextAnchor.MiddleLeft;

        int total = roadGenerator.bakedMarkerPositions != null
            ? roadGenerator.bakedMarkerPositions.Length : 0;

        GUI.Label(new Rect(panelRect.x + padding, panelRect.y + padding,
                           panelWidth - padding * 2, 20f),
            "Velocity Markers  [" + _nearbyMarkers.Count +
            " near / " + total + " total]", header);

        float rowY = panelRect.y + padding + 20f;

        if (_nearbyMarkers.Count == 0)
        {
            GUIStyle none = new GUIStyle(GUI.skin.label);
            none.fontSize = 11;
            none.normal.textColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            none.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(panelRect.x + padding, rowY,
                               panelWidth - padding * 2, rowH),
                "No markers within " + visibilityRadius + " m", none);
            return;
        }

        GUIStyle rowStyle = new GUIStyle(GUI.skin.label);
        rowStyle.fontSize = 12;
        rowStyle.fontStyle = FontStyle.Bold;
        rowStyle.alignment = TextAnchor.MiddleLeft;

        for (int i = 0; i < _nearbyMarkers.Count; i++)
        {
            VelocityMarker marker = _nearbyMarkers[i];
            float fade = _fadeValues[i];
            float vs = marker.velScale;

            int markerType = (roadGenerator.bakedMarkerType != null &&
                              i < roadGenerator.bakedMarkerType.Length)
                             ? roadGenerator.bakedMarkerType[i] : 0;

            Color markerColor;
            switch (markerType)
            {
                case 1: // Crest — purple
                    markerColor = new Color(0.6f, 0.2f, 1.0f, fade); break;
                case 2: // Flat baseline — grey
                    markerColor = new Color(0.75f, 0.75f, 0.75f, fade); break;
                case 3: // Cross-slope — orange
                    markerColor = new Color(1.0f, 0.5f, 0.0f, fade); break;
                case 4: // Tight curve — cyan
                    markerColor = new Color(0.0f, 0.9f, 0.9f, fade); break;
                case 5: // Sustained steep — bright red
                    markerColor = new Color(1.0f, 0.1f, 0.1f, fade); break;
                default: // Grade — green/yellow/red gradient
                    if (vs <= 0.5f)
                        markerColor = new Color(0.75f, 0.75f, 0.75f, fade);
                    else if (vs <= 50f)
                        markerColor = Color.Lerp(Color.green,
                                      new Color(1f, 1f, 0f, 1f), vs / 50f);
                    else
                        markerColor = Color.Lerp(new Color(1f, 1f, 0f, 1f),
                                      Color.red, (vs - 50f) / 50f);
                    markerColor.a = fade;
                    break;
            }

            // Colour bar on left
            float barW = 6f;
            GUI.color = markerColor;
            GUI.DrawTexture(new Rect(panelRect.x + padding, rowY + 4f,
                                     barW, rowH - 8f), _barTex);
            GUI.color = Color.white;

            // Label
            string typeLabel = "";
            switch (markerType)
            {
                case 1: typeLabel = "  Crest"; break;
                case 2: typeLabel = "  Flat"; break;
                case 3: typeLabel = "  Cross-slope"; break;
                case 4: typeLabel = "  Curve"; break;
                case 5: typeLabel = "  Sustained"; break;
            }
            string kmhText = Mathf.RoundToInt(marker.kmh) + " km/h";
            string tag = marker.kmh <= 25.5f ? "  baseline"
                           : vs >= 80f ? "  MAX"
                           : typeLabel;

            rowStyle.normal.textColor = markerColor;
            GUI.Label(new Rect(panelRect.x + padding + barW + 4f,
                               rowY, panelWidth - padding * 2 - barW - 4f,
                               rowH), kmhText + tag, rowStyle);

            // Speed bar background
            float barFullW = panelWidth - padding * 2 - barW - 4f;
            float barFill = (vs / 100f) * barFullW;
            GUI.color = new Color(0.2f, 0.2f, 0.2f, fade * 0.5f);
            GUI.DrawTexture(new Rect(panelRect.x + padding + barW + 4f,
                                     rowY + rowH - 5f, barFullW, 3f), _bgTex);
            GUI.color = new Color(markerColor.r, markerColor.g,
                                  markerColor.b, fade * 0.8f);
            GUI.DrawTexture(new Rect(panelRect.x + padding + barW + 4f,
                                     rowY + rowH - 5f, barFill, 3f), _barTex);
            GUI.color = Color.white;

            rowY += rowH;
        }
    }

    Rect ComputePanelRect(float panelH)
    {
        float sw = Screen.width;
        float sh = Screen.height;
        float x, y;

        switch (panelAnchor)
        {
            case VelocityPanelCorner.TopLeft:
                x = panelMargin.x; y = panelMargin.y; break;
            case VelocityPanelCorner.TopRight:
                x = sw - panelWidth - panelMargin.x; y = panelMargin.y; break;
            case VelocityPanelCorner.BottomRight:
                x = sw - panelWidth - panelMargin.x;
                y = sh - panelH - panelMargin.y; break;
            default:
                x = panelMargin.x;
                y = sh - panelH - panelMargin.y; break;
        }
        return new Rect(x, y, panelWidth, panelH);
    }

    void BuildTextures()
    {
        _bgTex = new Texture2D(1, 1);
        _bgTex.SetPixel(0, 0, Color.white);
        _bgTex.Apply();

        _barTex = new Texture2D(1, 1);
        _barTex.SetPixel(0, 0, Color.white);
        _barTex.Apply();
    }

    void TryFindPlayer()
    {
        if (_playerTransform != null) return;
        GameObject go = GameObject.Find(playerObjectName);
        if (go == null) return;
        _playerTransform = go.transform;
        if (!_playerFoundLogged)
        {
            Debug.Log("[VelocityOverlay] Player found: " + playerObjectName);
            _playerFoundLogged = true;
        }
    }
}
