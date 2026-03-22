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
    public int paintTextureIndex = 1;

    [Range(0f, 20f)]
    public float paintShoulderWidth = 0f;

    [Range(0f, 20f)]
    public float paintShoulderFalloff = 0f;

    [Range(0f, 1f)]
    public float paintStrength = 1f;

    [Tooltip("When true, the curb settings below replace the global settings for this segment.")]
    public bool overrideCurbs = false;

    [Tooltip("Generate curbs in this segment (only used when overrideCurbs is true).")]
    public bool generateCurbs = false;

    [Tooltip("Curb material for this segment (only used when overrideCurbs is true).")]
    public Material curbMaterial;

    [Tooltip("Include this segment in the MeshCollider.")]
    public bool generateCollision = false;
}

// ─────────────────────────────────────────────────────────────
//  RoadDistanceField
//  Pre-baked per-pixel road distance data covering the full
//  heightmap region of interest.  Built once per GenerateRoad()
//  and shared by every terrain pass so all operations use the
//  same consistent distance values — eliminating Voronoi zigzag
//  and misaligned boundaries between passes permanently.
// ─────────────────────────────────────────────────────────────
public class RoadDistanceField
{
    public int pxMin, pzMin, subW, subH;
    public float wpx, wpz;
    float _tPosX, _tPosZ, _tW, _tH;
    int _hw;

    public float[] crossDist;    // unsigned perpendicular distance
    public float[] signedCross;  // signed: left of road = negative
    public float[] centreH;      // slope-limited centreline world height

    public RoadDistanceField(int pxMin, int pzMin, int subW, int subH,
                             float tPosX, float tPosZ, float tW, float tH,
                             int hw, float wpx, float wpz)
    {
        this.pxMin = pxMin; this.pzMin = pzMin;
        this.subW = subW; this.subH = subH;
        this.wpx = wpx; this.wpz = wpz;
        _tPosX = tPosX; _tPosZ = tPosZ; _tW = tW; _tH = tH; _hw = hw;
        int n = subW * subH;
        crossDist = new float[n];
        signedCross = new float[n];
        centreH = new float[n];
        for (int i = 0; i < n; i++) crossDist[i] = 1000000f;
    }

    public int Idx(int lz, int lx) { return lz * subW + lx; }

    float Bilerp(float[] arr, float wx, float wz)
    {
        float fx = ((wx - _tPosX) / _tW) * (_hw - 1) - pxMin;
        float fz = ((wz - _tPosZ) / _tH) * (_hw - 1) - pzMin;
        int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, subW - 2);
        int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, subH - 2);
        float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
        return Mathf.Lerp(
            Mathf.Lerp(arr[z0 * subW + x0], arr[z0 * subW + x0 + 1], tx),
            Mathf.Lerp(arr[(z0 + 1) * subW + x0], arr[(z0 + 1) * subW + x0 + 1], tx), tz);
    }

    public float SampleCross(float wx, float wz) { return Bilerp(crossDist, wx, wz); }
    public float SampleSigned(float wx, float wz) { return Bilerp(signedCross, wx, wz); }
    public float SampleCentreH(float wx, float wz) { return Bilerp(centreH, wx, wz); }

    // Returns true if the world position falls within the distance field bounds.
    // Pixels outside the bounds should fall back to direct segment search.
    public bool Contains(float wx, float wz, float tPosX, float tPosZ,
                         float tW, float tH, int hw)
    {
        float fx = ((wx - tPosX) / tW) * (hw - 1);
        float fz = ((wz - tPosZ) / tH) * (hw - 1);
        return fx >= pxMin + 1 && fx <= pxMin + subW - 2 &&
               fz >= pzMin + 1 && fz <= pzMin + subH - 2;
    }
}

// ─────────────────────────────────────────────────────────────
//  VelocityMarker — serialized runtime data baked during
//  GenerateRoad() and read by VelocityOverlay at runtime.
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public class VelocityMarker
{
    public Vector3 position;
    public float kmh;        // actual speed in km/h
    public float velScale;   // 0-100 scale for colour
}

// ─────────────────────────────────────────────────────────────
//  RockScatterEntry — one prefab type in the scatter list.
// ─────────────────────────────────────────────────────────────
[System.Serializable]
public class RockScatterEntry
{
    [Tooltip("Rock or boulder prefab to scatter in the shoulder zone.")]
    public GameObject prefab;

    [Tooltip("When true, adds a MeshCollider to this prefab when placed.")]
    public bool addMeshCollider = false;
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

    [Tooltip("Number of box-blur passes applied to the distance field after baking.\n" +
         "Smooths micro-discontinuities at segment boundaries.\n" +
         "0 = sharpest edge.  1-2 = recommended for natural soft shoulder.")]
    [Range(0, 3)]
    public int distanceFieldBlur = 1;

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

    [Tooltip("When true, berm blends toward original pre-generate terrain heights\n" +
         "at its outer boundary — preserves the natural landscape shape.\n" +
         "When false, blends toward whatever the current heightmap shows\n" +
         "after flatten, ditch, and shoulder have run.")]
    public bool bermUseOriginalTerrain = true;

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
    public float ditchInnerWidth = 6f;

    [Tooltip("Width of the flat ditch floor (world units).\n" +
             "0  = V-ditch (inner and outer walls meet at a point).\n" +
             ">0 = U/trapezoidal ditch with a flat bottom.")]
    [Range(0f, 10f)]
    public float ditchBottomWidth = 7f;

    [Tooltip("Width of the slope climbing OUT of the ditch back to natural terrain (world units).\n" +
             "Larger = gentler grade up.  Usually >= ditchInnerWidth for a natural asymmetric look.")]
    [Range(0.5f, 20f)]
    public float ditchOuterWidth = 6f;

    [Tooltip("Maximum depth of the ditch floor below the terrain surface at the inner wall start (world units).\n" +
             "The bottom is cut this deep below whatever height the terrain already has at that point\n" +
             "(i.e. after flatten / berm have run), so the ditch always reads as a positive excavation.")]
    [Range(0.1f, 10f)]
    public float ditchDepth = 6.0f;

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
    [Tooltip("When true, applies a smooth cosine blend in the strip between the\n" +
         "road edge and the ditch inner wall top, eliminating the shelf or\n" +
         "jagged ledge that forms in that gap after flattening.\n" +
         "Only meaningful when both flattenTerrain and generateDitch are active.")]
    public bool smoothRoadToDitchTransition = false;

    [Tooltip("Blend strength of the road-to-ditch transition.\n" +
             "1.0 = fully overrides whatever flatten left in the transition strip\n" +
             "with a smooth cosine curve from road edge down to ditch inner wall top.\n" +
             "Lower values let the existing terrain contribute partially.")]
    [Range(0f, 1f)]
    public float roadToDitchBlendStrength = 1.0f;

    // ── Advanced Smoothing ────────────────────────────────────
    [Header("Terrain – Advanced Smoothing")]
    [Tooltip("When enabled, replaces the standard Gaussian kernel radius calculation\n" +
             "in all smoothing passes with a unified texel-space Blur Radius, and adds\n" +
             "Verticality bias to shoulder, ditch, ridge cap, and berm passes.\n" +
             "When disabled, all smoothing behaves exactly as before.")]
    public bool useAdvancedSmoothing = true;

    [Tooltip("Size of the blur kernel in heightmap texels.\n" +
             "Larger values sample a broader area during each smoothing pass.\n" +
             "1 = minimal blur, higher values = progressively broader smoothing.\n" +
             "Independent of terrain world size — purely texel-space.")]
    [Range(1, 32)]
    public int blurRadius = 1;

    [Tooltip("Directional bias for shoulder smoothing.\n" +
             " 0 = pure average (same as current behaviour).\n" +
             "-1 = strongly biased downward — shoulder drains away from road naturally.\n" +
             "+1 = strongly biased upward — shoulder raises toward road edge.")]
    [Range(-1f, 1f)]
    public float shoulderVerticality = .2f;

    [Tooltip("Directional bias for ditch smoothing.\n" +
             " 0 = pure average (same as current behaviour).\n" +
             "-1 = strongly biased downward — ditch walls settle into drainage profile.\n" +
             "+1 = biased upward — partially fills the ditch back in.")]
    [Range(-1f, 1f)]
    public float ditchVerticality = -0.4f;

    [Tooltip("Directional bias for ridge cap smoothing.\n" +
             " 0 = pure average (same as current behaviour).\n" +
             "+1 = biased upward — crest is emphasised.\n" +
             "-1 = biased downward — crest is softened into the shoulder.")]
    [Range(-1f, 1f)]
    public float ridgeCapVerticality = 0.2f;

    [Tooltip("Directional bias for berm blending.\n" +
             " 0 = pure average (same as current behaviour).\n" +
             "+1 = berm settles upward — embankment sits higher and more pronounced.\n" +
             "-1 = berm settles downward — embankment blends more into natural terrain.")]
    [Range(-1f, 1f)]
    public float bermVerticality = 0.3f;

    // ── Velocity Preview ──────────────────────────────────────
    [Header("Velocity Preview (Gizmos Only)")]
    [Tooltip("Show estimated rider velocity markers along the path in the scene view.\n" +
             "Markers appear where velocity changes by more than the threshold amount.\n" +
             "Colour: Grey = 25 km/h baseline, Green = building speed,\n" +
             "Yellow = moderate speed, Red = maximum speed.\n" +
             "Scale: 0 = 25 km/h (flat baseline)  |  100 = 120 km/h (maximum)")]
    public bool showVelocityPreview = false;

    [Tooltip("How quickly velocity responds to grade changes.\n" +
             "Lower = more momentum carried over from previous grade (realistic).\n" +
             "Higher = velocity snaps quickly to current grade target.\n" +
             "0.03–0.08 recommended for Descenders-like physics.")]
    [Range(0.01f, 1f)]
    public float velocityMomentumFactor = 0.05f;

    [Tooltip("Minimum velocity scale change (0-100) required to place a new marker.\n" +
             "Lower values = more markers. 5 recommended.")]
    [Range(1f, 20f)]
    public float velocityMarkerThreshold = 5f;

    // ── Shoulder Smoothing ────────────────────────────────────
    [Header("Terrain – Shoulder Smoothing")]
    [Tooltip("Apply Gaussian smoothing to the terrain shoulder zone on both sides\n" +
             "of the road after all other terrain passes have run.\n" +
             "Softens any remaining hard edges or artefacts at the road boundary.")]
    public bool smoothShoulder = true;

    [Tooltip("Number of Gaussian smoothing passes applied to the shoulder zone.\n" +
             "More passes = softer shoulder but slower generation.")]
    [Range(0, 10)]
    public int shoulderSmoothPasses = 4;

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
    public float shoulderSmoothInwardOverlap = 1.8f;

    // ── Rock Scatter ──────────────────────────────────────────
    [Header("Terrain – Rock Scatter")]
    [Tooltip("Scatter rock and boulder prefabs in the shoulder zone\n" +
             "between the ditch outer toe and the berm start.\n" +
             "Placed as children of a RockScatter container GameObject.\n" +
             "Only runs in the editor during Generate Road.")]
    public bool enableRockScatter = false;

    [Tooltip("List of prefabs to randomly choose from when scattering.\n" +
             "Each entry has its own mesh collider toggle.")]
    public List<RockScatterEntry> rockScatterPrefabs = new List<RockScatterEntry>();

    [Tooltip("Number of placement attempts per square metre of shoulder zone.\n" +
             "Higher = denser coverage. 0.5-2 recommended.")]
    [Range(0.1f, 5f)]
    public float rockScatterDensity = 1f;

    [Tooltip("Minimum world-unit spacing between placed rocks.\n" +
             "Prevents overlapping regardless of density setting.")]
    [Range(0.5f, 20f)]
    public float rockScatterMinSpacing = 2f;

    [Tooltip("Minimum uniform scale applied to each placed rock.")]
    [Range(0.1f, 10f)]
    public float rockScatterMinScale = 0.5f;

    [Tooltip("Maximum uniform scale applied to each placed rock.")]
    [Range(0.1f, 10f)]
    public float rockScatterMaxScale = 2f;

    [Tooltip("Minimum Y rotation in degrees applied to each placed rock.")]
    [Range(0f, 360f)]
    public float rockScatterMinRotY = 0f;

    [Tooltip("Maximum Y rotation in degrees applied to each placed rock.")]
    [Range(0f, 360f)]
    public float rockScatterMaxRotY = 360f;

    [Tooltip("Maximum random tilt in degrees on X and Z axes.\n" +
             "Gives rocks a naturally tumbled look.\n" +
             "0 = always upright.")]
    [Range(0f, 45f)]
    public float rockScatterMaxTilt = 15f;

    [Tooltip("When true, rocks are rotated to align with the terrain\n" +
             "slope at their placement position.")]
    public bool rockScatterAlignToSlope = true;

    [Tooltip("Random seed for scatter placement.\n" +
             "Same seed always produces the same layout.\n" +
             "Change to get a different arrangement.")]
    public int rockScatterSeed = 42;

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
    public float flattenFalloff = 2f;

    [Tooltip("When true, flattens terrain on both sides of the road beyond the road width\n" +
         "before Berm, Ditch, and Shoulder operations run.\n" +
         "Useful for clearing uneven ground adjacent to the road corridor.")]
    public bool flattenBeyondPath = false;

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
    public float paintShoulderWidth = 0f;

    [Tooltip("Width in world units over which the shoulder paint fades to zero\n" +
             "at its outer edge.  Creates a soft blend into surrounding terrain.")]
    [Range(0f, 20f)]
    public float paintShoulderFalloff = 0f;

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
    [SerializeField, HideInInspector] private int _savedHeightsRes;
    [SerializeField, HideInInspector] private int _savedHeightsPxMin;
    [SerializeField, HideInInspector] private int _savedHeightsPzMin;
    [SerializeField, HideInInspector] private int _savedHeightsSubW;
    [SerializeField, HideInInspector] private int _savedHeightsSubH;
    [SerializeField, HideInInspector] private float[] _savedAlphasFlat;
    [SerializeField, HideInInspector] private int _savedAlphasW;
    [SerializeField, HideInInspector] private int _savedAlphasH;
    [SerializeField, HideInInspector] private int _savedAlphasLayers;
    [SerializeField, HideInInspector] private int _savedAlphasAxMin;
    [SerializeField, HideInInspector] private int _savedAlphasAzMin;
    [SerializeField, HideInInspector] private bool _terrainSnapshotTaken = false;
    [System.NonSerialized] public bool _skipTerrainShaping = false;
    // Pre-baked distance field shared by all terrain passes in GenerateRoad().
    // Built once per generate call, null when terrain shaping is skipped.
    [System.NonSerialized] private RoadDistanceField _distField;

    // ── Runtime Velocity Overlay ──────────────────────────────
    [Header("Runtime Velocity Overlay")]
    [Tooltip("When enabled, bakes velocity marker data during Generate Road\n" +
             "and makes it available to the VelocityOverlay component at runtime.\n" +
             "Assign the VelocityOverlay GameObject to the field below.")]
    public bool showRuntimeVelocityOverlay = false;

    [Tooltip("When enabled, detects and bakes six distinct marker types along\n" +
             "the path. Each type can be individually enabled or disabled.\n" +
             "When disabled only basic grade-based markers are baked.")]
    public bool enableAdvancedVelocityMarkers = false;

    [Tooltip("Grade transitions — where velocity changes due to slope.\n" +
             "Colour: green to yellow to red based on speed.")]
    public bool markerTypeGrade = true;

    [Tooltip("Crests and compression zones — where uphill becomes downhill.\n" +
             "Colour: purple.")]
    public bool markerTypeCrest = true;

    [Tooltip("Long flat baseline sections — every 200m at baseline speed.\n" +
             "Colour: grey.")]
    public bool markerTypeFlat = true;

    [Tooltip("Cross-slope transitions — where terrain side slope changes.\n" +
             "Colour: orange.")]
    public bool markerTypeCrossSlope = true;

    [Tooltip("Tight curves — where spline curvature scrubs speed.\n" +
             "Colour: cyan.")]
    public bool markerTypeCurve = true;

    [Tooltip("Sustained steep sections — where high velocity holds over distance.\n" +
             "Colour: bright red.")]
    public bool markerTypeSustained = true;

    [Tooltip("Minimum arc length in metres a high velocity must be maintained\n" +
             "before a Sustained Steep marker is placed.")]
    [Range(10f, 200f)]
    public float sustainedSteepMinDistance = 40f;

    [Tooltip("Minimum velocity scale value (0-100) that counts as sustained steep.")]
    [Range(10f, 100f)]
    public float sustainedSteepThreshold = 60f;

    [Tooltip("Dot product threshold for tight curve detection.\n" +
             "Lower = only very tight curves. Higher = catches gentler curves.\n" +
             "0.85-0.95 recommended.")]
    [Range(0.5f, 0.99f)]
    public float curveTightnessDotThreshold = 0.9f;

    [Tooltip("Minimum cross-slope height difference in world units between\n" +
             "left and right sides of the road to trigger a cross-slope marker.")]
    [Range(0.1f, 5f)]
    public float crossSlopeThreshold = 0.5f;

    [Tooltip("Assign the GameObject that carries the VelocityOverlay component.\n" +
             "Only required when Show Runtime Velocity Overlay is enabled.")]
    public GameObject velocityOverlayHost;

    [SerializeField] public Vector3[] bakedMarkerPositions = new Vector3[0];
    [SerializeField] public float[] bakedMarkerKmh = new float[0];
    [SerializeField] public float[] bakedMarkerVelScale = new float[0];
    // Marker type: 0=grade, 1=crest, 2=flat, 3=crossSlope, 4=curve, 5=sustained
    [SerializeField] public int[] bakedMarkerType = new int[0];

#if UNITY_EDITOR
    float[,] GetSavedHeights()
    {
        if (_savedHeightsFlat == null || _savedHeightsSubW == 0) return null;
        float[,] h = new float[_savedHeightsSubH, _savedHeightsSubW];
        Buffer.BlockCopy(_savedHeightsFlat, 0, h, 0,
            _savedHeightsFlat.Length * sizeof(float));
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
        _splineStyleIndices = BuildSplineStyleIndices(_cachedSplinePoints.Count, 0);

        // High-density spline for all terrain passes.
        // More sample points = smooth projected road edge = no zigzag / faceted walls.
        List<Vector3> terrainPts = (terrainSplineSubdivision > 1)
            ? BuildTerrainSplinePoints()
            : _cachedSplinePoints;
        // Terrain spline style indices — built at terrain spline density so
        // per-segment paint correctly maps to terrainPts rather than mesh spline.
        int[] terrainStyleIndices = BuildSplineStyleIndices(terrainPts.Count,
                                                            terrainSplineSubdivision);

        // 2. Slope-limited flatten — always uses the low-density mesh spline
        // (_cachedSplinePoints) so the road surface heights come from the
        // mathematically smooth Catmull-Rom curve.  This gives a perfectly
        // smooth riding surface at any speed regardless of terrainSplineSubdivision.
        // The high-density terrainPts is only used for edge operations (berm,
        // ditch, paint) where accurate perpendicular projection matters.
        float[] postFlattenH = null;
        if (flattenTerrain)
        {
            // Flatten operates on a copy of the mesh spline so it does not
            // disturb the terrainPts reference used by subsequent passes.
            List<Vector3> flattenPts = new List<Vector3>(_cachedSplinePoints);
            FlattenTerrainSlopeLimited(ref flattenPts, out postFlattenH);

            // Write the slope-limited Y values back onto both splines so all
            // subsequent passes share the same centreline height profile.
            for (int i = 0; i < _cachedSplinePoints.Count; i++)
            {
                Vector3 p = _cachedSplinePoints[i];
                p.y = flattenPts[i].y;
                _cachedSplinePoints[i] = p;
            }

            // Sync terrainPts Y values from the mesh spline by mapping each
            // high-density point to its nearest low-density equivalent.
            // This ensures berm, ditch, and paint use the correct centreline
            // elevation without any micro-bumps from dense point sampling.
            if (terrainSplineSubdivision > 1)
            {
                int subdiv = terrainSplineSubdivision;
                for (int i = 0; i < terrainPts.Count; i++)
                {
                    int mi = Mathf.Clamp(i / subdiv, 0, _cachedSplinePoints.Count - 1);
                    Vector3 p = terrainPts[i];
                    p.y = _cachedSplinePoints[mi].y;
                    terrainPts[i] = p;
                }
            }
            else
            {
                // When subdivision is 0 or 1, terrainPts IS the mesh spline —
                // copy the corrected heights directly.
                for (int i = 0; i < terrainPts.Count; i++)
                {
                    Vector3 p = terrainPts[i];
                    p.y = flattenPts[i].y;
                    terrainPts[i] = p;
                }
            }
        }

        // Fall back: sample terrain now so ridge cap has accurate heights.
        // Always sized to terrainPts so ridge cap and berm have correct
        // elevation data at whatever spline density is active.
        if (postFlattenH == null)
        {
            Terrain at = Terrain.activeTerrain;
            postFlattenH = new float[terrainPts.Count];
            for (int i = 0; i < terrainPts.Count; i++)
                postFlattenH[i] = SampleTerrainHeight(terrainPts[i], at);
        }
        // Save mesh-density heights before expansion — passed to BakeDistanceField
        // separately so centreH uses smooth Catmull-Rom values, not stepped
        // nearest-neighbour mapped values that cause pointy peaks at subdivision > 0.
        float[] meshSplineH = postFlattenH;

        if (postFlattenH.Length != terrainPts.Count)
        {
            // postFlattenH was built from the mesh spline (low density).
            // Expand it to terrainPts density by nearest-neighbour mapping
            // so all subsequent passes receive a correctly-sized height array.
            float[] expandedH = new float[terrainPts.Count];
            int subdiv = Mathf.Max(1, terrainSplineSubdivision);
            for (int i = 0; i < terrainPts.Count; i++)
            {
                int mi = Mathf.Clamp(i / subdiv, 0, postFlattenH.Length - 1);
                expandedH[i] = postFlattenH[mi];
            }
            postFlattenH = expandedH;
        }

        // Bake distance field once — all terrain passes share these arrays.
        // Every operation now uses the same consistent per-pixel distance so
        // there are no Voronoi zigzag boundaries between flatten, berm, ditch,
        // shoulder, and paint.
        _distField = null;
        if (!_skipTerrainShaping)
            _distField = BakeDistanceField(terrainPts, postFlattenH, meshSplineH);

        // Write flatten heightmap using the blurred distance field so the
        // road edge boundary is perfectly smooth with no Voronoi stepping.
        if (flattenTerrain && !_skipTerrainShaping)
            ApplyFlattenHeightmap();

        // Apply flatten profile using the distance field with cosine shoulder blend.
        // This replaces the old per-pixel search in FlattenTerrainSlopeLimited.
        // if (flattenTerrain && !_skipTerrainShaping)
        //    ApplyFlattenProfile(terrainPts, postFlattenH);

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

        // 5. Unified ditch profile — writes the complete cross-section from
        // road edge outward using only the distance field for all height and
        // boundary decisions.  Replaces ApplyDitchToTerrain and
        // SmoothRoadToDitchTransition entirely.
        if (generateDitch && !_skipTerrainShaping)
            ApplyUnifiedDitchProfile();

        // 6. Shoulder profile — writes cosine rolling transition
        // from ditch outer wall top back up to raised platform
        // level so the zone between magenta and white has a
        // deliberate smooth profile rather than an uncontrolled
        // depression. Runs before Gaussian passes.
        if (smoothShoulder && !_skipTerrainShaping)
            ApplyShoulderProfile();

        // 6b. Shoulder smooth — Gaussian passes refine the shoulder profile
        // or soften the ditch outer boundary when ditch is active.
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
            // Pass _splineStyleIndices so PaintTerrainSection knows which
            // spline points belong to which segment.  Passing null caused
            // every point to default to index 0, making segment 0 paint
            // the entire trail and segments 1+ paint nothing.
            PaintTerrainSection(terrainPts, terrainStyleIndices,
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

        // Bake velocity markers for runtime overlay if enabled.
        // Markers are serialized on this component so they are
        // available after export without re-running generation.
        if (showRuntimeVelocityOverlay)
        {
            BakeVelocityMarkers();
            // Pass reference to overlay host if assigned
            if (velocityOverlayHost != null)
            {
                VelocityOverlay overlay =
                    velocityOverlayHost.GetComponent<VelocityOverlay>();
                if (overlay != null)
                    overlay.roadGenerator = this;
            }
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
        // 11. Rock scatter — editor only, places prefabs in shoulder
        // zone as children of a RockScatter container GameObject.
#if UNITY_EDITOR
        if (enableRockScatter && !_skipTerrainShaping)
            ScatterRocks();
#endif
    }

    public void ClearRoad()
    {
        if (_meshFilter != null) _meshFilter.sharedMesh = null;
        if (_meshCollider != null) _meshCollider.sharedMesh = null;
#if UNITY_EDITOR
        // Destroy rock scatter container and all children
        Transform existing = transform.Find("RockScatter");
        if (existing != null)
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (existing != null)
                    DestroyImmediate(existing.gameObject);
            };
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

    int[] BuildSplineStyleIndices(int splineCount, int subdiv)
    {
        int[] idx = new int[splineCount];
        int cpGaps = Mathf.Max(1, controlPoints.Count - 1);
        int segsPerGap = segmentsPerCurve * Mathf.Max(1, subdiv);
        for (int k = 0; k < splineCount; k++)
            idx[k] = Mathf.Clamp(k / segsPerGap, 0, cpGaps - 1);
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
    /*
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
        {
            float[,,] a = GetSavedAlphas();
            // Only restore alphamaps if layer count matches current terrain.
            // A mismatch means the snapshot is stale — skip silently rather
            // than crashing, the heights will still be restored correctly.
            if (a != null && td.alphamapLayers == _savedAlphasLayers)
                td.SetAlphamaps(0, 0, a);
            else if (a != null)
                Debug.LogWarning("[RoadPathGenerator] Alphamap layer count mismatch — " +
                                 "height restore succeeded but paint was not restored. " +
                                 "Click Generate Road to repaint.");
        }
        Debug.Log("[RoadPathGenerator] Terrain restored.");
    }
    #endif
    */

#if UNITY_EDITOR
    void SnapshotTerrain()
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        if (controlPoints == null || controlPoints.Count < 2) return;

        TerrainData td  = terrain.terrainData;
        int   hw        = td.heightmapResolution;
        int   aw        = td.alphamapWidth;
        int   ah        = td.alphamapHeight;
        int   nl        = td.alphamapLayers;
        float tW        = td.size.x, tH = td.size.z;
        Vector3 tPos    = terrain.transform.position;

        // Compute road corridor bounding box with generous padding
        // covering the full berm extent so berm can always read
        // original terrain heights for its outer blend target.
        float maxFeatureRadius = roadWidth * 0.5f
            + Mathf.Max(bermSlopeDistance + bermOuterFalloff + 25f,
                        ditchOffset + ditchInnerWidth + ditchBottomWidth
                        + ditchOuterWidth + shoulderSmoothDistance + 25f);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Transform t in controlPoints)
        {
            if (t == null) continue;
            if (t.position.x - maxFeatureRadius < minX)
                minX = t.position.x - maxFeatureRadius;
            if (t.position.x + maxFeatureRadius > maxX)
                maxX = t.position.x + maxFeatureRadius;
            if (t.position.z - maxFeatureRadius < minZ)
                minZ = t.position.z - maxFeatureRadius;
            if (t.position.z + maxFeatureRadius > maxZ)
                maxZ = t.position.z + maxFeatureRadius;
        }

        // Heights region
        int hpxMin = Mathf.Clamp(
            Mathf.FloorToInt(((minX - tPos.x) / tW) * (hw-1)), 0, hw-1);
        int hpxMax = Mathf.Clamp(
            Mathf.CeilToInt( ((maxX - tPos.x) / tW) * (hw-1)), 0, hw-1);
        int hpzMin = Mathf.Clamp(
            Mathf.FloorToInt(((minZ - tPos.z) / tH) * (hw-1)), 0, hw-1);
        int hpzMax = Mathf.Clamp(
            Mathf.CeilToInt( ((maxZ - tPos.z) / tH) * (hw-1)), 0, hw-1);
        int hSubW  = hpxMax - hpxMin + 1;
        int hSubH  = hpzMax - hpzMin + 1;

        float[,] srcH = td.GetHeights(hpxMin, hpzMin, hSubW, hSubH);
        _savedHeightsFlat = new float[hSubW * hSubH];
        _savedHeightsRes  = hw;
        _savedHeightsPxMin = hpxMin;
        _savedHeightsPzMin = hpzMin;
        _savedHeightsSubW  = hSubW;
        _savedHeightsSubH  = hSubH;
        Buffer.BlockCopy(srcH, 0, _savedHeightsFlat, 0,
            _savedHeightsFlat.Length * sizeof(float));

        // Alphamaps region
        int axMin = Mathf.Clamp(
            Mathf.FloorToInt(((minX - tPos.x) / tW) * aw), 0, aw-1);
        int axMax = Mathf.Clamp(
            Mathf.CeilToInt( ((maxX - tPos.x) / tW) * aw), 0, aw-1);
        int azMin = Mathf.Clamp(
            Mathf.FloorToInt(((minZ - tPos.z) / tH) * ah), 0, ah-1);
        int azMax = Mathf.Clamp(
            Mathf.CeilToInt( ((maxZ - tPos.z) / tH) * ah), 0, ah-1);
        int aSubW = axMax - axMin + 1;
        int aSubH = azMax - azMin + 1;

        float[,,] srcA = td.GetAlphamaps(axMin, azMin, aSubW, aSubH);
        _savedAlphasFlat   = new float[aSubH * aSubW * nl];
        _savedAlphasW      = aSubW;
        _savedAlphasH      = aSubH;
        _savedAlphasLayers = nl;
        _savedAlphasAxMin  = axMin;
        _savedAlphasAzMin  = azMin;
        Buffer.BlockCopy(srcA, 0, _savedAlphasFlat, 0,
            _savedAlphasFlat.Length * sizeof(float));

        _terrainSnapshotTaken = true;
        Debug.Log("[RoadPathGenerator] Regional terrain snapshot saved. " +
                  "Heights: " + hSubW + "x" + hSubH +
                  "  Alphas: " + aSubW + "x" + aSubH);
    }

        void RestoreTerrain()
    {
        if (!_terrainSnapshotTaken) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;
        TerrainData td = terrain.terrainData;

        // Restore heights to saved region only
        if (_savedHeightsFlat != null && _savedHeightsSubW > 0)
        {
            float[,] h = GetSavedHeights();
            if (h != null)
                td.SetHeights(_savedHeightsPxMin, _savedHeightsPzMin, h);
        }

        // Restore alphamaps to saved region only
        if (_savedAlphasFlat != null && _savedAlphasW > 0)
        {
            float[,,] a = GetSavedAlphas();
            if (a != null && td.alphamapLayers == _savedAlphasLayers)
                td.SetAlphamaps(_savedAlphasAxMin, _savedAlphasAzMin, a);
            else if (a != null)
                Debug.LogWarning("[RoadPathGenerator] Alphamap layer " +
                    "count mismatch — heights restored, paint was not.");
        }

        Debug.Log("[RoadPathGenerator] Regional terrain restored.");
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
        /*
                float halfRoad = roadWidth * 0.5f + flattenExtraWidth;
                float totalHalf = halfRoad + flattenFalloff;
                float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (Vector3 p in pts)
                {
                    if (p.x - totalHalf < minX) minX = p.x - totalHalf; if (p.x + totalHalf > maxX) maxX = p.x + totalHalf;
                    if (p.z - totalHalf < minZ) minZ = p.z - totalHalf; if (p.z + totalHalf > maxZ) maxZ = p.z + totalHalf;
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
                        if (cd > totalHalf) continue;
                        float target = interpH - cSlope * sc;
                        float blend = (cd <= halfRoad) ? 1f : (flattenFalloff > 0.001f ? 1f - (cd - halfRoad) / flattenFalloff : 0f);
                        blend = Mathf.Clamp01(blend);
                        heights[lz, lx] = Mathf.Lerp(heights[lz, lx], (target - terrainPos.y) / terrainMaxY, blend);
                    }
                td.SetHeights(pxMin, pzMin, heights);
        */
/*  second attempt
        // When ditch is active the flatten platform extends to the full
        // ditch outer toe so both sides of the road have an identical flat
        // base for the ditch profile to work from.  When ditch is inactive
        // the platform extends only to flattenExtraWidth + flattenFalloff.
        float ditchOuterToe = generateDitch
            ? roadWidth * 0.5f + ditchOffset + ditchInnerWidth
              + ditchBottomWidth + ditchOuterWidth
            : 0f;

        float halfRoad = roadWidth * 0.5f + flattenExtraWidth;
        float totalHalf = generateDitch
            ? ditchOuterToe
            : halfRoad + flattenFalloff;

        // Raise the platform by ditchDepth when ditch is active so the
        // profile always has room to descend even on flat zero terrain.
        // On elevated terrain this raise is imperceptible.
        float platformRaise = (generateDitch && !_skipTerrainShaping)
            ? ditchDepth : 0f;

        float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - totalHalf < minX) minX = p.x - totalHalf; if (p.x + totalHalf > maxX) maxX = p.x + totalHalf;
            if (p.z - totalHalf < minZ) minZ = p.z - totalHalf; if (p.z + totalHalf > maxZ) maxZ = p.z + totalHalf;
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
                if (cd > totalHalf) continue;

                // Zone 1 — flat road surface at centreline height.
                // Platform raised by ditchDepth when ditch active so the
                // ditch profile always has room to descend.
                if (cd <= halfRoad)
                {
                    float target = interpH + platformRaise;
                    heights[lz, lx] = Mathf.Clamp01(
                        (target - terrainPos.y) / terrainMaxY);
                    continue;
                }

                // Zone 2 — extended flat platform to ditch outer toe.
                // No falloff, no blend — hard flat at road height + raise
                // so ditch profile has an identical base on both sides.
                if (generateDitch && cd <= ditchOuterToe)
                {
                    float target = interpH + platformRaise;
                    heights[lz, lx] = Mathf.Clamp01(
                        (target - terrainPos.y) / terrainMaxY);
                    continue;
                }

                // Zone 3 — shoulder falloff when ditch is not active.
                // Cosine ease from road edge back toward natural terrain.
                if (!generateDitch && flattenFalloff > 0.001f)
                {
                    float t = (cd - halfRoad) / flattenFalloff;
                    float blend = Mathf.Pow(
                        Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI * 0.5f), 2f);
                    float target = interpH + platformRaise;
                    heights[lz, lx] = Mathf.Lerp(heights[lz, lx],
                        Mathf.Clamp01((target - terrainPos.y) / terrainMaxY),
                        Mathf.Clamp01(blend));
                }
            }
        td.SetHeights(pxMin, pzMin, heights);
*/

        for (int i = 0; i < pts.Count; i++) { Vector3 p=pts[i]; p.y=smoothH[i]; pts[i]=p; }
        postFlattenH = smoothH;
    }

    // ─────────────────────────────────────────────────────────
    //  Distance Field Baking
    // ─────────────────────────────────────────────────────────
    //  Computes crossDist, signedCross, and centreH for every
    //  heightmap pixel in the road region of interest.  Called
    //  once per GenerateRoad() and stored in _distField so all
    //  terrain passes share the same consistent distance values.

    RoadDistanceField BakeDistanceField(List<Vector3> pts, float[] centrelineH,
                                        float[] meshCentreH)
    {
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null || pts.Count < 2 ||
            centrelineH == null || centrelineH.Length < pts.Count) return null;

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float tW = td.size.x, tH = td.size.z;
        Vector3 tPos = terrain.transform.position;
        float wpx = tW / (hw - 1), wpz = tH / (hw - 1);

        // Bounding radius covers every possible feature extent
        float halfRoad = roadWidth * 0.5f;
        float maxRadius = flattenExtraWidth + flattenFalloff + 2f;
        if (flattenBeyondPath && flattenBeyondDistance + 2f > maxRadius)
            maxRadius = flattenBeyondDistance + 2f;
        if (generateBerm && bermSlopeDistance + bermOuterFalloff + 2f > maxRadius)
            maxRadius = bermSlopeDistance + bermOuterFalloff + 2f;
        if (generateDitch)
        {
            float dr = ditchOffset + ditchInnerWidth + ditchBottomWidth
                       + ditchOuterWidth + ditchSmoothRadius + 4f;
            if (dr > maxRadius) maxRadius = dr;
        }
        if (smoothShoulder && shoulderSmoothDistance + shoulderSmoothRadius + 2f > maxRadius)
            maxRadius = shoulderSmoothDistance + shoulderSmoothRadius + 2f;
        if (paintTerrain && paintShoulderWidth + 4f > maxRadius)
            maxRadius = paintShoulderWidth + 4f;
        maxRadius += halfRoad;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - maxRadius < minX) minX = p.x - maxRadius; if (p.x + maxRadius > maxX) maxX = p.x + maxRadius;
            if (p.z - maxRadius < minZ) minZ = p.z - maxRadius; if (p.z + maxRadius > maxZ) maxZ = p.z + maxRadius;
        }
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt(((maxX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt(((maxZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int subW = pxMax - pxMin + 1, subH = pzMax - pzMin + 1;

        RoadDistanceField field = new RoadDistanceField(
            pxMin, pzMin, subW, subH, tPos.x, tPos.z, tW, tH, hw, wpx, wpz);

        // Fill per-pixel data.  High-density terrain spline gives accurate XZ
        // cross-distance and signed cross-track.  centreH uses the low-density
        // mesh spline so height values come from the smooth Catmull-Rom curve
        // rather than stepped nearest-neighbour mapped values — this prevents
        // the pointy peaks that appear at subdivision > 0.
        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                float wx = tPos.x + ((pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z + ((pzMin + lz) / (float)(hw - 1)) * tH;
                float iH, sc;
                float cd = ClosestPointOnSplineXZ(wx, wz, pts, centrelineH, out iH, out sc);
                // centreH from low-density mesh spline — smooth heights only
                float meshH, meshSc;
                ClosestPointOnSplineXZ(wx, wz, _cachedSplinePoints, meshCentreH,
                                       out meshH, out meshSc);
                int idx = field.Idx(lz, lx);
                field.crossDist[idx] = cd;       // high-density — accurate edge boundary
                field.signedCross[idx] = meshSc;   // mesh spline — consistent with centreH
                field.centreH[idx] = meshH;    // mesh spline — smooth heights
            }

        // Box blur on crossDist only — dissolves Voronoi micro-discontinuities
        // without affecting the signed cross-track or height accuracy.
        if (distanceFieldBlur > 0)
        {
            for (int pass = 0; pass < distanceFieldBlur; pass++)
            {
                float[] src = (float[])field.crossDist.Clone();
                for (int lz = 1; lz < subH - 1; lz++)
                    for (int lx = 1; lx < subW - 1; lx++)
                    {
                        int idx = field.Idx(lz, lx);
                        if (src[idx] > maxRadius + 2f) continue;
                        float sum = 0f;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                                sum += src[field.Idx(lz + dz, lx + dx)];
                        field.crossDist[idx] = sum / 9f;
                    }
            }
        }

        return field;
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Flatten Heightmap  (distance field version)
    // ─────────────────────────────────────────────────────────
    //  Writes the flat road corridor and shoulder/ditch platform
    //  to the heightmap using the pre-baked blurred distance field.
    //  This eliminates the Voronoi edge stepping that occurs when
    //  using per-pixel segment search for boundary decisions.
    //
    //  Zone 1 — 0 to halfRoad + flattenExtraWidth
    //    Perfectly flat at centreline height + platformRaise.
    //    No cross-track math — pure centreH stamp.
    //
    //  Zone 2 — ditch active: extends flat to full ditch outer toe
    //    Same flat height as Zone 1 so ditch profile has identical
    //    base on both sides regardless of natural terrain slope.
    //
    //  Zone 3 — ditch inactive: cosine falloff to natural terrain
    //    Smooth rounded shoulder transition with zero gradient at
    //    both ends — no visible kink at road edge or outer boundary.
    // ─────────────────────────────────────────────────────────

    void ApplyFlattenHeightmap()
    {
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        float terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;

        float halfRoad = roadWidth * 0.5f + flattenExtraWidth;

        // Full flat platform extends to ditch outer toe when ditch active
        /*    float ditchOuterToe = generateDitch
                ? roadWidth * 0.5f + ditchOffset + ditchInnerWidth
                  + ditchBottomWidth + ditchOuterWidth
                : 0f;

            float totalHalf = generateDitch
                ? ditchOuterToe
                : halfRoad + flattenFalloff;
        */
        float totalHalf = generateDitch
            ? roadWidth * 0.5f + ditchOffset + ditchInnerWidth
              + ditchBottomWidth + ditchOuterWidth
            : halfRoad + flattenFalloff;

        // Raise platform by ditchDepth when ditch active so the profile
        // always has room to descend even on flat zero terrain.
        float platformRaise = generateDitch ? ditchDepth : 0f;

        // Endpoint tangents for end-cap rejection
        // (reuse cached spline points — terrainPts not available here
        //  but the distance field already covers the correct region)
        Terrain at = Terrain.activeTerrain;

        float[,] heights = td.GetHeights(_distField.pxMin, _distField.pzMin,
                                         _distField.subW, _distField.subH);

        for (int lz = 0; lz < _distField.subH; lz++)
            for (int lx = 0; lx < _distField.subW; lx++)
            {
                int idx = _distField.Idx(lz, lx);
                float cd = _distField.crossDist[idx];
                if (cd > totalHalf + 0.5f) continue;

                float centreH = _distField.centreH[idx];
                float target = centreH + platformRaise;

                // Zone 1 — flat road corridor
                if (cd <= halfRoad)
                {
                    heights[lz, lx] = Mathf.Clamp01(
                        (target - terrainPos.y) / terrainMaxY);
                    continue;
                }

                // Zone 2 — extended flat platform when ditch active
                if (generateDitch && cd <= totalHalf)
                {
                    heights[lz, lx] = Mathf.Clamp01(
                        (target - terrainPos.y) / terrainMaxY);
                    continue;
                }

                // Zone 3 — cosine falloff when ditch not active
                if (!generateDitch && flattenFalloff > 0.001f && cd <= totalHalf)
                {
                    float t = (cd - halfRoad) / flattenFalloff;
                    float blend = Mathf.Pow(
                        Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI * 0.5f), 2f);
                    if (blend < 0.001f) continue;
                    heights[lz, lx] = Mathf.Lerp(heights[lz, lx],
                        Mathf.Clamp01((target - terrainPos.y) / terrainMaxY),
                        blend);
                }
            }

        td.SetHeights(_distField.pxMin, _distField.pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Unified Ditch Profile
    // ─────────────────────────────────────────────────────────
    //  Writes the complete cross-section from road edge outward
    //  using ONLY the pre-baked distance field for all distance
    //  and height decisions.  No per-pixel segment search, no
    //  terrain sampling for reference heights — pure distance
    //  field mathematics producing one smooth continuous curve
    //  identical on both sides of the road.
    //
    //  Cross-section from road edge outward:
    //
    //    halfRoad to halfRoad + ditchOffset
    //      Cosine descent from roadEdgeH toward ditch inner wall.
    //
    //    ditchOffset zone (inner wall)
    //      S-curve descent from roadEdgeH down to ditch floor
    //      at roadEdgeH - ditchDepth.
    //
    //    ditchBottomWidth zone
    //      Flat floor at roadEdgeH - ditchDepth.
    //
    //    ditchOuterWidth zone (outer wall)
    //      S-curve rise from floor blending back to existing
    //      terrain — no target height needed, just lerp toward
    //      whatever natural terrain is already there.
    //
    //    Beyond outer toe — nothing written.
    // ─────────────────────────────────────────────────────────

    void ApplyUnifiedDitchProfile()
    {
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        float terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;
        float tW = td.size.x, tH = td.size.z;
        int hw = td.heightmapResolution;

        float halfRoad = roadWidth * 0.5f;
        float roadEdgeH_offset = ditchDepth; // path raised by this amount

        // Zone boundaries measured from centreline
        float zShoulderEnd = halfRoad + ditchOffset;
        float zInnerEnd = zShoulderEnd + ditchInnerWidth;
        float zFloorEnd = zInnerEnd + ditchBottomWidth;
        float zOuterEnd = zFloorEnd + ditchOuterWidth;

        // Endpoint tangents for end-cap rejection
        List<Vector3> pts = _cachedSplinePoints;
        if (pts == null || pts.Count < 2) return;
        Vector2 startTangent = new Vector2(
            pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin = new Vector2(pts[0].x, pts[0].z);
        int lastIdx = pts.Count - 1;
        Vector2 endTangent = new Vector2(
            pts[lastIdx].x - pts[lastIdx - 1].x,
            pts[lastIdx].z - pts[lastIdx - 1].z).normalized;
        Vector2 endOrigin = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        float[,] heights = td.GetHeights(_distField.pxMin, _distField.pzMin,
                                         _distField.subW, _distField.subH);

        for (int lz = 0; lz < _distField.subH; lz++)
            for (int lx = 0; lx < _distField.subW; lx++)
            {
                int idx = _distField.Idx(lz, lx);
                float cd = _distField.crossDist[idx];
                if (cd <= halfRoad || cd > zOuterEnd + 1f) continue;

                // ── End-cap rejection ─────────────────────────────
                float wx = terrainPos.x + ((_distField.pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = terrainPos.z + ((_distField.pzMin + lz) / (float)(hw - 1)) * tH;
                Vector2 pq = new Vector2(wx, wz);
                if (Vector2.Dot(pq - startOrigin, startTangent) < 0f) continue;
                if (Vector2.Dot(pq - endOrigin, endTangent) > 0f) continue;

                // ── Heights from distance field only ──────────────
                float centreH = _distField.centreH[idx];
                float roadEdgeH = centreH + roadEdgeH_offset;
                float floorH = centreH; // floor sits at original terrain level

                float currentH = heights[lz, lx];

                float targetH;

                if (cd <= zShoulderEnd)
                {
                    // Shoulder strip — cosine ease from road edge down
                    // toward ditch inner wall top.  Arrives tangent-
                    // continuous at both ends — no kink at road edge.
                    float t = Mathf.Clamp01((cd - halfRoad) /
                                   Mathf.Max(0.001f, ditchOffset));
                    float cosT = (1f - Mathf.Cos(t * Mathf.PI)) * 0.5f;
                    targetH = Mathf.Lerp(roadEdgeH, roadEdgeH, cosT);
                    // Shoulder is flat — road edge height held across ditchOffset
                    targetH = roadEdgeH;
                }
                else if (cd <= zInnerEnd)
                {
                    // Inner wall — S-curve descent from roadEdgeH to floorH
                    float t = Mathf.Clamp01((cd - zShoulderEnd) /
                               Mathf.Max(0.001f, ditchInnerWidth));
                    float tS = t * t * (3f - 2f * t);
                    float tF = Mathf.Lerp(t, tS, ditchCurvature);
                    targetH = Mathf.Lerp(roadEdgeH, floorH, tF);
                }
                else if (cd <= zFloorEnd)
                {
                    // Flat floor
                    targetH = floorH;
                }
                else if (cd <= zOuterEnd)
                {
                    // Outer wall — S-curve rise from floorH back to
                    // existing terrain (lerp toward current height)
                    float t = Mathf.Clamp01((cd - zFloorEnd) /
                               Mathf.Max(0.001f, ditchOuterWidth));
                    float tS = t * t * (3f - 2f * t);
                    float tF = Mathf.Lerp(t, tS, ditchCurvature);
                    // Blend from floor height back to whatever terrain is
                    targetH = Mathf.Lerp(floorH,
                               currentH * terrainMaxY + terrainPos.y, tF);
                }
                else continue;

                heights[lz, lx] = Mathf.Lerp(currentH,
                    Mathf.Clamp01((targetH - terrainPos.y) / terrainMaxY),
                    ditchStrength);
            }

        td.SetHeights(_distField.pxMin, _distField.pzMin, heights);

        // ── Smoothing passes ──────────────────────────────────
        if (ditchSmoothPasses > 0)
        {
            float wpx = tW / (hw - 1), wpz = tH / (hw - 1);
            int kRad = useAdvancedSmoothing
                         ? blurRadius
                         : Mathf.Max(1, Mathf.CeilToInt(
                             ditchSmoothRadius / Mathf.Min(wpx, wpz))) + 1;
            float sig = useAdvancedSmoothing
                         ? blurRadius * Mathf.Min(wpx, wpz) * 0.5f
                         : ditchSmoothRadius * 0.5f;
            float sig2 = 2f * sig * sig;

            for (int pass = 0; pass < ditchSmoothPasses; pass++)
            {
                float[,] src = td.GetHeights(_distField.pxMin, _distField.pzMin,
                                             _distField.subW, _distField.subH);
                float[,] dst = (float[,])src.Clone();

                for (int lz = 0; lz < _distField.subH; lz++)
                    for (int lx = 0; lx < _distField.subW; lx++)
                    {
                        int idx = _distField.Idx(lz, lx);
                        float cd = _distField.crossDist[idx];
                        if (cd <= halfRoad || cd > zOuterEnd) continue;

                        float wx2 = terrainPos.x + ((_distField.pxMin + lx) / (float)(hw - 1)) * tW;
                        float wz2 = terrainPos.z + ((_distField.pzMin + lz) / (float)(hw - 1)) * tH;
                        Vector2 pq2 = new Vector2(wx2, wz2);
                        if (Vector2.Dot(pq2 - startOrigin, startTangent) < 0f) continue;
                        if (Vector2.Dot(pq2 - endOrigin, endTangent) > 0f) continue;

                        // Blend weight peaks at floor, tapers at zone edges
                        float blendW;
                        if (cd <= zInnerEnd)
                            blendW = Mathf.Clamp01((cd - zShoulderEnd) /
                                     Mathf.Max(0.001f, ditchInnerWidth));
                        else if (cd <= zFloorEnd)
                            blendW = 1f;
                        else
                            blendW = Mathf.Clamp01(1f - (cd - zFloorEnd) /
                                     Mathf.Max(0.001f, ditchOuterWidth));
                        if (blendW < 0.001f) continue;

                        float ws = 0f, hs = 0f;
                        for (int kz = -kRad; kz <= kRad; kz++)
                        {
                            int nz = lz + kz;
                            if (nz < 0 || nz >= _distField.subH) continue;
                            float dz = kz * wpz;
                            for (int kx = -kRad; kx <= kRad; kx++)
                            {
                                int nx = lx + kx;
                                if (nx < 0 || nx >= _distField.subW) continue;
                                float nc = _distField.crossDist[_distField.Idx(nz, nx)];
                                if (nc > zOuterEnd + ditchSmoothRadius) continue;
                                if (nc < halfRoad) continue;
                                float dx2 = kx * wpx, d2 = dx2 * dx2 + dz * dz;
                                float w = Mathf.Exp(-d2 / sig2);
                                if (useAdvancedSmoothing &&
                                    Mathf.Abs(ditchVerticality) > 0.001f)
                                {
                                    float hd2 = src[nz, nx] - src[lz, lx];
                                    float vB = 1f + ditchVerticality * Mathf.Sign(hd2);
                                    w *= Mathf.Max(0.001f, vB);
                                }
                                ws += w; hs += src[nz, nx] * w;
                            }
                        }
                        if (ws < 0.0001f) continue;
                        dst[lz, lx] = Mathf.Lerp(src[lz, lx], hs / ws, blendW);
                    }

                td.SetHeights(_distField.pxMin, _distField.pzMin, dst);
            }
        }
    }


    // ─────────────────────────────────────────────────────────
    //  Terrain – Shoulder Profile  (standalone, no ditch)
    // ─────────────────────────────────────────────────────────
    //  Writes a cosine rolling transition from the road edge
    //  outward to natural terrain when ditch is not active.
    //  Uses the distance field for smooth consistent boundaries
    //  and the saved terrain snapshot as the outer blend target
    //  so cross-sloped terrain transitions naturally on both
    //  sides identically.
    //
    //  Runs before SmoothTerrainShoulder Gaussian passes so
    //  the shaped profile is refined rather than just blurred.
    //
    //  Cross-section from road edge outward:
    //    halfRoad to halfRoad + flattenFalloff
    //      Already handled by ApplyFlattenHeightmap cosine falloff.
    //      Shoulder profile starts just beyond this zone.
    //    flattenFalloff end to flattenFalloff + shoulderSmoothDistance
    //      Cosine descent from road edge height toward original
    //      natural terrain with zero gradient at both ends.
    // ─────────────────────────────────────────────────────────

    void ApplyShoulderProfile()
    {
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        float terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;
        float tW = td.size.x, tH = td.size.z;
        int hw = td.heightmapResolution;

        float halfRoad = roadWidth * 0.5f + flattenExtraWidth;

        // Start position adapts to active features so the shoulder
        // cosine always begins exactly where the previous feature ends.
        // This ensures a zero-gradient join between ditch outer wall
        // and shoulder in every combination.
        float profileStart;
        if (generateDitch)
        {
            // Start from ditch floor end — purple gizmo line.
            // Shoulder owns the outer wall zone and blends it
            // back to natural terrain with a cosine profile.
            profileStart = roadWidth * 0.5f + ditchOffset
                           + ditchInnerWidth + ditchBottomWidth;
        }
        else
        {
            // Start from flatten falloff outer edge — just beyond
            // the cosine falloff the flatten pass already wrote.
            profileStart = halfRoad + flattenFalloff;
        }

        float profileEnd = profileStart + shoulderSmoothDistance;

        // Endpoint tangents for end-cap rejection
        List<Vector3> pts = _cachedSplinePoints;
        if (pts == null || pts.Count < 2) return;
        Vector2 startTangent = new Vector2(
            pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin = new Vector2(pts[0].x, pts[0].z);
        int lastIdx = pts.Count - 1;
        Vector2 endTangent = new Vector2(
            pts[lastIdx].x - pts[lastIdx - 1].x,
            pts[lastIdx].z - pts[lastIdx - 1].z).normalized;
        Vector2 endOrigin = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        float[,] heights = td.GetHeights(_distField.pxMin, _distField.pzMin,
                                         _distField.subW, _distField.subH);

        for (int lz = 0; lz < _distField.subH; lz++)
            for (int lx = 0; lx < _distField.subW; lx++)
            {
                int idx = _distField.Idx(lz, lx);
                float cd = _distField.crossDist[idx];
                if (cd <= profileStart || cd > profileEnd) continue;

                float wx = terrainPos.x + ((_distField.pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = terrainPos.z + ((_distField.pzMin + lz) / (float)(hw - 1)) * tH;

                // ── End-cap rejection ─────────────────────────────
                Vector2 pq = new Vector2(wx, wz);
                if (Vector2.Dot(pq - startOrigin, startTangent) < 0f) continue;
                if (Vector2.Dot(pq - endOrigin, endTangent) > 0f) continue;

                // ── Inner edge height ─────────────────────────────
                float centreH = _distField.centreH[idx];
                float platformRaise = generateDitch ? ditchDepth : 0f;
                float roadEdgeH = centreH + platformRaise;

                // ── Cosine rise from ditch outer wall top back up
                // to platform level at white line.
                // t=0 at profileStart (purple), t=1 at profileEnd (white)
                float t = Mathf.Clamp01(
                              (cd - profileStart) / shoulderSmoothDistance);
                float cosT = (1f - Mathf.Cos(t * Mathf.PI)) * 0.5f;

                // Inner anchor is ditch floor level — centreH.
                // Outer anchor is raised platform — centreH + platformRaise.
                // Shoulder rises from ditch floor back up to platform
                // giving the rolling cosine hill between magenta and white.
                float innerH = centreH;
                float targetH = Mathf.Lerp(innerH, roadEdgeH, cosT);

                heights[lz, lx] = Mathf.Clamp01(
                    (targetH - terrainPos.y) / terrainMaxY);
            }

        td.SetHeights(_distField.pxMin, _distField.pzMin, heights);
    }

    // ─────────────────────────────────────────────────────────
    //  Terrain – Flatten Profile  (distance field version)
    // ─────────────────────────────────────────────────────────
    //  Writes the flat road corridor and shoulder transition to
    //  the heightmap using the pre-baked distance field.
    //
    //  The falloff zone now uses a cosine ease rather than the
    //  old linear blend — this is what produces the smooth
    //  rounded shoulder seen in the reference screenshots rather
    //  than a hard shelf at the road edge.

/*    void ApplyFlattenProfile(List<Vector3> pts, float[] smoothH)
    {
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        float terrainMaxY = td.size.y;
        Vector3 terrainPos = terrain.transform.position;

        float flatZone = roadWidth * 0.5f + flattenExtraWidth;
        float totalZone = flatZone + flattenFalloff;
        float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);

        float[,] heights = td.GetHeights(_distField.pxMin, _distField.pzMin,
                                         _distField.subW, _distField.subH);

        for (int lz = 0; lz < _distField.subH; lz++)
            for (int lx = 0; lx < _distField.subW; lx++)
            {
                int idx = _distField.Idx(lz, lx);
                float cd = _distField.crossDist[idx];
                if (cd > totalZone) continue;

                float interpH = _distField.centreH[idx];
                float sc = _distField.signedCross[idx];
                float target = interpH - cSlope * sc;

                float blend;
                if (cd <= flatZone)
                {
                    blend = 1f;
                }
                else if (flattenFalloff > 0.001f)
                {
                    // Cosine ease — zero gradient at both ends so the
                    // shoulder curves smoothly from the flat road surface
                    // back to natural terrain with no visible kink.
                    float t = (cd - flatZone) / flattenFalloff;
                    blend = Mathf.Pow(Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI * 0.5f), 2f);
                }
                else
                {
                    // Zero falloff — apply a sub-pixel soft blend at the
                    // boundary using the blurred distance field so the edge
                    // is always smooth rather than a hard Voronoi step.
                    float pixelWorld = Mathf.Max(_distField.wpx, _distField.wpz);
                    float t = (cd - flatZone) / (pixelWorld * 2f);
                    blend = t < 1f ? Mathf.Pow(Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI * 0.5f), 2f) : 0f;
                }

                if (blend < 0.001f) continue;
                heights[lz, lx] = Mathf.Lerp(heights[lz, lx],
                    Mathf.Clamp01((target - terrainPos.y) / terrainMaxY), blend);
            }

        td.SetHeights(_distField.pxMin, _distField.pzMin, heights);
    }
*/

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

    // ─────────────────────────────────────────────────────────
    //  Terrain – Berm / Natural Embankment
    // ─────────────────────────────────────────────────────────
    //  Uses the pre-baked distance field for all boundary
    //  decisions so berm zone edges align perfectly with
    //  flatten, ditch, and shoulder boundaries.
    //
    //  Berm start point depends on which features are active:
    //    Ditch + Shoulder active  → white shoulder outer line
    //    Ditch active only        → purple ditch floor end line
    //    Shoulder active only     → white shoulder outer line
    //    Neither active           → road edge halfRoad

    void ApplyBermToTerrain(List<Vector3> pts)
    {
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) { Debug.LogWarning("[RoadPathGenerator] Berm: no terrain."); return; }

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float tW = td.size.x, tH = td.size.z, tMaxY = td.size.y;
        Vector3 tPos = terrain.transform.position;

        // ── Berm start position based on active features ──────
        float halfRoad = roadWidth * 0.5f;
        float ditchFloorEnd = halfRoad + ditchOffset + ditchInnerWidth + ditchBottomWidth;
        float shoulderOuter = generateDitch
            ? ditchFloorEnd + shoulderSmoothDistance
            : halfRoad + shoulderSmoothDistance;

        float bermStart;
        if (generateDitch && smoothShoulder)
            bermStart = shoulderOuter;
        else if (generateDitch)
            bermStart = ditchFloorEnd;
        else if (smoothShoulder)
            bermStart = shoulderOuter;
        else
            bermStart = halfRoad;

        float bermEnd = bermStart + bermSlopeDistance;
        float bermToe = bermEnd + bermOuterFalloff;

        // ── Original terrain heights for outer blend target ───
#if UNITY_EDITOR
        bool hasSnapshot = _savedHeightsFlat != null && _savedHeightsRes > 0;
        float[,] savedH  = (bermUseOriginalTerrain && hasSnapshot)
            ? GetSavedHeights() : null;
#else
        float[,] savedH = null;
#endif

        // ── Bounding box ──────────────────────────────────────
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - bermToe < minX) minX = p.x - bermToe; if (p.x + bermToe > maxX) maxX = p.x + bermToe;
            if (p.z - bermToe < minZ) minZ = p.z - bermToe; if (p.z + bermToe > maxZ) maxZ = p.z + bermToe;
        }
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt(((maxX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt(((maxZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int subW = pxMax - pxMin + 1, subH = pzMax - pzMin + 1;
        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        // ── Endpoint tangents for end-cap rejection ───────────
        Vector2 startTangent = new Vector2(
            pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin = new Vector2(pts[0].x, pts[0].z);
        int lastIdx = pts.Count - 1;
        Vector2 endTangent = new Vector2(
            pts[lastIdx].x - pts[lastIdx - 1].x,
            pts[lastIdx].z - pts[lastIdx - 1].z).normalized;
        Vector2 endOrigin = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                float cd = float.MaxValue;
                float wx = tPos.x + ((pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z + ((pzMin + lz) / (float)(hw - 1)) * tH;

                // Use distance field when pixel falls within its bounds
                if (_distField != null)
                {
                    float df = _distField.SampleCross(wx, wz);
                    if (df < 999999f) cd = df;
                }

                // Fall back to direct search if outside field bounds
                if (cd > 999999f)
                {
                    float iH2, sc2;
                    float[] tmpH = new float[pts.Count];
                    for (int i = 0; i < pts.Count; i++)
                        tmpH[i] = terrain.SampleHeight(pts[i]) + tPos.y;
                    cd = ClosestPointOnSplineXZ(wx, wz, pts, tmpH, out iH2, out sc2);
                }

                if (cd < bermStart || cd > bermToe) continue;

                // ── End-cap rejection ─────────────────────────────
                Vector2 pq = new Vector2(wx, wz);
                if (Vector2.Dot(pq - startOrigin, startTangent) < 0f) continue;
                if (Vector2.Dot(pq - endOrigin, endTangent) > 0f) continue;

                // ── Height references from distance field ─────────
                float centreH = _distField.SampleCentreH(wx, wz);
                float edgeH = centreH + (generateDitch ? ditchDepth : 0f);

                // Outer target — original terrain or current heightmap
                float origH;
                if (bermUseOriginalTerrain && savedH != null)
                {
                    int absPx = Mathf.Clamp(pxMin + lx, 0, hw - 1);
                    int absPz = Mathf.Clamp(pzMin + lz, 0, hw - 1);
                    int relPx = Mathf.Clamp(absPx - _savedHeightsPxMin,
                                0, _savedHeightsSubW - 1);
                    int relPz = Mathf.Clamp(absPz - _savedHeightsPzMin,
                                0, _savedHeightsSubH - 1);
                    origH = savedH[relPz, relPx] * tMaxY + tPos.y;
                }
                else
                {
                    origH = heights[lz, lx] * tMaxY + tPos.y;
                }

                // ── Berm profile — cosine ease ────────────────────
                // Pure cosine gives zero gradient at both the inner
                // edge and outer toe so the berm joins smoothly to
                // whatever feature precedes it and to natural terrain
                // at its outer boundary with no visible kinks.
                // bermCurvature blends between linear (0) and full
                // cosine (1) so existing setups are not broken.
                float t = Mathf.Clamp01((cd - bermStart) / bermSlopeDistance);
                float tCos = (1f - Mathf.Cos(t * Mathf.PI)) * 0.5f;
                float tF = Mathf.Lerp(t, tCos, bermCurvature);
                float ob = (bermOuterFalloff > 0.001f && cd > bermEnd)
                              ? Mathf.Clamp01(1f - (cd - bermEnd) / bermOuterFalloff) : 1f;

                float targetH = Mathf.Lerp(edgeH, origH, tF);
                float targetN = Mathf.Clamp01((targetH - tPos.y) / tMaxY);

                if (useAdvancedSmoothing && Mathf.Abs(bermVerticality) > 0.001f)
                {
                    float currentH2 = heights[lz, lx];
                    float higher = Mathf.Max(currentH2, targetN);
                    float lower = Mathf.Min(currentH2, targetN);
                    float biased = Mathf.Lerp(targetN,
                        bermVerticality > 0f ? higher : lower,
                        Mathf.Abs(bermVerticality));
                    heights[lz, lx] = Mathf.Lerp(currentH2, biased, ob);
                }
                else
                {
                    heights[lz, lx] = Mathf.Lerp(heights[lz, lx], targetN, ob);
                }
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
            int kRad = useAdvancedSmoothing
                           ? blurRadius
                           : Mathf.Max(1, Mathf.CeilToInt(capW / Mathf.Min(wpx, wpz))) + 1;
            float sig2 = useAdvancedSmoothing
                           ? 2f * (blurRadius * Mathf.Min(wpx, wpz) * 0.5f)
                                * (blurRadius * Mathf.Min(wpx, wpz) * 0.5f)
                           : (capW * 0.5f) * (capW * 0.5f) * 2f;   // 2σ² with σ = capW/2

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
                                float dx2 = kx * wpx, d2 = dx2 * dx2 + dz * dz;
                                float w = Mathf.Exp(-d2 / sig2);
                                if (useAdvancedSmoothing && Mathf.Abs(ridgeCapVerticality) > 0.001f)
                                {
                                    float heightDiff = src[nz, nx] - src[lz, lx];
                                    float vBias = 1f + ridgeCapVerticality * Mathf.Sign(heightDiff);
                                    w *= Mathf.Max(0.001f, vBias);
                                }
                                ws += w; hs += src[nz, nx] * w;
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

        float halfRoad = roadWidth * 0.5f;
        float ditchFloorEnd = halfRoad + ditchOffset + ditchInnerWidth + ditchBottomWidth;
        float ditchOuterToe = ditchFloorEnd + ditchOuterWidth;

        // When ditch active — shoulder starts from ditch floor end (purple
        // gizmo line) so it smoothly continues the outer wall transition
        // back to natural terrain rather than leaving the S-curve as a
        // hard boundary at the magenta outer toe line.
        // When ditch inactive — shoulder starts just inside the road edge
        // with the configured inward overlap.
        float innerStart = generateDitch
            ? ditchFloorEnd
            : halfRoad - Mathf.Max(0f, shoulderSmoothInwardOverlap);

        float outerEnd = generateDitch
            ? ditchFloorEnd + shoulderSmoothDistance
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
        int kRad = useAdvancedSmoothing
                       ? blurRadius
                       : Mathf.Max(1, Mathf.CeilToInt(shoulderSmoothRadius / Mathf.Min(wpx, wpz))) + 1;
        float sig = useAdvancedSmoothing
                       ? blurRadius * Mathf.Min(wpx, wpz) * 0.5f
                       : shoulderSmoothRadius * 0.5f;
        float sig2x2 = 2f * sig * sig;

        float[] cdCache = new float[subW * subH];
        for (int lz = 0; lz < subH; lz++) for (int lx = 0; lx < subW; lx++)
            {
                float wx = tPos.x + ((pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z + ((pzMin + lz) / (float)(hw - 1)) * tH;
                // Use pre-baked blurred distance field when available so
                // shoulder zone boundaries are smooth and consistent with
                // all other terrain passes — no Voronoi stepping.
                if (_distField != null)
                {
                    float df = _distField.SampleCross(wx, wz);
                    cdCache[lz * subW + lx] = (df > 999999f)
                        ? RoadCrossTrackDistance(wx, wz, pts)
                        : df;
                }
                else
                {
                    cdCache[lz * subW + lx] = RoadCrossTrackDistance(wx, wz, pts);
                }
            }

        for (int pass = 0; pass < shoulderSmoothPasses; pass++)
        {
            float[,] src = td.GetHeights(pxMin, pzMin, subW, subH);
            float[,] dst = (float[,])src.Clone();
            for (int lz=0;lz<subH;lz++) for (int lx=0;lx<subW;lx++)
            {
                float cd = cdCache[lz*subW+lx];
                    if (cd < innerStart || cd > outerEnd) continue;
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
                            if (cdCache[nz * subW + nx] < innerStart) continue;
                            float dx = kx * wpx, d2 = dx * dx + dz * dz;
                            float w = Mathf.Exp(-d2 / sig2x2);
                            if (useAdvancedSmoothing && Mathf.Abs(shoulderVerticality) > 0.001f)
                            {
                                float heightDiff = src[nz, nx] - src[lz, lx];
                                float vBias = 1f + shoulderVerticality * Mathf.Sign(heightDiff);
                                w *= Mathf.Max(0.001f, vBias);
                            }
                            ws += w; hs += src[nz, nx] * w;
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

 /*   void ApplyDitchToTerrain(List<Vector3> pts)
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

        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                int idx = lz * subW + lx;
                float wx = tPos.x + ((pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z + ((pzMin + lz) / (float)(hw - 1)) * tH;
                Vector2 pq = new Vector2(wx, wz);

                // ── End-cap rejection ─────────────────────────────
                // Still needs its own segment search to find bestSeg and bestT
                // for the endpoint tangent dot-product tests.
                float bestDist = float.MaxValue;
                int bestSeg = 0;
                float bestT = 0f;
                Vector2 bestPt = Vector2.zero;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Vector2 a = new Vector2(pts[i].x, pts[i].z);
                    Vector2 b = new Vector2(pts[i + 1].x, pts[i + 1].z);
                    float t;
                    float d = ClosestPointOnSegment2D(pq, a, b, out t);
                    if (d < bestDist) { bestDist = d; bestSeg = i; bestT = t; bestPt = a + (b - a) * t; }
                }

                Vector2 toPixelFromStart = pq - startOrigin;
                if (bestSeg == 0 && bestT < 0.01f &&
                    Vector2.Dot(toPixelFromStart, startTangent) < 0f)
                { cdCache[idx] = float.MaxValue; continue; }

                Vector2 toPixelFromEnd = pq - endOrigin;
                if (bestSeg == pts.Count - 2 && bestT > 0.99f &&
                    Vector2.Dot(toPixelFromEnd, endTangent) > 0f)
                { cdCache[idx] = float.MaxValue; continue; }

                // ── Distance field lookup ─────────────────────────
                // Use pre-baked blurred crossDist so ditch zone boundaries
                // align exactly with flatten zone boundaries — no misaligned
                // shelf between the two passes.
                float crossDist;
                float signedCross;
                if (_distField != null)
                {
                    crossDist = _distField.SampleCross(wx, wz);
                    signedCross = _distField.SampleSigned(wx, wz);
                    // Fall back if pixel is outside field bounds
                    if (crossDist > 999999f)
                    {
                        Vector2 currDir2 = SegDir2D(pts, bestSeg);
                        Vector2 prevDir2 = bestSeg > 0 ? SegDir2D(pts, bestSeg - 1) : currDir2;
                        Vector2 nextDir2 = bestSeg < pts.Count - 2 ? SegDir2D(pts, bestSeg + 1) : currDir2;
                        Vector2 tang2 = bestT < 0.5f
                            ? Vector2.Lerp((prevDir2 + currDir2) * 0.5f, currDir2, bestT * 2f)
                            : Vector2.Lerp(currDir2, (currDir2 + nextDir2) * 0.5f, (bestT - 0.5f) * 2f);
                        tang2.Normalize();
                        Vector2 rr2 = new Vector2(-tang2.y, tang2.x);
                        signedCross = Vector2.Dot(pq - bestPt, rr2);
                        crossDist = Mathf.Abs(signedCross);
                    }
                }
                else
                {
                    Vector2 currDir = SegDir2D(pts, bestSeg);
                    Vector2 prevDir = bestSeg > 0 ? SegDir2D(pts, bestSeg - 1) : currDir;
                    Vector2 nextDir = bestSeg < pts.Count - 2 ? SegDir2D(pts, bestSeg + 1) : currDir;
                    Vector2 tangent = bestT < 0.5f
                        ? Vector2.Lerp((prevDir + currDir) * 0.5f, currDir, bestT * 2f)
                        : Vector2.Lerp(currDir, (currDir + nextDir) * 0.5f, (bestT - 0.5f) * 2f);
                    tangent.Normalize();
                    Vector2 roadRight2 = new Vector2(-tangent.y, tangent.x);
                    signedCross = Vector2.Dot(pq - bestPt, roadRight2);
                    crossDist = Mathf.Abs(signedCross);
                }

                cdCache[idx] = crossDist;
                if (crossDist < zoneStart || crossDist > zoneEnd + 1f) continue;

                // ── Inner-wall reference height ───────────────────
                // Derive road-right direction from signedCross and bestPt
                // so the inner wall sample position is geometrically correct.
                float side = signedCross >= 0f ? 1f : -1f;
                Vector2 outwardDir = (pq - new Vector2(bestPt.x, bestPt.y));
                if (outwardDir.sqrMagnitude < 0.0001f)
                {
                    Vector2 cd2 = SegDir2D(pts, bestSeg);
                    outwardDir = new Vector2(-cd2.y, cd2.x) * side;
                }
                outwardDir.Normalize();
                Vector2 innerWallXZ = new Vector2(bestPt.x, bestPt.y)
                                      + outwardDir * (side >= 0f ? zoneStart : -zoneStart);

                Vector3 innerSample = new Vector3(innerWallXZ.x, 0f, innerWallXZ.y);
                innerSample.x = Mathf.Clamp(innerSample.x, tPos.x, tPos.x + tW);
                innerSample.z = Mathf.Clamp(innerSample.z, tPos.z, tPos.z + tH);
                float refH = terrain.SampleHeight(innerSample) + tPos.y;

                refHCache[idx] = refH;
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
            int kRad = useAdvancedSmoothing
                         ? blurRadius
                         : Mathf.Max(1, Mathf.CeilToInt(ditchSmoothRadius / Mathf.Min(wpx, wpz))) + 1;
            float sig = useAdvancedSmoothing
                         ? blurRadius * Mathf.Min(wpx, wpz) * 0.5f
                         : ditchSmoothRadius * 0.5f;
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
                                float dx = kx * wpx, d2 = dx * dx + dz * dz;
                                float w = Mathf.Exp(-d2 / sig2);
                                if (useAdvancedSmoothing && Mathf.Abs(ditchVerticality) > 0.001f)
                                {
                                    float heightDiff = src[nz, nx] - src[lz, lx];
                                    float vBias = 1f + ditchVerticality * Mathf.Sign(heightDiff);
                                    w *= Mathf.Max(0.001f, vBias);
                                }
                                ws += w; hs += src[nz, nx] * w;
                            }
                        }
                        if (ws < 0.0001f) continue;
                        // Keep the cut-only constraint during smoothing too
                        float smoothed = Mathf.Lerp(src[lz, lx], hs / ws, blendW);
                        dst[lz,lx] = Mathf.Min(src[lz,lx], smoothed);
                }

                td.SetHeights(pxMin, pzMin, dst);
            }
        }
    }
 */
    // ─────────────────────────────────────────────────────────
    //  Terrain – Road Edge to Ditch Transition
    // ─────────────────────────────────────────────────────────
    //  Operates exclusively in the strip from halfRoad to
    //  halfRoad + ditchOffset — the gap between the flat road
    //  surface and the top of the ditch inner wall.
    //
    //  For each pixel in that strip it:
    //    1. Samples the terrain height at the road edge (inner anchor)
    //    2. Samples the terrain height at the ditch inner wall top
    //       (outer anchor) — this is what the ditch carved to
    //    3. Applies a cosine blend between those two anchors so the
    //       strip curves smoothly from one to the other rather than
    //       leaving whatever the flatten falloff happened to write
    //
    //  The cosine profile means the blend is tangent-continuous at
    //  both ends — it arrives flat at the road edge and flat at the
    //  ditch inner wall top, producing a smooth rounded shoulder
    //  with no visible kink at either boundary.
    // ─────────────────────────────────────────────────────────

 /*   void SmoothRoadToDitchTransition(List<Vector3> pts)
    {
        if (pts.Count < 2) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        int hw = td.heightmapResolution;
        float tW = td.size.x, tH = td.size.z, tMaxY = td.size.y;
        Vector3 tPos = terrain.transform.position;

        float halfRoad = roadWidth * 0.5f;
        float ditchTopDist = halfRoad + ditchOffset;   // outer edge of transition strip
        float stripWidth = ditchOffset;               // width of the strip to operate on
        float padded = ditchTopDist + 1f;

        if (stripWidth < 0.001f) return;

        // ── Bounding box ──────────────────────────────────────
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector3 p in pts)
        {
            if (p.x - padded < minX) minX = p.x - padded; if (p.x + padded > maxX) maxX = p.x + padded;
            if (p.z - padded < minZ) minZ = p.z - padded; if (p.z + padded > maxZ) maxZ = p.z + padded;
        }
        int pxMin = Mathf.Clamp(Mathf.FloorToInt(((minX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pxMax = Mathf.Clamp(Mathf.CeilToInt(((maxX - tPos.x) / tW) * (hw - 1)), 0, hw - 1);
        int pzMin = Mathf.Clamp(Mathf.FloorToInt(((minZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int pzMax = Mathf.Clamp(Mathf.CeilToInt(((maxZ - tPos.z) / tH) * (hw - 1)), 0, hw - 1);
        int subW = pxMax - pxMin + 1, subH = pzMax - pzMin + 1;

        // ── Build spline height array ─────────────────────────
        float[] splineH = new float[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            splineH[i] = terrain.SampleHeight(pts[i]) + tPos.y;

        float[,] heights = td.GetHeights(pxMin, pzMin, subW, subH);

        for (int lz = 0; lz < subH; lz++)
            for (int lx = 0; lx < subW; lx++)
            {
                float wx = tPos.x + ((pxMin + lx) / (float)(hw - 1)) * tW;
                float wz = tPos.z + ((pzMin + lz) / (float)(hw - 1)) * tH;

                float interpH, sc;
                float cd = ClosestPointOnSplineXZ(wx, wz, pts, splineH, out interpH, out sc);

                // Only operate inside the transition strip
                if (cd < halfRoad || cd > ditchTopDist) continue;

                // ── Inner anchor: height at the road edge ─────────
                // Step inward from the current pixel to the road edge
                // and sample the terrain there — this is the height the
                // flat road surface was written to by the flatten pass.
                float innerAnchorH = terrain.SampleHeight(
                    new Vector3(wx, 0f, wz)) + tPos.y;
                // Re-sample closer to road edge for a cleaner anchor
                Vector3 towardCentre = new Vector3(wx, 0f, wz);
                // The road edge anchor is just the current flattened
                // road surface height at the projected centreline point
                // adjusted for camber at the edge.
                float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);
                float roadEdgeH = interpH - cSlope * Mathf.Clamp(sc, -halfRoad, halfRoad);

                // ── Outer anchor: height at the ditch inner wall top ──
                // Step outward from the spline projection point to
                // ditchTopDist and sample terrain there — this is what
                // the ditch carve left at the top of the inner wall.
                Vector2 proj2D = new Vector2(wx, wz);

                // Find the perpendicular outward direction at this pixel
                Vector2 q = new Vector2(wx, wz);
                int bSeg = 0; float bT = 0f; Vector2 bPt = Vector2.zero;
                float bestD = float.MaxValue;
                for (int ii = 0; ii < pts.Count - 1; ii++)
                {
                    Vector2 a2 = new Vector2(pts[ii].x, pts[ii].z);
                    Vector2 b2 = new Vector2(pts[ii + 1].x, pts[ii + 1].z);
                    float ttt;
                    float dd = ClosestPointOnSegment2D(q, a2, b2, out ttt);
                    if (dd < bestD) { bestD = dd; bSeg = ii; bT = ttt; bPt = a2 + (b2 - a2) * ttt; }
                }
                Vector2 cDir = SegDir2D(pts, bSeg);
                Vector2 pDir = bSeg > 0 ? SegDir2D(pts, bSeg - 1) : cDir;
                Vector2 nDir = bSeg < pts.Count - 2 ? SegDir2D(pts, bSeg + 1) : cDir;
                Vector2 tang = bT < 0.5f
                    ? Vector2.Lerp((pDir + cDir) * 0.5f, cDir, bT * 2f)
                    : Vector2.Lerp(cDir, (cDir + nDir) * 0.5f, (bT - 0.5f) * 2f);
                tang.Normalize();
                Vector2 roadRight = new Vector2(-tang.y, tang.x);
                float side = Vector2.Dot(q - bPt, roadRight) >= 0f ? 1f : -1f;
                // Step outward by ditchTopDist to find the outer anchor
                Vector2 outerXZ = new Vector2(bPt.x, bPt.y) + roadRight * (side * ditchTopDist);
                Vector3 outerSample = new Vector3(
                    Mathf.Clamp(outerXZ.x, tPos.x, tPos.x + tW),
                    0f,
                    Mathf.Clamp(outerXZ.y, tPos.z, tPos.z + tH));
                float ditchTopH = terrain.SampleHeight(outerSample) + tPos.y;

                // ── Cosine blend between the two anchors ──────────
                // tN = 0 at road edge, tN = 1 at ditch inner wall top
                float tN = Mathf.Clamp01((cd - halfRoad) / stripWidth);
                // Cosine ease — tangent continuous at both ends
                float cosBlend = (1f - Mathf.Cos(tN * Mathf.PI)) * 0.5f;
                float targetH = Mathf.Lerp(roadEdgeH, ditchTopH, cosBlend);
                float targetN = Mathf.Clamp01((targetH - tPos.y) / tMaxY);

                heights[lz, lx] = Mathf.Lerp(heights[lz, lx], targetN,
                                             roadToDitchBlendStrength);
            }

        td.SetHeights(pxMin, pzMin, heights);
    }
 */
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
        // Pre-compute endpoint tangents for end-cap rejection.
        // Pixels behind the spline start or beyond the spline end
        // project onto the endpoint and bleed outward in a triangle.
        Vector2 paintStartTangent = new Vector2(
            activePts[1].x - activePts[0].x,
            activePts[1].z - activePts[0].z).normalized;
        Vector2 paintStartOrigin = new Vector2(activePts[0].x, activePts[0].z);
        int paintLastIdx = activePts.Count - 1;
        Vector2 paintEndTangent = new Vector2(
            activePts[paintLastIdx].x - activePts[paintLastIdx - 1].x,
            activePts[paintLastIdx].z - activePts[paintLastIdx - 1].z).normalized;
        Vector2 paintEndOrigin = new Vector2(
            activePts[paintLastIdx].x, activePts[paintLastIdx].z);

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
                // Global paint uses the blurred distance field for smooth
                // zigzag-free edges.  Falls back to direct search for pixels
                // outside the field bounding box.
                float cd;
                if (_distField != null && styleFilter < 0)
                {
                    Terrain _pt = Terrain.activeTerrain;
                    TerrainData _ptd = _pt != null ? _pt.terrainData : null;
                    if (_ptd != null && _distField.Contains(wx, wz,
                        _pt.transform.position.x, _pt.transform.position.z,
                        _ptd.size.x, _ptd.size.z, _ptd.heightmapResolution))
                        cd = _distField.SampleCross(wx, wz);
                    else
                        cd = RoadCrossTrackDistance(wx, wz, activePts);
                }
                else
                {
                    cd = RoadCrossTrackDistance(wx, wz, activePts);
                }
                if (cd > totalHalf) continue;

                // ── End-cap rejection ─────────────────────────────────────
                // Skip pixels behind the spline start or beyond the spline end
                // to prevent the triangular bleed at path endpoints.
                Vector2 paintPq = new Vector2(wx, wz);
                if (Vector2.Dot(paintPq - paintStartOrigin, paintStartTangent) < 0f) continue;
                if (Vector2.Dot(paintPq - paintEndOrigin, paintEndTangent) > 0f) continue;

                // ── Steep slope rejection ─────────────────────────────────
                // Compare the pixel's actual terrain height against the target
                // height the flatten pass would write at this position.
                // Pixels significantly above the flatten target are on slope
                // walls and should not receive paint regardless of XZ distance.
                if (_distField != null)
                {
                    Terrain _hTerrain = Terrain.activeTerrain;
                    if (_hTerrain != null)
                    {
                        float pixelH = _hTerrain.SampleHeight(
                            new Vector3(wx, 0f, wz)) + _hTerrain.transform.position.y;
                        // Flatten target at this pixel: centreH minus camber offset.
                        // This is what the flatten pass wrote to the heightmap here.
                        float centreH = _distField.SampleCentreH(wx, wz);
                        float signedSc = _distField.SampleSigned(wx, wz);
                        float cSlope = Mathf.Sin(camberDegrees * Mathf.Deg2Rad);
                        // Account for platform raise when ditch is active —
                        // flatten wrote centreH + ditchDepth to the heightmap
                        // so the rejection reference must match that raised height.
                        float platformRaise = (generateDitch) ? ditchDepth : 0f;
                        float flattenTarget = centreH - cSlope * signedSc + platformRaise;
                        if (pixelH > flattenTarget + 1.0f) continue;
                    }
                }
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
        Vector2 q = new Vector2(wx, wz);
        int bSeg = 0; float bT = 0f, bE = float.MaxValue; Vector2 bPt = Vector2.zero;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = new Vector2(pts[i].x, pts[i].z), b = new Vector2(pts[i + 1].x, pts[i + 1].z);
            float t; float d = ClosestPointOnSegment2D(q, a, b, out t);
            if (d < bE) { bE = d; bSeg = i; bT = t; bPt = a + (b - a) * t; }
        }

        // Smooth the anchor point across segment boundaries only.
        // Within blendZone of each segment endpoint, blend the anchor
        // toward the adjacent segment's nearest point.  This eliminates
        // the discontinuous jump that causes zigzag ridges without
        // disturbing zone boundary distance tests — we only move the
        // anchor a small fraction so cross-track values stay accurate.
        const float blendZone = 0.25f;
        if (bT < blendZone && bSeg > 0)
        {
            Vector2 a2 = new Vector2(pts[bSeg - 1].x, pts[bSeg - 1].z);
            Vector2 b2 = new Vector2(pts[bSeg].x, pts[bSeg].z);
            float t2; ClosestPointOnSegment2D(q, a2, b2, out t2);
            Vector2 altPt = a2 + (b2 - a2) * t2;
            float w = (1f - bT / blendZone) * 0.5f;
            bPt = Vector2.Lerp(bPt, altPt, w);
        }
        else if (bT > 1f - blendZone && bSeg < pts.Count - 2)
        {
            Vector2 a2 = new Vector2(pts[bSeg + 1].x, pts[bSeg + 1].z);
            Vector2 b2 = new Vector2(pts[bSeg + 2].x, pts[bSeg + 2].z);
            float t2; ClosestPointOnSegment2D(q, a2, b2, out t2);
            Vector2 altPt = a2 + (b2 - a2) * t2;
            float w = ((bT - (1f - blendZone)) / blendZone) * 0.5f;
            bPt = Vector2.Lerp(bPt, altPt, w);
        }

        Vector2 curr = SegDir2D(pts, bSeg);
        Vector2 prev = bSeg > 0 ? SegDir2D(pts, bSeg - 1) : curr;
        Vector2 next = bSeg < pts.Count - 2 ? SegDir2D(pts, bSeg + 1) : curr;
        Vector2 dir = bT < 0.5f ? Vector2.Lerp((prev + curr) * 0.5f, curr, bT * 2f) : Vector2.Lerp(curr, (curr + next) * 0.5f, (bT - 0.5f) * 2f);
        if (dir.sqrMagnitude < 0.00001f) return bE;
        dir.Normalize();
        return Mathf.Abs(Vector2.Dot(q - bPt, new Vector2(-dir.y, dir.x)));
    }

    static float ClosestPointOnSplineXZ(float wx, float wz, List<Vector3> pts, float[] smoothH,
        out float interpHeight, out float signedCrossTrack)
    {
        float minD = float.MaxValue; interpHeight = 0f; signedCrossTrack = 0f;
        Vector2 q = new Vector2(wx, wz);
        int bSeg = 0; float bT = 0f; Vector2 bPt = Vector2.zero;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector2 a = new Vector2(pts[i].x, pts[i].z), b = new Vector2(pts[i + 1].x, pts[i + 1].z);
            float t; float d = ClosestPointOnSegment2D(q, a, b, out t);
            if (d < minD) { minD = d; bSeg = i; bT = t; bPt = a + (b - a) * t; interpHeight = Mathf.Lerp(smoothH[i], smoothH[i + 1], t); }
        }

        // Smooth the anchor point across segment boundaries only.
        // Blend toward the adjacent segment within blendZone of each endpoint.
        // Height is also blended so the centreline profile is continuous.
        // The hard minimum minD is still returned so zone boundary tests
        // receive true Euclidean distances — only the anchor XZ and height
        // are smoothed, not the distance used for culling.
        const float blendZone = 0.25f;
        if (bT < blendZone && bSeg > 0)
        {
            Vector2 a2 = new Vector2(pts[bSeg - 1].x, pts[bSeg - 1].z);
            Vector2 b2 = new Vector2(pts[bSeg].x, pts[bSeg].z);
            float t2; ClosestPointOnSegment2D(q, a2, b2, out t2);
            Vector2 altPt = a2 + (b2 - a2) * t2;
            float altH = Mathf.Lerp(smoothH[bSeg - 1], smoothH[bSeg], t2);
            float w = (1f - bT / blendZone) * 0.5f;
            bPt = Vector2.Lerp(bPt, altPt, w);
            interpHeight = Mathf.Lerp(interpHeight, altH, w);
        }
        else if (bT > 1f - blendZone && bSeg < pts.Count - 2)
        {
            Vector2 a2 = new Vector2(pts[bSeg + 1].x, pts[bSeg + 1].z);
            Vector2 b2 = new Vector2(pts[bSeg + 2].x, pts[bSeg + 2].z);
            float t2; ClosestPointOnSegment2D(q, a2, b2, out t2);
            Vector2 altPt = a2 + (b2 - a2) * t2;
            float altH = Mathf.Lerp(smoothH[bSeg + 1], smoothH[bSeg + 2], t2);
            float w = ((bT - (1f - blendZone)) / blendZone) * 0.5f;
            bPt = Vector2.Lerp(bPt, altPt, w);
            interpHeight = Mathf.Lerp(interpHeight, altH, w);
        }

        Vector2 curr = SegDir2D(pts, bSeg);
        Vector2 prev = bSeg > 0 ? SegDir2D(pts, bSeg - 1) : curr;
        Vector2 next = bSeg < pts.Count - 2 ? SegDir2D(pts, bSeg + 1) : curr;
        Vector2 dir = bT < 0.5f ? Vector2.Lerp((prev + curr) * 0.5f, curr, bT * 2f) : Vector2.Lerp(curr, (curr + next) * 0.5f, (bT - 0.5f) * 2f);
        if (dir.sqrMagnitude > 0.00001f) { dir.Normalize(); signedCrossTrack = Vector2.Dot(q - bPt, new Vector2(-dir.y, dir.x)); }
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
    //  Velocity Marker Baking
    //  Runs during GenerateRoad() and stores marker positions
    //  and speed values so VelocityOverlay can read them at
    //  runtime without any editor dependency.
    // ─────────────────────────────────────────────────────────
    void BakeVelocityMarkers()
    {
        if (_cachedSplinePoints == null || _cachedSplinePoints.Count < 2) return;

        List<Vector3> velPts = _cachedSplinePoints;
        List<Vector3> positions = new List<Vector3>();
        List<float> kmhValues = new List<float>();
        List<float> velScales = new List<float>();
        List<int> markerTypes = new List<int>();

        float velCurrent = 25f;
        float arcSinceLastMarker = 0f;
        float arcSinceLastFlat = 0f;
        bool firstMarker = true;
        int lastBand = -1;
        float sustainedArc = 0f;
        bool sustainedActive = false;
        bool lastWasUphill = false;
        const float flatInterval = 200f;

        Terrain activeTerrain = Terrain.activeTerrain;

        for (int i = 1; i < velPts.Count; i++)
        {
            float hd = HorizDist(velPts[i - 1], velPts[i]);
            if (hd < 0.001f) continue;

            float arcStep = Vector3.Distance(velPts[i - 1], velPts[i]);
            arcSinceLastMarker += arcStep;
            arcSinceLastFlat += arcStep;

            float dh = velPts[i].y - velPts[i - 1].y;
            float gradeAngle = Mathf.Atan2(dh, hd) * Mathf.Rad2Deg;

            float velTargetKmh = gradeAngle >= 0f
                ? 25f
                : Mathf.Lerp(25f, 120f,
                  Mathf.Clamp01(Mathf.Abs(gradeAngle) / 60f));

            float momentum = Mathf.Clamp01(arcStep * velocityMomentumFactor);
            velCurrent = Mathf.Clamp(
                Mathf.Lerp(velCurrent, velTargetKmh, momentum), 25f, 120f);

            float velScale = Mathf.Clamp01((velCurrent - 25f) / 95f) * 100f;
            int curBand = Mathf.FloorToInt(velScale / velocityMarkerThreshold);

            // ── Sustained steep tracking ──────────────────────
            if (enableAdvancedVelocityMarkers && markerTypeSustained)
            {
                if (velScale >= sustainedSteepThreshold)
                {
                    sustainedArc += arcStep;
                    sustainedActive = true;
                }
                else
                {
                    if (sustainedActive && sustainedArc >= sustainedSteepMinDistance)
                    {
                        // Place sustained marker at end of sustained zone
                        positions.Add(velPts[i] + Vector3.up * 2f);
                        kmhValues.Add(velCurrent);
                        velScales.Add(velScale);
                        markerTypes.Add(5);
                        arcSinceLastMarker = 0f;
                        lastBand = curBand;
                    }
                    sustainedArc = 0f;
                    sustainedActive = false;
                }
            }

            // ── Crest detection ───────────────────────────────
            bool placeAsCrest = false;
            if (enableAdvancedVelocityMarkers && markerTypeCrest)
            {
                bool currentUphill = gradeAngle > 0f;
                if (lastWasUphill && !currentUphill && i > 1)
                    placeAsCrest = true;
                lastWasUphill = currentUphill;
            }

            // ── Tight curve detection ─────────────────────────
            bool placeAsCurve = false;
            if (enableAdvancedVelocityMarkers && markerTypeCurve && i < velPts.Count - 1)
            {
                Vector2 dirA = new Vector2(
                    velPts[i].x - velPts[i - 1].x,
                    velPts[i].z - velPts[i - 1].z).normalized;
                Vector2 dirB = new Vector2(
                    velPts[i + 1].x - velPts[i].x,
                    velPts[i + 1].z - velPts[i].z).normalized;
                float dot = Vector2.Dot(dirA, dirB);
                if (dot < curveTightnessDotThreshold && dot > -1f)
                    placeAsCurve = true;
            }

            // ── Cross-slope transition detection ──────────────
            bool placeAsCrossSlope = false;
            if (enableAdvancedVelocityMarkers && markerTypeCrossSlope
                && activeTerrain != null)
            {
                Vector3 fwd = GetForward(velPts, i);
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                float sampleDist = roadWidth * 0.5f + 1f;

                Vector3 leftPt = velPts[i] - right * sampleDist;
                Vector3 rightPt = velPts[i] + right * sampleDist;
                float leftH = SampleTerrainHeight(leftPt, activeTerrain);
                float rightH = SampleTerrainHeight(rightPt, activeTerrain);
                float crossH = Mathf.Abs(leftH - rightH);

                if (crossH >= crossSlopeThreshold && i > 1)
                {
                    // Compare to previous point cross-slope
                    Vector3 fwdP = GetForward(velPts, i - 1);
                    Vector3 rightP = Vector3.Cross(Vector3.up, fwdP).normalized;
                    Vector3 leftPtP = velPts[i - 1] - rightP * sampleDist;
                    Vector3 rightPtP = velPts[i - 1] + rightP * sampleDist;
                    float leftHP = SampleTerrainHeight(leftPtP, activeTerrain);
                    float rightHP = SampleTerrainHeight(rightPtP, activeTerrain);
                    float crossHP = Mathf.Abs(leftHP - rightHP);
                    if (Mathf.Abs(crossH - crossHP) >= crossSlopeThreshold * 0.5f)
                        placeAsCrossSlope = true;
                }
            }

            // ── Grade or band change ──────────────────────────
            bool placeByBand = (!enableAdvancedVelocityMarkers || markerTypeGrade)
                                   && curBand != lastBand;
            bool placeByDistance = (!enableAdvancedVelocityMarkers || markerTypeFlat)
                                   && velCurrent <= 25.5f
                                   && arcSinceLastFlat >= flatInterval;
            bool placeFirst = firstMarker;

            // ── Place marker ──────────────────────────────────
            int typeToPlace = -1;

            if (placeAsCrest)
                typeToPlace = 1;
            else if (placeAsCurve && enableAdvancedVelocityMarkers)
                typeToPlace = 4;
            else if (placeAsCrossSlope && enableAdvancedVelocityMarkers)
                typeToPlace = 3;
            else if (placeByBand)
                typeToPlace = 0;
            else if (placeByDistance)
                typeToPlace = 2;
            else if (placeFirst)
                typeToPlace = 0;

            if (typeToPlace < 0)
            {
                continue;
            }

            firstMarker = false;
            lastBand = curBand;
            arcSinceLastMarker = 0f;
            if (typeToPlace == 2) arcSinceLastFlat = 0f;

            positions.Add(velPts[i] + Vector3.up * 2f);
            kmhValues.Add(velCurrent);
            velScales.Add(velScale);
            markerTypes.Add(typeToPlace);

        }

        bakedMarkerPositions = positions.ToArray();
        bakedMarkerKmh = kmhValues.ToArray();
        bakedMarkerVelScale = velScales.ToArray();
        bakedMarkerType = markerTypes.ToArray();

        Debug.Log("[RoadPathGenerator] Baked " +
                  bakedMarkerPositions.Length + " velocity markers.");
    }

    // ─────────────────────────────────────────────────────────
    //  Rock Scatter
    //  Places prefabs from rockScatterPrefabs in the shoulder
    //  zone between ditch outer toe and berm start.
    //  Editor only — rocks are real scene GameObjects that
    //  persist into the exported build via scene save.
    //
    //  Uses a grid-based placement approach for performance:
    //  zone divided into cells sized to rockScatterMinSpacing,
    //  one attempt per cell with random offset, overlap checked
    //  against neighbouring cells only — no global search.
    // ─────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void ScatterRocks()
    {
        if (rockScatterPrefabs == null || rockScatterPrefabs.Count == 0) return;
        if (_distField == null) return;
        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null) return;

        // ── Zone boundaries ───────────────────────────────────
        float halfRoad      = roadWidth * 0.5f;
        float ditchFloorEnd = halfRoad + ditchOffset
                              + ditchInnerWidth + ditchBottomWidth;
        float ditchOuterToe = ditchFloorEnd + ditchOuterWidth;

        float zoneInner = generateDitch
            ? ditchOuterToe
            : halfRoad + shoulderSmoothDistance;

        // Berm start — same calculation as ApplyBermToTerrain
        float shoulderOuter = generateDitch
            ? ditchFloorEnd + shoulderSmoothDistance
            : halfRoad + shoulderSmoothDistance;

        float zoneOuter = generateBerm
            ? (generateDitch && smoothShoulder ? shoulderOuter
               : generateDitch                 ? ditchFloorEnd
               : smoothShoulder                ? shoulderOuter
               : halfRoad)
            : zoneInner + shoulderSmoothDistance;

        if (zoneOuter <= zoneInner) return;

        // ── Endpoint tangents for end-cap rejection ───────────
        List<Vector3> pts = _cachedSplinePoints;
        if (pts == null || pts.Count < 2) return;
        Vector2 startTangent = new Vector2(
            pts[1].x - pts[0].x, pts[1].z - pts[0].z).normalized;
        Vector2 startOrigin  = new Vector2(pts[0].x, pts[0].z);
        int     lastIdx      = pts.Count - 1;
        Vector2 endTangent   = new Vector2(
            pts[lastIdx].x - pts[lastIdx-1].x,
            pts[lastIdx].z - pts[lastIdx-1].z).normalized;
        Vector2 endOrigin    = new Vector2(pts[lastIdx].x, pts[lastIdx].z);

        // ── Create or clear container ─────────────────────────
        Transform container = transform.Find("RockScatter");
        if (container == null)
        {
            GameObject go = new GameObject("RockScatter");
            go.transform.SetParent(transform, false);
            container = go.transform;
        }

        // ── Grid setup ────────────────────────────────────────
        float cellSize = rockScatterMinSpacing;
        System.Random rng = new System.Random(rockScatterSeed);

        TerrainData td   = terrain.terrainData;
        float tW         = td.size.x, tH = td.size.z;
        Vector3 tPos     = terrain.transform.position;
        int hw           = td.heightmapResolution;

        // Build candidate cells from distance field pixels
        // that fall within the shoulder zone
        Dictionary<int, Vector3> placedCells =
            new Dictionary<int, Vector3>();

        for (int lz = 0; lz < _distField.subH; lz++)
        for (int lx = 0; lx < _distField.subW; lx++)
        {
            int   idx = _distField.Idx(lz, lx);
            float cd  = _distField.crossDist[idx];
            if (cd < zoneInner || cd > zoneOuter) continue;

            float wx = tPos.x + ((_distField.pxMin+lx)/(float)(hw-1))*tW;
            float wz = tPos.z + ((_distField.pzMin+lz)/(float)(hw-1))*tH;

            // End-cap rejection
            Vector2 pq = new Vector2(wx, wz);
            if (Vector2.Dot(pq - startOrigin, startTangent) < 0f) continue;
            if (Vector2.Dot(pq - endOrigin,   endTangent)   > 0f) continue;

            // Density check — skip cell based on density setting
            if (rng.NextDouble() > rockScatterDensity * cellSize * cellSize)
                continue;

            // Add random offset within cell
            float ox = (float)(rng.NextDouble() - 0.5) * cellSize;
            float oz = (float)(rng.NextDouble() - 0.5) * cellSize;
            float px = wx + ox;
            float pz = wz + oz;

            // Grid cell key for overlap check
            int cx = Mathf.FloorToInt(px / cellSize);
            int cz = Mathf.FloorToInt(pz / cellSize);
            int key = cx * 100000 + cz;

            // Check neighbouring cells for minimum spacing
            bool tooClose = false;
            for (int nx = cx-1; nx <= cx+1 && !tooClose; nx++)
            for (int nz = cz-1; nz <= cz+1 && !tooClose; nz++)
            {
                int nkey = nx * 100000 + nz;
                if (placedCells.ContainsKey(nkey))
                {
                    Vector3 other = placedCells[nkey];
                    float dx = other.x - px;
                    float dz2 = other.z - pz;
                    float minSpacingSq = rockScatterMinSpacing * rockScatterMinSpacing;
                    if (dx*dx + dz2*dz2 < minSpacingSq)
                        tooClose = true;
                }
            }
            if (tooClose) continue;

            // ── Place rock ────────────────────────────────────
            float py = SampleTerrainHeight(
                new Vector3(px, 0f, pz), terrain);

            // Pick random prefab
            int prefabIdx = rng.Next(0, rockScatterPrefabs.Count);
            RockScatterEntry entry = rockScatterPrefabs[prefabIdx];
            if (entry == null || entry.prefab == null) continue;

            // Random scale
            float scale = Mathf.Lerp(rockScatterMinScale,
                rockScatterMaxScale, (float)rng.NextDouble());

            // Random Y rotation
            float rotY = Mathf.Lerp(rockScatterMinRotY,
                rockScatterMaxRotY, (float)rng.NextDouble());

            // Random XZ tilt
            float tiltX = (float)(rng.NextDouble() - 0.5) * 2f
                          * rockScatterMaxTilt;
            float tiltZ = (float)(rng.NextDouble() - 0.5) * 2f
                          * rockScatterMaxTilt;

            Quaternion rot = Quaternion.Euler(tiltX, rotY, tiltZ);

            // Align to slope if enabled
            if (rockScatterAlignToSlope)
            {
                Vector3 normal = terrain.terrainData
                    .GetInterpolatedNormal(
                        (px - tPos.x) / tW,
                        (pz - tPos.z) / tH);
                Quaternion slopeRot = Quaternion.FromToRotation(
                    Vector3.up, normal);
                rot = slopeRot * Quaternion.Euler(
                    tiltX, rotY, tiltZ);
            }

            GameObject rock = (GameObject)
                UnityEditor.PrefabUtility.InstantiatePrefab(
                    entry.prefab);
            rock.transform.SetParent(container, true);
            rock.transform.position   =
                new Vector3(px, py, pz);
            rock.transform.rotation   = rot;
            rock.transform.localScale =
                Vector3.one * scale;

            // Add mesh collider if requested
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
#endif

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
        float ditchFloorEndG = halfRoad + ditchOffset + ditchInnerWidth + ditchBottomWidth;
        float shoulderOuterG = generateDitch
            ? ditchFloorEndG + shoulderSmoothDistance
            : halfRoad + shoulderSmoothDistance;
        float bermStartG = (generateBerm)
            ? (generateDitch && smoothShoulder ? shoulderOuterG
               : generateDitch ? ditchFloorEndG
               : smoothShoulder ? shoulderOuterG
               : halfRoad)
            : halfRoad;
        float bermOuterEdge = bermStartG + bermSlopeDistance;
        float bermToeEdge = bermOuterEdge + bermOuterFalloff;
        float ditchInnerTop = halfRoad + ditchOffset;
        float ditchFloorS = ditchInnerTop + ditchInnerWidth;
        float ditchFloorE = ditchFloorS + ditchBottomWidth;
        float ditchOuterToe = ditchFloorE + ditchOuterWidth;
        float ditchFloorEndGizmo = halfRoad + ditchOffset + ditchInnerWidth + ditchBottomWidth;
        float shoulderOuter = generateDitch
            ? ditchFloorEndGizmo + shoulderSmoothDistance
            : halfRoad + shoulderSmoothDistance;
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

        // ── Velocity Preview ──────────────────────────────────
        // Velocity model:
        //   0         = rider at 25 km/h baseline or slower (flat/uphill)
        //   1 to 100  = rider faster than baseline due to downhill grade
        //   100       = rider at approximately 120 km/h (maximum)
        //
        // Speed reference points:
        //   0   = 25 km/h  (flat terrain baseline)
        //   25  = ~53 km/h (gentle descent)
        //   50  = ~73 km/h (moderate descent)
        //   75  = ~96 km/h (steep descent)
        //   100 = ~120 km/h (maximum — very steep grade)
        //
        // Momentum carries accumulated speed between sections so a
        // descent followed by flat correctly shows carried velocity
        // decaying back toward 0 over distance — critical for
        // understanding jump takeoff speeds once jumps are added.
        //
        // Marker placement rules:
        //   - Every time velocity crosses a 5-point band boundary
        //   - Every 200m arc length when velocity is at 0 (flat checkpoint)
        if (!showVelocityPreview) return;

        List<Vector3> velPts = null;
        if (_cachedSplinePoints != null && _cachedSplinePoints.Count > 1)
        {
            velPts = _cachedSplinePoints;
        }
        else
        {
            velPts = new List<Vector3>();
            int last2 = cp.Count - 1;
            for (int i = 0; i < last2; i++)
            {
                Vector3 p0v = cp[Mathf.Max(0, i - 1)];
                Vector3 p1v = cp[i];
                Vector3 p2v = cp[Mathf.Min(cp.Count - 1, i + 1)];
                Vector3 p3v = cp[Mathf.Min(cp.Count - 1, i + 2)];
                for (int s = 0; s < segmentsPerCurve; s++)
                    velPts.Add(CatmullRom(p0v, p1v, p2v, p3v,
                               s / (float)segmentsPerCurve));
            }
            velPts.Add(cp[cp.Count - 1]);
        }
        if (velPts == null || velPts.Count < 2) return;

        float velCurrent = 0f;
        float arcSinceLastMarker = 0f;
        bool firstMarker = true;
        // Threshold band tracker — advances in 5-point steps so every
        // 5-point boundary crossing places a marker regardless of how
        // large the velocity jump is in a single spline step.
        int lastBand = -1;
        velCurrent = 25f;
        const float flatInterval = 200f;

        for (int i = 1; i < velPts.Count; i++)
        {
            float hd = HorizDist(velPts[i - 1], velPts[i]);
            if (hd < 0.001f) continue;

            float arcStep = Vector3.Distance(velPts[i - 1], velPts[i]);
            arcSinceLastMarker += arcStep;

            float dh = velPts[i].y - velPts[i - 1].y;
            float gradeAngle = Mathf.Atan2(dh, hd) * Mathf.Rad2Deg;

            // Velocity target in km/h.
            // Flat or uphill — clamp to baseline 25 km/h.
            // Downhill — scales from 25 km/h at 0 degrees
            // to 120 km/h at 60 degrees.
            float velTargetKmh;
            if (gradeAngle >= 0f)
                velTargetKmh = 25f;
            else
            {
                float t = Mathf.Clamp01(Mathf.Abs(gradeAngle) / 60f);
                velTargetKmh = Mathf.Lerp(25f, 120f, t);
            }

            float momentum = Mathf.Clamp01(arcStep * velocityMomentumFactor);
            velCurrent = Mathf.Clamp(
                Mathf.Lerp(velCurrent, velTargetKmh, momentum), 25f, 120f);

            // Convert km/h to 0-100 scale for band tracking.
            // 25 km/h = 0, 120 km/h = 100.
            float velScale = Mathf.Clamp01((velCurrent - 25f) / 95f) * 100f;

            // Current 5-point band on the 0-100 scale
            int curBand = Mathf.FloorToInt(velScale / velocityMarkerThreshold);

            bool placeByBand = curBand != lastBand;
            bool placeByDistance = velCurrent <= 25.5f &&
                                   arcSinceLastMarker >= flatInterval;
            bool placeFirst = firstMarker;

            if (!placeByBand && !placeByDistance && !placeFirst) continue;

            firstMarker = false;
            lastBand = curBand;
            arcSinceLastMarker = 0f;

            // Colour by marker type when advanced markers enabled,
            // otherwise use standard grade gradient.
            Color markerColor;
            if (enableAdvancedVelocityMarkers)
            {
                // Type colours shown in gizmos match VelocityOverlay panel
                // 0=grade gradient, 1=purple crest, 2=grey flat,
                // 3=orange cross-slope, 4=cyan curve, 5=bright red sustained
                markerColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            }
            else if (velCurrent <= 25.5f)
                markerColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            else if (velScale <= 50f)
                markerColor = Color.Lerp(Color.green,
                              new Color(1f, 1f, 0f, 1f), velScale / 50f);
            else
                markerColor = Color.Lerp(new Color(1f, 1f, 0f, 1f),
                              Color.red, (velScale - 50f) / 50f);
            // Override colour for advanced marker types
            // (gizmo system does not have type array so use grade angle
            //  to approximate type colour for the gizmo preview)
            if (enableAdvancedVelocityMarkers)
            {
                bool isUphill = gradeAngle > 0f;
                bool wasPrevDown = i > 1 &&
                    (velPts[i - 1].y - velPts[i - 2 < 0 ? 0 : i - 2].y) < 0f;

                if (isUphill && wasPrevDown)
                    markerColor = new Color(0.6f, 0.2f, 1.0f, 1f); // purple crest
                else if (velCurrent <= 25.5f)
                    markerColor = new Color(0.55f, 0.55f, 0.55f, 1f); // grey flat
                else if (velScale >= sustainedSteepThreshold)
                    markerColor = new Color(1f, 0.1f, 0.1f, 1f); // bright red sustained
                else if (velScale <= 50f)
                    markerColor = Color.Lerp(Color.green,
                                  new Color(1f, 1f, 0f, 1f), velScale / 50f);
                else
                    markerColor = Color.Lerp(new Color(1f, 1f, 0f, 1f),
                                  Color.red, (velScale - 50f) / 50f);
            }

            Gizmos.color = markerColor;
            Vector3 markerPos = velPts[i] + Vector3.up * 2f;
            Gizmos.DrawSphere(markerPos, 0.6f);

            Gizmos.color = new Color(markerColor.r, markerColor.g,
                                     markerColor.b, 0.25f);
            float ringSize = Mathf.Lerp(0.8f, 3.5f, velScale / 100f);
            Gizmos.DrawWireSphere(markerPos, ringSize);

#if UNITY_EDITOR
            string label = Mathf.RoundToInt(velCurrent) + " km/h";
            if (placeByDistance && !placeByBand)
                label += " (200m)";
            UnityEditor.Handles.color = markerColor;
            UnityEditor.Handles.Label(markerPos + Vector3.up * 1.5f, label);
#endif
        }
    }
}

