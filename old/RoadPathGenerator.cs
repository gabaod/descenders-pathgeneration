// ============================================================
//  RoadPathGenerator.cs
//  Unity 2017  |  C# 4.0  |  ModTool.Interface / ModBehaviour
// ============================================================

using System.Collections.Generic;
using UnityEngine;
using ModTool.Interface;

[DisallowMultipleComponent]
public class RoadPathGenerator : ModBehaviour
{
    // ── Control Points ────────────────────────────────────────
    [Header("Control Points")]
    [Tooltip("Assign Transform objects that mark each road waypoint.")]
    public List<Transform> controlPoints = new List<Transform>();

    [Tooltip("Close the spline into a loop.")]
    public bool closedLoop = false;

    // ── Spline / Resolution ───────────────────────────────────
    [Header("Spline Resolution")]
    [Range(2, 64)]
    public int segmentsPerCurve = 12;

    // ── Road Geometry ─────────────────────────────────────────
    [Header("Road Geometry")]
    [Range(0.5f, 50f)]
    public float roadWidth = 6f;

    [Range(0f, 2f)]
    public float roadThickness = 0.1f;

    [Tooltip("Camber (cross-slope) angle in degrees — positive = left side higher.")]
    [Range(-15f, 15f)]
    public float camberDegrees = 0f;

    // ── Road Material ─────────────────────────────────────────
    [Header("Road Material")]
    public Material roadMaterial;

    [Range(0.1f, 50f)]
    public float uvTileLength = 10f;

    // ── Curbs / Shoulders ─────────────────────────────────────
    [Header("Curbs / Shoulders")]
    public bool generateCurbs = false;

    [Range(0.05f, 2f)]
    public float curbHeight = 0.2f;

    [Range(0.1f, 3f)]
    public float curbWidth = 0.4f;

    public Material curbMaterial;

    // ── Berm / Embankment ─────────────────────────────────────
    [Header("Berm / Embankment")]
    [Tooltip("Grade the terrain on both sides of the road to meet the road edge.\n" +
             "Low side  → terrain ramps UP  to road level.\n" +
             "High side → terrain ramps DOWN to road level (cliff cut).")]
    public bool generateBerm = false;

    [Tooltip("How far from the road edge (world units) before the berm returns to natural terrain.")]
    [Range(0.5f, 40f)]
    public float bermSlopeDistance = 6f;

    [Tooltip("Shape of the berm profile.\n" +
             "0 = straight linear slope.\n" +
             "1 = smooth S-curve (eases in and out).\n" +
             "Values in between blend the two.")]
    [Range(0f, 1f)]
    public float bermCurvature = 0.5f;

    [Tooltip("Optional soft-edge falloff (world units) at the outer toe of the berm " +
             "so the berm blends gently into untouched terrain.")]
    [Range(0f, 10f)]
    public float bermOuterFalloff = 1.5f;

    // ── Collider ──────────────────────────────────────────────
    [Header("Collider")]
    public bool generateCollider = true;

    // ── Terrain – Snap ────────────────────────────────────────
    [Header("Terrain – Snap")]
    [Tooltip("Lift road vertices onto terrain/colliders.")]
    public bool snapToTerrain = true;

    [Range(0f, 1f)]
    public float terrainSnapOffset = 0.05f;

    // ── Terrain – Slope Flatten ───────────────────────────────
    [Header("Terrain – Slope Flatten")]
    [Tooltip("Smooth terrain heights along the road so the slope never exceeds maxSlopeDegrees. " +
             "Heights are adjusted RELATIVE to existing terrain — nothing is dragged to world-zero.")]
    public bool flattenTerrain = false;

    [Tooltip("Maximum allowable road gradient in degrees (e.g. 20 = gentle hill, 40 = steep).")]
    [Range(1f, 60f)]
    public float maxSlopeDegrees = 20f;

    [Tooltip("How many smoothing passes to run along the spline (more = softer result).")]
    [Range(1, 10)]
    public int slopeSmoothPasses = 3;

    [Tooltip("Window size (world units) used to detect sections of the path with large elevation " +
             "changes. Any section where the height change across this distance exceeds what " +
             "maxSlopeDegrees would allow gets extra smoothing applied.")]
    [Range(10f, 200f)]
    public float steepZoneWindowMeters = 50f;

    [Tooltip("How many additional Gaussian smoothing passes are applied inside steep zones. " +
             "Higher = smoother but may pull the road further from the original terrain contour.")]
    [Range(0, 20)]
    public int steepZoneExtraPasses = 8;

    [Tooltip("How many extra world units either side of the road to blend the adjusted heights into.")]
    [Range(0f, 20f)]
    public float flattenExtraWidth = 1f;

    [Tooltip("Soft-edge falloff distance (world units) beyond road+extra band.")]
    [Range(0f, 20f)]
    public float flattenFalloff = 3f;

    // ── Terrain – Paint ───────────────────────────────────────
    [Header("Terrain – Paint")]
    public bool paintTerrain = false;

    [Range(0, 15)]
    public int paintTextureIndex = 0;

    [Tooltip("Extra shoulder painted beyond the road edge on each side (world units).\n" +
             "The road surface itself (roadWidth) is ALWAYS painted at full strength.\n" +
             "Set to 0 for paint exactly flush with the road / curb boundary.")]
    [Range(0f, 20f)]
    public float paintShoulderWidth = 1f;

    [Tooltip("Falloff distance (world units) at the outer edge of the shoulder.\n" +
             "Paint fades from full strength at road edge to 0 over this distance.\n" +
             "Must be <= paintShoulderWidth. Set to 0 for a hard edge.")]
    [Range(0f, 20f)]
    public float paintShoulderFalloff = 0.8f;

    [Range(0f, 1f)]
    public float paintStrength = 0.9f;

    // ── Private Runtime Objects ───────────────────────────────
    // All mesh components live on this same GameObject — no child objects created.
    // Road = submesh 0, Curbs = submesh 1 (when generateCurbs is true).
    private MeshFilter   _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;

    private List<Vector3> _cachedSplinePoints = new List<Vector3>();

    // ── Terrain snapshot (heights + alphas before ANY modification) ──
    private float[,]  _savedHeights;
    private float[,,] _savedAlphas;
    private bool      _terrainSnapshotTaken = false;

    // ─────────────────────────────────────────────────────────
    //  ModBehaviour lifecycle
    // ─────────────────────────────────────────────────────────

    void Start()
    {
        // Attach all components directly to this GameObject.
        // ModBehaviour requires the AddComponent<T>(host) form — host is gameObject.
        _meshFilter   = AddComponent<MeshFilter>(gameObject);
        _meshRenderer = AddComponent<MeshRenderer>(gameObject);

        if (generateCollider)
            _meshCollider = AddComponent<MeshCollider>(gameObject);

        GenerateRoad();
    }

    // ─────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────

    public void GenerateRoad()
    {
        if (controlPoints == null || controlPoints.Count < 2)
        {
            Debug.LogWarning("[RoadPathGenerator] Need at least 2 control points.");
            return;
        }

        bool needsTerrainWork = flattenTerrain || paintTerrain || snapToTerrain || generateBerm;

        if (needsTerrainWork)
        {
            if (!_terrainSnapshotTaken)
            {
                // First run — capture the pristine terrain state
                SnapshotTerrain();
            }
            else
            {
                // Subsequent run — restore pristine state before re-applying
                RestoreTerrain();
            }
        }

        // 1. Build spline (raw positions from control points)
        _cachedSplinePoints = BuildSplinePoints();
        if (_cachedSplinePoints.Count < 2) return;

        // 2. Slope-limited flatten — adjusts terrain AND returns
        //    updated Y values for the spline so the road follows
        //    the smoothed surface.
        if (flattenTerrain)
            FlattenTerrainSlopeLimited(ref _cachedSplinePoints);

        // 3. Berm — grade terrain on both sides of the road edge back
        //    to natural terrain height over bermSlopeDistance.
        //    Runs AFTER flatten so the road-edge height is already correct,
        //    and BEFORE snap so the snap reads the final adjusted surface.
        if (generateBerm)
            ApplyBermToTerrain(_cachedSplinePoints);

        // 4. Snap road vertices onto (possibly adjusted) terrain
        if (snapToTerrain)
            SnapToTerrain(ref _cachedSplinePoints);

        // 4. Paint splat texture
        if (paintTerrain)
            PaintTerrainAlongPath(_cachedSplinePoints);

        // 5. Build combined mesh (submesh 0 = road surface, submesh 1 = curbs)
        //
        //    Road surface rules
        //    ──────────────────
        //    • roadMaterial assigned → surface mesh spans roadWidth so the
        //      material exactly covers the painted terrain strip.
        //    • roadMaterial null     → road tris are empty; terrain paint
        //      shows through with no overlapping geometry or material.
        if (_meshFilter != null)
        {
            // Road surface mesh spans roadWidth so it exactly covers the road lane.
            // When no material is assigned, skip road geometry so terrain paint shows through.
            float surfaceWidth = (roadMaterial != null) ? roadWidth : 0f;

            Mesh combined = BuildCombinedMesh(_cachedSplinePoints, surfaceWidth);
            _meshFilter.sharedMesh = combined;

            // Only populate material slots that are actually used.
            if (roadMaterial != null && curbMaterial != null)
                _meshRenderer.sharedMaterials = new Material[] { roadMaterial, curbMaterial };
            else if (roadMaterial != null)
                _meshRenderer.sharedMaterials = new Material[] { roadMaterial, roadMaterial };
            else if (curbMaterial != null && generateCurbs)
                _meshRenderer.sharedMaterials = new Material[] { null, curbMaterial };
            else
                _meshRenderer.sharedMaterials = new Material[0];

            if (generateCollider && _meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.sharedMesh = combined;
            }
        }
    }

    /// <summary>
    /// Remove generated meshes AND restore terrain to its pre-generation state.
    /// </summary>
    public void ClearRoad()
    {
        if (_meshFilter   != null) _meshFilter.sharedMesh   = null;
        if (_meshCollider != null) _meshCollider.sharedMesh = null;

        RestoreTerrain();
        _terrainSnapshotTaken = false;   // allow a fresh snapshot on next Generate
    }

    public void AddControlPoint(Transform point)
    {
        if (controlPoints == null) controlPoints = new List<Transform>();
        controlPoints.Add(point);
    }

    public void RemoveControlPoint(int index)
    {
        if (controlPoints == null || index < 0 || index >= controlPoints.Count) return;
        controlPoints.RemoveAt(index);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain Snapshot / Restore
    // ─────────────────────────────────────────────────────────

    void SnapshotTerrain()
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        int aw = td.alphamapWidth;
        int ah = td.alphamapHeight;
        int nl = td.alphamapLayers;

        // Deep-copy the arrays — Unity returns references that change in-place
        float[,]  srcH = td.GetHeights(0, 0, hw, hw);
        float[,,] srcA = td.GetAlphamaps(0, 0, aw, ah);

        _savedHeights = new float[hw, hw];
        _savedAlphas  = new float[ah, aw, nl];

        System.Array.Copy(srcH, _savedHeights, srcH.Length);
        System.Array.Copy(srcA, _savedAlphas,  srcA.Length);

        _terrainSnapshotTaken = true;
        Debug.Log("[RoadPathGenerator] Terrain snapshot saved.");
    }

    void RestoreTerrain()
    {
        if (!_terrainSnapshotTaken) return;

        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;

        // Restore heights
        if (_savedHeights != null)
        {
            int hw = td.heightmapResolution;
            td.SetHeights(0, 0, _savedHeights);
        }

        // Restore alphas
        if (_savedAlphas != null)
        {
            td.SetAlphamaps(0, 0, _savedAlphas);
        }

        Debug.Log("[RoadPathGenerator] Terrain restored to pre-generation state.");
    }

    // ─────────────────────────────────────────────────────────
    //  Spline
    // ─────────────────────────────────────────────────────────

    List<Vector3> BuildSplinePoints()
    {
        List<Vector3> cp = new List<Vector3>();
        foreach (Transform t in controlPoints)
            if (t != null) cp.Add(t.position);

        if (closedLoop && cp.Count > 2)
            cp.Add(cp[0]);

        List<Vector3> result = new List<Vector3>();
        int last = cp.Count - 1;

        for (int i = 0; i < last; i++)
        {
            Vector3 p0 = cp[Mathf.Max(0, i - 1)];
            Vector3 p1 = cp[i];
            Vector3 p2 = cp[Mathf.Min(cp.Count - 1, i + 1)];
            Vector3 p3 = cp[Mathf.Min(cp.Count - 1, i + 2)];

            for (int s = 0; s < segmentsPerCurve; s++)
            {
                float t = s / (float)segmentsPerCurve;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }
        result.Add(cp[cp.Count - 1]);
        return result;
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
              (2f * p1)
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // ─────────────────────────────────────────────────────────
    //  Combined Mesh  (submesh 0 = road, submesh 1 = curbs)
    // ─────────────────────────────────────────────────────────
    //
    //  Both road and curb geometry share one Mesh / MeshFilter.
    //  The MeshRenderer uses a 2-element sharedMaterials array
    //  so each submesh gets its own material without needing
    //  a second GameObject.
    // ─────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────
    //  Combined Mesh  (submesh 0 = road, submesh 1 = curbs)
    // ─────────────────────────────────────────────────────────
    //
    //  surfaceWidth controls the visual road surface:
    //    > 0  → geometry is generated at that width (roadWidth when material is set)
    //    = 0  → road tris are skipped; terrain paint shows through.
    //
    //  roadWidth still governs curb placement and collider extent.
    // ─────────────────────────────────────────────────────────

    Mesh BuildCombinedMesh(List<Vector3> pts, float surfaceWidth)
    {
        int n = pts.Count;

        List<Vector3> verts    = new List<Vector3>();
        List<Vector2> uvs      = new List<Vector2>();
        List<int>     roadTris = new List<int>();
        List<int>     curbTris = new List<int>();

        float camberRad = camberDegrees * Mathf.Deg2Rad;
        float camberL   =  Mathf.Sin(camberRad) * (surfaceWidth * 0.5f);
        float camberR   = -camberL;
        float[] arcLen  = BuildArcLengths(pts);

        // ──────────────────────────────────────────────────────
        //  ROAD GEOMETRY  — only when surfaceWidth > 0
        // ──────────────────────────────────────────────────────

        if (surfaceWidth > 0.001f)
        {
            int roadTopBase = verts.Count;

            for (int i = 0; i < n; i++)
            {
                Vector3 fwd   = GetForward(pts, i);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                float   v     = arcLen[i] / uvTileLength;

                verts.Add(pts[i] - right * (surfaceWidth * 0.5f) + Vector3.up * camberL);
                verts.Add(pts[i] + right * (surfaceWidth * 0.5f) + Vector3.up * camberR);
                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
            }

            for (int i = 0; i < n - 1; i++)
            {
                int bl = roadTopBase + i * 2,     br = roadTopBase + i * 2 + 1;
                int tl = roadTopBase + (i+1) * 2, tr = roadTopBase + (i+1) * 2 + 1;
                roadTris.Add(bl); roadTris.Add(tl); roadTris.Add(br);
                roadTris.Add(br); roadTris.Add(tl); roadTris.Add(tr);
            }

            if (roadThickness > 0.001f)
            {
                int roadBotBase = verts.Count;

                for (int i = 0; i < n; i++)
                {
                    Vector3 fwd   = GetForward(pts, i);
                    Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                    float   v     = arcLen[i] / uvTileLength;

                    verts.Add(pts[i] - right * (surfaceWidth * 0.5f) + Vector3.up * (camberL - roadThickness));
                    verts.Add(pts[i] + right * (surfaceWidth * 0.5f) + Vector3.up * (camberR - roadThickness));
                    uvs.Add(new Vector2(0f, v));
                    uvs.Add(new Vector2(1f, v));
                }

                for (int i = 0; i < n - 1; i++)
                {
                    int bl = roadBotBase + i * 2,     br = roadBotBase + i * 2 + 1;
                    int tl = roadBotBase + (i+1) * 2, tr = roadBotBase + (i+1) * 2 + 1;
                    roadTris.Add(br); roadTris.Add(tl); roadTris.Add(bl);
                    roadTris.Add(tr); roadTris.Add(tl); roadTris.Add(br);
                }
            }
        }

        if (generateCurbs)
        {
            int     curbBase    = verts.Count;
            Terrain activeTerr  = Terrain.activeTerrain;

            for (int i = 0; i < n; i++)
            {
                Vector3 fwd   = GetForward(pts, i);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

                // ── XZ positions of the four curb base points ────────────
                // (Y set by terrain sampling below, not inherited from centreline)
                Vector3 eLInnerXZ  = pts[i] - right * (roadWidth * 0.5f);
                Vector3 eRInnerXZ  = pts[i] + right * (roadWidth * 0.5f);
                Vector3 eLOuterXZ  = eLInnerXZ - right * curbWidth;
                Vector3 eROuterXZ  = eRInnerXZ + right * curbWidth;

                // ── Sample terrain height at each base position ──────────
                // SampleTerrainHeight falls back to pts[i].y if no terrain found,
                // so this works whether snapToTerrain is on or off.
                float yLInner = SampleTerrainHeight(eLInnerXZ, activeTerr) + terrainSnapOffset;
                float yRInner = SampleTerrainHeight(eRInnerXZ, activeTerr) + terrainSnapOffset;
                float yLOuter = SampleTerrainHeight(eLOuterXZ, activeTerr) + terrainSnapOffset;
                float yROuter = SampleTerrainHeight(eROuterXZ, activeTerr) + terrainSnapOffset;

                // ── Left curb profile (inner→outer, left side) ───────────
                // inner-bottom sits on terrain; top rises by curbHeight above it.
                // outer-bottom sits on terrain at the outer edge.
                // outer-top matches inner-top height so the curb top is level.
                float curbTopY_L = yLInner + curbHeight;

                Vector3 eLInner      = new Vector3(eLInnerXZ.x, yLInner,    eLInnerXZ.z);
                Vector3 eLInnerTop   = new Vector3(eLInnerXZ.x, curbTopY_L, eLInnerXZ.z);
                Vector3 eLOuterTop   = new Vector3(eLOuterXZ.x, curbTopY_L, eLOuterXZ.z);
                Vector3 eLOuter      = new Vector3(eLOuterXZ.x, yLOuter,    eLOuterXZ.z);

                verts.Add(eLInner);       // 0 inner-bottom
                verts.Add(eLInnerTop);    // 1 inner-top
                verts.Add(eLOuterTop);    // 2 outer-top
                verts.Add(eLOuter);       // 3 outer-bottom

                // ── Right curb profile ────────────────────────────────────
                float curbTopY_R = yRInner + curbHeight;

                Vector3 eRInner      = new Vector3(eRInnerXZ.x, yRInner,    eRInnerXZ.z);
                Vector3 eRInnerTop   = new Vector3(eRInnerXZ.x, curbTopY_R, eRInnerXZ.z);
                Vector3 eROuterTop   = new Vector3(eROuterXZ.x, curbTopY_R, eROuterXZ.z);
                Vector3 eROuter      = new Vector3(eROuterXZ.x, yROuter,    eROuterXZ.z);

                verts.Add(eRInner);       // 4 inner-bottom
                verts.Add(eRInnerTop);    // 5 inner-top
                verts.Add(eROuterTop);    // 6 outer-top
                verts.Add(eROuter);       // 7 outer-bottom

                // UVs (flat 0..1 across curb width)
                uvs.Add(Vector2.zero); uvs.Add(Vector2.up);
                uvs.Add(Vector2.one);  uvs.Add(Vector2.right);
                uvs.Add(Vector2.zero); uvs.Add(Vector2.up);
                uvs.Add(Vector2.one);  uvs.Add(Vector2.right);
            }

            for (int i = 0; i < n - 1; i++)
            {
                int aL = curbBase + i * 8;
                int bL = curbBase + (i + 1) * 8;
                int aR = aL + 4;
                int bR = bL + 4;
                AddCurbSegment(curbTris, aL, bL, false);
                AddCurbSegment(curbTris, aR, bR, true);
            }
        }

        // ──────────────────────────────────────────────────────
        //  Assemble Mesh with 2 submeshes
        // ──────────────────────────────────────────────────────

        Mesh m = new Mesh { name = "RoadCombined" };
        m.subMeshCount = 2;
        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(roadTris, 0);
        m.SetTriangles(curbTris, 1);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    static void AddCurbSegment(List<int> tris, int a, int b, bool flip)
    {
        for (int face = 0; face < 3; face++)
        {
            int v0 = a + face, v1 = a + face + 1;
            int v2 = b + face, v3 = b + face + 1;
            if (flip)
            { tris.Add(v0); tris.Add(v2); tris.Add(v1); tris.Add(v1); tris.Add(v2); tris.Add(v3); }
            else
            { tris.Add(v0); tris.Add(v1); tris.Add(v2); tris.Add(v1); tris.Add(v3); tris.Add(v2); }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Snap
    // ─────────────────────────────────────────────────────────

    void SnapToTerrain(ref List<Vector3> pts)
    {
        Terrain activeTerrain = Terrain.activeTerrain;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i];
            p.y = SampleTerrainHeight(p, activeTerrain) + terrainSnapOffset;
            pts[i] = p;
        }
    }

    float SampleTerrainHeight(Vector3 worldPos, Terrain terrain)
    {
        if (terrain != null)
            return terrain.SampleHeight(worldPos) + terrain.transform.position.y;

        RaycastHit hit;
        Vector3 origin = new Vector3(worldPos.x, worldPos.y + 500f, worldPos.z);
        if (Physics.Raycast(origin, Vector3.down, out hit, 1000f))
            return hit.point.y;

        return worldPos.y;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Slope-Limited Flatten
    // ─────────────────────────────────────────────────────────
    //
    //  The loop is PIXEL-FIRST:
    //    For each heightmap pixel inside the corridor bounding box,
    //    find the nearest point on the nearest spline segment (XZ),
    //    get the interpolated slope-corrected height at that point,
    //    and blend the pixel toward that height.
    //
    //  This guarantees every pixel gets exactly ONE target height
    //  (the nearest-segment interpolation), eliminating the
    //  random bumps caused by multiple spline points fighting
    //  over the same pixel.
    // ─────────────────────────────────────────────────────────

    void FlattenTerrainSlopeLimited(ref List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
        {
            Debug.LogWarning("[RoadPathGenerator] FlattenTerrain: no active terrain found.");
            return;
        }

        TerrainData td          = terrain.terrainData;
        int         hw          = td.heightmapResolution;
        float       terrainW    = td.size.x;
        float       terrainH    = td.size.z;
        float       terrainMaxY = td.size.y;
        Vector3     terrainPos  = terrain.transform.position;

        // ── Step 1: sample existing terrain height at each spline point ──
        float[] rawH = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            rawH[i] = terrain.SampleHeight(pts[i]) + terrainPos.y;

        // ── Step 2: slope-limited smoothing passes on the 1-D height profile ──
        //    Only the along-path heights are modified here; the actual terrain
        //    is not touched until step 3.
        float maxTanSlope = Mathf.Tan(maxSlopeDegrees * Mathf.Deg2Rad);

        float[] smoothH = new float[pts.Count];
        System.Array.Copy(rawH, smoothH, pts.Count);

        for (int pass = 0; pass < slopeSmoothPasses; pass++)
        {
            // Forward pass
            for (int i = 1; i < pts.Count; i++)
            {
                float hDist = HorizDist(pts[i - 1], pts[i]);
                if (hDist < 0.001f) continue;
                float maxDelta = hDist * maxTanSlope;
                float d = smoothH[i] - smoothH[i - 1];
                if (Mathf.Abs(d) > maxDelta)
                    smoothH[i] = smoothH[i - 1] + Mathf.Sign(d) * maxDelta;
            }
            // Backward pass
            for (int i = pts.Count - 2; i >= 0; i--)
            {
                float hDist = HorizDist(pts[i], pts[i + 1]);
                if (hDist < 0.001f) continue;
                float maxDelta = hDist * maxTanSlope;
                float d = smoothH[i] - smoothH[i + 1];
                if (Mathf.Abs(d) > maxDelta)
                    smoothH[i] = smoothH[i + 1] + Mathf.Sign(d) * maxDelta;
            }
        }

        // ── Step 2b: steep-zone detection + extra Gaussian smoothing ─────────
        //
        //  Scan a sliding arc-length window of steepZoneWindowMeters.
        //  Where the height change inside that window exceeds what
        //  maxSlopeDegrees allows over that distance, the section is
        //  flagged as a steep zone.  steepZoneExtraPasses of weighted
        //  Gaussian averaging are then applied — proportionally stronger
        //  on the steepest points — to dissolve large steps into gentle
        //  gradients without altering the profile outside the flagged zone.
        //
        if (steepZoneExtraPasses > 0)
        {
            // Build cumulative arc lengths for the spline point array
            float[] arc = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++)
                arc[i] = arc[i - 1] + HorizDist(pts[i - 1], pts[i]);

            // Per-point steepness weight in [0..1]
            // 0 = slope is fine, 1 = slope is far over the limit
            float[] steepWeight = new float[pts.Count];
            float allowedRisePerHalf = maxTanSlope * (steepZoneWindowMeters * 0.5f);

            for (int i = 0; i < pts.Count; i++)
            {
                float halfW = steepZoneWindowMeters * 0.5f;
                float arcL  = arc[i];

                // Find neighbours within the window
                float hMin = smoothH[i], hMax = smoothH[i];
                for (int j = 0; j < pts.Count; j++)
                {
                    if (Mathf.Abs(arc[j] - arcL) > halfW) continue;
                    if (smoothH[j] < hMin) hMin = smoothH[j];
                    if (smoothH[j] > hMax) hMax = smoothH[j];
                }

                float actualRise = hMax - hMin;
                // How many times over the allowance is this zone?
                // allowedRisePerHalf * 2 = full window allowance
                float ratio = actualRise / Mathf.Max(0.001f, allowedRisePerHalf * 2f);
                // Weight starts at 0 until at 1× over limit, reaches 1 at 3×
                steepWeight[i] = Mathf.Clamp01((ratio - 1f) * 0.5f);
            }

            // Extra Gaussian smoothing passes weighted per-point
            // Kernel: neighbours within steepZoneWindowMeters/4 weighted by distance
            float kernelRadius = steepZoneWindowMeters * 0.25f;

            for (int pass = 0; pass < steepZoneExtraPasses; pass++)
            {
                float[] next = new float[pts.Count];

                for (int i = 0; i < pts.Count; i++)
                {
                    if (steepWeight[i] < 0.001f)
                    {
                        next[i] = smoothH[i];
                        continue;
                    }

                    // Weighted average of neighbours within kernel radius
                    float wSum = 0f, hSum = 0f;
                    float arcI = arc[i];

                    for (int j = 0; j < pts.Count; j++)
                    {
                        float dist = Mathf.Abs(arc[j] - arcI);
                        if (dist > kernelRadius) continue;
                        // Gaussian-shaped weight, sigma = kernelRadius/2
                        float sigma = kernelRadius * 0.5f;
                        float w = Mathf.Exp(-(dist * dist) / (2f * sigma * sigma));
                        wSum += w;
                        hSum += smoothH[j] * w;
                    }

                    float averaged = wSum > 0.0001f ? hSum / wSum : smoothH[i];
                    // Blend toward the averaged value proportionally to steepness weight
                    next[i] = Mathf.Lerp(smoothH[i], averaged, steepWeight[i]);
                }

                System.Array.Copy(next, smoothH, pts.Count);

                // Re-run slope clamp after each Gaussian pass to stay within limit
                for (int i = 1; i < pts.Count; i++)
                {
                    float hDist = HorizDist(pts[i - 1], pts[i]);
                    if (hDist < 0.001f) continue;
                    float maxDelta = hDist * maxTanSlope;
                    float d = smoothH[i] - smoothH[i - 1];
                    if (Mathf.Abs(d) > maxDelta)
                        smoothH[i] = smoothH[i - 1] + Mathf.Sign(d) * maxDelta;
                }
                for (int i = pts.Count - 2; i >= 0; i--)
                {
                    float hDist = HorizDist(pts[i], pts[i + 1]);
                    if (hDist < 0.001f) continue;
                    float maxDelta = hDist * maxTanSlope;
                    float d = smoothH[i] - smoothH[i + 1];
                    if (Mathf.Abs(d) > maxDelta)
                        smoothH[i] = smoothH[i + 1] + Mathf.Sign(d) * maxDelta;
                }
            }
        }

        // ── Step 3: pixel-first corridor write ───────────────────────────────
        //    Compute a tight bounding box in heightmap pixel space so we only
        //    touch pixels that could possibly be inside the corridor.
        float halfRoad  = roadWidth * 0.5f + flattenExtraWidth;
        float totalHalf = halfRoad + flattenFalloff;

        // Bounding box of all spline points, padded by corridor width
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - totalHalf < minX) minX = p.x - totalHalf;
            if (p.x + totalHalf > maxX) maxX = p.x + totalHalf;
            if (p.z - totalHalf < minZ) minZ = p.z - totalHalf;
            if (p.z + totalHalf > maxZ) maxZ = p.z + totalHalf;
        }

        // Convert to heightmap pixel indices (clamped to valid range)
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt( ((maxX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt( ((maxZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);

        // Read only the sub-region we need (faster than GetHeights(0,0,full,full))
        int subW = pxMax - pxMin + 1;
        int subH = pzMax - pzMin + 1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
        {
            for (int lx = 0; lx < subW; lx++)
            {
                int   px = pxMin + lx;
                int   pz = pzMin + lz;

                // World XZ of this heightmap texel
                float wx = terrainPos.x + (px / (float)(hw - 1)) * terrainW;
                float wz = terrainPos.z + (pz / (float)(hw - 1)) * terrainH;

                // Nearest point on the spline in XZ → cross-track dist + target height
                float interpH;
                float crossDist = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out interpH);

                if (crossDist > totalHalf) continue;

                float blend;
                if (crossDist <= halfRoad)
                    blend = 1f;
                else if (flattenFalloff > 0.001f)
                    blend = 1f - ((crossDist - halfRoad) / flattenFalloff);
                else
                    continue;

                blend = Mathf.Clamp01(blend);

                // Convert world height → normalised heightmap value (0..1)
                // Relative to existing terrain — never drags to world 0
                float targetNorm = (interpH - terrainPos.y) / terrainMaxY;
                heights[lz, lx]  = Mathf.Lerp(heights[lz, lx], targetNorm, blend);
            }
        }

        td.SetHeights(pxMin, pzMin, heights);

        // ── Step 4: update spline Y values so road mesh follows corrected surface ──
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i];
            p.y = smoothH[i];
            pts[i] = p;
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Berm / Embankment
    // ─────────────────────────────────────────────────────────
    //
    //  For every heightmap pixel in the berm zone
    //  (road edge → road edge + bermSlopeDistance):
    //
    //    t = 0  at road edge     → height = road surface height
    //    t = 1  at berm toe      → height = original terrain height
    //
    //  This naturally handles both sides of any road:
    //    Low side  (terrain below road) → berm slopes DOWN to ground.
    //    High side (terrain above road) → berm cuts INTO the hill and
    //                                     slopes back UP to ground.
    //
    //  The "original terrain height" is read from the snapshot that
    //  was taken before any modifications, so cut-and-fill is always
    //  relative to untouched ground — not any previous flatten pass.
    //
    //  bermCurvature blends between a straight linear slope (0) and
    //  a smooth SmoothStep S-curve (1) for a more natural look.
    // ─────────────────────────────────────────────────────────

    void ApplyBermToTerrain(List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
        {
            Debug.LogWarning("[RoadPathGenerator] Berm: no active terrain found.");
            return;
        }
        if (_savedHeights == null)
        {
            Debug.LogWarning("[RoadPathGenerator] Berm: no terrain snapshot available.");
            return;
        }

        TerrainData td          = terrain.terrainData;
        int         hw          = td.heightmapResolution;
        float       terrainW    = td.size.x;
        float       terrainH    = td.size.z;
        float       terrainMaxY = td.size.y;
        Vector3     terrainPos  = terrain.transform.position;

        // Road-edge distance and outer berm-toe distance
        float halfRoad   = roadWidth * 0.5f;
        float bermInner  = halfRoad;
        float bermOuter  = halfRoad + bermSlopeDistance;
        float totalOuter = bermOuter + bermOuterFalloff;

        // Bounding box over the berm zone
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - totalOuter < minX) minX = p.x - totalOuter;
            if (p.x + totalOuter > maxX) maxX = p.x + totalOuter;
            if (p.z - totalOuter < minZ) minZ = p.z - totalOuter;
            if (p.z + totalOuter > maxZ) maxZ = p.z + totalOuter;
        }

        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt( ((maxX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt( ((maxZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);

        int subW = pxMax - pxMin + 1;
        int subH = pzMax - pzMin + 1;

        // Read current heightmap (may already have flatten applied in corridor)
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        // Build per-spline-point smoothed height array (same smoothH used by flatten)
        // Re-sample here — just use terrain.SampleHeight which reflects any flatten work
        float[] smoothH = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            smoothH[i] = terrain.SampleHeight(pts[i]) + terrainPos.y;

        for (int lz = 0; lz < subH; lz++)
        {
            for (int lx = 0; lx < subW; lx++)
            {
                int   px = pxMin + lx;
                int   pz = pzMin + lz;

                // World XZ of this heightmap texel
                float wx = terrainPos.x + (px / (float)(hw - 1)) * terrainW;
                float wz = terrainPos.z + (pz / (float)(hw - 1)) * terrainH;

                // Nearest spline point → cross-track distance + interpolated road height
                float roadH;
                float crossDist = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out roadH);

                // Only process the berm zone (outside road edge, up to berm toe + falloff)
                if (crossDist < bermInner || crossDist > totalOuter) continue;

                // t: 0 = road edge, 1 = berm toe
                float t = Mathf.Clamp01((crossDist - bermInner) / bermSlopeDistance);

                // Apply curvature blend: lerp between linear t and smoothstep t
                float tLinear     = t;
                float tSmooth     = t * t * (3f - 2f * t);   // SmoothStep
                float tShaped     = Mathf.Lerp(tLinear, tSmooth, bermCurvature);

                // Outer falloff — fade blend strength back to 0 at the very toe
                // so the berm doesn't stamp a hard seam on unmodified terrain
                float outerBlend = 1f;
                if (bermOuterFalloff > 0.001f && crossDist > bermOuter)
                    outerBlend = 1f - ((crossDist - bermOuter) / bermOuterFalloff);
                outerBlend = Mathf.Clamp01(outerBlend);

                // Original (pre-generation) terrain height at this pixel
                // _savedHeights is indexed [z, x] in FULL heightmap space
                float origNorm   = _savedHeights[pz, px];
                float origWorldH = origNorm * terrainMaxY + terrainPos.y;

                // Target: lerp from road-edge height (t=0) → original terrain (t=1)
                float targetWorldH = Mathf.Lerp(roadH, origWorldH, tShaped);
                float targetNorm   = (targetWorldH - terrainPos.y) / terrainMaxY;

                // Blend with outerBlend so the outer toe fades to existing heightmap
                heights[lz, lx] = Mathf.Lerp(heights[lz, lx], targetNorm, outerBlend);
            }
        }

        td.SetHeights(pxMin, pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Paint
    // ─────────────────────────────────────────────────────────
    //
    //  PIXEL-FIRST with smooth road-frame cross-track distance.
    //
    //  Zone layout (from centreline outward):
    //   [0 → halfRoad]         = solid at paintStrength (full road coverage)
    //   [halfRoad → halfRoad+shoulder] = falloff from paintStrength → 0
    //   beyond                 = not painted
    //
    //  Cross-track distance uses RoadCrossTrackDistance which
    //  projects through the SMOOTH road tangent (not Euclidean
    //  nearest-point), eliminating zig-zag at bends.
    // ─────────────────────────────────────────────────────────

    void PaintTerrainAlongPath(List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null)
        {
            Debug.LogWarning("[RoadPathGenerator] PaintTerrain: no active terrain found.");
            return;
        }

        TerrainData td        = terrain.terrainData;
        int         aw        = td.alphamapWidth;
        int         ah        = td.alphamapHeight;
        int         numLayers = td.alphamapLayers;
        Vector3     tPos      = terrain.transform.position;

        if (paintTextureIndex >= numLayers)
        {
            Debug.LogWarning("[RoadPathGenerator] paintTextureIndex out of range.");
            return;
        }

        // Road hard edge = roadWidth/2.
        // Shoulder extends paintShoulderWidth beyond that, with paintShoulderFalloff fade.
        float halfRoad     = roadWidth * 0.5f;
        float shoulder     = Mathf.Max(0f, paintShoulderWidth);
        float falloffWidth = Mathf.Clamp(paintShoulderFalloff, 0f, shoulder);
        float solidEnd     = halfRoad;                   // full strength up to here
        float fadeStart    = halfRoad + (shoulder - falloffWidth);  // fade starts
        float totalHalf    = halfRoad + shoulder;        // outer edge

        // Bounding box padded by total paint half-width
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - totalHalf < minX) minX = p.x - totalHalf;
            if (p.x + totalHalf > maxX) maxX = p.x + totalHalf;
            if (p.z - totalHalf < minZ) minZ = p.z - totalHalf;
            if (p.z + totalHalf > maxZ) maxZ = p.z + totalHalf;
        }

        int axMin = Mathf.Clamp(Mathf.FloorToInt(((minX - tPos.x) / td.size.x) * aw), 0, aw - 1);
        int axMax = Mathf.Clamp(Mathf.CeilToInt( ((maxX - tPos.x) / td.size.x) * aw), 0, aw - 1);
        int azMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - tPos.z) / td.size.z) * ah), 0, ah - 1);
        int azMax = Mathf.Clamp(Mathf.CeilToInt( ((maxZ - tPos.z) / td.size.z) * ah), 0, ah - 1);

        int subW = axMax - axMin + 1;
        int subH = azMax - azMin + 1;
        float[,,] alphas = td.GetAlphamaps(axMin, azMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
        {
            for (int lx = 0; lx < subW; lx++)
            {
                int   px = axMin + lx;
                int   pz = azMin + lz;

                float wx = tPos.x + (px / (float)aw) * td.size.x;
                float wz = tPos.z + (pz / (float)ah) * td.size.z;

                // Road-frame cross-track distance (smooth tangent, no zig-zag)
                float crossDist = RoadCrossTrackDistance(wx, wz, pts);

                if (crossDist > totalHalf) continue;

                // Solid zone: full strength up to road edge
                float strength;
                if (crossDist <= solidEnd)
                {
                    strength = paintStrength;
                }
                // Shoulder solid zone (no falloff yet)
                else if (crossDist <= fadeStart || falloffWidth < 0.001f)
                {
                    strength = paintStrength;
                }
                // Shoulder falloff zone
                else
                {
                    float t = (crossDist - fadeStart) / falloffWidth;
                    strength = paintStrength * (1f - Mathf.Clamp01(t));
                }

                if (strength < 0.001f) continue;

                float current = alphas[lz, lx, paintTextureIndex];
                float newVal  = Mathf.Lerp(current, 1f, strength);
                alphas[lz, lx, paintTextureIndex] = newVal;

                // Re-normalise other layers so they sum to 1
                float otherSum = 0f;
                for (int l = 0; l < numLayers; l++)
                    if (l != paintTextureIndex) otherSum += alphas[lz, lx, l];

                if (otherSum > 0.0001f)
                {
                    float scale = (1f - newVal) / otherSum;
                    for (int l = 0; l < numLayers; l++)
                        if (l != paintTextureIndex)
                            alphas[lz, lx, l] *= scale;
                }
            }
        }

        td.SetAlphamaps(axMin, azMin, alphas);
    }

    // ─────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────

    // Returns the road-frame cross-track distance for a world XZ point.
    //
    // Unlike raw Euclidean nearest-point distance, this measures the
    // displacement projected onto the SMOOTH road perpendicular at the
    // nearest location.  At bends the direction is blended from adjacent
    // segments (central difference), giving clean parallel paint edges
    // instead of circular zig-zag arcs at junctions.
    static float RoadCrossTrackDistance(float wx, float wz, List<Vector3> pts)
    {
        if (pts.Count < 2) return float.MaxValue;

        Vector2 query = new Vector2(wx, wz);

        // Step 1: find nearest segment and parameter t
        int    bestSeg   = 0;
        float  bestT     = 0f;
        float  bestEuclid = float.MaxValue;
        Vector2 bestPoint = Vector2.zero;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = new Vector2(pts[i].x,     pts[i].z);
            Vector2 b = new Vector2(pts[i + 1].x, pts[i + 1].z);
            float t;
            float d = ClosestPointOnSegment2D(query, a, b, out t);
            if (d < bestEuclid)
            {
                bestEuclid = d;
                bestSeg    = i;
                bestT      = t;
                bestPoint  = a + (b - a) * t;
            }
        }

        // Step 2: smooth road tangent at best point via central-difference blending
        //   currDir  = direction of bestSeg
        //   prevDir  = direction of segment before (if exists)
        //   nextDir  = direction of segment after  (if exists)
        // Blend linearly: at t=0 use prev+curr average, at t=1 use curr+next average.
        Vector2 currDir = SegDir2D(pts, bestSeg);
        Vector2 prevDir = bestSeg > 0             ? SegDir2D(pts, bestSeg - 1) : currDir;
        Vector2 nextDir = bestSeg < pts.Count - 2 ? SegDir2D(pts, bestSeg + 1) : currDir;

        // Blend toward neighbouring segment direction at each end of the segment
        Vector2 dir;
        if (bestT < 0.5f)
            dir = Vector2.Lerp((prevDir + currDir) * 0.5f, currDir, bestT * 2f);
        else
            dir = Vector2.Lerp(currDir, (currDir + nextDir) * 0.5f, (bestT - 0.5f) * 2f);

        if (dir.sqrMagnitude < 0.00001f) return bestEuclid;
        dir.Normalize();

        // Step 3: road-right perpendicular and project query offset onto it
        Vector2 roadRight = new Vector2(-dir.y, dir.x);
        float crossDist   = Mathf.Abs(Vector2.Dot(query - bestPoint, roadRight));

        return crossDist;
    }

    // Returns the minimum XZ distance from (wx,wz) to the nearest
    // spline segment, and fills interpHeight with the height linearly
    // interpolated from smoothH[] at that nearest point.
    // Used by flatten and berm (height matters; zig-zag is irrelevant there).
    static float ClosestPointOnSplineXZ(
        float wx, float wz,
        List<Vector3> pts,
        float[] smoothH,
        out float interpHeight)
    {
        float minDist = float.MaxValue;
        interpHeight = 0f;

        Vector2 query = new Vector2(wx, wz);

        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = new Vector2(pts[i].x,     pts[i].z);
            Vector2 b = new Vector2(pts[i + 1].x, pts[i + 1].z);

            float t;
            float d = ClosestPointOnSegment2D(query, a, b, out t);

            if (d < minDist)
            {
                minDist = d;
                interpHeight = Mathf.Lerp(smoothH[i], smoothH[i + 1], t);
            }
        }

        return minDist;
    }

    // Normalised 2-D direction of spline segment[si] in XZ.
    static Vector2 SegDir2D(List<Vector3> pts, int si)
    {
        return (new Vector2(pts[si + 1].x, pts[si + 1].z)
              - new Vector2(pts[si].x,     pts[si].z)).normalized;
    }

    // Distance from point p to line segment (a,b) in 2-D.
    // Fills t with the clamped [0,1] parameter along the segment.
    static float ClosestPointOnSegment2D(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        Vector2 ab   = b - a;
        float   len2 = ab.sqrMagnitude;
        if (len2 < 0.00001f) { t = 0f; return (p - a).magnitude; }
        t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).magnitude;
    }

    // Horizontal (XZ-only) distance between two spline points.
    static float HorizDist(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static float[] BuildArcLengths(List<Vector3> pts)
    {
        float[] lens = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            lens[i] = lens[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
        return lens;
    }

    static Vector3 GetForward(List<Vector3> pts, int i)
    {
        if (i < pts.Count - 1) return (pts[i + 1] - pts[i]).normalized;
        return (pts[i] - pts[i - 1]).normalized;
    }

    // ─────────────────────────────────────────────────────────
    //  Gizmos (Editor preview)
    // ─────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (controlPoints == null || controlPoints.Count < 2) return;

        List<Vector3> cp = new List<Vector3>();
        foreach (Transform t in controlPoints)
            if (t != null) cp.Add(t.position);

        if (closedLoop && cp.Count > 2) cp.Add(cp[0]);

        Color edgeColor = new Color(1f, 0.3f, 0.3f, 0.7f);

        Gizmos.color = new Color(1f, 0.8f, 0.1f, 1f);
        foreach (Transform t in controlPoints)
            if (t != null) Gizmos.DrawWireSphere(t.position, 0.4f);

        for (int i = 0; i < cp.Count - 1; i++)
        {
            Vector3 p0 = cp[Mathf.Max(0, i - 1)];
            Vector3 p1 = cp[i];
            Vector3 p2 = cp[Mathf.Min(cp.Count - 1, i + 1)];
            Vector3 p3 = cp[Mathf.Min(cp.Count - 1, i + 2)];

            Vector3 prev = CatmullRom(p0, p1, p2, p3, 0f);
            for (int s = 1; s <= segmentsPerCurve; s++)
            {
                float   t   = s / (float)segmentsPerCurve;
                Vector3 cur = CatmullRom(p0, p1, p2, p3, t);

                Gizmos.color = new Color(0.2f, 0.9f, 1f, 1f);
                Gizmos.DrawLine(prev, cur);

                Vector3 fwd   = (cur - prev).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

                Gizmos.color = edgeColor;
                Gizmos.DrawLine(prev - right * (roadWidth * 0.5f),
                                cur  - right * (roadWidth * 0.5f));
                Gizmos.DrawLine(prev + right * (roadWidth * 0.5f),
                                cur  + right * (roadWidth * 0.5f));

                prev = cur;
            }
        }
    }
}
