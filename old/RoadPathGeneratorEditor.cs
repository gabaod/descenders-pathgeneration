// ============================================================
//  RoadPathGeneratorEditor.cs
//  Place this file inside an "Editor" folder in your project.
//  Unity 2017  |  C# 4.0
// ============================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(RoadPathGenerator))]
public class RoadPathGeneratorEditor : Editor
{
    private RoadPathGenerator _gen;
    private bool _editMode = false;

    private static readonly GUIContent _labelEditMode
        = new GUIContent("Edit Points Mode",
            "Toggle to move control points with scene handles.");

    // ─────────────────────────────────────────────────────────
    //  Tools Menu
    // ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Road Path Generator/Create Road Generator")]
    public static void CreateRoadGenerator()
    {
        GameObject go = new GameObject("RoadPathGenerator");
        go.AddComponent<RoadPathGenerator>();
        Selection.activeGameObject = go;
        Undo.RegisterCreatedObjectUndo(go, "Create Road Path Generator");
        Debug.Log("[RoadPathGenerator] Created new Road Path Generator object.");
    }

    [MenuItem("Tools/Road Path Generator/Add Control Point To Selected")]
    public static void AddControlPointToSelected()
    {
        RoadPathGenerator gen = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<RoadPathGenerator>()
            : null;

        if (gen == null)
        {
            Debug.LogWarning("[RoadPathGenerator] Select a GameObject with RoadPathGenerator first.");
            return;
        }

        GameObject pt = new GameObject("RoadPoint_" + gen.controlPoints.Count);
        pt.transform.SetParent(gen.transform);

        if (gen.controlPoints != null && gen.controlPoints.Count > 0)
        {
            Transform last = gen.controlPoints[gen.controlPoints.Count - 1];
            pt.transform.position = (last != null)
                ? last.position + last.forward * 5f
                : gen.transform.position;
        }
        else
        {
            pt.transform.position = gen.transform.position;
        }

        if (gen.controlPoints == null) gen.controlPoints = new List<Transform>();
        gen.controlPoints.Add(pt.transform);

        Undo.RegisterCreatedObjectUndo(pt, "Add Road Control Point");
        EditorUtility.SetDirty(gen);
        Debug.Log("[RoadPathGenerator] Added: " + pt.name);
    }

    [MenuItem("Tools/Road Path Generator/Generate Selected Road")]
    public static void GenerateSelectedRoad()
    {
        RoadPathGenerator gen = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<RoadPathGenerator>()
            : null;

        if (gen == null)
        {
            Debug.LogWarning("[RoadPathGenerator] Select a GameObject with RoadPathGenerator first.");
            return;
        }

        Undo.RecordObject(gen, "Generate Road");
        gen.GenerateRoad();
        EditorUtility.SetDirty(gen);
    }

    // ─────────────────────────────────────────────────────────
    //  Editor lifecycle
    // ─────────────────────────────────────────────────────────

    void OnEnable()
    {
        _gen = (RoadPathGenerator)target;
    }

    // ─────────────────────────────────────────────────────────
    //  Inspector GUI
    // ─────────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // ── Header ────────────────────────────────────────────
        GUILayout.Space(4f);
        GUIStyle title = new GUIStyle(EditorStyles.boldLabel);
        title.fontSize  = 13;
        title.alignment = TextAnchor.MiddleCenter;
        EditorGUILayout.LabelField("  Road / Path Generator", title);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(2f);

        // ── Edit mode toggle ──────────────────────────────────
        _editMode = GUILayout.Toggle(_editMode, _labelEditMode, "Button",
                                     GUILayout.Height(24));
        GUILayout.Space(4f);

        // ── Default inspector fields ──────────────────────────
        DrawDefaultInspector();

        GUILayout.Space(8f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        // ── Control-point management ──────────────────────────
        EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("+ Add Point", GUILayout.Height(26)))
            AddPointAtEnd();

        bool hasPoints = _gen.controlPoints != null && _gen.controlPoints.Count > 0;
        GUI.enabled = hasPoints;
        if (GUILayout.Button("- Remove Last", GUILayout.Height(26)))
        {
            Undo.RecordObject(_gen, "Remove Road Point");
            _gen.RemoveControlPoint(_gen.controlPoints.Count - 1);
            EditorUtility.SetDirty(_gen);
        }
        GUI.enabled = true;

        if (GUILayout.Button("Clear All", GUILayout.Height(26)))
        {
            if (EditorUtility.DisplayDialog("Clear Road Points",
                "Remove all control points?", "Yes", "Cancel"))
            {
                Undo.RecordObject(_gen, "Clear Road Points");
                _gen.controlPoints.Clear();
                EditorUtility.SetDirty(_gen);
            }
        }

        EditorGUILayout.EndHorizontal();

        GUILayout.Space(6f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        // ── Generate / Clear ──────────────────────────────────
        EditorGUILayout.LabelField("Generation", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.85f, 0.4f);
        if (GUILayout.Button("Generate Road", GUILayout.Height(30)))
        {
            Undo.RecordObject(_gen, "Generate Road");
            _gen.GenerateRoad();
            EditorUtility.SetDirty(_gen);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear Road", GUILayout.Height(30)))
        {
            Undo.RecordObject(_gen, "Clear Road");
            _gen.ClearRoad();
            EditorUtility.SetDirty(_gen);
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndHorizontal();

        // ── Info bar ─────────────────────────────────────────
        GUILayout.Space(6f);
        int ptCount = (_gen.controlPoints != null) ? _gen.controlPoints.Count : 0;

        // Read private snapshot flag via reflection for status display
        System.Reflection.FieldInfo snapField = typeof(RoadPathGenerator)
            .GetField("_terrainSnapshotTaken",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        bool snapshotTaken = snapField != null && (bool)snapField.GetValue(_gen);

        string snapStatus = snapshotTaken
            ? "Terrain snapshot: SAVED  (Clear Road will restore terrain)"
            : "Terrain snapshot: none yet";

        EditorGUILayout.HelpBox(
            string.Format(
                "Points: {0}  |  Segments: {1}  |  Road: {2:F1} m  |  Shoulder: {3:F1} m  |  Max slope: {4:F0} deg\n{5}",
                ptCount, _gen.segmentsPerCurve, _gen.roadWidth,
                _gen.paintShoulderWidth, _gen.maxSlopeDegrees, snapStatus),
            snapshotTaken ? MessageType.Warning : MessageType.Info);

        serializedObject.ApplyModifiedProperties();
    }

    // ─────────────────────────────────────────────────────────
    //  Scene GUI  (handles + labels)
    // ─────────────────────────────────────────────────────────

    void OnSceneGUI()
    {
        if (_gen == null || _gen.controlPoints == null) return;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = Color.yellow;
        labelStyle.fontStyle = FontStyle.Bold;
        labelStyle.fontSize  = 11;

        Handles.color = new Color(1f, 0.8f, 0.1f, 1f);

        for (int i = 0; i < _gen.controlPoints.Count; i++)
        {
            Transform t = _gen.controlPoints[i];
            if (t == null) continue;

            Vector3 pos = t.position;

            Handles.Label(pos + Vector3.up * 1.2f,
                string.Format("P{0}", i), labelStyle);

            if (!_editMode) continue;

            EditorGUI.BeginChangeCheck();
            Vector3 newPos = Handles.FreeMoveHandle(
                pos,
                Quaternion.identity,
                HandleUtility.GetHandleSize(pos) * 0.2f,
                Vector3.zero,
                Handles.DotHandleCap);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(t, "Move Road Point");
                t.position = newPos;
                EditorUtility.SetDirty(t);
                _gen.GenerateRoad();
                EditorUtility.SetDirty(_gen);
            }
        }

        // Shift + Right-click to place a new point on any collider/terrain
        Event e = Event.current;
        if (_editMode && e.type == EventType.MouseDown
            && e.button == 1 && e.shift)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit))
            {
                Undo.RecordObject(_gen, "Add Road Point");
                AddPointAtPosition(hit.point);
                e.Use();
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────

    void AddPointAtEnd()
    {
        Undo.RecordObject(_gen, "Add Road Point");

        Vector3 spawnPos;
        if (_gen.controlPoints != null && _gen.controlPoints.Count > 0)
        {
            Transform last = _gen.controlPoints[_gen.controlPoints.Count - 1];
            spawnPos = (last != null)
                ? last.position + last.forward * 5f
                : _gen.transform.position;
        }
        else
        {
            spawnPos = _gen.transform.position;
        }

        AddPointAtPosition(spawnPos);
    }

    void AddPointAtPosition(Vector3 worldPos)
    {
        GameObject go = new GameObject("RoadPoint_" + _gen.controlPoints.Count);
        go.transform.SetParent(_gen.transform);
        go.transform.position = worldPos;

        Undo.RegisterCreatedObjectUndo(go, "Create Road Point");

        if (_gen.controlPoints == null)
            _gen.controlPoints = new List<Transform>();

        _gen.controlPoints.Add(go.transform);
        EditorUtility.SetDirty(_gen);
    }

    static void DrawHorizontalLine(Color color)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 3f);
        r.height = 1f;
        r.y += 1f;
        EditorGUI.DrawRect(r, color);
    }
}
