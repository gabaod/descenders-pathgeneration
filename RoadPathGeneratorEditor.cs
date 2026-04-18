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
    private bool _editMode  = false;
    private bool _showStyles = true;

    private static readonly Color[] _segColors = new Color[]
    {
        new Color(0.2f, 0.9f, 1.0f),
        new Color(1.0f, 0.6f, 0.1f),
        new Color(0.4f, 1.0f, 0.4f),
        new Color(1.0f, 0.3f, 0.6f),
        new Color(0.9f, 0.9f, 0.2f),
        new Color(0.6f, 0.3f, 1.0f),
        new Color(0.3f, 0.8f, 0.6f),
        new Color(1.0f, 0.5f, 0.5f),
    };

    private static readonly GUIContent _labelEditMode = new GUIContent(
        "Edit Points Mode",
        "Toggle to move control points with scene handles.\nShift+Right-click to place a new point.");

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
    }

    [MenuItem("Tools/Road Path Generator/Add Control Point To Selected")]
    public static void AddControlPointToSelected()
    {
        RoadPathGenerator gen = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<RoadPathGenerator>() : null;
        if (gen == null) { Debug.LogWarning("[RoadPathGenerator] Select an object with RoadPathGenerator."); return; }

        GameObject pt = new GameObject("RoadPoint_" + gen.controlPoints.Count);
        pt.transform.SetParent(gen.transform);
        if (gen.controlPoints != null && gen.controlPoints.Count > 0)
        {
            Transform last = gen.controlPoints[gen.controlPoints.Count - 1];
            pt.transform.position = last != null ? last.position + last.forward * 5f : gen.transform.position;
        }
        else pt.transform.position = gen.transform.position;

        if (gen.controlPoints == null) gen.controlPoints = new List<Transform>();
        gen.controlPoints.Add(pt.transform);
        gen.EnsureStyleListLength();
        Undo.RegisterCreatedObjectUndo(pt, "Add Road Control Point");
        EditorUtility.SetDirty(gen);
    }

    [MenuItem("Tools/Road Path Generator/Generate Selected Road")]
    public static void GenerateSelectedRoad()
    {
        RoadPathGenerator gen = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<RoadPathGenerator>() : null;
        if (gen == null) { Debug.LogWarning("[RoadPathGenerator] Select an object with RoadPathGenerator."); return; }
        Undo.RecordObject(gen, "Generate Road");
        gen.GenerateRoad();
        EditorUtility.SetDirty(gen);
    }

    // ─────────────────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────────────────

    void OnEnable() { _gen = (RoadPathGenerator)target; }

    // ─────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Read snapshot state up-front — needed both for the Restore button
        // and the info bar at the bottom.
        System.Reflection.FieldInfo snapField = typeof(RoadPathGenerator).GetField(
            "_terrainSnapshotTaken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        bool snapshotTaken = snapField != null && (bool)snapField.GetValue(_gen);

        GUILayout.Space(4f);
        GUIStyle title = new GUIStyle(EditorStyles.boldLabel);
        title.fontSize  = 13;
        title.alignment = TextAnchor.MiddleCenter;
        EditorGUILayout.LabelField("  Road / Path Generator", title);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(2f);

        Color prevBg = GUI.backgroundColor;
        GUI.backgroundColor = _editMode ? new Color(0.4f, 0.8f, 1f) : Color.white;
        _editMode = GUILayout.Toggle(_editMode, _labelEditMode, "Button", GUILayout.Height(26));
        GUI.backgroundColor = prevBg;
        GUILayout.Space(4f);

        // Draw all fields except segmentStyles (we draw those ourselves below)
        DrawPropertiesExcluding(serializedObject, "segmentStyles");

        // ── Segment Styles ────────────────────────────────────
        GUILayout.Space(6f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        if (_gen.controlPoints != null)
            _gen.EnsureStyleListLength();

        int gapCount = (_gen.segmentStyles != null) ? _gen.segmentStyles.Count : 0;

        GUIStyle foldStyle = new GUIStyle(EditorStyles.foldout);
        foldStyle.fontStyle = FontStyle.Bold;
        _showStyles = EditorGUILayout.Foldout(_showStyles,
            string.Format("Segment Styles  ({0} segment{1})", gapCount, gapCount == 1 ? "" : "s"),
            foldStyle);

        if (_showStyles && gapCount > 0)
        {
            GUILayout.Space(2f);
            EditorGUILayout.HelpBox(
                "Each entry controls one gap between control points.\n" +
                "Index 0 = CP[0]->CP[1],  index 1 = CP[1]->CP[2], etc.\n\n" +
                "  roadMaterial = null  ->  terrain paint only (no mesh surface).\n" +
                "  roadMaterial set     ->  mesh surface with that material.\n" +
                "  overridePaint        ->  use this segment's own texture layer.\n" +
                "  overrideCurbs        ->  use this segment's own curb settings.\n" +
                "  generateCollision    ->  include in physics MeshCollider.",
                MessageType.None);
            GUILayout.Space(4f);

            SerializedProperty stylesProp = serializedObject.FindProperty("segmentStyles");

            for (int i = 0; i < gapCount; i++)
            {
                Color segCol = _segColors[i % _segColors.Length];

                // Coloured header bar
                Rect headerRect = EditorGUILayout.GetControlRect(false, 22f);
                Color barCol = segCol; barCol.a = 0.22f;
                EditorGUI.DrawRect(headerRect, barCol);

                string cpA = (_gen.controlPoints != null && i   < _gen.controlPoints.Count && _gen.controlPoints[i]   != null) ? _gen.controlPoints[i].name   : "CP " + i;
                string cpB = (_gen.controlPoints != null && i+1 < _gen.controlPoints.Count && _gen.controlPoints[i+1] != null) ? _gen.controlPoints[i+1].name : "CP " + (i+1);
                string styleName = (_gen.segmentStyles[i] != null && !string.IsNullOrEmpty(_gen.segmentStyles[i].styleName) && _gen.segmentStyles[i].styleName != "Segment")
                                   ? _gen.segmentStyles[i].styleName : cpA + "  ->  " + cpB;

                GUI.color = segCol;
                EditorGUI.LabelField(
                    new Rect(headerRect.x + 6, headerRect.y + 3, headerRect.width - 12, 17),
                    string.Format("[{0}]  {1}", i, styleName),
                    EditorStyles.boldLabel);
                GUI.color = Color.white;

                // Properties
                EditorGUI.indentLevel++;
                SerializedProperty elem = stylesProp.GetArrayElementAtIndex(i);
                SerializedProperty iter = elem.Copy();
                SerializedProperty iterEnd = elem.GetEndProperty();
                iter.NextVisible(true);
                while (!SerializedProperty.EqualContents(iter, iterEnd))
                {
                    EditorGUILayout.PropertyField(iter, true);
                    if (!iter.NextVisible(false)) break;
                }
                EditorGUI.indentLevel--;
                GUILayout.Space(3f);
            }
        }

        GUILayout.Space(6f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        // ── Control point buttons ─────────────────────────────
        EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("+ Add Point", GUILayout.Height(26))) AddPointAtEnd();

        bool hasPoints = _gen.controlPoints != null && _gen.controlPoints.Count > 0;
        GUI.enabled = hasPoints;
        if (GUILayout.Button("- Remove Last", GUILayout.Height(26)))
        {
            Undo.RecordObject(_gen, "Remove Road Point");
            _gen.RemoveControlPoint(_gen.controlPoints.Count - 1);
            _gen.EnsureStyleListLength();
            EditorUtility.SetDirty(_gen);
        }
        GUI.enabled = true;

        if (GUILayout.Button("Clear All", GUILayout.Height(26)))
        {
            if (EditorUtility.DisplayDialog("Clear Road Points", "Remove all control points?", "Yes", "Cancel"))
            {
                Undo.RecordObject(_gen, "Clear Road Points");
                _gen.controlPoints.Clear();
                _gen.EnsureStyleListLength();
                EditorUtility.SetDirty(_gen);
            }
        }
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(2f);

        if (GUILayout.Button("Sync Segment Styles List", GUILayout.Height(22)))
        {
            Undo.RecordObject(_gen, "Sync Styles");
            _gen.EnsureStyleListLength();
            EditorUtility.SetDirty(_gen);
        }

        GUILayout.Space(6f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        EditorGUILayout.LabelField("Bake Mesh", EditorStyles.boldLabel);
        GUI.backgroundColor = new Color(0.8f, 0.7f, 1.0f);
        if (GUILayout.Button("Bake Mesh to Object", GUILayout.Height(26)))
        {
            Undo.RecordObject(_gen, "Bake Mesh to Object");
            _gen.BakeMeshToObject();
            EditorUtility.SetDirty(_gen);
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(6f);
        DrawHorizontalLine(Color.grey);
        GUILayout.Space(4f);

        // ── Generate / Clear / Restore ────────────────────────
        EditorGUILayout.LabelField("Generation", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();

        GUI.backgroundColor = new Color(0.4f, 0.85f, 0.4f);
        if (GUILayout.Button("Generate Road", GUILayout.Height(32)))
        {
            Undo.RecordObject(_gen, "Generate Road");
            _gen._skipTerrainShaping = false;
            _gen.GenerateRoad();
            if (_gen.enableRockScatter)
                ScatterRocksEditor(_gen);
            EditorUtility.SetDirty(_gen);
        }

        GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
        if (GUILayout.Button("Clear Road", GUILayout.Height(32)))
        {
            Undo.RecordObject(_gen, "Clear Road");
            Transform existing = _gen.transform.Find("RockScatter");
            if (existing != null)
                DestroyImmediate(existing.gameObject);
            _gen.ClearRoad();
            EditorUtility.SetDirty(_gen);
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // Restore button — only shown when a snapshot exists
        if (snapshotTaken)
        {
            GUILayout.Space(3f);
            GUI.backgroundColor = new Color(0.4f, 0.7f, 1.0f);
            if (GUILayout.Button("Restore Terrain  (keep road mesh)", GUILayout.Height(26)))
            {
                Undo.RecordObject(_gen, "Restore Terrain");
                // Call RestoreTerrain via reflection so we don't need to make it public
                System.Reflection.MethodInfo mi = typeof(RoadPathGenerator).GetMethod(
                    "RestoreTerrain",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mi != null) mi.Invoke(_gen, null);
                EditorUtility.SetDirty(_gen);
            }
            GUI.backgroundColor = Color.white;
        }

        // ── Info bar ──────────────────────────────────────────
        GUILayout.Space(6f);
        int ptCount = (_gen.controlPoints != null) ? _gen.controlPoints.Count : 0;

        string snapStatus  = snapshotTaken ? "Terrain snapshot: SAVED  (restore available)"
                                           : "Terrain snapshot: none  —  generate first";

        int meshSegs = 0, paintSegs = 0;
        if (_gen.segmentStyles != null)
            foreach (RoadSegmentStyle s in _gen.segmentStyles)
                if (s != null && s.roadMaterial != null) meshSegs++; else paintSegs++;

        EditorGUILayout.HelpBox(
            string.Format("Points: {0}  |  Gaps: {1}  (mesh: {2},  paint-only: {3})  |  Width: {4:F1} m\n{5}",
                ptCount, gapCount, meshSegs, paintSegs, _gen.roadWidth, snapStatus),
            snapshotTaken ? MessageType.Warning : MessageType.Info);

        serializedObject.ApplyModifiedProperties();


    }

    // ─────────────────────────────────────────────────────────
    //  Scene GUI
    // ─────────────────────────────────────────────────────────

    void OnSceneGUI()
    {
        if (_gen == null || _gen.controlPoints == null) return;
        _gen._skipTerrainShaping = _editMode;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.fontStyle = FontStyle.Bold;
        labelStyle.fontSize  = 11;

        for (int i = 0; i < _gen.controlPoints.Count; i++)
        {
            Transform t = _gen.controlPoints[i];
            if (t == null) continue;

            int   styleIdx = i < _gen.controlPoints.Count - 1 ? i : Mathf.Max(0, i - 1);
            Color col      = _segColors[styleIdx % _segColors.Length];

            Handles.color              = col;
            labelStyle.normal.textColor = col;

            Vector3 pos = t.position;
            string  lbl = "P" + i;
            if (_gen.segmentStyles != null && i < _gen.segmentStyles.Count
                && _gen.segmentStyles[i] != null
                && !string.IsNullOrEmpty(_gen.segmentStyles[i].styleName)
                && _gen.segmentStyles[i].styleName != "Segment")
                lbl += "\n" + _gen.segmentStyles[i].styleName;

            Handles.Label(pos + Vector3.up * 1.4f, lbl, labelStyle);

            if (!_editMode) continue;

            EditorGUI.BeginChangeCheck();
            Vector3 newPos = Handles.FreeMoveHandle(pos, Quaternion.identity,
                HandleUtility.GetHandleSize(pos) * 0.2f, Vector3.zero, Handles.DotHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(t, "Move Road Point");
                t.position = newPos;
                EditorUtility.SetDirty(t);
                EditorUtility.SetDirty(_gen);
            }
        }

        // Segment midpoint labels: material name or "paint only"
        if (_gen.segmentStyles != null)
        {
            GUIStyle midStyle = new GUIStyle(GUI.skin.label);
            midStyle.fontSize  = 10;
            midStyle.alignment = TextAnchor.MiddleCenter;

            for (int i = 0; i < _gen.segmentStyles.Count; i++)
            {
                if (_gen.controlPoints == null || i >= _gen.controlPoints.Count - 1) break;
                Transform a = _gen.controlPoints[i];
                Transform b = _gen.controlPoints[i + 1];
                if (a == null || b == null) continue;

                Color col = _segColors[i % _segColors.Length];
                midStyle.normal.textColor = col;

                RoadSegmentStyle st = _gen.segmentStyles[i];
                string matName = (st != null && st.roadMaterial != null)
                    ? st.roadMaterial.name : "paint only";
                Vector3 mid = (a.position + b.position) * 0.5f + Vector3.up * 0.8f;
                Handles.Label(mid, string.Format("[{0}] {1}", i, matName), midStyle);
            }
        }

        // Shift+Right-click to add point
        Event e = Event.current;
        if (_editMode && e.type == EventType.MouseDown && e.button == 1 && e.shift)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit))
            {
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
        Vector3 pos;
        if (_gen.controlPoints != null && _gen.controlPoints.Count > 0)
        {
            Transform last = _gen.controlPoints[_gen.controlPoints.Count - 1];
            pos = last != null ? last.position + last.forward * 5f : _gen.transform.position;
        }
        else pos = _gen.transform.position;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null)
            pos.y = terrain.SampleHeight(pos) + terrain.transform.position.y;
        AddPointAtPosition(pos);
    }

    void AddPointAtPosition(Vector3 worldPos)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null)
            worldPos.y = terrain.SampleHeight(worldPos) + terrain.transform.position.y;
        GameObject go = new GameObject("RoadPoint_" + _gen.controlPoints.Count);
        go.transform.SetParent(_gen.transform);
        go.transform.position = worldPos;
        Undo.RegisterCreatedObjectUndo(go, "Create Road Point");
        if (_gen.controlPoints == null) _gen.controlPoints = new List<Transform>();
        _gen.controlPoints.Add(go.transform);
        _gen.EnsureStyleListLength();
        EditorUtility.SetDirty(_gen);
    }

    static void DrawHorizontalLine(Color color)
    {
        Rect r   = EditorGUILayout.GetControlRect(false, 3f);
        r.height = 1f; r.y += 1f;
        EditorGUI.DrawRect(r, color);
    }
    // ─────────────────────────────────────────────────────────
    //  Rock Scatter — editor only
    //  Called after GenerateRoad() from the Generate Road button.
    //  Lives here rather than in RoadPathGenerator to avoid
    //  ModTool flagging new GameObject and AddComponent calls.
    // ─────────────────────────────────────────────────────────
    static void ScatterRocksEditor(RoadPathGenerator gen)
    {

        if (gen.rockScatterPrefabs == null ||
            gen.rockScatterPrefabs.Count == 0) return;

        // Access distField via reflection since it is private
        System.Reflection.FieldInfo dfField =
            typeof(RoadPathGenerator).GetField("_distField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        RoadDistanceField distField =
            dfField != null ? (RoadDistanceField)dfField.GetValue(gen) : null;
        if (distField == null) return;

        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        // ── Zone boundaries ───────────────────────────────────
        float halfRoad = gen.roadWidth * 0.5f;
        float ditchFloorEnd = halfRoad + gen.ditchOffset
                              + gen.ditchInnerWidth + gen.ditchBottomWidth;
        float ditchOuterToe = ditchFloorEnd + gen.ditchOuterWidth;

        float zoneInner = gen.generateDitch
            ? ditchOuterToe
            : halfRoad + gen.shoulderSmoothDistance;

        float shoulderOuter = gen.generateDitch
            ? ditchFloorEnd + gen.shoulderSmoothDistance
            : halfRoad + gen.shoulderSmoothDistance;

        float bermStart = (gen.generateDitch && gen.smoothShoulder)
            ? shoulderOuter
            : gen.generateDitch ? ditchFloorEnd
            : gen.smoothShoulder ? shoulderOuter
            : halfRoad;

        float zoneOuter = gen.generateBerm
            ? bermStart + gen.bermSlopeDistance + gen.bermOuterFalloff
            : zoneInner + 5f;

        if (zoneOuter <= zoneInner)
        {
            Debug.LogWarning("[RoadPathGenerator] Rock scatter: " +
                "zoneOuter=" + zoneOuter + " <= zoneInner=" + zoneInner +
                " — no valid scatter zone.");
            return;
        }

        Debug.Log("[Scatter] distField=" + (distField == null ? "NULL" : "OK") +
          " zoneInner=" + zoneInner + " zoneOuter=" + zoneOuter +
          " prefabs=" + gen.rockScatterPrefabs.Count);

        // Access cached spline points via reflection
        System.Reflection.FieldInfo splineField =
            typeof(RoadPathGenerator).GetField("_cachedSplinePoints",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        List<Vector3> pts = splineField != null
            ? (List<Vector3>)splineField.GetValue(gen) : null;
        if (pts == null || pts.Count < 2) return;

        Vector2 startTangent = new Vector2(
            pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin = new Vector2(pts[0].x, pts[0].z);
        int lastIdx = pts.Count - 1;
        Vector2 endTangent = new Vector2(
            pts[lastIdx].x - pts[lastIdx - 1].x,
            pts[lastIdx].z - pts[lastIdx - 1].z).normalized;
        Vector2 endOrigin = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        // ── Create container ──────────────────────────────────
        Transform container = gen.transform.Find("RockScatter");
        if (container == null)
        {
            GameObject go = new GameObject("RockScatter");
            go.transform.SetParent(gen.transform, false);
            Undo.RegisterCreatedObjectUndo(go, "Rock Scatter Container");
            container = go.transform;
        }

        // ── Grid scatter ──────────────────────────────────────
        float cellSize = gen.rockScatterMinSpacing;
        System.Random rng = new System.Random(gen.rockScatterSeed);

        TerrainData td = terrain.terrainData;
        float tW = td.size.x, tH = td.size.z;
        Vector3 tPos = terrain.transform.position;
        int hw = td.heightmapResolution;

        Dictionary<int, Vector3> placedCells =
            new Dictionary<int, Vector3>();

        for (int lz = 0; lz < distField.subH; lz++)
            for (int lx = 0; lx < distField.subW; lx++)
            {
                int idx = distField.Idx(lz, lx);
                float cd = distField.crossDist[idx];
                if (cd < zoneInner || cd > zoneOuter) continue;

                float wx = tPos.x +
                    ((distField.pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z +
                    ((distField.pzMin + lz) / (float)(hw - 1)) * tH;

                Vector2 pq = new Vector2(wx, wz);
                if (Vector2.Dot(pq - startOrigin, startTangent) < 0f)
                    continue;
                if (Vector2.Dot(pq - endOrigin, endTangent) > 0f)
                    continue;

                if (rng.NextDouble() >
                    gen.rockScatterDensity * cellSize * cellSize)
                    continue;

                float ox = (float)(rng.NextDouble() - 0.5) * cellSize;
                float oz = (float)(rng.NextDouble() - 0.5) * cellSize;
                float px = wx + ox;
                float pz = wz + oz;

                int cx = Mathf.FloorToInt(px / cellSize);
                int cz2 = Mathf.FloorToInt(pz / cellSize);
                int key = cx * 100000 + cz2;

                bool tooClose = false;
                for (int nx = cx - 1; nx <= cx + 1 && !tooClose; nx++)
                    for (int nz = cz2 - 1; nz <= cz2 + 1 && !tooClose; nz++)
                    {
                        int nkey = nx * 100000 + nz;
                        if (placedCells.ContainsKey(nkey))
                        {
                            Vector3 other = placedCells[nkey];
                            float dx = other.x - px;
                            float dz3 = other.z - pz;
                            float minSq = gen.rockScatterMinSpacing *
                                          gen.rockScatterMinSpacing;
                            if (dx * dx + dz3 * dz3 < minSq)
                                tooClose = true;
                        }
                    }
                if (tooClose) continue;

                float py = terrain.SampleHeight(
                    new Vector3(px, 0f, pz)) + tPos.y;

                int prefabIdx = rng.Next(0, gen.rockScatterPrefabs.Count);
                RockScatterEntry entry = gen.rockScatterPrefabs[prefabIdx];
                if (entry == null || entry.prefab == null) continue;

                float scale = Mathf.Lerp(gen.rockScatterMinScale,
                    gen.rockScatterMaxScale, (float)rng.NextDouble());
                float rotY = Mathf.Lerp(gen.rockScatterMinRotY,
                    gen.rockScatterMaxRotY, (float)rng.NextDouble());
                float tiltX = (float)(rng.NextDouble() - 0.5) * 2f
                              * gen.rockScatterMaxTilt;
                float tiltZ = (float)(rng.NextDouble() - 0.5) * 2f
                              * gen.rockScatterMaxTilt;

                Quaternion rot = Quaternion.Euler(tiltX, rotY, tiltZ);

                if (gen.rockScatterAlignToSlope)
                {
                    Vector3 normal = td.GetInterpolatedNormal(
                        (px - tPos.x) / tW,
                        (pz - tPos.z) / tH);
                    rot = Quaternion.FromToRotation(Vector3.up, normal)
                          * Quaternion.Euler(tiltX, rotY, tiltZ);
                }

                GameObject rock = (GameObject)
                    PrefabUtility.InstantiatePrefab(entry.prefab);
                Undo.RegisterCreatedObjectUndo(rock, "Scatter Rock");
                rock.transform.SetParent(container, true);
                rock.transform.position = new Vector3(px, py, pz);
                rock.transform.rotation = rot;
                rock.transform.localScale = Vector3.one * scale;

                if (entry.addMeshCollider)
                {
                    MeshFilter mf =
                        rock.GetComponentInChildren<MeshFilter>();
                    if (mf != null)
                    {
                        MeshCollider mc =
                            rock.AddComponent<MeshCollider>();
                        mc.sharedMesh = mf.sharedMesh;
                    }
                }

                placedCells[key] = new Vector3(px, py, pz);
            }

        Debug.Log("[RoadPathGenerator] Scattered " +
                  container.childCount + " rocks.");
    }
}
