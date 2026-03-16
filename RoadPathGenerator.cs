// ============================================================
//  RoadPathGenerator.cs
//  Unity 2017  |  C# 4.0  |  ModTool.Interface / ModBehaviour
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using ModTool.Interface;

// ─────────────────────────────────────────────────────────────
//  RoadSegmentStyle
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public class RoadSegmentStyle
{
    [Tooltip("Label shown in the inspector.")]
    public string styleName = "Segment";

    [Tooltip("Material for the road surface mesh in this segment.\n" +
             "Null = no mesh; terrain paint shows through instead.")]
    public Material roadMaterial;

    [Tooltip("When true, the settings below replace the global paint settings for this segment.")]
    public bool overridePaint = false;

    [Range(0, 15)]
    public int paintTextureIndex = 0;

    [Range(0f, 20f)]
    public float paintShoulderWidth = 1f;

    [Range(0f, 20f)]
    public float paintShoulderFalloff = 0.8f;

    [Range(0f, 1f)]
    public float paintStrength = 0.9f;

    [Tooltip("When true, the curb settings below replace the global settings for this segment.")]
    public bool overrideCurbs = false;

    [Tooltip("Generate curbs in this segment (only used when overrideCurbs is true).")]
    public bool generateCurbs = true;

    [Tooltip("Curb material for this segment (only used when overrideCurbs is true).")]
    public Material curbMaterial;

    [Tooltip("Include this segment in the MeshCollider.")]
    public bool generateCollision = true;
}

[DisallowMultipleComponent]
public class RoadPathGenerator : ModBehaviour
{
    // ── Control Points ────────────────────────────────────────
    [Header("Control Points")]
    [Tooltip("List of transforms that define the road path.\n" +
             "At least 2 points are required.  Add via the inspector buttons\n" +
             "or Shift+Right-click in the scene when Edit Points Mode is active.")]
    public List<Transform> controlPoints = new List<Transform>();

    [Tooltip("When true, connects the last control point back to the first,\n" +
             "forming a closed loop circuit road.")]
    public bool closedLoop = false;

    // ── Spline / Resolution ───────────────────────────────────
    [Header("Spline Resolution")]
    [Tooltip("Number of mesh segments generated between each pair of control points.\n" +
             "Higher values produce a smoother road curve but increase vertex count.\n" +
             "20-30 is sufficient for most roads.")]
    [Range(2, 64)]
    public int segmentsPerCurve = 20;

    [Tooltip("Multiplier applied to segmentsPerCurve when building the spline used exclusively\n" +
             "for terrain operations (flatten, berm, ridge cap, ditch, shoulder smooth, paint).\n\n" +
             "The mesh spline is unaffected — this purely increases the density of sample points\n" +
             "that terrain passes query against.\n\n" +
             "WHY THIS MATTERS\n" +
             "Each heightmap pixel finds its nearest point on the spline by testing every segment.\n" +
             "With few segments the nearest-point projection jumps discontinuously between adjacent\n" +
             "segments at their midpoints, creating zigzag edges on the flattened road surface and\n" +
             "angular facets on the ditch / berm walls.\n\n" +
             "4 – 8x multiplier eliminates zigzag entirely for most road widths.\n" +
             "Higher values are more accurate but slower to generate.\n" +
             "1 = same density as the mesh (original behaviour, zigzag visible).")]
    [Range(0, 8)]
    public int terrainSplineSubdivision = 4;

    [Header("Keep this checked unless you want to build a mesh for the curbs")]
    [Tooltip("Tick this once you are happy with the generated road and have baked the mesh.\n" +
         "When true, Start() will skip all generation and terrain work entirely.\n" +
         "Untick if you need to re-generate the road in the editor.")]
    public bool generationComplete = true;

    // ── Baked Mesh ────────────────────────────────────────────
    [Header("Baked Mesh - only with use for curbs")]
    [Tooltip("Assign an empty GameObject here after clicking Generate Road,\n" +
             "then click Bake Mesh to Object.  At runtime Start() will detect\n" +
             "the pre-built mesh and skip all generation entirely.")]
    public GameObject bakedMeshObject;

    // ── Road Geometry ─────────────────────────────────────────
    [Header("Road Geometry")]
    [Tooltip("Total width of the road surface in world units, measured edge to edge.")]
    [Range(0.5f, 50f)]
    public float roadWidth = 10f;

    [Tooltip("Thickness of the road mesh in world units.\n" +
             "Adds a bottom face to the road surface so it does not appear\n" +
             "paper-thin when viewed from the side or below.\n" +
             "0 = single surface with no underside.")]
    [Range(0f, 2f)]
    public float roadThickness = 0.1f;

    [Tooltip("Camber (cross-slope) angle in degrees — positive = left side higher.")]
    [Range(-80f, 80f)]
    public float camberDegrees = 0f;

    // ── Road Material ─────────────────────────────────────────
    [Header("Road Material - not tested")]
    [Tooltip("Default material applied to the road surface mesh.\n" +
             "Can be overridden per segment in the Segment Styles list below.")]
    public Material roadMaterial;

    [Tooltip("Length in world units over which the road texture tiles once\n" +
             "along the road direction.  Smaller = more frequent tiling.\n" +
             "Match to your texture's real-world scale for best results.")]
    [Range(0.1f, 50f)]
    public float uvTileLength = 10f;

    // ── Curbs / Shoulders ─────────────────────────────────────
    [Header("Curbs / Shoulders - currently broken")]
    [Tooltip("Generate raised curb strips along both edges of the road.\n" +
             "Curbs are baked into the road mesh as additional submeshes.\n" +
             "Can be overridden per segment in the Segment Styles list.")]
    public bool generateCurbs = false;

    [Tooltip("Height of the curb above the road edge surface in world units.")]
    [Range(0.05f, 2f)]
    public float curbHeight = 0.2f;

    [Tooltip("Width of the curb strip in world units, measured outward from the road edge.")]
    [Range(0.1f, 3f)]
    public float curbWidth = 0.4f;

    [Tooltip("Material applied to the curb mesh.\n" +
             "Can be overridden per segment in the Segment Styles list.")]
    public Material curbMaterial;

    // ── Berm / Embankment ─────────────────────────────────────
    [Header("Berm / Embankment")]
    [Tooltip("Stamps an embankment slope on both sides of the road that blends\n" +
             "from the road edge height back down to natural terrain.\n" +
             "Useful for elevated roads or cutting a road into a hillside.")]
    public bool generateBerm = true;

    [Tooltip("Distance in world units from the road edge to where the berm slope\n" +
             "reaches natural terrain level.  Larger = gentler, wider embankment.")]
    [Range(0.5f, 40f)]
    public float bermSlopeDistance = 25f;

    [Tooltip("0 = linear slope, 1 = smooth S-curve.")]
    [Range(0f, 1f)]
    public float bermCurvature = 0.5f;

    [Tooltip("Distance in world units over which the berm blends back to natural\n" +
             "terrain at its outer edge.  Prevents a hard seam at the berm toe.")]
    [Range(0f, 10f)]
    public float bermOuterFalloff = 1.5f;

    // ── Ridge Cap ─────────────────────────────────────────────
    [Header("Ridge Cap - only used with camber")]
    [Tooltip("Stamps a smooth rounded bell-curve profile centred on EACH road edge.\n\n" +
             "This eliminates the triangular peaks that appear between spline segments\n" +
             "at the cambered edge.  The bell extends ridgeCapWidth metres on BOTH sides\n" +
             "of the road edge, creating a rolling-hill transition between the cambered\n" +
             "road surface and the berm / natural shoulder.\n\n" +
             "Only active when |camberDegrees| > 0.1°.")]
    public bool applyRidgeCap = false;

    [Tooltip("Half-width of the smooth rolling-hill zone in world units.\n" +
             "The bell profile is centred on the road edge and extends this distance\n" +
             "INWARD (into the road surface) and OUTWARD (into the shoulder / berm).\n\n" +
             "1 – 3 m produces the 'rolling hill' look.  Match to your bermSlopeDistance\n" +
             "for a seamless join between the cap and the berm.")]
    [Range(0.5f, 8f)]
    public float ridgeCapWidth = 6f;

    [Tooltip("0 = wide cosine bell (broad, gentle hill).\n" +
             "1 = narrow Gaussian bell (tighter crest).\n" +
             "0.3 – 0.5 gives the most natural rolling-hill result.")]
    [Range(0f, 1f)]
    public float ridgeCapSharpness = 0.35f;

    [Tooltip("Overall blend strength of the ridge cap at the road edge.\n" +
             "1.0 = fully overrides whatever flatten / berm wrote there.\n" +
             "Lower values let the underlying terrain show through more.")]
    [Range(0f, 1f)]
    public float ridgeCapStrength = 1.0f;

    [Tooltip("Extra Gaussian passes run only inside the cap zone after the bell is\n" +
             "stamped.  2 – 3 passes removes micro-jaggedness at the crest without\n" +
             "touching terrain outside the cap zone.")]
    [Range(0, 6)]
    public int ridgeCapSmoothPasses = 3;

    [Tooltip("Extra world-unit height added to the crest of the ridge cap bell.\n\n" +
             "0  = crest sits exactly at the cambered road-edge height (flush).\n" +
             "+  = crest is raised above the road edge — creates a visible berm\n" +
             "     peak or protective lip at the high side of the road.\n" +
             "-  = crest is lowered below the road edge — sinks the transition\n" +
             "     into a shallow channel or drainage swale.\n\n" +
             "The height offset is scaled by the bell profile, so it is at full\n" +
             "strength at the road edge and tapers smoothly to zero at ridgeCapWidth,\n" +
             "keeping the transition perfectly rounded with no sharp steps.")]
    [Range(-5f, 5f)]
    public float ridgeCapHeight = 2f;

    // ── Ditch / Drainage ─────────────────────────────────────
    [Header("Ditch / Drainage")]
    [Tooltip("Carve a smooth drainage ditch along BOTH sides of the road.\n\n" +
             "The ditch sits OUTSIDE the berm / shoulder zone.  Set ditchOffset\n" +
             "to match or exceed bermSlopeDistance + bermOuterFalloff so the ditch\n" +
             "toe starts exactly at the berm toe for a seamless join.\n\n" +
             "Cross-section profile (one side):\n" +
             "  road edge\n" +
             "  <── ditchOffset ──> <─inner─> <─bottom─> <─outer─>\n" +
             "                      ↘                    ↗  natural terrain")]
    public bool generateDitch = true;

    [Tooltip("Distance from the road edge to the top of the inner ditch wall (world units).\n" +
             "Set to bermSlopeDistance + bermOuterFalloff to start exactly at the berm toe.")]
    [Range(0f, 40f)]
    public float ditchOffset = 2f;

    [Tooltip("Width of the slope descending INTO the ditch from the road / berm side (world units).\n" +
             "Larger = gentler grade down.  Smaller = steeper cut.")]
    [Range(0.5f, 20f)]
    public float ditchInnerWidth = 5f;

    [Tooltip("Width of the flat ditch floor (world units).\n" +
             "0  = V-ditch (inner and outer walls meet at a point).\n" +
             ">0 = U/trapezoidal ditch with a flat bottom.")]
    [Range(0f, 10f)]
    public float ditchBottomWidth = 4f;

    [Tooltip("Width of the slope climbing OUT of the ditch back to natural terrain (world units).\n" +
             "Larger = gentler grade up.  Usually >= ditchInnerWidth for a natural asymmetric look.")]
    [Range(0.5f, 20f)]
    public float ditchOuterWidth = 5f;

    [Tooltip("Maximum depth of the ditch floor below the terrain surface at the inner wall start (world units).\n" +
             "The bottom is cut this deep below whatever height the terrain already has at that point\n" +
             "(i.e. after flatten / berm have run), so the ditch always reads as a positive excavation.")]
    [Range(0.1f, 10f)]
    public float ditchDepth = 2.0f;

    [Tooltip("Shape of the ditch wall profiles.\n" +
             "0 = straight linear slopes (sharp V or flat trapezoid).\n" +
             "1 = smooth S-curve (eases in at top and bottom for a rounded, natural gutter).\n" +
             "0.6 – 0.8 gives a nicely rounded profile.")]
    [Range(0f, 1f)]
    public float ditchCurvature = 0.7f;

    [Tooltip("Overall blend strength of the ditch carve.\n" +
             "1.0 = fully cuts to the target depth profile.\n" +
             "Lower values produce a shallower impression — good for subtle drainage hints.")]
    [Range(0f, 1f)]
    public float ditchStrength = 0.75f;

    [Tooltip("Number of Gaussian smoothing passes applied inside the ditch zone after carving.\n" +
             "Smooths the walls and floor without touching terrain outside the zone.\n" +
             "2 – 3 passes for a natural rounded gutter; more for soft swale-like features.")]
    [Range(0, 6)]
    public int ditchSmoothPasses = 3;

    [Tooltip("Gaussian kernel radius (world units) during ditch smoothing passes.\n" +
             "Larger radius = broader blending across walls and floor.")]
    [Range(0.5f, 10f)]
    public float ditchSmoothRadius = 4f;

    // ── Shoulder Smoothing ────────────────────────────────────
    [Header("Terrain – Shoulder Smoothing")]
    [Tooltip("Apply Gaussian smoothing to the terrain shoulder zone on both sides\n" +
             "of the road after all other terrain passes have run.\n" +
             "Softens any remaining hard edges or artefacts at the road boundary.")]
    public bool smoothShoulder = true;

    [Tooltip("Number of Gaussian smoothing passes applied to the shoulder zone.\n" +
             "More passes = softer shoulder but slower generation.")]
    [Range(0, 10)]
    public int shoulderSmoothPasses = 3;

    [Tooltip("Distance in world units outward from the road edge that the shoulder\n" +
             "smoothing covers.  Automatically clamped to stop at the ditch zone\n" +
             "start when a ditch is active, preventing it from undoing the carve.")]
    [Range(0.5f, 50f)]
    public float shoulderSmoothDistance = 12f;

    [Tooltip("Gaussian kernel radius in world units for shoulder smoothing.\n" +
             "Larger radius = broader, softer blending across the shoulder zone.")]
    [Range(0.5f, 20f)]
    public float shoulderSmoothRadius = 5f;

    [Tooltip("How far in world units the shoulder smoothing kernel reaches inward\n" +
             "past the road edge.  A small overlap blends the transition between\n" +
             "the flat road surface and the smoothed shoulder.")]
    [Range(0f, 5f)]
    public float shoulderSmoothInwardOverlap = 1.5f;

    // ── Collider ──────────────────────────────────────────────
    [Header("Collider - only used for curbs")]
    [Tooltip("Generate a MeshCollider for the road surface so players and physics\n" +
             "objects interact with it.  Can be disabled per segment in Segment Styles.\n" +
             "Disable if the terrain collider alone is sufficient.")]
    public bool generateCollider = false;

    // ── Terrain – Snap ────────────────────────────────────────
    [Header("Terrain – Snap")]
    [Tooltip("Snap each spline point vertically onto the terrain surface before\n" +
             "building the road mesh.  Ensures the road sits flush on the terrain\n" +
             "rather than floating above or sinking below it.")]
    public bool snapToTerrain = true;

    [Tooltip("Small upward offset in world units applied after snapping to terrain.\n" +
             "Prevents z-fighting between the road mesh and terrain surface.\n" +
             "0.05 is usually sufficient.")]
    [Range(0f, 1f)]
    public float terrainSnapOffset = 0.05f;

    // ── Terrain – Slope Flatten ───────────────────────────────
    [Header("Terrain – Slope Flatten")]
    [Tooltip("Flatten the terrain heightmap along the road corridor so the road\n" +
             "surface sits on level ground.  Uses slope-limited smoothing to avoid\n" +
             "sharp cuts into the terrain on steep sections.")]
    public bool flattenTerrain = true;

    [Range(1f, 30f)]
    [Tooltip("Maximum uphill grade in degrees. Slope limiting is enforced on climbs.\n" +
             "Keeps the path rideable and prevents flatten from over-raising terrain.")]
    public float maxUphillDegrees = 5f;

    [Range(1f, 80f)]
    [Tooltip("Maximum downhill grade in degrees. Can be set much higher than uphill\n" +
             "so descents follow natural terrain more closely, preserving the surrounding\n" +
             "ground context that berm, ditch, and shoulder depend on.")]
    public float maxDownhillDegrees = 40f;

    [Tooltip("Number of forward-backward smoothing passes applied to the centreline\n" +
             "height profile before flattening.  More passes = smoother grade changes\n" +
             "along the road length.  5 is a good default.")]
    [Range(1, 10)]
    public int slopeSmoothPasses = 5;

    [Range(0f, 45f)]
    [Tooltip("Minimum grade change in degrees between neighbouring spline points\n" +
             "that triggers transition smoothing.  Lower = more sensitive.\n" +
             "3-5 degrees catches most abrupt transitions without over-smoothing.")]
    public float transitionAngleThreshold = 4f;

    [Range(1f, 40f)]
    [Tooltip("World unit radius around each detected transition point that gets\n" +
             "smoothed.  20m covers roughly the run-in and run-out of most\n" +
             "abrupt grade changes without touching the steady slope either side.")]
    public float transitionSmoothRadius = 30f;

    [Range(0, 20)]
    [Tooltip("Number of Gaussian smoothing passes applied at transition points.\n" +
             "6-10 is enough for most transitions.  Higher = smoother but slower.")]
    public int transitionSmoothPasses = 15;

    [Tooltip("Extra width in world units added beyond the road edge on each side\n" +
             "that is fully flattened.  Clears bumps right at the road shoulder.")]
    [Range(0f, 20f)]
    public float flattenExtraWidth = 1f;

    [Tooltip("Width in world units beyond flattenExtraWidth over which the flatten\n" +
             "blends back to natural terrain height.  Prevents a hard cliff edge\n" +
             "at the outer boundary of the flattened zone.")]
    [Range(0f, 20f)]
    public float flattenFalloff = 3f;

    [Tooltip("When true, flattens terrain on both sides of the road beyond the road width\n" +
         "before Berm, Ditch, and Shoulder operations run.\n" +
         "Useful for clearing uneven ground adjacent to the road corridor.")]
    public bool flattenBeyondPath = true;

    [Tooltip("Distance in world units to flatten outward from each road edge.\n" +
             "Applied before Berm, Ditch, and Shoulder Smoothing.")]
    [Range(1f, 30f)]
    public float flattenBeyondDistance = 15f;

    // ── Terrain – Paint ───────────────────────────────────────
    [Header("Terrain – Paint")]
    [Tooltip("Paint a terrain texture layer along the road corridor after all\n" +
             "terrain height operations have completed.\n" +
             "Uses the alphamap so it blends naturally with surrounding textures.")]
    public bool paintTerrain = true;

    [Tooltip("Terrain texture layer index to paint onto the road surface globally.\n" +
             "Must match a valid layer index in the terrain's terrain data.\n" +
             "Can be overridden per segment in the Segment Styles list.")]
    [Range(0, 15)]
    public int paintTextureIndex = 1;

    [Tooltip("Extra paint width in world units beyond the road edge on each side.\n" +
             "Paints a shoulder strip alongside the road surface globally.")]
    [Range(0f, 20f)]
    public float paintShoulderWidth = 1f;

    [Tooltip("Width in world units over which the shoulder paint fades to zero\n" +
             "at its outer edge.  Creates a soft blend into surrounding terrain.")]
    [Range(0f, 20f)]
    public float paintShoulderFalloff = 0.8f;

    [Tooltip("Global blend strength of the terrain paint.  1.0 = fully overrides\n" +
             "existing texture.  Lower values let underlying terrain show through.")]
    [Range(0f, 1f)]
    public float paintStrength = 1.0f;

    // ── Segment Styles ────────────────────────────────────────
    [Header("Segment Styles")]
    public List<RoadSegmentStyle> segmentStyles = new List<RoadSegmentStyle>();

    // ── Private ───────────────────────────────────────────────
    private MeshFilter   _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;

    private List<Vector3> _cachedSplinePoints = new List<Vector3>();
    private int[]         _splineStyleIndices;

    [SerializeField, HideInInspector] private float[] _savedHeightsFlat;
    [SerializeField, HideInInspector] private int     _savedHeightsRes;
    [SerializeField, HideInInspector] private float[] _savedAlphasFlat;
    [SerializeField, HideInInspector] private int     _savedAlphasW;
    [SerializeField, HideInInspector] private int     _savedAlphasH;
    [SerializeField, HideInInspector] private int     _savedAlphasLayers;
    [SerializeField, HideInInspector] private bool    _terrainSnapshotTaken = false;
    [System.NonSerialized] public bool _skipTerrainShaping = false;

#if UNITY_EDITOR
    float[,] GetSavedHeights()
    {
        if (_savedHeightsFlat == null || _savedHeightsRes == 0) return null;
        int r = _savedHeightsRes;
        float[,] h = new float[r, r];
        Buffer.BlockCopy(_savedHeightsFlat, 0, h, 0, _savedHeightsFlat.Length * sizeof(float));
        return h;
    }
    float[,,] GetSavedAlphas()
    {
        if (_savedAlphasFlat == null || _savedAlphasW == 0) return null;
        float[,,] a = new float[_savedAlphasH, _savedAlphasW, _savedAlphasLayers];
        Buffer.BlockCopy(_savedAlphasFlat, 0, a, 0, _savedAlphasFlat.Length * sizeof(float));
        return a;
    }
#endif

    // ─────────────────────────────────────────────────────────
    //  ModBehaviour lifecycle
    // ─────────────────────────────────────────────────────────

    void Start()
    {
        // If a baked mesh object is assigned and already carries a valid mesh,
        // skip all generation — terrain is already baked into the TerrainData
        // and the mesh is pre-built on the assigned object.
        //if (bakedMeshObject != null)
        //{
        //   MeshFilter bmf = bakedMeshObject.GetComponent<MeshFilter>();
        //  if (bmf != null && bmf.sharedMesh != null)
        //      return;
        //}

        // Skip all generation if the road has been finalized in the editor.
        // Terrain is already baked into TerrainData and the mesh exists on
        // bakedMeshObject — there is nothing to do at runtime.
        if (generationComplete) return;

        _meshFilter = AddComponent<MeshFilter>(gameObject);
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

        EnsureStyleListLength();

        bool needsTerrainWork = flattenTerrain || snapToTerrain || generateBerm;
        if (!needsTerrainWork && paintTerrain) needsTerrainWork = true;
        if (!needsTerrainWork)
            foreach (RoadSegmentStyle s in segmentStyles)
                if (s.overridePaint) { needsTerrainWork = true; break; }

        if (needsTerrainWork)
        {
#if UNITY_EDITOR
            if (!_terrainSnapshotTaken) SnapshotTerrain();
            else                        RestoreTerrain();
#endif
        }

        // 1. Build spline (mesh density) and terrain spline (high density)
        _cachedSplinePoints = BuildSplinePoints();
        if (_cachedSplinePoints.Count < 2) return;
        _splineStyleIndices = BuildSplineStyleIndices(_cachedSplinePoints.Count);

        // High-density spline for all terrain passes.
        // More sample points = smooth projected road edge = no zigzag / faceted walls.
        List<Vector3> terrainPts = (terrainSplineSubdivision > 1)
            ? BuildTerrainSplinePoints()
            : _cachedSplinePoints;

        // 2. Slope-limited flatten — returns post-flatten centreline heights
        float[] postFlattenH = null;
        if (flattenTerrain)
        {
            FlattenTerrainSlopeLimited(ref terrainPts, out postFlattenH);

            // Sync mesh spline Y values from the high-density terrain spline.
            // terrainPts was built at (segmentsPerCurve * terrainSplineSubdivision)
            // density.  Index i*subdiv on the terrain spline corresponds to index i
            // on the mesh spline — both are Catmull-Rom at the same control points.
            // Without this sync, _cachedSplinePoints still has raw Catmull-Rom Y
            // values that don't match the slope-limited terrain profile, causing
            // visible bumps in the middle of the road after SnapToTerrain.
            if (terrainSplineSubdivision > 1)
            {
                int subdiv = terrainSplineSubdivision;
                for (int i = 0; i < _cachedSplinePoints.Count; i++)
                {
                    int ti = Mathf.Clamp(i * subdiv, 0, terrainPts.Count - 1);
                    Vector3 p = _cachedSplinePoints[i];
                    p.y = terrainPts[ti].y;
                    _cachedSplinePoints[i] = p;
                }
            }
        }

        // Fall back: sample terrain now so ridge cap has accurate heights
        if (postFlattenH == null)
        {
            Terrain at = Terrain.activeTerrain;
            postFlattenH = new float[terrainPts.Count];
            for (int i = 0; i < terrainPts.Count; i++)
                postFlattenH[i] = SampleTerrainHeight(terrainPts[i], at);
        }
        // 2b. Flatten beyond path width — clears uneven ground on both sides
        // of the road corridor before Berm, Ditch, and Shoulder ops run.
        if (flattenBeyondPath && !_skipTerrainShaping)
            FlattenBeyondPathWidth(terrainPts, postFlattenH);

        // 3. Berm
        if (generateBerm && !_skipTerrainShaping)
            ApplyBermToTerrain(terrainPts);

        // Resample terrain heights after Berm so Ridge Cap works from the
        // actual current terrain state rather than the stale post-flatten
        // snapshot.  If Berm raised the shoulder, Ridge Cap now sees those
        // correct elevations when computing its crest targets.
        if (applyRidgeCap && Mathf.Abs(camberDegrees) > 0.1f && !_skipTerrainShaping)
        {
            Terrain _ridgeCapTerrain = Terrain.activeTerrain;
            float[] postBermH = new float[terrainPts.Count];
            for (int i = 0; i < terrainPts.Count; i++)
                postBermH[i] = SampleTerrainHeight(terrainPts[i], _ridgeCapTerrain);

            // 4. Ridge Cap — eliminates triangular peaks at cambered road edges
            ApplyRidgeCap(terrainPts, postBermH);
            // 4b. Narrow re-flatten — restores the flat cambered road bed inside
            // the road corridor only, correcting any inward disturbance that
            // Ridge Cap's bell zone may have written onto the road surface.
            if (flattenTerrain && !_skipTerrainShaping)
                ReflattenRoadSurface(terrainPts, postFlattenH);
        }

        // 5. Ditch / drainage
        if (generateDitch)
            ApplyDitchToTerrain(terrainPts);

        // 6. Shoulder smooth
        if (smoothShoulder && shoulderSmoothPasses > 0 &&
            (flattenTerrain || generateBerm || applyRidgeCap))
            SmoothTerrainShoulder(terrainPts);

        // 7. Snap (uses mesh spline so road vertices sit on the modified terrain)
        if (snapToTerrain)
            SnapToTerrain(ref _cachedSplinePoints);

        // 8. Paint (use terrain spline for accurate edge projection)
        if (paintTerrain && !_skipTerrainShaping)
            PaintTerrainSection(terrainPts, null,
                                -1, paintTextureIndex, paintShoulderWidth,
                                paintShoulderFalloff, paintStrength);
        for (int si = 0; si < segmentStyles.Count; si++)
        {
            RoadSegmentStyle st = segmentStyles[si];
            if (!st.overridePaint) continue;
            PaintTerrainSection(terrainPts, null,
                                si, st.paintTextureIndex, st.paintShoulderWidth,
                                st.paintShoulderFalloff, st.paintStrength);
        }

        // 9. Visual mesh
        if (_meshFilter != null)
        {
            Material[] matArray;
            Mesh combined = BuildCombinedMesh(_cachedSplinePoints,
                                              _splineStyleIndices, out matArray);
            _meshFilter.sharedMesh        = combined;
            _meshRenderer.sharedMaterials = matArray;
        }

        // 10. Collision mesh
        if (_meshCollider != null)
        {
            bool anyCollision = false;
            if (_splineStyleIndices != null)
                foreach (int si in _splineStyleIndices)
                    if (GetSegmentGenerateCollision(si)) { anyCollision = true; break; }
            else
                anyCollision = generateCollider;
            _meshCollider.sharedMesh = null;
            if (anyCollision)
                _meshCollider.sharedMesh = BuildCollisionMesh(_cachedSplinePoints,
                                                              _splineStyleIndices);
        }
    }

    public void ClearRoad()
    {
        if (_meshFilter   != null) _meshFilter.sharedMesh   = null;
        if (_meshCollider != null) _meshCollider.sharedMesh = null;
#if UNITY_EDITOR
        RestoreTerrain();
        _terrainSnapshotTaken = false;
        _savedHeightsFlat = null; _savedHeightsRes   = 0;
        _savedAlphasFlat  = null; _savedAlphasW      = 0;
        _savedAlphasH     = 0;    _savedAlphasLayers = 0;
#endif
    }

    #if UNITY_EDITOR
    public void BakeMeshToObject()
    {
        // Copies the current generated mesh and materials onto bakedMeshObject
        // so they are saved with the scene and available at runtime without
        // needing to call GenerateRoad() again.
        if (bakedMeshObject == null)
        {
            Debug.LogWarning("[RoadPathGenerator] Assign a GameObject to bakedMeshObject first.");
            return;
        }

        MeshFilter mf = bakedMeshObject.GetComponent<MeshFilter>();
        MeshRenderer mr = bakedMeshObject.GetComponent<MeshRenderer>();
        MeshCollider mc = bakedMeshObject.GetComponent<MeshCollider>();

        if (mf == null) mf = AddComponent<MeshFilter>(bakedMeshObject);
        if (mr == null) mr = AddComponent<MeshRenderer>(bakedMeshObject);

        if (_meshFilter != null && _meshFilter.sharedMesh != null)
        {
            mf.sharedMesh = _meshFilter.sharedMesh;
            mr.sharedMaterials = _meshRenderer.sharedMaterials;
        }

        if (generateCollider && _meshCollider != null && _meshCollider.sharedMesh != null)
        {
            if (mc == null) mc = AddComponent<MeshCollider>(bakedMeshObject);
            mc.sharedMesh = _meshCollider.sharedMesh;
        }

        Debug.Log("[RoadPathGenerator] Mesh baked to " + bakedMeshObject.name);
    }
#endif
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
    //  Style Helpers
    // ─────────────────────────────────────────────────────────

    public void EnsureStyleListLength()
    {
        if (segmentStyles == null) segmentStyles = new List<RoadSegmentStyle>();
        int needed = Mathf.Max(0, controlPoints.Count - 1);
        while (segmentStyles.Count < needed)
            segmentStyles.Add(new RoadSegmentStyle { styleName = "Segment " + segmentStyles.Count });
        while (segmentStyles.Count > needed)
            segmentStyles.RemoveAt(segmentStyles.Count - 1);
    }

    int[] BuildSplineStyleIndices(int splineCount)
    {
        int[] idx   = new int[splineCount];
        int cpGaps  = Mathf.Max(1, controlPoints.Count - 1);
        for (int k = 0; k < splineCount; k++)
            idx[k] = Mathf.Clamp(k / segmentsPerCurve, 0, cpGaps - 1);
        return idx;
    }

    Material GetSegmentRoadMaterial(int segIdx)
    {
        if (segmentStyles != null && segIdx >= 0 && segIdx < segmentStyles.Count)
        { Material m = segmentStyles[segIdx].roadMaterial; if (m != null) return m; }
        return roadMaterial;
    }
    Material GetSegmentCurbMaterial(int segIdx)
    {
        if (segmentStyles != null && segIdx >= 0 && segIdx < segmentStyles.Count
            && segmentStyles[segIdx].overrideCurbs)
            return segmentStyles[segIdx].curbMaterial ?? curbMaterial;
        return curbMaterial;
    }
    bool GetSegmentGenerateCurbs(int segIdx)
    {
        if (segmentStyles != null && segIdx >= 0 && segIdx < segmentStyles.Count
            && segmentStyles[segIdx].overrideCurbs)
            return segmentStyles[segIdx].generateCurbs;
        return generateCurbs;
    }
    bool GetSegmentGenerateCollision(int segIdx)
    {
        if (segmentStyles != null && segIdx >= 0 && segIdx < segmentStyles.Count)
            return segmentStyles[segIdx].generateCollision;
        return generateCollider;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain Snapshot / Restore
    // ─────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void SnapshotTerrain()
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        int aw = td.alphamapWidth, ah = td.alphamapHeight, nl = td.alphamapLayers;

        float[,] srcH = td.GetHeights(0, 0, hw, hw);
        _savedHeightsFlat = new float[hw * hw]; _savedHeightsRes = hw;
        Buffer.BlockCopy(srcH, 0, _savedHeightsFlat, 0, _savedHeightsFlat.Length * sizeof(float));

        float[,,] srcA = td.GetAlphamaps(0, 0, aw, ah);
        _savedAlphasFlat = new float[ah * aw * nl];
        _savedAlphasW = aw; _savedAlphasH = ah; _savedAlphasLayers = nl;
        Buffer.BlockCopy(srcA, 0, _savedAlphasFlat, 0, _savedAlphasFlat.Length * sizeof(float));

        _terrainSnapshotTaken = true;
        Debug.Log("[RoadPathGenerator] Terrain snapshot saved.");
    }

    void RestoreTerrain()
    {
        if (!_terrainSnapshotTaken) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        TerrainData td = terrain.terrainData;
        if (_savedHeightsFlat != null && _savedHeightsRes > 0)
        { float[,] h = GetSavedHeights(); if (h != null) td.SetHeights(0, 0, h); }
        if (_savedAlphasFlat != null && _savedAlphasW > 0)
        { float[,,] a = GetSavedAlphas(); if (a != null) td.SetAlphamaps(0, 0, a); }
        Debug.Log("[RoadPathGenerator] Terrain restored.");
    }
#endif

    // ─────────────────────────────────────────────────────────
    //  Spline
    // ─────────────────────────────────────────────────────────

    List<Vector3> BuildSplinePoints()
    {
        List<Vector3> cp = new List<Vector3>();
        foreach (Transform t in controlPoints) if (t != null) cp.Add(t.position);
        if (closedLoop && cp.Count > 2) cp.Add(cp[0]);

        List<Vector3> result = new List<Vector3>();
        int last = cp.Count - 1;
        for (int i = 0; i < last; i++)
        {
            Vector3 p0 = cp[Mathf.Max(0, i-1)];
            Vector3 p1 = cp[i];
            Vector3 p2 = cp[Mathf.Min(cp.Count-1, i+1)];
            Vector3 p3 = cp[Mathf.Min(cp.Count-1, i+2)];
            for (int s = 0; s < segmentsPerCurve; s++)
                result.Add(CatmullRom(p0, p1, p2, p3, s / (float)segmentsPerCurve));
        }
        result.Add(cp[cp.Count - 1]);
        return result;
    }

    // Builds a higher-density spline used only for terrain queries.
    // terrainSplineSubdivision multiplies segmentsPerCurve so that the
    // nearest-point search is much finer, eliminating zigzag road edges.
    List<Vector3> BuildTerrainSplinePoints()
    {
        List<Vector3> cp = new List<Vector3>();
        foreach (Transform t in controlPoints) if (t != null) cp.Add(t.position);
        if (closedLoop && cp.Count > 2) cp.Add(cp[0]);

        int subdiv = segmentsPerCurve * Mathf.Max(1, terrainSplineSubdivision);
        List<Vector3> result = new List<Vector3>();
        int last = cp.Count - 1;
        for (int i = 0; i < last; i++)
        {
            Vector3 p0 = cp[Mathf.Max(0, i - 1)];
            Vector3 p1 = cp[i];
            Vector3 p2 = cp[Mathf.Min(cp.Count - 1, i + 1)];
            Vector3 p3 = cp[Mathf.Min(cp.Count - 1, i + 2)];
            for (int s = 0; s < subdiv; s++)
                result.Add(CatmullRom(p0, p1, p2, p3, s / (float)subdiv));
        }
        result.Add(cp[cp.Count - 1]);
        return result;
    }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t*t, t3 = t2*t;
        return 0.5f * ((2f*p1) + (-p0+p2)*t + (2f*p0-5f*p1+4f*p2-p3)*t2 + (-p0+3f*p1-3f*p2+p3)*t3);
    }

    // ─────────────────────────────────────────────────────────
    //  Mesh Building
    // ─────────────────────────────────────────────────────────

    Mesh BuildCombinedMesh(List<Vector3> pts, int[] styleIndices, out Material[] outMaterials)
    {
        int n = pts.Count;
        List<Vector3> verts = new List<Vector3>();
        List<Vector2> uvs   = new List<Vector2>();
        float[] arcLen      = BuildArcLengths(pts);
        float camberRad     = camberDegrees * Mathf.Deg2Rad;
        float cU            = Mathf.Sin(camberRad);

        List<Material>  matKeys = new List<Material>();
        List<List<int>> matTris = new List<List<int>>();

        int[] ringTopBase = new int[n];
        int[] ringBotBase = new int[n];
        for (int i = 0; i < n; i++) ringBotBase[i] = -1;

        for (int i = 0; i < n; i++)
        {
            Material segMat = GetSegmentRoadMaterial(styleIndices != null ? styleIndices[i] : 0);
            float sw = (segMat != null) ? roadWidth : 0f;
            ringTopBase[i] = verts.Count;
            Vector3 fwd = GetForward(pts, i);
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            float v = arcLen[i] / uvTileLength;
            float cL = cU * (sw * 0.5f), cR = -cL;
            verts.Add(pts[i] - right*(sw*0.5f) + Vector3.up*cL);
            verts.Add(pts[i] + right*(sw*0.5f) + Vector3.up*cR);
            uvs.Add(new Vector2(0f, v)); uvs.Add(new Vector2(1f, v));
        }

        if (roadThickness > 0.001f)
        {
            for (int i = 0; i < n; i++)
            {
                Material segMat = GetSegmentRoadMaterial(styleIndices != null ? styleIndices[i] : 0);
                float sw = (segMat != null) ? roadWidth : 0f;
                ringBotBase[i] = verts.Count;
                Vector3 fwd = GetForward(pts, i);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                float v = arcLen[i] / uvTileLength;
                float cL = cU * (sw * 0.5f), cR = -cL;
                verts.Add(pts[i] - right*(sw*0.5f) + Vector3.up*(cL-roadThickness));
                verts.Add(pts[i] + right*(sw*0.5f) + Vector3.up*(cR-roadThickness));
                uvs.Add(new Vector2(0f, v)); uvs.Add(new Vector2(1f, v));
            }
        }

        for (int i = 0; i < n-1; i++)
        {
            int si = styleIndices != null ? styleIndices[i] : 0;
            Material segMat = GetSegmentRoadMaterial(si);
            if (segMat == null) continue;
            List<int> tris = GetOrAddMatTris(matKeys, matTris, segMat);
            int bl = ringTopBase[i], br = ringTopBase[i]+1;
            int tl = ringTopBase[i+1], tr = ringTopBase[i+1]+1;
            tris.Add(bl); tris.Add(tl); tris.Add(br);
            tris.Add(br); tris.Add(tl); tris.Add(tr);
            if (ringBotBase[i] >= 0 && ringBotBase[i+1] >= 0)
            {
                int bbl = ringBotBase[i], bbr = ringBotBase[i]+1;
                int btl = ringBotBase[i+1], btr = ringBotBase[i+1]+1;
                tris.Add(bbr); tris.Add(btl); tris.Add(bbl);
                tris.Add(btr); tris.Add(btl); tris.Add(bbr);
            }
        }

        Terrain activeTerr  = Terrain.activeTerrain;
        float   cLU         = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);
        int[]   curbRing    = new int[n];
        bool    anyCurbs    = false;

        for (int i = 0; i < n; i++)
        {
            int si = styleIndices != null ? styleIndices[i] : 0;
            if (!GetSegmentGenerateCurbs(si)) { curbRing[i] = -1; continue; }
            anyCurbs = true;
            Vector3 fwd = GetForward(pts, i);
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 eLi = pts[i] - right*(roadWidth*0.5f);
            Vector3 eRi = pts[i] + right*(roadWidth*0.5f);
            Vector3 eLo = eLi - right*curbWidth;
            Vector3 eRo = eRi + right*curbWidth;
            float hw2 = roadWidth*0.5f;
            float yLi = pts[i].y + cLU* hw2;
            float yRi = pts[i].y + cLU*-hw2;
            float yLo = SampleTerrainHeight(eLo, activeTerr);
            float yRo = SampleTerrainHeight(eRo, activeTerr);
            float ctL = yLi + curbHeight, ctR = yRi + curbHeight;
            curbRing[i] = verts.Count;
            verts.Add(new Vector3(eLi.x, yLi,  eLi.z));
            verts.Add(new Vector3(eLi.x, ctL,  eLi.z));
            verts.Add(new Vector3(eLo.x, ctL,  eLo.z));
            verts.Add(new Vector3(eLo.x, yLo,  eLo.z));
            verts.Add(new Vector3(eRi.x, yRi,  eRi.z));
            verts.Add(new Vector3(eRi.x, ctR,  eRi.z));
            verts.Add(new Vector3(eRo.x, ctR,  eRo.z));
            verts.Add(new Vector3(eRo.x, yRo,  eRo.z));
            uvs.Add(Vector2.zero); uvs.Add(Vector2.up);
            uvs.Add(Vector2.one);  uvs.Add(Vector2.right);
            uvs.Add(Vector2.zero); uvs.Add(Vector2.up);
            uvs.Add(Vector2.one);  uvs.Add(Vector2.right);
        }

        if (anyCurbs)
        {
            for (int i = 0; i < n-1; i++)
            {
                int si = styleIndices != null ? styleIndices[i] : 0;
                if (curbRing[i] < 0 || curbRing[i+1] < 0) continue;
                Material cm = GetSegmentCurbMaterial(si);
                if (cm == null) continue;
                List<int> ct = GetOrAddMatTris(matKeys, matTris, cm);
                AddCurbSegment(ct, curbRing[i],   curbRing[i+1],   false);
                AddCurbSegment(ct, curbRing[i]+4, curbRing[i+1]+4, true);
            }
        }

        Mesh m = new Mesh { name = "RoadCombined" };
        m.subMeshCount = matKeys.Count;
        m.SetVertices(verts); m.SetUVs(0, uvs);
        for (int si = 0; si < matKeys.Count; si++) m.SetTriangles(matTris[si], si);
        m.RecalculateNormals(); m.RecalculateBounds();
        outMaterials = matKeys.ToArray();
        return m;
    }

    static List<int> GetOrAddMatTris(List<Material> keys, List<List<int>> tris, Material mat)
    {
        for (int i = 0; i < keys.Count; i++) if (keys[i] == mat) return tris[i];
        keys.Add(mat); var nl = new List<int>(); tris.Add(nl); return nl;
    }

    static void AddCurbSegment(List<int> tris, int a, int b, bool flip)
    {
        for (int f = 0; f < 3; f++)
        {
            int v0=a+f, v1=a+f+1, v2=b+f, v3=b+f+1;
            if (flip) { tris.Add(v0); tris.Add(v2); tris.Add(v1); tris.Add(v1); tris.Add(v2); tris.Add(v3); }
            else      { tris.Add(v0); tris.Add(v1); tris.Add(v2); tris.Add(v1); tris.Add(v3); tris.Add(v2); }
        }
    }

    Mesh BuildCollisionMesh(List<Vector3> pts, int[] styleIndices)
    {
        int n = pts.Count;
        var verts = new List<Vector3>(n*2);
        var tris  = new List<int>();
        for (int i = 0; i < n; i++)
        {
            Vector3 r = Vector3.Cross(Vector3.up, GetForward(pts, i)).normalized;
            verts.Add(pts[i] - r*(roadWidth*0.5f));
            verts.Add(pts[i] + r*(roadWidth*0.5f));
        }
        for (int i = 0; i < n-1; i++)
        {
            int si = (styleIndices != null && i < styleIndices.Length) ? styleIndices[i] : 0;
            if (!GetSegmentGenerateCollision(si)) continue;
            int bl=i*2, br=i*2+1, tl=(i+1)*2, tr=(i+1)*2+1;
            tris.Add(bl); tris.Add(tl); tris.Add(br);
            tris.Add(br); tris.Add(tl); tris.Add(tr);
        }
        if (tris.Count == 0) return null;
        Mesh m = new Mesh { name = "RoadCollision" };
        m.SetVertices(verts); m.SetTriangles(tris, 0); m.RecalculateBounds();
        return m;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Snap
    // ─────────────────────────────────────────────────────────

    void SnapToTerrain(ref List<Vector3> pts)
    {
        Terrain at = Terrain.activeTerrain;
        for (int i = 0; i < pts.Count; i++)
        { Vector3 p = pts[i]; p.y = SampleTerrainHeight(p, at) + terrainSnapOffset; pts[i] = p; }
    }

    float SampleTerrainHeight(Vector3 worldPos, Terrain terrain)
    {
        if (terrain != null)
            return terrain.SampleHeight(worldPos) + terrain.transform.position.y;
        RaycastHit hit;
        if (Physics.Raycast(new Vector3(worldPos.x, worldPos.y+500f, worldPos.z),
                            Vector3.down, out hit, 1000f)) return hit.point.y;
        return worldPos.y;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Slope-Limited Flatten
    // ─────────────────────────────────────────────────────────

    void FlattenTerrainSlopeLimited(ref List<Vector3> pts, out float[] postFlattenH)
    {
        postFlattenH = null;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) { Debug.LogWarning("[RoadPathGenerator] FlattenTerrain: no terrain."); return; }

        TerrainData td = terrain.terrainData;
        int   hw          = td.heightmapResolution;
        float terrainW    = td.size.x, terrainH = td.size.z, terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;

        float[] rawH = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++) rawH[i] = terrain.SampleHeight(pts[i]) + terrainPos.y;

        float maxUphillTan = Mathf.Tan(maxUphillDegrees * Mathf.Deg2Rad);
        float maxDownhillTan = Mathf.Tan(maxDownhillDegrees * Mathf.Deg2Rad);
        float[] smoothH = new float[pts.Count];
        System.Array.Copy(rawH, smoothH, pts.Count);

        for (int pass = 0; pass < slopeSmoothPasses; pass++)
        {
            // Forward pass — travelling in spline direction
            // d > 0 = climbing,  d < 0 = descending
            for (int i = 1; i < pts.Count; i++)
            {
                float hd = HorizDist(pts[i - 1], pts[i]); if (hd < 0.001f) continue;
                float d = smoothH[i] - smoothH[i - 1];
                float md = hd * (d > 0f ? maxUphillTan : maxDownhillTan);
                if (Mathf.Abs(d) > md) smoothH[i] = smoothH[i - 1] + Mathf.Sign(d) * md;
            }
            // Backward pass — travelling against spline direction
            // d > 0 means forward direction is descending, d < 0 means climbing
            for (int i = pts.Count - 2; i >= 0; i--)
            {
                float hd = HorizDist(pts[i], pts[i + 1]); if (hd < 0.001f) continue;
                float d = smoothH[i] - smoothH[i + 1];
                float md = hd * (d < 0f ? maxUphillTan : maxDownhillTan);
                if (Mathf.Abs(d) > md) smoothH[i] = smoothH[i + 1] + Mathf.Sign(d) * md;
            }
        }

        if (transitionSmoothPasses > 0 && transitionAngleThreshold < 45f)
        {
            // Build arc length array for distance-based kernel lookups
            float[] arc = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++)
                arc[i] = arc[i - 1] + HorizDist(pts[i - 1], pts[i]);

            // Compute grade angle at each point using central differences.
            // This gives a smooth angle estimate that is not biased toward
            // either the forward or backward segment alone.
            float[] gradeAngle = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                int prev = Mathf.Max(0, i - 1);
                int next = Mathf.Min(pts.Count - 1, i + 1);
                float hd = HorizDist(pts[prev], pts[next]);
                float dh = smoothH[next] - smoothH[prev];
                gradeAngle[i] = hd > 0.001f
                    ? Mathf.Atan2(dh, hd) * Mathf.Rad2Deg
                    : 0f;
            }

            // Detect transition weight at each point — how much the grade
            // angle changes relative to its neighbours.  Only points where
            // the grade change exceeds the threshold get smoothed.
            // Consistent slopes (steady climb or steady descent) register
            // near-zero change and are left completely untouched.
            float[] transWeight = new float[pts.Count];
            for (int i = 1; i < pts.Count - 1; i++)
            {
                float angleDelta = Mathf.Abs(gradeAngle[i + 1] - gradeAngle[i - 1]) * 0.5f;
                transWeight[i] = Mathf.Clamp01(
                    (angleDelta - transitionAngleThreshold) / transitionAngleThreshold);
            }

            // Spread transition weight outward by transitionSmoothRadius so
            // the smoothing kernel covers the full run-in and run-out zone
            // around each transition point, not just the point itself.
            float[] spreadWeight = new float[pts.Count];
            float sig2spread = 2f * (transitionSmoothRadius * 0.5f)
                                       * (transitionSmoothRadius * 0.5f);
            for (int i = 0; i < pts.Count; i++)
            {
                float maxW = 0f;
                for (int j = 0; j < pts.Count; j++)
                {
                    if (transWeight[j] < 0.001f) continue;
                    float dist = Mathf.Abs(arc[j] - arc[i]);
                    if (dist > transitionSmoothRadius) continue;
                    float w = transWeight[j] * Mathf.Exp(-(dist * dist) / sig2spread);
                    if (w > maxW) maxW = w;
                }
                spreadWeight[i] = maxW;
            }

            // Gaussian smoothing passes weighted by spreadWeight.
            // Points with zero spread weight are skipped entirely so steady
            // slopes are never touched regardless of pass count.
            float sig2smooth = 2f * (transitionSmoothRadius * 0.4f)
                                   * (transitionSmoothRadius * 0.4f);
            for (int pass = 0; pass < transitionSmoothPasses; pass++)
            {
                float[] next = new float[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    if (spreadWeight[i] < 0.001f) { next[i] = smoothH[i]; continue; }
                    float ws = 0f, hs = 0f;
                    for (int j = 0; j < pts.Count; j++)
                    {
                        float dist = Mathf.Abs(arc[j] - arc[i]);
                        if (dist > transitionSmoothRadius) continue;
                        float w = Mathf.Exp(-(dist * dist) / sig2smooth);
                        ws += w; hs += smoothH[j] * w;
                    }
                    float smoothed = ws > 0.0001f ? hs / ws : smoothH[i];
                    next[i] = Mathf.Lerp(smoothH[i], smoothed, spreadWeight[i]);
                }
                System.Array.Copy(next, smoothH, pts.Count);

                // Re-apply directional slope limits after each smoothing pass
                // so the transition blend never creates a grade that exceeds
                // either the uphill or downhill limit.
                for (int i = 1; i < pts.Count; i++)
                {
                    float hd = HorizDist(pts[i - 1], pts[i]); if (hd < 0.001f) continue;
                    float d = smoothH[i] - smoothH[i - 1];
                    float md = hd * (d > 0f ? maxUphillTan : maxDownhillTan);
                    if (Mathf.Abs(d) > md) smoothH[i] = smoothH[i - 1] + Mathf.Sign(d) * md;
                }
                for (int i = pts.Count - 2; i >= 0; i--)
                {
                    float hd = HorizDist(pts[i], pts[i + 1]); if (hd < 0.001f) continue;
                    float d = smoothH[i] - smoothH[i + 1];
                    float md = hd * (d < 0f ? maxUphillTan : maxDownhillTan);
                    if (Mathf.Abs(d) > md) smoothH[i] = smoothH[i + 1] + Mathf.Sign(d) * md;
                }
            }
        }

        float halfRoad  = roadWidth*0.5f + flattenExtraWidth;
        float totalHalf = halfRoad + flattenFalloff;
        float cSlope    = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        float minX=float.MaxValue, maxX=float.MinValue, minZ=float.MaxValue, maxZ=float.MinValue;
        foreach (Vector3 p in pts)
        {
            if(p.x-totalHalf<minX) minX=p.x-totalHalf; if(p.x+totalHalf>maxX) maxX=p.x+totalHalf;
            if(p.z-totalHalf<minZ) minZ=p.z-totalHalf; if(p.z+totalHalf>maxZ) maxZ=p.z+totalHalf;
        }
        int pxMin=Mathf.Clamp(Mathf.FloorToInt(((minX-terrainPos.x)/terrainW)*(hw-1)),0,hw-1);
        int pxMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-terrainPos.x)/terrainW)*(hw-1)),0,hw-1);
        int pzMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-terrainPos.z)/terrainH)*(hw-1)),0,hw-1);
        int pzMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-terrainPos.z)/terrainH)*(hw-1)),0,hw-1);
        int subW=pxMax-pxMin+1, subH=pzMax-pzMin+1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
        for (int lx = 0; lx < subW; lx++)
        {
            float wx=terrainPos.x+((pxMin+lx)/(float)(hw-1))*terrainW;
            float wz=terrainPos.z+((pzMin+lz)/(float)(hw-1))*terrainH;
            float interpH, sc;
            float cd = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out interpH, out sc);
            if (cd > totalHalf) continue;
            float target = interpH - cSlope*sc;
            float blend  = (cd <= halfRoad) ? 1f : (flattenFalloff>0.001f ? 1f-(cd-halfRoad)/flattenFalloff : 0f);
            blend = Mathf.Clamp01(blend);
            heights[lz,lx] = Mathf.Lerp(heights[lz,lx], (target-terrainPos.y)/terrainMaxY, blend);
        }
        td.SetHeights(pxMin, pzMin, heights);
        for (int i = 0; i < pts.Count; i++) { Vector3 p=pts[i]; p.y=smoothH[i]; pts[i]=p; }
        postFlattenH = smoothH;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Narrow Road Surface Re-flatten
    // ─────────────────────────────────────────────────────────
    //  Runs after Ridge Cap to restore the cambered road bed within
    //  roadWidth only.  No flattenExtraWidth, no falloff zone, no
    //  steep-zone extra passes — just a clean overwrite of the inner
    //  corridor to the slope-limited centreline heights that Flatten
    //  originally computed.  This corrects any inward bell disturbance
    //  Ridge Cap wrote onto the road surface without touching the
    //  shoulder, berm, or ditch zones.

    void ReflattenRoadSurface(List<Vector3> pts, float[] smoothH)
    {
        if (smoothH == null || smoothH.Length != pts.Count) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float terrainW = td.size.x, terrainH = td.size.z, terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;

        float halfRoad = roadWidth * 0.5f;
        float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - halfRoad < minX) minX = p.x - halfRoad; if (p.x + halfRoad > maxX) maxX = p.x + halfRoad;
            if (p.z - halfRoad < minZ) minZ = p.z - halfRoad; if (p.z + halfRoad > maxZ) maxZ = p.z + halfRoad;
        }
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt(((maxX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt(((maxZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int subW = pxMax - pxMin + 1, subH = pzMax - pzMin + 1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                float wx = terrainPos.x + ((pxMin + lx) / (float)(hw - 1)) * terrainW;
                float wz = terrainPos.z + ((pzMin + lz) / (float)(hw - 1)) * terrainH;
                float interpH, sc;
                float cd = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out interpH, out sc);
                // Strictly inside the road corridor only — no falloff
                if (cd > halfRoad) continue;
                float target = interpH - cSlope * sc;
                heights[lz, lx] = Mathf.Clamp01((target - terrainPos.y) / terrainMaxY);
            }

        td.SetHeights(pxMin, pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Flatten Beyond Path Width
    // ─────────────────────────────────────────────────────────
    //  Flattens terrain from the road edge outward by flattenBeyondDistance
    //  on both sides, using the slope-limited centreline heights as the
    //  target elevation.  Runs before Berm, Ditch, and Shoulder Smoothing
    //  so those operations have a clean flat base to work from.
    //  A short falloff at the outer boundary blends back to natural terrain
    //  to avoid a hard seam at the edge of the flattened zone.

    void FlattenBeyondPathWidth(List<Vector3> pts, float[] smoothH)
    {
        if (smoothH == null || smoothH.Length != pts.Count) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float terrainW = td.size.x, terrainH = td.size.z, terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;

        float halfRoad = roadWidth * 0.5f;
        float outerEdge = halfRoad + flattenBeyondDistance;
        // Short falloff at outer boundary to blend back to natural terrain
        float falloff = Mathf.Min(2f, flattenBeyondDistance * 0.2f);
        float fadeStart = outerEdge - falloff;
        float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - outerEdge < minX) minX = p.x - outerEdge; if (p.x + outerEdge > maxX) maxX = p.x + outerEdge;
            if (p.z - outerEdge < minZ) minZ = p.z - outerEdge; if (p.z + outerEdge > maxZ) maxZ = p.z + outerEdge;
        }
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt(((maxX - terrainPos.x) / terrainW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt(((maxZ - terrainPos.z) / terrainH) * (hw - 1)), 0, hw - 1);
        int subW = pxMax - pxMin + 1, subH = pzMax - pzMin + 1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                float wx = terrainPos.x + ((pxMin + lx) / (float)(hw - 1)) * terrainW;
                float wz = terrainPos.z + ((pzMin + lz) / (float)(hw - 1)) * terrainH;
                float interpH, sc;
                float cd = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out interpH, out sc);
                // Only operate in the band beyond the road edge
                if (cd <= halfRoad || cd > outerEdge) continue;
                float target = interpH - cSlope * Mathf.Clamp(sc, -halfRoad, halfRoad);
                float blend = (cd <= fadeStart) ? 1f
                               : (falloff > 0.001f ? 1f - (cd - fadeStart) / falloff : 0f);
                blend = Mathf.Clamp01(blend);
                heights[lz, lx] = Mathf.Lerp(heights[lz, lx],
                                   Mathf.Clamp01((target - terrainPos.y) / terrainMaxY), blend);
            }

        td.SetHeights(pxMin, pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Berm
    // ─────────────────────────────────────────────────────────

    void ApplyBermToTerrain(List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) { Debug.LogWarning("[RoadPathGenerator] Berm: no terrain."); return; }
        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float tW=td.size.x, tH=td.size.z, tMaxY=td.size.y;
        Vector3 tPos = terrain.transform.position;

#if UNITY_EDITOR
        bool hs = _savedHeightsFlat != null && _savedHeightsRes > 0;
        float[,] saved = hs ? GetSavedHeights() : td.GetHeights(0,0,hw,hw);
#else
        float[,] saved = td.GetHeights(0,0,hw,hw);
#endif
        float halfRoad=roadWidth*0.5f, bermIn=halfRoad, bermOut=halfRoad+bermSlopeDistance;
        float totalOut=bermOut+bermOuterFalloff, cSB=Mathf.Sin(camberDegrees*Mathf.Deg2Rad);

        float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
        foreach(Vector3 p in pts)
        { if(p.x-totalOut<minX)minX=p.x-totalOut; if(p.x+totalOut>maxX)maxX=p.x+totalOut;
          if(p.z-totalOut<minZ)minZ=p.z-totalOut; if(p.z+totalOut>maxZ)maxZ=p.z+totalOut; }
        int pxMin=Mathf.Clamp(Mathf.FloorToInt(((minX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pxMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pzMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int pzMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int subW=pxMax-pxMin+1, subH=pzMax-pzMin+1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);
        float[] smoothH = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++) smoothH[i] = terrain.SampleHeight(pts[i]) + tPos.y;

        for (int lz = 0; lz < subH; lz++)
        for (int lx = 0; lx < subW; lx++)
        {
            int px=pxMin+lx, pz=pzMin+lz;
            float wx=tPos.x+(px/(float)(hw-1))*tW, wz=tPos.z+(pz/(float)(hw-1))*tH;
            float roadH, sc;
            float cd = ClosestPointOnSplineXZ(wx, wz, pts, smoothH, out roadH, out sc);
            if (cd < bermIn || cd > totalOut) continue;
            float edgeH  = roadH - cSB*Mathf.Clamp(sc,-bermIn,bermIn);
            float t      = Mathf.Clamp01((cd-bermIn)/bermSlopeDistance);
            float tS     = Mathf.Lerp(t, t*t*(3f-2f*t), bermCurvature);
            float ob     = (bermOuterFalloff>0.001f && cd>bermOut)
                           ? Mathf.Clamp01(1f-(cd-bermOut)/bermOuterFalloff) : 1f;
            float origH  = saved[pz,px]*tMaxY + tPos.y;
            float tN     = (Mathf.Lerp(edgeH,origH,tS)-tPos.y)/tMaxY;
            heights[lz,lx] = Mathf.Lerp(heights[lz,lx], tN, ob);
        }
        td.SetHeights(pxMin, pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Ridge Cap  ◄ NEW
    // ─────────────────────────────────────────────────────────
    //
    //  WHY PEAKS FORM
    //  ──────────────
    //  The flatten pass writes the camber-adjusted height at every
    //  pixel inside the road corridor.  The road edge sits at a
    //  specific XZ position for each spline point, but because the
    //  spline is sampled at discrete intervals the exact "road edge
    //  line" zigzags slightly in terrain space.  Pixels right on
    //  that zigzag receive height contributions from TWO consecutive
    //  spline rings that disagree on the edge XZ position, producing
    //  a ragged ridge of triangular spikes.
    //
    //  THE FIX
    //  ────────
    //  For every heightmap pixel within ridgeCapWidth of EITHER road
    //  edge, we:
    //
    //    1.  Find the nearest single spline segment and interpolate
    //        the EXACT smooth crest height at that projection point:
    //          left  edge: crestH = centrelineH + camberSlope × halfRoad
    //          right edge: crestH = centrelineH - camberSlope × halfRoad
    //
    //    2.  Stamp that height onto the terrain using a smooth bell
    //        profile centred on the road edge:
    //          d = 0 (road edge)       → blend = ridgeCapStrength  (peak)
    //          d = ridgeCapWidth       → blend = 0.0               (tapers off)
    //
    //        The bell extends BOTH inward (into the flat road surface)
    //        and outward (into the berm / shoulder), so the transition
    //        is a smooth rolling hill, not a sharp ridge.
    //
    //    3.  Run ridgeCapSmoothPasses Gaussian passes confined to the
    //        cap zone to dissolve any remaining micro-jaggedness.
    //
    //  The road interior pixels (crossDist well inside halfRoad) are
    //  not written, so the cambered road surface itself is untouched.
    // ─────────────────────────────────────────────────────────

    void ApplyRidgeCap(List<Vector3> pts, float[] splineH)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        int   hw       = td.heightmapResolution;
        float tW       = td.size.x, tH = td.size.z, tMaxY = td.size.y;
        Vector3 tPos   = terrain.transform.position;

        float halfRoad   = roadWidth * 0.5f;
        float capW       = ridgeCapWidth;
        // Zone: from (halfRoad - capW) to (halfRoad + capW)
        // Clamped so we never go negative (inner boundary always >= 0)
        float zoneInner  = Mathf.Max(0f, halfRoad - capW);
        float zoneOuter  = halfRoad + capW;
        float padded     = zoneOuter + 2f;
        float cSlope     = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        // ── Bounding box ──────────────────────────────────────
        float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
        foreach (Vector3 p in pts)
        {
            if(p.x-padded<minX)minX=p.x-padded; if(p.x+padded>maxX)maxX=p.x+padded;
            if(p.z-padded<minZ)minZ=p.z-padded; if(p.z+padded>maxZ)maxZ=p.z+padded;
        }
        int pxMin=Mathf.Clamp(Mathf.FloorToInt(((minX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pxMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pzMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int pzMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int subW=pxMax-pxMin+1, subH=pzMax-pzMin+1;

        float wpx = tW / (hw-1), wpz = tH / (hw-1);

        // ── Cache spline query per pixel ─────────────────────
        float[] cCross   = new float[subW*subH];
        float[] cSigned  = new float[subW*subH];
        float[] cInterpH = new float[subW*subH];

        for (int lz = 0; lz < subH; lz++)
        for (int lx = 0; lx < subW; lx++)
        {
            float wx=tPos.x+((pxMin+lx)/(float)(hw-1))*tW;
            float wz=tPos.z+((pzMin+lz)/(float)(hw-1))*tH;
            float iH, sc;
            float cd = ClosestPointOnSplineXZ(wx, wz, pts, splineH, out iH, out sc);
            int idx = lz*subW+lx;
            cCross[idx]=cd; cSigned[idx]=sc; cInterpH[idx]=iH;
        }

        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        // ── Bell stamp ───────────────────────────────────────
        for (int lz = 0; lz < subH; lz++)
        for (int lx = 0; lx < subW; lx++)
        {
            int   idx = lz*subW+lx;
            float cd  = cCross[idx];
            if (cd < zoneInner || cd > zoneOuter) continue;

            float signed  = cSigned[idx];
            float interpH = cInterpH[idx];

            // Smooth crest height at the road edge nearest to this pixel.
            // signed < 0 → left side (high when camber > 0)
            // signed > 0 → right side
            // Formula: targetH = interpH - cSlope * signedCross
            // At left edge (signed = -halfRoad): targetH = interpH + cSlope*halfRoad
            // At right edge (signed = +halfRoad): targetH = interpH - cSlope*halfRoad
            float crestH = (signed <= 0f)
                ? interpH + cSlope * halfRoad
                : interpH - cSlope * halfRoad;

            // Normalised distance from road edge [0=edge, 1=boundary]
            float dFromEdge = Mathf.Abs(cd - halfRoad);
            float tN        = Mathf.Clamp01(dFromEdge / capW);

            // Bell: blend cosine and Gaussian by ridgeCapSharpness
            float cosBell  = Mathf.Pow(Mathf.Cos(tN * Mathf.PI * 0.5f), 2f);
            float gSigma   = 0.4f;
            float gaussB   = Mathf.Exp(-(tN*tN)/(2f*gSigma*gSigma));
            float bell     = Mathf.Lerp(cosBell, gaussB, ridgeCapSharpness);
            float blendW   = bell * ridgeCapStrength;
            if (blendW < 0.001f) continue;

            // ridgeCapHeight lifts/lowers the crest, scaled by bell so the rounded
            // profile is preserved — full offset at road edge, zero at cap boundary.
            float crestNorm  = (crestH + ridgeCapHeight * bell - tPos.y) / tMaxY;
            heights[lz, lx] = Mathf.Lerp(heights[lz, lx], crestNorm, blendW);
        }

        td.SetHeights(pxMin, pzMin, heights);

        // ── In-zone Gaussian smoothing passes ────────────────
        if (ridgeCapSmoothPasses > 0)
        {
            int   kRad   = Mathf.Max(1, Mathf.CeilToInt(capW / Mathf.Min(wpx, wpz))) + 1;
            float sig2   = (capW * 0.5f) * (capW * 0.5f) * 2f;   // 2σ² with σ = capW/2

            for (int pass = 0; pass < ridgeCapSmoothPasses; pass++)
            {
                float[,] src = td.GetHeights(pxMin, pzMin, subW, subH);
                float[,] dst = (float[,])src.Clone();

                for (int lz = 0; lz < subH; lz++)
                for (int lx = 0; lx < subW; lx++)
                {
                    int   idx = lz*subW+lx;
                    float cd  = cCross[idx];
                    if (cd < zoneInner || cd > zoneOuter) continue;

                    float dFromEdge = Mathf.Abs(cd - halfRoad);
                    float tNorm     = Mathf.Clamp01(dFromEdge / capW);
                    // Peak blend at road edge, zero at boundaries
                    float blendW    = Mathf.Pow(Mathf.Cos(tNorm * Mathf.PI * 0.5f), 2f)
                                      * ridgeCapStrength;
                    if (blendW < 0.001f) continue;

                    float ws = 0f, hs = 0f;
                    for (int kz = -kRad; kz <= kRad; kz++)
                    {
                        int nz = lz+kz;
                        if (nz < 0 || nz >= subH) continue;
                        float dz = kz*wpz;
                        for (int kx = -kRad; kx <= kRad; kx++)
                        {
                            int nx = lx+kx;
                            if (nx < 0 || nx >= subW) continue;
                            float nc = cCross[nz*subW+nx];
                            // Kernel only samples from inside the cap zone
                            if (nc < zoneInner || nc > zoneOuter) continue;
                            float dx2 = kx*wpx, d2 = dx2*dx2 + dz*dz;
                            float w   = Mathf.Exp(-d2/sig2);
                            ws += w; hs += src[nz,nx]*w;
                        }
                    }
                    if (ws < 0.0001f) continue;
                    dst[lz, lx] = Mathf.Lerp(src[lz,lx], hs/ws, blendW);
                }

                td.SetHeights(pxMin, pzMin, dst);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Shoulder Smoothing
    // ─────────────────────────────────────────────────────────

    void SmoothTerrainShoulder(List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null || shoulderSmoothPasses <= 0) return;

        TerrainData td = terrain.terrainData;
        int   hw       = td.heightmapResolution;
        float tW       = td.size.x, tH = td.size.z;
        Vector3 tPos   = terrain.transform.position;

        float halfRoad   = roadWidth*0.5f;
        float innerStart = halfRoad - Mathf.Max(0f, shoulderSmoothInwardOverlap);
        // Clamp the shoulder smooth outer edge to the ditch zone start when
        // a ditch is active.  Without this, a large shoulderSmoothDistance
        // bleeds into the ditch zone and the Gaussian passes pull the freshly
        // carved walls upward, partially undoing the ditch carve.
        float ditchZoneStart = halfRoad + ditchOffset;
        float outerEnd = (generateDitch)
            ? Mathf.Min(halfRoad + shoulderSmoothDistance, ditchZoneStart)
            : halfRoad + shoulderSmoothDistance;
        float padded     = outerEnd + shoulderSmoothRadius;

        float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
        foreach(Vector3 p in pts)
        { if(p.x-padded<minX)minX=p.x-padded; if(p.x+padded>maxX)maxX=p.x+padded;
          if(p.z-padded<minZ)minZ=p.z-padded; if(p.z+padded>maxZ)maxZ=p.z+padded; }
        int pxMin=Mathf.Clamp(Mathf.FloorToInt(((minX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pxMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pzMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int pzMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int subW=pxMax-pxMin+1, subH=pzMax-pzMin+1;
        float wpx=tW/(hw-1), wpz=tH/(hw-1);
        int kRad = Mathf.Max(1, Mathf.CeilToInt(shoulderSmoothRadius/Mathf.Min(wpx,wpz)))+1;
        float sig = shoulderSmoothRadius*0.5f, sig2x2 = 2f*sig*sig;

        float[] cdCache = new float[subW*subH];
        for (int lz=0;lz<subH;lz++) for (int lx=0;lx<subW;lx++)
        {
            float wx=tPos.x+((pxMin+lx)/(float)(hw-1))*tW;
            float wz=tPos.z+((pzMin+lz)/(float)(hw-1))*tH;
            cdCache[lz*subW+lx] = RoadCrossTrackDistance(wx, wz, pts);
        }

        for (int pass = 0; pass < shoulderSmoothPasses; pass++)
        {
            float[,] src = td.GetHeights(pxMin, pzMin, subW, subH);
            float[,] dst = (float[,])src.Clone();
            for (int lz=0;lz<subH;lz++) for (int lx=0;lx<subW;lx++)
            {
                float cd = cdCache[lz*subW+lx];
                if (cd < halfRoad || cd > outerEnd) continue;
                float tO = (cd-halfRoad)/(outerEnd-halfRoad);
                float bW = Mathf.Clamp01(1f - tO*tO*(3f-2f*tO));
                if (bW < 0.001f) continue;
                float ws=0f, hs=0f;
                for (int kz=-kRad;kz<=kRad;kz++)
                {
                    int nz=lz+kz; if(nz<0||nz>=subH) continue;
                    float dz=kz*wpz;
                    for (int kx=-kRad;kx<=kRad;kx++)
                    {
                        int nx=lx+kx; if(nx<0||nx>=subW) continue;
                        if (cdCache[nz*subW+nx] < innerStart) continue;
                        float dx=kx*wpx, d2=dx*dx+dz*dz;
                        float w=Mathf.Exp(-d2/sig2x2);
                        ws+=w; hs+=src[nz,nx]*w;
                    }
                }
                if (ws<0.0001f) continue;
                dst[lz,lx] = Mathf.Lerp(src[lz,lx], hs/ws, bW);
            }
            td.SetHeights(pxMin, pzMin, dst);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Ditch / Drainage  (rewritten)
    // ─────────────────────────────────────────────────────────
    //
    //  FIXES vs previous version:
    //
    //  1. END CAPS ELIMINATED
    //     Pixels that project "before" the spline start or "after" the spline
    //     end are skipped using endpoint tangent dot-product tests, so the
    //     ditch only runs alongside the path, never wrapping around its tips.
    //
    //  2. CORRECT REFERENCE HEIGHT
    //     The depth reference is the terrain height at the inner-wall-start
    //     position, computed by stepping outward from the nearest spline
    //     projection point in the true road-perpendicular direction by
    //     ditchOffset.  The old shiftFactor approach scaled the pixel around
    //     the terrain origin, which was geometrically wrong and produced
    //     wildly inconsistent wall heights at every spline segment junction.
    //
    //  3. SMOOTH WALLS
    //     Because refH is now derived from the continuously-smooth spline
    //     projection (central-difference tangent blending) rather than per-
    //     segment geometry, wall heights vary smoothly along the ditch and
    //     the angular faceting between segments disappears.
    //
    //  4. ALWAYS CARVES
    //     The old "cut only" Min() guard was correct in intent but combined
    //     with the wrong refH it often computed a target above terrain and
    //     silently did nothing.  The new logic computes refH correctly first,
    //     ensuring the target is always genuinely below the shoulder surface,
    //     then applies a soft blend so the ditch fades gracefully without hard
    //     seams.  A safety clamp still prevents the ditch from being raised
    //     if the terrain is somehow already lower than the target.
    //
    //  5. SMOOTHING ONLY INSIDE ZONE
    //     The Gaussian passes are restricted to the carved ditch zone so they
    //     never pull the berm or shoulder terrain downward.
    // ─────────────────────────────────────────────────────────

    void ApplyDitchToTerrain(List<Vector3> pts)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) { Debug.LogWarning("[RoadPathGenerator] Ditch: no terrain."); return; }
        if (pts.Count < 2) return;

        TerrainData td = terrain.terrainData;
        int   hw       = td.heightmapResolution;
        float tW       = td.size.x, tH = td.size.z, tMaxY = td.size.y;
        Vector3 tPos   = terrain.transform.position;

        float halfRoad   = roadWidth * 0.5f;
        float zoneStart  = halfRoad + ditchOffset;           // inner wall top
        float innerEnd   = zoneStart + ditchInnerWidth;      // floor start
        float outerStart = innerEnd  + ditchBottomWidth;     // floor end
        float zoneEnd    = outerStart + ditchOuterWidth;     // outer wall top
        float padded     = zoneEnd + ditchSmoothRadius + 2f;

        // ── Endpoint tangents for end-cap rejection ───────────
        // A pixel that projects to the very start of the spline is "before
        // the start" if it lies on the negative side of the start tangent.
        // Similarly for the end.  We use the first and last segment directions.
        Vector2 startTangent = new Vector2(pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin  = new Vector2(pts[0].x, pts[0].z);
        int     lastIdx      = pts.Count - 1;
        Vector2 endTangent   = new Vector2(pts[lastIdx].x - pts[lastIdx-1].x,
                                           pts[lastIdx].z - pts[lastIdx-1].z).normalized;
        Vector2 endOrigin    = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        // ── Bounding box ──────────────────────────────────────
        float minX=float.MaxValue,maxX=float.MinValue;
        float minZ=float.MaxValue,maxZ=float.MinValue;
        foreach (Vector3 p in pts)
        {
            if(p.x-padded<minX)minX=p.x-padded; if(p.x+padded>maxX)maxX=p.x+padded;
            if(p.z-padded<minZ)minZ=p.z-padded; if(p.z+padded>maxZ)maxZ=p.z+padded;
        }
        int pxMin=Mathf.Clamp(Mathf.FloorToInt(((minX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pxMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-tPos.x)/tW)*(hw-1)),0,hw-1);
        int pzMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int pzMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-tPos.z)/tH)*(hw-1)),0,hw-1);
        int subW=pxMax-pxMin+1, subH=pzMax-pzMin+1;
        float wpx=tW/(hw-1), wpz=tH/(hw-1);

        // ── Spline height array (current terrain after all prior passes) ──
        float[] splineH = new float[pts.Count];
        for (int i=0;i<pts.Count;i++) splineH[i] = terrain.SampleHeight(pts[i]) + tPos.y;

        // ── Per-pixel cache ───────────────────────────────────
        //  For each pixel we store:
        //    cdCache   - unsigned cross-track distance
        //    refHCache - terrain height at the inner-wall-start position
        //                (the correct depth reference, computed once here)
        //    validCache- whether this pixel should be processed at all
        //
        //  refH is found by:
        //    a) Finding the nearest point on the spline (bestPt in XZ)
        //    b) Computing the outward unit direction: normalize(pixel - bestPt)
        //    c) Stepping outward by zoneStart from bestPt to get innerWallXZ
        //    d) Sampling terrain.SampleHeight at innerWallXZ
        //
        //  This is geometrically correct regardless of spline curvature or
        //  road width, and produces a continuously smooth refH along the ditch.

        float[] cdCache   = new float[subW * subH];
        float[] refHCache = new float[subW * subH];
        bool[]  validCache = new bool[subW * subH];

        for (int lz=0;lz<subH;lz++)
        for (int lx=0;lx<subW;lx++)
        {
            int idx = lz*subW+lx;
            float wx = tPos.x + ((pxMin+lx)/(float)(hw-1))*tW;
            float wz = tPos.z + ((pzMin+lz)/(float)(hw-1))*tH;
            Vector2 pq = new Vector2(wx, wz);

            // Find nearest spline segment
            float bestDist = float.MaxValue;
            int   bestSeg  = 0;
            float bestT    = 0f;
            Vector2 bestPt = Vector2.zero;

            for (int i=0;i<pts.Count-1;i++)
            {
                Vector2 a = new Vector2(pts[i].x, pts[i].z);
                Vector2 b = new Vector2(pts[i+1].x, pts[i+1].z);
                float t;
                float d = ClosestPointOnSegment2D(pq, a, b, out t);
                if (d < bestDist) { bestDist=d; bestSeg=i; bestT=t; bestPt=a+(b-a)*t; }
            }

            // ── End-cap rejection ─────────────────────────────
            // Skip if pixel is "before" the start endpoint
            Vector2 toPixelFromStart = pq - startOrigin;
            if (bestSeg == 0 && bestT < 0.01f &&
                Vector2.Dot(toPixelFromStart, startTangent) < 0f)
            {
                cdCache[idx] = float.MaxValue;
                continue;
            }
            // Skip if pixel is "after" the end endpoint
            Vector2 toPixelFromEnd = pq - endOrigin;
            if (bestSeg == pts.Count-2 && bestT > 0.99f &&
                Vector2.Dot(toPixelFromEnd, endTangent) > 0f)
            {
                cdCache[idx] = float.MaxValue;
                continue;
            }

            // ── Smooth road-right direction ───────────────────
            // Use central-difference blending (same as RoadCrossTrackDistance)
            // so the perpendicular direction is continuous across segment junctions.
            Vector2 currDir = SegDir2D(pts, bestSeg);
            Vector2 prevDir = bestSeg > 0             ? SegDir2D(pts, bestSeg-1) : currDir;
            Vector2 nextDir = bestSeg < pts.Count-2   ? SegDir2D(pts, bestSeg+1) : currDir;
            Vector2 tangent;
            if (bestT < 0.5f)
                tangent = Vector2.Lerp((prevDir+currDir)*0.5f, currDir, bestT*2f);
            else
                tangent = Vector2.Lerp(currDir, (currDir+nextDir)*0.5f, (bestT-0.5f)*2f);
            tangent.Normalize();
            Vector2 roadRight = new Vector2(-tangent.y, tangent.x);

            // Signed cross-track: which side of the road is this pixel on?
            float signedCross = Vector2.Dot(pq - bestPt, roadRight);
            float crossDist   = Mathf.Abs(signedCross);
            cdCache[idx]      = crossDist;

            if (crossDist < zoneStart || crossDist > zoneEnd + 1f) continue;

            // ── Inner-wall reference height ───────────────────
            // Step from bestPt outward in the road-right direction by zoneStart.
            // This lands exactly at the inner-wall-start position for this pixel.
            float side = signedCross >= 0f ? 1f : -1f;
            Vector2 innerWallXZ = new Vector2(bestPt.x, bestPt.y)
                                  + roadRight * (side * zoneStart);

            Vector3 innerSample = new Vector3(innerWallXZ.x, 0f, innerWallXZ.y);
            // Clamp sample point to terrain bounds
            innerSample.x = Mathf.Clamp(innerSample.x, tPos.x, tPos.x + tW);
            innerSample.z = Mathf.Clamp(innerSample.z, tPos.z, tPos.z + tH);
            float refH = terrain.SampleHeight(innerSample) + tPos.y;

            refHCache [idx] = refH;
            validCache[idx] = true;
        }

        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        // ── Carve pass ───────────────────────────────────────
        for (int lz=0;lz<subH;lz++)
        for (int lx=0;lx<subW;lx++)
        {
            int   idx = lz*subW+lx;
            float cd  = cdCache[idx];
            if (!validCache[idx] || cd < zoneStart || cd > zoneEnd) continue;

            float refH = refHCache[idx];

            // ── Depth fraction based on sub-zone ─────────────
            float depthFrac;
            if (cd <= innerEnd)
            {
                // Descending inner wall: 0 at top → 1 at floor
                float t = Mathf.Clamp01((cd - zoneStart) / Mathf.Max(0.001f, ditchInnerWidth));
                float tS = t*t*(3f - 2f*t);
                depthFrac = Mathf.Lerp(t, tS, ditchCurvature);
            }
            else if (cd <= outerStart)
            {
                // Flat floor
                depthFrac = 1f;
            }
            else
            {
                // Rising outer wall: 1 at floor → 0 at top
                float t = Mathf.Clamp01((cd - outerStart) / Mathf.Max(0.001f, ditchOuterWidth));
                float tS = t*t*(3f - 2f*t);
                depthFrac = 1f - Mathf.Lerp(t, tS, ditchCurvature);
            }

            // ── Target height ─────────────────────────────────
            float targetH    = refH - depthFrac * ditchDepth;
            float targetNorm = Mathf.Clamp01((targetH - tPos.y) / tMaxY);

            // Apply with ditchStrength; safety: never raise terrain above what
            // it already is (ditch should only ever cut downward).
            float currentH   = heights[lz, lx];
            float blended    = Mathf.Lerp(currentH, targetNorm, ditchStrength);
            heights[lz, lx]  = Mathf.Min(currentH, blended);
        }

        td.SetHeights(pxMin, pzMin, heights);

        // ── Smoothing passes (zone-confined Gaussian) ─────────
        if (ditchSmoothPasses > 0)
        {
            int   kRad = Mathf.Max(1, Mathf.CeilToInt(ditchSmoothRadius / Mathf.Min(wpx,wpz))) + 1;
            float sig  = ditchSmoothRadius * 0.5f;
            float sig2 = 2f * sig * sig;

            for (int pass=0; pass<ditchSmoothPasses; pass++)
            {
                float[,] src = td.GetHeights(pxMin, pzMin, subW, subH);
                float[,] dst = (float[,])src.Clone();

                for (int lz=0;lz<subH;lz++)
                for (int lx=0;lx<subW;lx++)
                {
                    int   idx = lz*subW+lx;
                    float cd  = cdCache[idx];
                    if (!validCache[idx] || cd < zoneStart || cd > zoneEnd) continue;

                    // Blend weight peaks at ditch floor, tapers at zone edges
                    float blendW;
                    if (cd <= innerEnd)
                        blendW = Mathf.Clamp01((cd - zoneStart) /
                                               Mathf.Max(0.001f, ditchInnerWidth));
                    else if (cd <= outerStart)
                        blendW = 1f;
                    else
                        blendW = Mathf.Clamp01(1f - (cd - outerStart) /
                                               Mathf.Max(0.001f, ditchOuterWidth));

                    if (blendW < 0.001f) continue;

                    float ws=0f, hs=0f;
                    for (int kz=-kRad;kz<=kRad;kz++)
                    {
                        int nz=lz+kz; if(nz<0||nz>=subH) continue;
                        float dz=kz*wpz;
                        for (int kx=-kRad;kx<=kRad;kx++)
                        {
                            int nx=lx+kx; if(nx<0||nx>=subW) continue;
                            float nc = cdCache[nz*subW+nx];
                            // Allow kernel to sample slightly outside zone so
                            // zone-edge pixels have full, unbiased kernels
                            if (nc > zoneEnd + ditchSmoothRadius) continue;
                            if (nc < zoneStart - ditchSmoothRadius * 0.5f) continue;
                            float dx=kx*wpx, d2=dx*dx+dz*dz;
                            float w=Mathf.Exp(-d2/sig2);
                            ws+=w; hs+=src[nz,nx]*w;
                        }
                    }
                    if (ws<0.0001f) continue;
                    // Keep the cut-only constraint during smoothing too
                    float smoothed = Mathf.Lerp(src[lz,lx], hs/ws, blendW);
                    dst[lz,lx] = Mathf.Min(src[lz,lx], smoothed);
                }

                td.SetHeights(pxMin, pzMin, dst);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Paint
    // ─────────────────────────────────────────────────────────

    void PaintTerrainSection(List<Vector3> pts, int[] styleIndices, int styleFilter,
        int texIndex, float shoulderWidth, float shoulderFalloff, float strength)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        TerrainData td = terrain.terrainData;
        int aw=td.alphamapWidth, ah=td.alphamapHeight, nl=td.alphamapLayers;
        Vector3 tPos = terrain.transform.position;
        if (texIndex < 0 || texIndex >= nl) { Debug.LogWarning("[RoadPathGenerator] Paint index OOB."); return; }

        float halfRoad = roadWidth*0.5f, sh = Mathf.Max(0f,shoulderWidth);
        float fw = Mathf.Clamp(shoulderFalloff,0f,sh);
        float fadeStart = halfRoad+(sh-fw), totalHalf = halfRoad+sh;

        var activePts = new List<Vector3>();
        for (int k=0;k<pts.Count;k++)
        {
            int si=(styleIndices!=null&&k<styleIndices.Length)?styleIndices[k]:0;
            if(styleFilter<0||si==styleFilter) activePts.Add(pts[k]);
        }
        if (activePts.Count < 2) return;

        float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
        foreach(Vector3 p in activePts)
        { if(p.x-totalHalf<minX)minX=p.x-totalHalf; if(p.x+totalHalf>maxX)maxX=p.x+totalHalf;
          if(p.z-totalHalf<minZ)minZ=p.z-totalHalf; if(p.z+totalHalf>maxZ)maxZ=p.z+totalHalf; }
        int axMin=Mathf.Clamp(Mathf.FloorToInt(((minX-tPos.x)/td.size.x)*aw),0,aw-1);
        int axMax=Mathf.Clamp(Mathf.CeilToInt( ((maxX-tPos.x)/td.size.x)*aw),0,aw-1);
        int azMin=Mathf.Clamp(Mathf.FloorToInt(((minZ-tPos.z)/td.size.z)*ah),0,ah-1);
        int azMax=Mathf.Clamp(Mathf.CeilToInt( ((maxZ-tPos.z)/td.size.z)*ah),0,ah-1);
        int subW=axMax-axMin+1, subH=azMax-azMin+1;
        float[,,] alphas = td.GetAlphamaps(axMin, azMin, subW, subH);

        for (int lz=0;lz<subH;lz++) for (int lx=0;lx<subW;lx++)
        {
            float wx=tPos.x+((axMin+lx)/(float)aw)*td.size.x;
            float wz=tPos.z+((azMin+lz)/(float)ah)*td.size.z;
            float cd = RoadCrossTrackDistance(wx, wz, activePts);
            if (cd > totalHalf) continue;
            float str=(cd<=fadeStart||fw<0.001f)?strength:strength*(1f-Mathf.Clamp01((cd-fadeStart)/fw));
            if (str<0.001f) continue;
            float nv=Mathf.Lerp(alphas[lz,lx,texIndex],1f,str);
            alphas[lz,lx,texIndex]=nv;
            float os=0f;
            for(int l=0;l<nl;l++) if(l!=texIndex) os+=alphas[lz,lx,l];
            if(os>0.0001f){float sc=(1f-nv)/os; for(int l=0;l<nl;l++) if(l!=texIndex) alphas[lz,lx,l]*=sc;}
        }
        td.SetAlphamaps(axMin, azMin, alphas);
    }

    // ─────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────

    static float RoadCrossTrackDistance(float wx, float wz, List<Vector3> pts)
    {
        if (pts.Count < 2) return float.MaxValue;
        Vector2 q = new Vector2(wx,wz);
        int bSeg=0; float bT=0f, bE=float.MaxValue; Vector2 bPt=Vector2.zero;
        for (int i=0;i<pts.Count-1;i++)
        {
            Vector2 a=new Vector2(pts[i].x,pts[i].z), b=new Vector2(pts[i+1].x,pts[i+1].z);
            float t; float d=ClosestPointOnSegment2D(q,a,b,out t);
            if(d<bE){bE=d;bSeg=i;bT=t;bPt=a+(b-a)*t;}
        }
        Vector2 curr=SegDir2D(pts,bSeg);
        Vector2 prev=bSeg>0?SegDir2D(pts,bSeg-1):curr;
        Vector2 next=bSeg<pts.Count-2?SegDir2D(pts,bSeg+1):curr;
        Vector2 dir=bT<0.5f?Vector2.Lerp((prev+curr)*0.5f,curr,bT*2f):Vector2.Lerp(curr,(curr+next)*0.5f,(bT-0.5f)*2f);
        if(dir.sqrMagnitude<0.00001f) return bE;
        dir.Normalize();
        return Mathf.Abs(Vector2.Dot(q-bPt, new Vector2(-dir.y,dir.x)));
    }

    static float ClosestPointOnSplineXZ(float wx, float wz, List<Vector3> pts, float[] smoothH,
        out float interpHeight, out float signedCrossTrack)
    {
        float minD=float.MaxValue; interpHeight=0f; signedCrossTrack=0f;
        Vector2 q=new Vector2(wx,wz);
        int bSeg=0; float bT=0f; Vector2 bPt=Vector2.zero;
        for (int i=0;i<pts.Count-1;i++)
        {
            Vector2 a=new Vector2(pts[i].x,pts[i].z), b=new Vector2(pts[i+1].x,pts[i+1].z);
            float t; float d=ClosestPointOnSegment2D(q,a,b,out t);
            if(d<minD){minD=d;bSeg=i;bT=t;bPt=a+(b-a)*t;interpHeight=Mathf.Lerp(smoothH[i],smoothH[i+1],t);}
        }
        Vector2 curr=SegDir2D(pts,bSeg);
        Vector2 prev=bSeg>0?SegDir2D(pts,bSeg-1):curr;
        Vector2 next=bSeg<pts.Count-2?SegDir2D(pts,bSeg+1):curr;
        Vector2 dir=bT<0.5f?Vector2.Lerp((prev+curr)*0.5f,curr,bT*2f):Vector2.Lerp(curr,(curr+next)*0.5f,(bT-0.5f)*2f);
        if(dir.sqrMagnitude>0.00001f){dir.Normalize();signedCrossTrack=Vector2.Dot(q-bPt,new Vector2(-dir.y,dir.x));}
        return minD;
    }

    static Vector2 SegDir2D(List<Vector3> pts, int si)
    { return (new Vector2(pts[si+1].x,pts[si+1].z)-new Vector2(pts[si].x,pts[si].z)).normalized; }

    static float ClosestPointOnSegment2D(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        Vector2 ab=b-a; float len2=ab.sqrMagnitude;
        if(len2<0.00001f){t=0f;return(p-a).magnitude;}
        t=Mathf.Clamp01(Vector2.Dot(p-a,ab)/len2);
        return(p-(a+ab*t)).magnitude;
    }

    static float HorizDist(Vector3 a, Vector3 b)
    { float dx=b.x-a.x,dz=b.z-a.z; return Mathf.Sqrt(dx*dx+dz*dz); }

    static float[] BuildArcLengths(List<Vector3> pts)
    {
        float[] l=new float[pts.Count];
        for(int i=1;i<pts.Count;i++) l[i]=l[i-1]+Vector3.Distance(pts[i],pts[i-1]);
        return l;
    }

    static Vector3 GetForward(List<Vector3> pts, int i)
    { return i<pts.Count-1?(pts[i+1]-pts[i]).normalized:(pts[i]-pts[i-1]).normalized; }

    // ─────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (controlPoints == null || controlPoints.Count < 2) return;
        var cp = new List<Vector3>();
        foreach (Transform t in controlPoints) if (t != null) cp.Add(t.position);
        if (closedLoop && cp.Count > 2) cp.Add(cp[0]);

        // Control point spheres
        Gizmos.color = new Color(1f, 0.8f, 0.1f, 1f);
        foreach (Transform t in controlPoints) if (t != null) Gizmos.DrawWireSphere(t.position, 0.4f);

        // ── Colour key ────────────────────────────────────────
        // Cyan        — centreline
        // Red         — road edges
        // Orange      — flatten extra width
        // Yellow      — flatten falloff outer / flatten beyond path outer
        // Green       — berm slope outer
        // Light green — berm outer falloff toe
        // Blue        — ditch inner wall top
        // Cornflower  — ditch floor start
        // Purple      — ditch floor end
        // Magenta     — ditch outer wall toe
        // White       — shoulder smooth outer
        // Pink        — ridge cap zone inner / outer
        // Teal        — curb outer edge

        Color cCentre = new Color(0.2f, 0.9f, 1.0f, 1.0f);
        Color cRoadEdge = new Color(1.0f, 0.3f, 0.3f, 0.8f);
        Color cFlattenExtra = new Color(1.0f, 0.6f, 0.1f, 0.7f);
        Color cFlattenFallof = new Color(1.0f, 1.0f, 0.1f, 0.6f);
        Color cBeyondPath = new Color(0.9f, 0.9f, 0.0f, 0.6f);
        Color cBermOuter = new Color(0.2f, 0.9f, 0.2f, 0.7f);
        Color cBermToe = new Color(0.6f, 1.0f, 0.4f, 0.6f);
        Color cDitchInner = new Color(0.3f, 0.5f, 1.0f, 0.8f);
        Color cDitchFloorS = new Color(0.5f, 0.7f, 1.0f, 0.7f);
        Color cDitchFloorE = new Color(0.7f, 0.4f, 1.0f, 0.7f);
        Color cDitchOuter = new Color(1.0f, 0.2f, 1.0f, 0.8f);
        Color cShoulder = new Color(1.0f, 1.0f, 1.0f, 0.6f);
        Color cRidgeCap = new Color(1.0f, 0.6f, 0.8f, 0.7f);
        Color cCurb = new Color(0.2f, 0.9f, 0.8f, 0.7f);

        // Pre-compute all the half-widths once
        float halfRoad = roadWidth * 0.5f;
        float flatExtraEdge = halfRoad + flattenExtraWidth;
        float flatFallofEdge = flatExtraEdge + flattenFalloff;
        float beyondEdge = halfRoad + flattenBeyondDistance;
        float bermOuterEdge = halfRoad + bermSlopeDistance;
        float bermToeEdge = bermOuterEdge + bermOuterFalloff;
        float ditchInnerTop = halfRoad + ditchOffset;
        float ditchFloorS = ditchInnerTop + ditchInnerWidth;
        float ditchFloorE = ditchFloorS + ditchBottomWidth;
        float ditchOuterToe = ditchFloorE + ditchOuterWidth;
        float shoulderOuter = halfRoad + shoulderSmoothDistance;
        float ridgeCapInner = Mathf.Max(0f, halfRoad - ridgeCapWidth);
        float ridgeCapOuter = halfRoad + ridgeCapWidth;
        float curbOuter = halfRoad + curbWidth;

        // Draw at terrainSplineSubdivision density so edge gizmo lines match
        // what the terrain operations actually project onto — gives an accurate
        // preview of where the road edge / ditch / berm will land.
        int gizmoSubdiv = segmentsPerCurve * Mathf.Max(1, terrainSplineSubdivision);

        for (int i = 0; i < cp.Count - 1; i++)
        {
            Vector3 p0 = cp[Mathf.Max(0, i - 1)], p1 = cp[i];
            Vector3 p2 = cp[Mathf.Min(cp.Count - 1, i + 1)], p3 = cp[Mathf.Min(cp.Count - 1, i + 2)];
            Vector3 prev = CatmullRom(p0, p1, p2, p3, 0f);

            for (int s = 1; s <= gizmoSubdiv; s++)
            {
                float tN = s / (float)gizmoSubdiv;
                Vector3 cur = CatmullRom(p0, p1, p2, p3, tN);
                Vector3 fwd = (cur - prev).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

                // ── Centreline ────────────────────────────────
                Gizmos.color = cCentre;
                Gizmos.DrawLine(prev, cur);

                // ── Road edges ────────────────────────────────
                Gizmos.color = cRoadEdge;
                Gizmos.DrawLine(prev - right * halfRoad, cur - right * halfRoad);
                Gizmos.DrawLine(prev + right * halfRoad, cur + right * halfRoad);

                // ── Curb outer edge ───────────────────────────
                if (generateCurbs)
                {
                    Gizmos.color = cCurb;
                    Gizmos.DrawLine(prev - right * curbOuter, cur - right * curbOuter);
                    Gizmos.DrawLine(prev + right * curbOuter, cur + right * curbOuter);
                }

                // ── Flatten extra width ───────────────────────
                if (flattenTerrain)
                {
                    Gizmos.color = cFlattenExtra;
                    Gizmos.DrawLine(prev - right * flatExtraEdge, cur - right * flatExtraEdge);
                    Gizmos.DrawLine(prev + right * flatExtraEdge, cur + right * flatExtraEdge);

                    // Flatten falloff outer boundary
                    Gizmos.color = cFlattenFallof;
                    Gizmos.DrawLine(prev - right * flatFallofEdge, cur - right * flatFallofEdge);
                    Gizmos.DrawLine(prev + right * flatFallofEdge, cur + right * flatFallofEdge);
                }

                // ── Flatten beyond path ───────────────────────
                if (flattenBeyondPath)
                {
                    Gizmos.color = cBeyondPath;
                    Gizmos.DrawLine(prev - right * beyondEdge, cur - right * beyondEdge);
                    Gizmos.DrawLine(prev + right * beyondEdge, cur + right * beyondEdge);
                }

                // ── Ridge cap inner / outer ───────────────────
                if (applyRidgeCap && Mathf.Abs(camberDegrees) > 0.1f)
                {
                    Gizmos.color = cRidgeCap;
                    Gizmos.DrawLine(prev - right * ridgeCapInner, cur - right * ridgeCapInner);
                    Gizmos.DrawLine(prev + right * ridgeCapInner, cur + right * ridgeCapInner);
                    Gizmos.DrawLine(prev - right * ridgeCapOuter, cur - right * ridgeCapOuter);
                    Gizmos.DrawLine(prev + right * ridgeCapOuter, cur + right * ridgeCapOuter);
                }

                // ── Berm outer slope / toe ────────────────────
                if (generateBerm)
                {
                    Gizmos.color = cBermOuter;
                    Gizmos.DrawLine(prev - right * bermOuterEdge, cur - right * bermOuterEdge);
                    Gizmos.DrawLine(prev + right * bermOuterEdge, cur + right * bermOuterEdge);

                    Gizmos.color = cBermToe;
                    Gizmos.DrawLine(prev - right * bermToeEdge, cur - right * bermToeEdge);
                    Gizmos.DrawLine(prev + right * bermToeEdge, cur + right * bermToeEdge);
                }

                // ── Ditch zones ───────────────────────────────
                if (generateDitch)
                {
                    // Inner wall top
                    Gizmos.color = cDitchInner;
                    Gizmos.DrawLine(prev - right * ditchInnerTop, cur - right * ditchInnerTop);
                    Gizmos.DrawLine(prev + right * ditchInnerTop, cur + right * ditchInnerTop);

                    // Floor start
                    Gizmos.color = cDitchFloorS;
                    Gizmos.DrawLine(prev - right * ditchFloorS, cur - right * ditchFloorS);
                    Gizmos.DrawLine(prev + right * ditchFloorS, cur + right * ditchFloorS);

                    // Floor end
                    Gizmos.color = cDitchFloorE;
                    Gizmos.DrawLine(prev - right * ditchFloorE, cur - right * ditchFloorE);
                    Gizmos.DrawLine(prev + right * ditchFloorE, cur + right * ditchFloorE);

                    // Outer wall toe
                    Gizmos.color = cDitchOuter;
                    Gizmos.DrawLine(prev - right * ditchOuterToe, cur - right * ditchOuterToe);
                    Gizmos.DrawLine(prev + right * ditchOuterToe, cur + right * ditchOuterToe);
                }

                // ── Shoulder smooth outer ─────────────────────
                if (smoothShoulder)
                {
                    Gizmos.color = cShoulder;
                    Gizmos.DrawLine(prev - right * shoulderOuter, cur - right * shoulderOuter);
                    Gizmos.DrawLine(prev + right * shoulderOuter, cur + right * shoulderOuter);
                }

                prev = cur;
            }
        }
    }
}
