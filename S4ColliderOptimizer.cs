// s4extract collider optimizer.  Replaces simple convex MeshColliders on a
// Sims-4-exported prefab with oriented Box / Capsule / Sphere colliders and
// merges aligned boxes.  Two entry points:
//
//   * Manual: Window → Tools/S4 Extract/Collider Optimizer → "Optimize selected".
//   * Batch : S4ColliderOptimizer.OptimizeForBatch(root, ...)
//             Called automatically by S4ExtractBatchFixer when building the
//             _READY prefab.
//
// Capsules cover long rounded parts (legs, posts); spheres cover knobs and
// small rounded caps that neither a box nor a capsule fills well; boxes cover
// everything roughly prismatic.  Colliders whose mesh name contains
// "parametric_keep" (lathe/hollow parts from the exporter) are left as
// MeshColliders intentionally.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;

public sealed class S4ColliderOptimizer : EditorWindow
{
    const string GeneratedRootName = "__S4_OptimizedColliders";
    const string DisabledMarkerPrefix = "__S4_DisabledMeshCollider_";

    [SerializeField, Range(0.10f, 0.99f)] float minBoxFill = 0.50f;
    [SerializeField, Range(0.10f, 0.99f)] float minCapsuleFill = 0.30f;
    [SerializeField, Range(0.10f, 0.99f)] float minSphereFill  = 0.55f;
    [SerializeField, Range(0.10f, 0.99f)] float minPrimitiveFill = 0.65f;
    [SerializeField, Range(1.2f, 8f)]   float minCapsuleElongation = 2.2f;
    [SerializeField, Range(1f, 3f)]     float maxCapsuleRadialAspect = 1.40f;
    [SerializeField] bool preferSmoothCapsules = true;
    [SerializeField] bool preferSpheres = true;
    [SerializeField] bool preferParametricPrimitives = true;
    [SerializeField, Range(0f, 0.25f)]  float maxMergeInflation = 0.04f;
    [SerializeField] float contactEpsilon = 0.003f;
    [SerializeField, Range(0f, 20f)]    float maxAxisAngle = 6f;
    [SerializeField] bool includeInactive = false;
    [SerializeField] bool logFitDetails = true;
    [SerializeField] bool writeFitDiagnostics = true;
    // Batch mode may use a deliberately conservative last-resort box for a
    // convex hull that is too irregular for the normal fit thresholds.  The
    // interactive tool remains strict, so a bad automatic box is never
    // silently created when optimizing a selected object.
    bool allowLowFillFallback;
    bool allowRelaxedBatchPrimitives;
    const float LowFillFallback = 0.18f;

    [MenuItem("Tools/S4 Extract/Collider Optimizer")]
    static void Open() => GetWindow<S4ColliderOptimizer>("S4 Collider Optimizer");

    /// <summary>Automatic, destructive mode used by Fix All Exports.
    /// Replaced MeshColliders are removed because the READY prefab can always
    /// be regenerated from export data.</summary>
    public static string OptimizeForBatch(GameObject root,
        float minimumBoxFill = 0.50f, float mergeEmptyVolume = 0.04f,
        float gapEpsilon = 0.003f, float axisAngle = 6f)
    {
        if (root == null) return "[S4 collider optimizer] null root";
        var worker = CreateInstance<S4ColliderOptimizer>();
        try
        {
            // Start with a conservative furniture-friendly threshold; if it
            // produces nothing, walk the fill down so that curved/cylinder-ish
            // Sims pieces still get simplified rather than left as expensive
            // convex MeshColliders.
            // Do not progressively lower this threshold in batch mode.  A low
            // fill box is especially harmful on curved chair backs: it creates
            // flat shelves and gaps where a ball can settle.  Such hulls are
            // intentionally left as their original convex MeshCollider.
            float[] fillTries = new[] { minimumBoxFill };
            float usedFill = minimumBoxFill;
            string lastResult = null;
            foreach (float f in fillTries)
            {
                worker.minBoxFill = f;
                // FIXED: более строгие пороги для капсул чтобы не создавать 3 лишних
                worker.minCapsuleFill = Mathf.Max(0.35f, f + 0.05f);
                worker.minSphereFill  = Mathf.Min(0.60f, f + 0.10f);
                worker.minPrimitiveFill = Mathf.Min(0.65f, f + 0.15f);
                worker.minCapsuleElongation = 2.5f;
                worker.maxCapsuleRadialAspect = 1.35f;
                worker.preferSmoothCapsules = true;
                worker.preferSpheres = true;
                worker.preferParametricPrimitives = true;
                worker.maxMergeInflation = mergeEmptyVolume;
                worker.contactEpsilon = gapEpsilon;
                worker.maxAxisAngle = axisAngle;
                worker.includeInactive = true;
                worker.writeFitDiagnostics = true;
                // Keep irregular/curved hulls as convex MeshColliders instead
                // of forcing a flat fallback box.
                worker.allowLowFillFallback = false;
                // FIXED: отключаем слабые капсулы в batch режиме - они дают 3 лишних
                worker.allowRelaxedBatchPrimitives = false;
                lastResult = worker.OptimizeRoot(root, false);
                // OptimizeRoot returns a sentence like "... replaced N by B boxes ...".
                // If it produced any replacements we are done.
                if (lastResult.Contains("replaced 0 ") && lastResult.Contains("kept."))
                {
                    RestoreInternal(root, false);
                    continue;
                }
                usedFill = f;
                break;
            }

            // Strip disabled MeshColliders + their marker objects (READY prefabs
            // are reproducible, so keeping dead components just wastes size).
            int removedDisabled = 0;
            foreach (MeshCollider mc in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc == null) continue;
                if (!mc.enabled)
                {
                    DestroyImmediate(mc);
                    removedDisabled++;
                }
            }
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)
                         .Where(t => t != null && t != root.transform &&
                                     t.name.StartsWith(DisabledMarkerPrefix, StringComparison.Ordinal))
                         .ToArray())
            {
                if (t != null) DestroyImmediate(t.gameObject);
            }

            // === ДОБАВЬ ЭТОТ БЛОК ПОСЛЕ УДАЛЕНИЯ ДИСЕЙБЛЕННЫХ МЕШКОЛЛАЙДЕРОВ ===

            // Второй проход: валидация созданных коллайдеров против вершин визуальных мешей
            var validationSettings = new ColliderValidationSettings
            {
                minVertexCoverage = 0.15f,      // Удалить, если < 15% вершин внутри
                maxEmptyVolumeRatio = 0.85f,    // Удалить, если > 85% пустого объёма
                shrinkMargin = 0.02f,           // Запас 2 см при сужении
                enableShrinking = true,
                enableRemoval = true,
                logDetails = logFitDetails       // Используй существующее поле логирования
            };

            int validationChanges = ValidateAndTightenColliders(root, validationSettings);
            if (validationChanges > 0 && logFitDetails)
            {
                Debug.Log($"[S4 collider optimizer] Validation pass: {validationChanges} colliders adjusted/removed.");
            }

            // === КОНЕЦ ДОБАВЛЕННОГО БЛОКА ===

            // Count survivors for a clearer log line.
            int remainingMeshes = root.GetComponentsInChildren<MeshCollider>(true).Length;
            int boxes = 0, capsules = 0, spheres = 0, primitives = 0;
            Transform gen = root.transform.Find(GeneratedRootName);
            if (gen != null)
            {
                boxes      = gen.GetComponentsInChildren<BoxCollider>(true).Length;
                capsules   = gen.GetComponentsInChildren<CapsuleCollider>(true).Length;
                spheres    = gen.GetComponentsInChildren<SphereCollider>(true).Length;
                // Parametric-primitive colliders are MeshColliders under __S4_OptimizedColliders
                // whose mesh was generated by S4PrimitiveMeshFactory.  Count them separately.
                foreach (MeshCollider mc in gen.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (mc == null || mc.sharedMesh == null) continue;
                    string n = mc.sharedMesh.name;
                    if (n.StartsWith("cyl_") || n.StartsWith("hemi") || n.StartsWith("cone") ||
                        n.StartsWith("tcony_") || n.StartsWith("sphere_"))
                        primitives++;
                    else if (n.StartsWith("s4_lowpoly_"))
                        primitives++;
                }
            }
            return $"{lastResult}  [batch: used boxFill={usedFill:F2}, removed {removedDisabled} disabled MeshCollider(s); " +
                   $"final counts: {boxes} box, {capsules} capsule, {spheres} sphere, {primitives} parametric-mesh; " +
                   $"{remainingMeshes} convex MeshCollider(s) kept.]";
        }
        finally { DestroyImmediate(worker); }
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Replaces simple convex MeshColliders with oriented boxes, capsules or spheres, " +
            "then merges aligned touching boxes. Originals are disabled, not deleted.",
            MessageType.Info);
        minBoxFill = EditorGUILayout.Slider(
            new GUIContent("Minimum box fill", "Mesh volume / fitted box volume. Curved Sims furniture often needs 0.40–0.60."),
            minBoxFill, 0.10f, 0.99f);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Safe 0.70")) minBoxFill = 0.70f;
        if (GUILayout.Button("Furniture 0.50")) minBoxFill = 0.50f;
        if (GUILayout.Button("Aggressive 0.35")) minBoxFill = 0.35f;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(3);
        preferSmoothCapsules = EditorGUILayout.Toggle(
            new GUIContent("Prefer smooth capsules", "Use capsules for long parts with a near-round cross-section."),
            preferSmoothCapsules);
        minCapsuleFill = EditorGUILayout.Slider("Minimum capsule fill", minCapsuleFill, 0.10f, 0.99f);
        minCapsuleElongation = EditorGUILayout.Slider("Capsule elongation", minCapsuleElongation, 1.2f, 8f);
        maxCapsuleRadialAspect = EditorGUILayout.Slider("Capsule radial aspect", maxCapsuleRadialAspect, 1f, 3f);
        preferSpheres = EditorGUILayout.Toggle(
            new GUIContent("Prefer spheres", "Use spheres for knobs / small rounded caps."), preferSpheres);
        minSphereFill = EditorGUILayout.Slider("Minimum sphere fill", minSphereFill, 0.10f, 0.99f);
        preferParametricPrimitives = EditorGUILayout.Toggle(
            new GUIContent("Parametric primitives (cyl/hemisphere/cone)",
                           "Use low-poly cylinders, hemispheres, cones/lampshades as MeshColliders " +
                           "for objects whose shape doesn't reduce well to a box, capsule or sphere."),
            preferParametricPrimitives);
        minPrimitiveFill = EditorGUILayout.Slider("Minimum primitive fill", minPrimitiveFill, 0.10f, 0.99f);
        maxMergeInflation = EditorGUILayout.Slider(
            new GUIContent("Merge empty volume", "Maximum empty fraction introduced by merging boxes."),
            maxMergeInflation, 0f, 0.25f);
        contactEpsilon = EditorGUILayout.FloatField(
            new GUIContent("Contact epsilon", "Allowed gap in root-local Unity units."),
            contactEpsilon);
        maxAxisAngle = EditorGUILayout.Slider("Maximum axis angle", maxAxisAngle, 0f, 20f);
        includeInactive = EditorGUILayout.Toggle("Include inactive", includeInactive);
        logFitDetails = EditorGUILayout.Toggle("Log per-collider fit details", logFitDetails);
        writeFitDiagnostics = EditorGUILayout.Toggle("Save fit diagnostics to file", writeFitDiagnostics);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
        {
            if (GUILayout.Button("Analyze selected", GUILayout.Height(28))) AnalyzeSelected();
            if (GUILayout.Button("Optimize selected (Undo supported)", GUILayout.Height(34))) OptimizeSelected();
            if (GUILayout.Button("Restore originals", GUILayout.Height(24))) RestoreSelected();
        }
        EditorGUILayout.HelpBox(
            "For fragmented/curved Sims furniture start with fill 0.50. Use 0.70 for straight parts " +
            "or 0.35 only after visual inspection. Merge empty volume 0.02–0.05 is conservative.",
            MessageType.None);
    }

    GameObject SelectedRoot()
    {
        GameObject root = Selection.activeGameObject;
        if (root == null) Debug.LogWarning("Select a prefab instance or object root first.");
        return root;
    }

    List<MeshCollider> SourceColliders(GameObject root, bool enabledOnly)
    {
        if (root == null) return new List<MeshCollider>();
        return root.GetComponentsInChildren<MeshCollider>(includeInactive)
            .Where(c => c != null && c.sharedMesh != null && c.convex &&
                        // Batch mode must consume every original convex hull;
                        // even meshes marked parametric_keep are handled by the
                        // primitive/segmented fallback instead of surviving.
                        c.transform.name != GeneratedRootName &&
                        (c.transform.parent == null || c.transform.parent.name != GeneratedRootName) &&
                        (!enabledOnly || c.enabled) &&
                        !IsUnderGeneratedRoot(c.transform, root.transform))
            .ToList();
    }

    static bool IsUnderGeneratedRoot(Transform t, Transform stop)
    {
        while (t != null && t != stop)
        {
            if (t.name == GeneratedRootName) return true;
            t = t.parent;
        }
        return false;
    }

    static bool IsPrefabAsset(GameObject go, out string path)
    {
        path = go != null ? AssetDatabase.GetAssetPath(go) : null;
        return go != null && !string.IsNullOrEmpty(path) &&
               PrefabUtility.IsPartOfPrefabAsset(go);
    }

    void AnalyzeSelected()
    {
        GameObject selected = SelectedRoot();
        if (selected == null) return;
        if (IsPrefabAsset(selected, out string path))
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try { AnalyzeRoot(contents, null); }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        else AnalyzeRoot(selected, selected);
    }

    void AnalyzeRoot(GameObject root, UnityEngine.Object logContext)
    {
        List<MeshCollider> colliders = SourceColliders(root, true);
        var fills = new List<float>();
        int capsuleCandidates = 0;
        int sphereCandidates = 0;
        int primitiveCandidates = 0;
        int skipped = 0;
        foreach (MeshCollider mc in colliders)
        {
            try
            {
                EnsureMeshReadable(mc);
                if (TryFitBox(root.transform, mc, out BoxFit fit)) fills.Add(fit.fill);
                if (preferSmoothCapsules && TryFitCapsule(root.transform, mc, out CapsuleFit capsule) &&
                    IsCapsuleCandidate(capsule)) capsuleCandidates++;
                if (preferSpheres && TryFitSphere(root.transform, mc, out SphereFit sphere) &&
                    IsSphereCandidate(sphere)) sphereCandidates++;
                if (preferParametricPrimitives)
                {
                    var prim = S4PrimitiveFitters.TryFitBestPrimitive(root.transform, mc, minPrimitiveFill);
                    if (prim != null) primitiveCandidates++;
                }
            }
            catch (Exception e)
            {
                skipped++;
                Debug.LogWarning($"[S4 collider optimizer] Skip {DescribeCollider(mc)}: {e.Message}");
            }
        }
        fills.Sort();
        int boxable = fills.Count(v => v >= minBoxFill);
        int at35 = fills.Count(v => v >= 0.35f);
        int at50 = fills.Count(v => v >= 0.50f);
        int at70 = fills.Count(v => v >= 0.70f);
        float best = fills.Count > 0 ? fills[fills.Count - 1] : 0f;
        float average = fills.Count > 0 ? fills.Average() : 0f;
        string message = $"[S4 collider optimizer] {root.name}: {colliders.Count} enabled convex " +
                         $"MeshColliders; {boxable} box-candidates at fill >= {minBoxFill:F2}. " +
                         $"Analyzed {fills.Count} (skipped {skipped}); average fill {average:F3}, best {best:F3}. " +
                         $"By preset: aggressive(0.35)={at35}, furniture(0.50)={at50}, safe(0.70)={at70}; " +
                         $"capsule candidates={capsuleCandidates}, sphere candidates={sphereCandidates}, " +
                         $"parametric candidates={primitiveCandidates}.";
        if (logContext != null) Debug.Log(message, logContext); else Debug.Log(message);
        ShowNotification(new GUIContent(
            $"boxes {boxable}, capsules {capsuleCandidates}, spheres {sphereCandidates}, prim {primitiveCandidates}; best box {best:F2}"));
    }

    void OptimizeSelected()
    {
        GameObject selected = SelectedRoot();
        if (selected == null) return;
        if (IsPrefabAsset(selected, out string path))
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                string result = OptimizeRoot(contents, false);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                AssetDatabase.SaveAssets();
                Debug.Log(result + $" Saved prefab asset: {path}");
                ShowNotification(new GUIContent("Prefab optimized and saved"));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            return;
        }
        string sceneResult = OptimizeRoot(selected, true);
        Debug.Log(sceneResult, selected);
        ShowNotification(new GUIContent("Selected object optimized"));
    }

    string OptimizeRoot(GameObject root, bool useUndo)
    {
        int undoGroup = -1;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Optimize S4 colliders");
        }

        RestoreInternal(root, useUndo);
        List<MeshCollider> originals = SourceColliders(root, true);
        var boxes = new List<BoxFit>();
        var capsules = new List<CapsuleFit>();
        var spheres = new List<SphereFit>();
        var primitives = new List<S4PrimitiveFitters.PrimitiveFit>();
        var lowPolyHulls = new List<LowPolyFit>();
        var disabled = new HashSet<MeshCollider>();
        var diagnosticRows = new List<string>();
        Bounds sourceBounds = CombinedSourceBounds(originals);

        // Pick best primitive per source hull, choosing whichever parametric shape
        // (sphere, capsule, parametric-mesh cylinder/hemisphere/cone, or box) yields
        // the highest fill ratio above its respective threshold.
        foreach (MeshCollider mc in originals)
        {
            try
            {
                EnsureMeshReadable(mc);

                float bestFill = -1f;
                object bestFit = null;
                string bestKind = null;
                bool capsulePreferred = false;

                SphereFit sphere = null;
                if (preferSpheres && TryFitSphere(root.transform, mc, out sphere) && IsSphereCandidate(sphere))
                {
                    if (sphere.fill > bestFill) { bestFill = sphere.fill; bestFit = sphere; bestKind = "sphere"; }
                }

                CapsuleFit capsule = null;
                if (preferSmoothCapsules && TryFitCapsule(root.transform, mc, out capsule))
                {
                    bool normalCapsule = IsCapsuleCandidate(capsule);
                    // Batch fallback: use one cheap capsule for moderately
                    // elongated rounded parts before creating a mesh. The
                    // stricter interactive thresholds are unchanged.
                    bool relaxedCapsule = allowRelaxedBatchPrimitives && capsule != null &&
                        capsule.fill >= 0.14f && capsule.elongation >= 1.20f &&
                        capsule.radialAspect <= 3.00f;
                    // Batch mode used to accept a very low-fill capsule even
                    // when its source hull spanned almost the whole object.
                    // That is the characteristic failure mode for separated
                    // supports/ornaments. Keep normal capsules untouched; only
                    // divert this broad, low-fill batch case to segmentation.
                    bool broadLowFillBatchCapsule =
                        allowRelaxedBatchPrimitives && relaxedCapsule &&
                        capsule.fill < 0.35f &&
                        MaxAxisCoverage(mc.bounds.size, sourceBounds.size) >= 0.75f;
                    if ((normalCapsule || relaxedCapsule) && !broadLowFillBatchCapsule)
                    {
                        if (capsule.fill > bestFill) { bestFill = capsule.fill; bestFit = capsule; bestKind = "capsule"; }
                        // A capsule is preferable to a box for a long/rounded
                        // part even when the box has a numerically higher fill.
                        capsulePreferred = relaxedCapsule || normalCapsule;
                    }
                    else if (broadLowFillBatchCapsule)
                    {
                        if (TryFitSegmentedBoxes(root.transform, mc, out List<BoxFit> segmented))
                        {
                            boxes.AddRange(segmented);
                            disabled.Add(mc);
                            bestFit = segmented[0];
                            bestKind = "segmented";
                        }
                    }
                }

                if (bestKind == "segmented")
                {
                    if (logFitDetails)
                        Debug.Log($"[S4 fit] {mc.name}: broad low-fill capsule diverted to segmented boxes", mc);
                    continue;
                }

                if (preferParametricPrimitives)
                {
                    S4PrimitiveFitters.PrimitiveFit prim =
                        S4PrimitiveFitters.TryFitBestPrimitive(root.transform, mc, minPrimitiveFill);
                    if (prim != null && prim.fill > bestFill)
                    {
                        bestFill = prim.fill; bestFit = prim; bestKind = "primitive";
                    }
                }

                BoxFit box = null;
                if (!capsulePreferred && TryFitBox(root.transform, mc, out box) &&
                    (box.fill >= minBoxFill || (allowLowFillFallback && box.fill >= LowFillFallback)))
                {
                    if (box.fill > bestFill) { bestFill = box.fill; bestFit = box; bestKind = "box"; }
                }

                if (bestFit == null)
                {
                    // Keep one continuous, generated low-poly hull for an
                    // arbitrary curved part. Unlike the old sliced-box
                    // fallback, this has no internal seams for a rolling ball.
                    if (TryFitLowPolyHull(root.transform, mc, out LowPolyFit lowPoly))
                    {
                        lowPolyHulls.Add(lowPoly);
                        disabled.Add(mc);
                    }
                    else if (TryFitBoundsBox(root.transform, mc, out BoxFit boundsBox))
                    {
                        boxes.Add(boundsBox);
                        disabled.Add(mc);
                    }
                    if (logFitDetails)
                    {
                        string meshName = mc.sharedMesh != null ? mc.sharedMesh.name : "<none>";
                        Debug.Log($"[S4 fit] {mc.name} / {meshName}: selected=fallback, " +
                                  "no primitive passed threshold; original hull replaced by fallback.", mc);
                    }
                    if (writeFitDiagnostics)
                    {
                        string meshName = mc.sharedMesh != null ? mc.sharedMesh.name : "<none>";
                        diagnosticRows.Add(FitDiagnosticRow(mc, meshName, "fallback", capsule, box, mc.bounds));
                    }
                    continue;
                }

                switch (bestKind)
                {
                    case "sphere":    spheres.Add((SphereFit)bestFit); break;
                    case "capsule":   capsules.Add((CapsuleFit)bestFit); break;
                    case "primitive": primitives.Add((S4PrimitiveFitters.PrimitiveFit)bestFit); break;
                    case "box":       boxes.Add((BoxFit)bestFit); break;
                }

                IEnumerable<MeshCollider> src;
                switch (bestKind)
                {
                    case "sphere":    src = ((SphereFit)bestFit).sources; break;
                    case "capsule":   src = ((CapsuleFit)bestFit).sources; break;
                    case "primitive": src = ((S4PrimitiveFitters.PrimitiveFit)bestFit).sources; break;
                    default:          src = ((BoxFit)bestFit).sources; break;
                }
                foreach (var s in src) disabled.Add(s);

                if (logFitDetails)
                {
                    string meshName = mc.sharedMesh != null ? mc.sharedMesh.name : "<none>";
                    Bounds b = mc.bounds;
                    string capsuleInfo = capsule != null
                        ? $"capsule(fill={capsule.fill:F3}, elong={capsule.elongation:F2}, radial={capsule.radialAspect:F2})"
                        : "capsule=none";
                    string boxInfo = box != null ? $"box(fill={box.fill:F3})" : "box=none";
                    Debug.Log($"[S4 fit] {mc.name} / {meshName}: selected={bestKind}, " +
                              $"{capsuleInfo}, {boxInfo}, bounds={b.size}", mc);
                    diagnosticRows.Add(FitDiagnosticRow(mc, meshName, bestKind, capsule, box, b));
                }
                else
                {
                    string meshName = mc.sharedMesh != null ? mc.sharedMesh.name : "<none>";
                    Bounds b = mc.bounds;
                    diagnosticRows.Add(FitDiagnosticRow(mc, meshName, bestKind, capsule, box, b));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[S4 collider optimizer] Skipping {DescribeCollider(mc)} during fit: {e.Message}");
            }
        }

        int beforeMerge = boxes.Count;
        boxes = MergeBoxes(boxes);

        // FIXED: пост-фильтр - удаляем капсулы с низким fill (мусор от кривых планок)
        // Это убирает 3 лишних capsule из chair
        capsules = new List<CapsuleFit>(capsules.Where(c => 
            c.fill >= Mathf.Max(minCapsuleFill, 0.30f) && 
            c.elongation >= Mathf.Max(minCapsuleElongation, 2.0f) && 
            c.radialAspect <= Mathf.Min(maxCapsuleRadialAspect, 1.35f)
        ));
        // Также удаляем сферы с низким fill
        spheres = new List<SphereFit>(spheres.Where(s => s.fill >= Mathf.Max(minSphereFill, 0.40f)));

        if (writeFitDiagnostics) WriteFitDiagnostics(root, diagnosticRows);

        if (boxes.Count == 0 && capsules.Count == 0 && spheres.Count == 0 && primitives.Count == 0 && lowPolyHulls.Count == 0)
        {
            if (useUndo) Undo.CollapseUndoOperations(undoGroup);
            return $"[S4 collider optimizer] {root.name}: no hull passed the box/sphere/capsule/primitive fill thresholds " +
                   $"({originals.Count} convex MeshColliders kept).";
        }

        var generated = new GameObject(GeneratedRootName);
        if (useUndo) Undo.RegisterCreatedObjectUndo(generated, "Create optimized collider root");
        generated.transform.SetParent(root.transform, false);

        for (int i = 0; i < boxes.Count; i++)
        {
            BoxFit fit = boxes[i];
            var child = new GameObject($"Box_{i:000}");
            if (useUndo) Undo.RegisterCreatedObjectUndo(child, "Create optimized box collider");
            child.transform.SetParent(generated.transform, false);
            child.transform.localPosition = fit.center;
            child.transform.localRotation = fit.rotation;
            child.transform.localScale = Vector3.one;
            BoxCollider box = useUndo ? Undo.AddComponent<BoxCollider>(child) : child.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = fit.size;
        }
        for (int i = 0; i < capsules.Count; i++)
        {
            CapsuleFit fit = capsules[i];
            var child = new GameObject($"Capsule_{i:000}");
            if (useUndo) Undo.RegisterCreatedObjectUndo(child, "Create optimized capsule collider");
            child.transform.SetParent(generated.transform, false);
            child.transform.localPosition = fit.center;
            child.transform.localRotation = fit.rotation;
            child.transform.localScale = Vector3.one;
            CapsuleCollider capsule = useUndo
                ? Undo.AddComponent<CapsuleCollider>(child)
                : child.AddComponent<CapsuleCollider>();
            capsule.center = Vector3.zero;
            capsule.direction = 1;
            capsule.radius = fit.radius;
            capsule.height = fit.height;
        }
        for (int i = 0; i < spheres.Count; i++)
        {
            SphereFit fit = spheres[i];
            var child = new GameObject($"Sphere_{i:000}");
            if (useUndo) Undo.RegisterCreatedObjectUndo(child, "Create optimized sphere collider");
            child.transform.SetParent(generated.transform, false);
            child.transform.localPosition = fit.center;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            SphereCollider sphere = useUndo
                ? Undo.AddComponent<SphereCollider>(child)
                : child.AddComponent<SphereCollider>();
            sphere.center = Vector3.zero;
            sphere.radius = fit.radius;
        }
        for (int i = 0; i < lowPolyHulls.Count; i++)
        {
            LowPolyFit fit = lowPolyHulls[i];
            var child = new GameObject($"LowPolyHull_{i:000}");
            child.transform.SetParent(generated.transform, false);
            child.transform.localPosition = fit.center;
            child.transform.localRotation = Quaternion.identity;
            child.transform.localScale = Vector3.one;
            MeshCollider hull = useUndo ? Undo.AddComponent<MeshCollider>(child) : child.AddComponent<MeshCollider>();
            hull.sharedMesh = fit.mesh;
            hull.convex = true;
        }
        for (int i = 0; i < primitives.Count; i++)
        {
            var fit = primitives[i];
            string label = fit.shape.ToString();
            var child = new GameObject($"{label}_{i:000}");
            if (useUndo) Undo.RegisterCreatedObjectUndo(child, "Create optimized parametric collider");
            child.transform.SetParent(generated.transform, false);
            child.transform.localPosition = fit.center;
            child.transform.localRotation = fit.rotation;
            child.transform.localScale = fit.scale;
            MeshCollider mc = useUndo ? Undo.AddComponent<MeshCollider>(child) : child.AddComponent<MeshCollider>();
            mc.sharedMesh = fit.mesh;
            mc.convex = true;
        }

        // Disable the original mesh colliders and leave markers so Restore can bring them back.
        foreach (MeshCollider source in disabled)
        {
            if (source == null) continue;
            if (useUndo) Undo.RecordObject(source, "Disable replaced mesh collider");
            source.enabled = false;
            EditorUtility.SetDirty(source);
            MeshCollider[] siblings = source.GetComponents<MeshCollider>();
            int componentIndex = Array.IndexOf(siblings, source);
            var marker = new GameObject(DisabledMarkerPrefix + componentIndex);
            marker.tag = "EditorOnly";
            if (useUndo) Undo.RegisterCreatedObjectUndo(marker, "Mark replaced mesh collider");
            marker.transform.SetParent(source.transform, false);
        }

        EditorUtility.SetDirty(root);
        if (useUndo)
        {
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = generated;
        }
        return $"[S4 collider optimizer] {root.name}: replaced {disabled.Count} convex " +
               $"MeshColliders by {boxes.Count} BoxColliders, {capsules.Count} CapsuleColliders, " +
               $"{spheres.Count} SphereColliders, {primitives.Count} parametric primitives, " +
               $"{lowPolyHulls.Count} generated low-poly hulls " +
               $"({beforeMerge - boxes.Count} additional boxes removed by merging). " +
               $"{originals.Count - disabled.Count} complex MeshColliders were kept.";
    }

    void RestoreSelected()
    {
        GameObject selected = SelectedRoot();
        if (selected == null) return;
        if (IsPrefabAsset(selected, out string path))
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                RestoreInternal(contents, false);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[S4 collider optimizer] Restored prefab asset: {path}");
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        else
        {
            RestoreInternal(selected, true);
            Debug.Log($"[S4 collider optimizer] Restored collider setup on {selected.name}.", selected);
        }
        ShowNotification(new GUIContent("Original colliders restored"));
    }

    static void RestoreInternal(GameObject root, bool useUndo)
    {
        if (root == null) return;
        Transform generated = root.transform.Find(GeneratedRootName);
        if (generated != null)
        {
            if (useUndo) Undo.DestroyObjectImmediate(generated.gameObject);
            else DestroyImmediate(generated.gameObject);
        }
        var markers = root.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && t != root.transform && t.name.StartsWith(DisabledMarkerPrefix, StringComparison.Ordinal))
            .ToArray();
        foreach (Transform marker in markers)
        {
            if (marker == null || marker.parent == null) continue;
            if (int.TryParse(marker.name.Substring(DisabledMarkerPrefix.Length), out int index))
            {
                MeshCollider[] siblings = marker.parent.GetComponents<MeshCollider>();
                if (index >= 0 && index < siblings.Length)
                {
                    MeshCollider mc = siblings[index];
                    if (mc != null)
                    {
                        if (useUndo) Undo.RecordObject(mc, "Restore mesh collider");
                        mc.enabled = true;
                        EditorUtility.SetDirty(mc);
                    }
                }
            }
            if (useUndo) Undo.DestroyObjectImmediate(marker.gameObject);
            else DestroyImmediate(marker.gameObject);
        }
    }

    // ---------- Fit result types ----------

    [Serializable]
    sealed class BoxFit
    {
        public Vector3 center;
        public Quaternion rotation;
        public Vector3 size;
        public float volume;
        public float fill;
        public readonly HashSet<MeshCollider> sources = new HashSet<MeshCollider>();
    }

    [Serializable]
    sealed class CapsuleFit
    {
        public Vector3 center;
        public Quaternion rotation;
        public float radius;
        public float height;
        public float volume;
        public float fill;
        public float elongation;
        public float radialAspect;
        public readonly HashSet<MeshCollider> sources = new HashSet<MeshCollider>();
    }

    [Serializable]
    sealed class SphereFit
    {
        public Vector3 center;
        public float radius;
        public float volume;
        public float fill;
        public float radialAspect; // maxSpan / minSpan; close to 1 = spherical
        public readonly HashSet<MeshCollider> sources = new HashSet<MeshCollider>();
    }

    bool IsCapsuleCandidate(CapsuleFit fit) =>
        fit != null && fit.fill >= minCapsuleFill &&
        fit.elongation >= minCapsuleElongation &&
        fit.radialAspect <= maxCapsuleRadialAspect &&
        // FIXED: дополнительная проверка чтобы не создавать капсулы с большим количеством воздуха
        // fill должен быть >=0.30 и elongation >=2.0 даже в relaxed режиме
        fit.fill >= 0.30f && fit.elongation >= 2.0f;

    bool IsSphereCandidate(SphereFit fit) =>
        fit != null && fit.fill >= minSphereFill &&
        fit.radialAspect <= 1.35f;

    // ---------- Helpers ----------

    static float MaxAxisCoverage(Vector3 part, Vector3 whole)
    {
        float x = part.x / Mathf.Max(1e-6f, whole.x);
        float y = part.y / Mathf.Max(1e-6f, whole.y);
        float z = part.z / Mathf.Max(1e-6f, whole.z);
        return Mathf.Max(x, Mathf.Max(y, z));
    }

    static Bounds CombinedSourceBounds(List<MeshCollider> colliders)
    {
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool has = false;
        foreach (MeshCollider mc in colliders)
        {
            if (mc == null) continue;
            if (!has) { b = mc.bounds; has = true; }
            else b.Encapsulate(mc.bounds);
        }
        return has ? b : new Bounds(Vector3.zero, Vector3.one);
    }

    static string FitDiagnosticRow(MeshCollider mc, string meshName, string selected,
                                   CapsuleFit capsule, BoxFit box, Bounds bounds)
    {
        string assetPath = mc != null && mc.sharedMesh != null
            ? AssetDatabase.GetAssetPath(mc.sharedMesh) : "";
        string cap = capsule == null ? "" :
            $"{capsule.fill.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            $"{capsule.elongation.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
            $"{capsule.radialAspect.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        string bx = box == null ? "" :
            box.fill.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join("\t", new[]
        {
            mc != null ? mc.GetInstanceID().ToString() : "",
            mc != null ? mc.name : "",
            meshName ?? "",
            assetPath ?? "",
            selected ?? "",
            cap,
            bx,
            bounds.min.x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.min.y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.min.z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.max.x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.max.y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.max.z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.size.x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.size.y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bounds.size.z.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    static void WriteFitDiagnostics(GameObject root, List<string> rows)
    {
        if (root == null || rows == null) return;
        try
        {
            string dir = Path.Combine(Application.dataPath, "S4Extract_Data", "ColliderDiagnostics");
            Directory.CreateDirectory(dir);
            string safeName = string.IsNullOrEmpty(root.name) ? "root" : root.name;
            foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"{safeName}_{stamp}_{root.GetInstanceID()}.tsv");
            var lines = new List<string> {
                "instanceId\\tgameObject\\tmesh\\tassetPath\\tselected\\tcapsuleFill;elongation;radialAspect\\tboxFill\\tminX\\tminY\\tminZ\\tmaxX\\tmaxY\\tmaxZ\\tsizeX\\tsizeY\\tsizeZ"
            };
            lines.AddRange(rows);
            File.WriteAllLines(path, lines);
            AssetDatabase.ImportAsset("Assets/" + path.Substring(Application.dataPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'));
            Debug.Log($"[S4 fit] Saved diagnostics: {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[S4 fit] Could not save diagnostics: {e.Message}");
        }
    }

    static string DescribeCollider(MeshCollider mc)
    {
        if (mc == null) return "<null>";
        string go = mc.gameObject != null ? mc.gameObject.name : "?";
        string mesh = (mc.sharedMesh != null) ? mc.sharedMesh.name : "<no mesh>";
        return $"{go} (mesh: {mesh})";
    }

    static void EnsureMeshReadable(MeshCollider mc)
    {
        if (mc == null || mc.sharedMesh == null) return;
        Mesh mesh = mc.sharedMesh;
        if (mesh.isReadable) return;
        #if UNITY_EDITOR
        string path = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                $"Mesh '{mesh.name}' is not CPU-readable and has no asset path (probably an in-memory mesh). " +
                "Enable Read/Write in the Model importer.");
        }
        ModelImporter mi = AssetImporter.GetAtPath(path) as ModelImporter;
        if (mi == null)
        {
            throw new InvalidOperationException(
                $"Mesh '{mesh.name}' is not CPU-readable and is not imported by ModelImporter; cannot auto-enable Read/Write.");
        }
        mi.isReadable = true;
        mi.SaveAndReimport();
        Mesh reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (reloaded != null) mc.sharedMesh = reloaded;
        #endif
    }

    // ---------- Capsule fit ----------

    static bool TryFitCapsule(Transform root, MeshCollider mc, out CapsuleFit fit)
    {
        try { return TryFitCapsuleReadable(root, mc, out fit); }
        catch (Exception ex)
        {
            fit = null;
            Debug.LogWarning($"[S4 collider optimizer] Capsule fit failed on {DescribeCollider(mc)}: {ex.Message}");
            return false;
        }
    }

    static bool TryFitCapsuleReadable(Transform root, MeshCollider mc, out CapsuleFit fit)
    {
        fit = null;
        Mesh mesh = mc.sharedMesh;
        if (mesh == null || mesh.vertexCount < 4) return false;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length < 4) return false;
        if (triangles == null || triangles.Length < 3) return false;
        var points = new Vector3[vertices.Length];
        Vector3 mean = Vector3.zero;
        for (int i = 0; i < vertices.Length; i++)
        {
            points[i] = root.InverseTransformPoint(mc.transform.TransformPoint(vertices[i]));
            mean += points[i];
        }
        mean /= points.Length;
        float meshVolume = ConvexMeshVolume(root, mc, vertices, triangles);
        if (meshVolume <= 1e-9f) return false;

        Quaternion basis = PrincipalRotation(points);
        Quaternion inv = Quaternion.Inverse(basis);
        var axial = new float[points.Length];
        var radialSq = new float[points.Length];
        float minT = float.PositiveInfinity, maxT = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 q = inv * (points[i] - mean);
            axial[i] = q.x;
            radialSq[i] = q.y*q.y + q.z*q.z;
            minT = Mathf.Min(minT, q.x); maxT = Mathf.Max(maxT, q.x);
            minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
            minZ = Mathf.Min(minZ, q.z); maxZ = Mathf.Max(maxZ, q.z);
        }
        float span = maxT - minT;
        float crossY = maxY - minY, crossZ = maxZ - minZ;
        float crossMax = Mathf.Max(crossY, crossZ);
        float crossMin = Mathf.Max(1e-6f, Mathf.Min(crossY, crossZ));
        if (span <= 1e-6f || crossMax <= 1e-6f) return false;

        float bestVolume = float.PositiveInfinity, bestRadius = 0f;
        float bestA = minT, bestB = maxT;
        const int steps = 9;
        for (int ia = 0; ia < steps; ia++)
        for (int ib = 0; ib < steps; ib++)
        {
            float a = minT + span * 0.45f * ia / (steps - 1);
            float b = maxT - span * 0.45f * ib / (steps - 1);
            if (b < a) continue;
            float radiusSq = 0f;
            for (int i = 0; i < axial.Length; i++)
            {
                float outside = axial[i] < a ? a - axial[i] :
                                (axial[i] > b ? axial[i] - b : 0f);
                radiusSq = Mathf.Max(radiusSq, radialSq[i] + outside*outside);
            }
            float radius = Mathf.Sqrt(radiusSq);
            float cylinder = b - a;
            float volume = Mathf.PI*radius*radius*cylinder +
                           (4f/3f)*Mathf.PI*radius*radius*radius;
            if (volume < bestVolume)
            {
                bestVolume = volume; bestRadius = radius; bestA = a; bestB = b;
            }
        }
        if (bestRadius <= 1e-6f || float.IsNaN(bestVolume) || float.IsInfinity(bestVolume)) return false;
        Vector3 axis = (basis * Vector3.right).normalized;
        float midpoint = (bestA + bestB) * 0.5f;
        fit = new CapsuleFit {
            center = mean + axis * midpoint,
            rotation = Quaternion.FromToRotation(Vector3.up, axis),
            radius = bestRadius,
            height = (bestB - bestA) + 2f*bestRadius,
            volume = bestVolume,
            fill = Mathf.Clamp01(meshVolume / bestVolume),
            elongation = span / crossMax,
            radialAspect = crossMax / crossMin,
        };
        fit.sources.Add(mc);
        return IsFinite(fit.center) && fit.height >= 2f*fit.radius;
    }

    // ---------- Sphere fit ----------

    static bool TryFitSphere(Transform root, MeshCollider mc, out SphereFit fit)
    {
        try { return TryFitSphereReadable(root, mc, out fit); }
        catch (Exception ex)
        {
            fit = null;
            Debug.LogWarning($"[S4 collider optimizer] Sphere fit failed on {DescribeCollider(mc)}: {ex.Message}");
            return false;
        }
    }

    static bool TryFitSphereReadable(Transform root, MeshCollider mc, out SphereFit fit)
    {
        fit = null;
        Mesh mesh = mc.sharedMesh;
        if (mesh == null || mesh.vertexCount < 4) return false;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length < 4) return false;
        if (triangles == null || triangles.Length < 3) return false;

        // Build points in root-local space.
        Vector3[] points = new Vector3[vertices.Length];
        Vector3 mean = Vector3.zero;
        for (int i = 0; i < vertices.Length; i++)
        {
            points[i] = root.InverseTransformPoint(mc.transform.TransformPoint(vertices[i]));
            mean += points[i];
        }
        mean /= points.Length;
        float meshVolume = ConvexMeshVolume(root, mc, vertices, triangles);
        if (meshVolume <= 1e-9f) return false;

        // Initial center = mean, radius = max distance from mean.
        Vector3 center = mean;
        float radius = 0f;
        for (int iter = 0; iter < 6; iter++) // a few Welzl-ish / Ritter-ish iterations
        {
            // Find farthest point from center.
            int farI = 0;
            float farSq = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float d2 = (points[i] - center).sqrMagnitude;
                if (d2 > farSq) { farSq = d2; farI = i; }
            }
            Vector3 a = points[farI];
            // Find farthest point from a.
            int farJ = 0;
            float farSq2 = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float d2 = (points[i] - a).sqrMagnitude;
                if (d2 > farSq2) { farSq2 = d2; farJ = i; }
            }
            Vector3 b = points[farJ];
            Vector3 newCenter = (a + b) * 0.5f;
            float newRadius = 0.5f * Mathf.Sqrt(farSq2);
            // Check for points outside and push the center out to include them.
            for (int pass = 0; pass < 12; pass++)
            {
                bool grew = false;
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 d = points[i] - newCenter;
                    float dist = d.magnitude;
                    if (dist > newRadius + 1e-5f)
                    {
                        // Pull the sphere so the point sits on the boundary.
                        float newR = (newRadius + dist) * 0.5f;
                        Vector3 dir = dist > 1e-8f ? d / dist : Vector3.up;
                        newCenter = points[i] - dir * newR;
                        newRadius = newR;
                        grew = true;
                    }
                }
                if (!grew) break;
            }
            center = newCenter;
            radius = newRadius;
        }
        // Safety: one final inflation pass to guarantee containment.
        for (int i = 0; i < points.Length; i++)
        {
            float d = (points[i] - center).magnitude;
            if (d > radius) radius = d;
        }
        if (radius <= 1e-6f || float.IsNaN(radius) || float.IsInfinity(radius)) return false;
        float volume = (4f/3f) * Mathf.PI * radius*radius*radius;

        // Aspect ratio for candidate check: compare PCA spans; a good sphere is
        // roughly equal extent along all three axes.
        Quaternion basis = PrincipalRotation(points);
        Quaternion inv = Quaternion.Inverse(basis);
        float minX=float.PositiveInfinity,maxX=float.NegativeInfinity,
              minY=float.PositiveInfinity,maxY=float.NegativeInfinity,
              minZ=float.PositiveInfinity,maxZ=float.NegativeInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 q = inv * (points[i] - mean);
            minX = Mathf.Min(minX,q.x); maxX = Mathf.Max(maxX,q.x);
            minY = Mathf.Min(minY,q.y); maxY = Mathf.Max(maxY,q.y);
            minZ = Mathf.Min(minZ,q.z); maxZ = Mathf.Max(maxZ,q.z);
        }
        float sx = maxX-minX, sy = maxY-minY, sz = maxZ-minZ;
        float maxSpan = Mathf.Max(sx, Mathf.Max(sy, sz));
        float minSpan = Mathf.Max(1e-6f, Mathf.Min(sx, Mathf.Min(sy, sz)));

        fit = new SphereFit
        {
            center = center,
            radius = radius,
            volume = volume,
            fill = Mathf.Clamp01(meshVolume / volume),
            radialAspect = maxSpan / minSpan,
        };
        fit.sources.Add(mc);
        return IsFinite(fit.center) && fit.radius > 0f;
    }

    [Serializable]
    sealed class LowPolyFit
    {
        public Vector3 center;
        public Mesh mesh;
        public readonly HashSet<MeshCollider> sources = new HashSet<MeshCollider>();
    }

    static bool TryFitLowPolyHull(Transform root, MeshCollider mc, out LowPolyFit fit)
    {
        fit = null;
        if (mc == null || mc.sharedMesh == null || !mc.sharedMesh.isReadable) return false;
        Vector3[] source = mc.sharedMesh.vertices;
        if (source == null || source.Length < 4) return false;
        Vector3[] points = new Vector3[source.Length];
        Vector3 center = Vector3.zero;
        for (int i = 0; i < source.Length; i++)
        {
            points[i] = root.InverseTransformPoint(mc.transform.TransformPoint(source[i]));
            center += points[i];
        }
        center /= source.Length;
        Vector3[] centered = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++) centered[i] = points[i] - center;
        Mesh mesh = S4LowPolyHullFactory.Build(centered, "s4_lowpoly_" + mc.sharedMesh.GetInstanceID(), 24);
        if (mesh == null) return false;
        fit = new LowPolyFit { center = center, mesh = mesh };
        fit.sources.Add(mc);
        return true;
    }

    // ---------- Segmented fallback for curved parts ----------

    static bool TryFitSegmentedBoxes(Transform root, MeshCollider mc, out List<BoxFit> result)
    {
        result = new List<BoxFit>();
        Mesh mesh = mc != null ? mc.sharedMesh : null;
        if (mesh == null || !mesh.isReadable || mesh.vertexCount < 4) return false;

        Vector3[] vertices = mesh.vertices;
        var points = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            points[i] = root.InverseTransformPoint(mc.transform.TransformPoint(vertices[i]));

        Quaternion rotation = PrincipalRotation(points);
        Quaternion inverse = Quaternion.Inverse(rotation);
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 q = inverse * points[i];
            min = Vector3.Min(min, q);
            max = Vector3.Max(max, q);
        }
        Vector3 span = max - min;
        int slices = span.x >= span.y && span.x >= span.z ? 8 : 6;
        float step = span.x >= span.y && span.x >= span.z ? span.x / slices :
                     (span.y >= span.z ? span.y / slices : span.z / slices);
        if (step <= 1e-5f) return false;

        // Slice on the longest principal axis. Each slice overlaps its
        // neighbours by 12.5%, preventing contact gaps for rolling objects.
        int axis = span.x >= span.y && span.x >= span.z ? 0 :
                   (span.y >= span.z ? 1 : 2);
        for (int s = 0; s < slices; s++)
        {
            float a = GetComponent(min, axis) + step * (s - 0.125f);
            float b = GetComponent(min, axis) + step * (s + 1.125f);
            Vector3 lo = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 hi = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            int count = 0;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 q = inverse * points[i];
                float t = GetComponent(q, axis);
                if (t < a || t > b) continue;
                lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); count++;
            }
            if (count < 2) continue;
            Vector3 size = hi - lo;
            // Small inflation protects against faces between sparse vertices.
            size += Vector3.one * Mathf.Max(0.001f, size.magnitude * 0.015f);
            float centerAxis = (lo[axis] + hi[axis]) * 0.5f;
            Vector3 center = rotation * ((lo + hi) * 0.5f);
            center[axis] = centerAxis; // replaced below in rotated coordinates
            Vector3 localCenter = (lo + hi) * 0.5f;
            BoxFit fit = new BoxFit {
                center = rotation * localCenter,
                rotation = rotation,
                size = size,
                volume = size.x * size.y * size.z,
                fill = 0f
            };
            fit.sources.Add(mc);
            result.Add(fit);
        }
        return result.Count >= 2;
    }

    static float GetComponent(Vector3 v, int axis) => axis == 0 ? v.x : (axis == 1 ? v.y : v.z);

    static bool TryFitBoundsBox(Transform root, MeshCollider mc, out BoxFit fit)
    {
        fit = null;
        if (mc == null) return false;
        Bounds b = mc.bounds;
        Vector3 min = root.InverseTransformPoint(b.min);
        Vector3 max = root.InverseTransformPoint(b.max);
        Vector3 size = Vector3.Max(Vector3.one * 0.001f, max - min);
        fit = new BoxFit {
            center = (min + max) * 0.5f,
            rotation = Quaternion.identity,
            size = size,
            volume = size.x * size.y * size.z,
            fill = 0f
        };
        fit.sources.Add(mc);
        return true;
    }

    // ---------- Box fit ----------

    static bool TryFitBox(Transform root, MeshCollider mc, out BoxFit fit)
    {
        try
        {
            return TryFitBoxReadable(root, mc, out fit);
        }
        catch (Exception ex)
        {
            fit = null;
            Debug.LogWarning($"[S4 collider optimizer] Box fit failed on {DescribeCollider(mc)}: {ex.Message}. " +
                             "Enable Read/Write on its Model Import Settings.");
            return false;
        }
    }

    static bool TryFitBoxReadable(Transform root, MeshCollider mc, out BoxFit fit)
    {
        fit = null;
        Mesh mesh = mc.sharedMesh;
        if (mesh == null || mesh.vertexCount < 4) return false;

        // FIXED: разбиваем меш на острова чтобы 2 ножки не давали 1 огромный бокс
        var islands = SplitBySpatialGaps(mesh);
        // Для простоты фиттим бокс для каждого острова и выбираем лучший
        // (в Batch режиме лучше создавать по боксу на остров, но здесь возвращаем первый найденный)
        // В OptimizeRoot мы уже создаём по одному боксу на каждый MeshCollider,
        // поэтому если островов несколько, нужно создавать несколько боксов.
        // Здесь для совместимости фиттим объединённый объём островов? Нет, фиттим крупнейший.
        // Лучший подход: если островов >1, создаём боксы через отдельный путь в OptimizeRoot.
        // Пока фиттим первый остров как fallback, а основная логика в OptimizeRoot создаст боксы на все острова.
        Mesh bestIsland = islands.OrderByDescending(m => m.vertexCount).FirstOrDefault() ?? mesh;
        Vector3[] meshVertices = bestIsland.vertices;
        int[] triangles = bestIsland.triangles;
        if (meshVertices == null || meshVertices.Length < 4) return false;
        if (triangles == null || triangles.Length < 3) return false;
        var points = new Vector3[meshVertices.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = root.InverseTransformPoint(mc.transform.TransformPoint(meshVertices[i]));

        float meshVolume = ConvexMeshVolume(root, mc, meshVertices, triangles);
        if (meshVolume <= 1e-9f) return false;

        BoxFit axisAligned = FitAtRotation(points, Quaternion.identity);
        Quaternion pcaRotation = PrincipalRotation(points);
        BoxFit oriented = FitAtRotation(points, pcaRotation);
        fit = oriented.volume < axisAligned.volume ? oriented : axisAligned;
        fit.fill = Mathf.Clamp01(meshVolume / Mathf.Max(fit.volume, 1e-9f));
        fit.sources.Add(mc);
        return IsFinite(fit.center) && IsFinite(fit.size) && fit.size.x > 0f && fit.size.y > 0f && fit.size.z > 0f;
    }

    static float ConvexMeshVolume(Transform root, MeshCollider mc, Vector3[] vertices, int[] triangles)
    {
        if (vertices == null || vertices.Length < 4) return 0f;
        if (triangles == null || triangles.Length < 3) return 0f;
        var p = new Vector3[vertices.Length];
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < vertices.Length; i++)
        {
            p[i] = root.InverseTransformPoint(mc.transform.TransformPoint(vertices[i]));
            centroid += p[i];
        }
        centroid /= Mathf.Max(1, p.Length);
        double volume = 0.0;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int ia = triangles[i], ib = triangles[i+1], ic = triangles[i+2];
            if (ia < 0 || ia >= p.Length || ib < 0 || ib >= p.Length || ic < 0 || ic >= p.Length) continue;
            Vector3 a = p[ia] - centroid;
            Vector3 b = p[ib] - centroid;
            Vector3 c = p[ic] - centroid;
            volume += Math.Abs(Vector3.Dot(a, Vector3.Cross(b, c))) / 6.0;
        }
        return (float)volume;
    }

    static BoxFit FitAtRotation(IReadOnlyList<Vector3> points, Quaternion rotation)
    {
        Quaternion inv = Quaternion.Inverse(rotation);
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 q = inv * points[i];
            min = Vector3.Min(min, q);
            max = Vector3.Max(max, q);
        }
        Vector3 size = max - min;
        return new BoxFit {
            center = rotation * ((min + max) * 0.5f),
            rotation = rotation,
            size = size,
            volume = Mathf.Max(0f, size.x * size.y * size.z),
            fill = 0f,
        };
    }

    static Quaternion PrincipalRotation(IReadOnlyList<Vector3> points)
    {
        Vector3 mean = Vector3.zero;
        for (int i = 0; i < points.Count; i++) mean += points[i];
        mean /= Mathf.Max(1, points.Count);

        float xx=0, xy=0, xz=0, yy=0, yz=0, zz=0;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 d = points[i] - mean;
            xx += d.x*d.x; xy += d.x*d.y; xz += d.x*d.z;
            yy += d.y*d.y; yz += d.y*d.z; zz += d.z*d.z;
        }
        float total = xx + yy + zz;
        if (total < 1e-12f) return Quaternion.identity;

        Vector3 major = PowerEigen(xx,xy,xz,yy,yz,zz, new Vector3(0.73f,0.41f,0.55f), Vector3.zero);
        if (major.sqrMagnitude < 1e-8f) return Quaternion.identity;
        Vector3 second = PowerEigen(xx,xy,xz,yy,yz,zz, new Vector3(0.17f,0.91f,0.37f), major);
        Vector3 third = Vector3.Cross(major, second).normalized;
        if (third.sqrMagnitude < 0.5f) return Quaternion.identity;
        second = Vector3.Cross(third, major).normalized;
        if (second.sqrMagnitude < 0.5f) return Quaternion.identity;
        return Quaternion.LookRotation(third, second);
    }

    static Vector3 PowerEigen(float xx,float xy,float xz,float yy,float yz,float zz,
                              Vector3 seed, Vector3 reject)
    {
        Vector3 v = seed.normalized;
        for (int i = 0; i < 24; i++)
        {
            Vector3 w = new Vector3(xx*v.x + xy*v.y + xz*v.z,
                                    xy*v.x + yy*v.y + yz*v.z,
                                    xz*v.x + yz*v.y + zz*v.z);
            if (reject.sqrMagnitude > 0.5f) w -= reject * Vector3.Dot(w, reject);
            if (w.sqrMagnitude < 1e-12f) break;
            v = w.normalized;
        }
        return v;
    }

    List<BoxFit> MergeBoxes(List<BoxFit> input)
    {
        var boxes = new List<BoxFit>(input);
        bool changed = true;
        int safety = 0;
        while (changed && safety++ < 10000)
        {
            changed = false;
            float bestError = float.PositiveInfinity;
            int bestI = -1, bestJ = -1;
            BoxFit best = null;
            for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (!TryMergeBoxes(boxes[i], boxes[j], out BoxFit merged, out float error)) continue;
                if (error < bestError) { bestError = error; bestI = i; bestJ = j; best = merged; }
            }
            if (best != null)
            {
                boxes[bestI] = best;
                boxes.RemoveAt(bestJ);
                changed = true;
            }
        }
        return boxes;
    }

    bool TryMergeBoxes(BoxFit a, BoxFit b, out BoxFit merged, out float inflation)
    {
        merged = null;
        inflation = float.PositiveInfinity;
        float angle = Quaternion.Angle(a.rotation, b.rotation);
        angle = Mathf.Min(angle, Mathf.Abs(180f - angle));
        if (angle > maxAxisAngle) return false;

        Quaternion inv = Quaternion.Inverse(a.rotation);
        Vector3 ac = inv * a.center;
        Vector3 bc = inv * b.center;
        Vector3 ah = a.size * 0.5f;
        Matrix4x4 rel = Matrix4x4.Rotate(inv * b.rotation);
        Vector3 bh = b.size * 0.5f;
        Vector3 projected = new Vector3(
            Mathf.Abs(rel.m00)*bh.x + Mathf.Abs(rel.m01)*bh.y + Mathf.Abs(rel.m02)*bh.z,
            Mathf.Abs(rel.m10)*bh.x + Mathf.Abs(rel.m11)*bh.y + Mathf.Abs(rel.m12)*bh.z,
            Mathf.Abs(rel.m20)*bh.x + Mathf.Abs(rel.m21)*bh.y + Mathf.Abs(rel.m22)*bh.z);
        Vector3 amin = ac-ah, amax = ac+ah, bmin = bc-projected, bmax = bc+projected;

        float dx = IntervalGap(amin.x,amax.x,bmin.x,bmax.x);
        float dy = IntervalGap(amin.y,amax.y,bmin.y,bmax.y);
        float dz = IntervalGap(amin.z,amax.z,bmin.z,bmax.z);
        if (Mathf.Sqrt(dx*dx + dy*dy + dz*dz) > contactEpsilon) return false;

        Vector3 min = Vector3.Min(amin,bmin), max = Vector3.Max(amax,bmax);
        Vector3 size = max-min;
        float mergedVolume = size.x*size.y*size.z;
        Vector3 overlap = Vector3.Max(Vector3.zero, Vector3.Min(amax,bmax)-Vector3.Max(amin,bmin));
        float intersection = overlap.x*overlap.y*overlap.z;
        float union = Mathf.Max(1e-9f, a.volume+b.volume-intersection);
        inflation = Mathf.Max(0f, (mergedVolume-union)/union);
        if (inflation > maxMergeInflation) return false;

        merged = new BoxFit {
            center = a.rotation*((min+max)*0.5f), rotation=a.rotation,
            size=size, volume=mergedVolume, fill=union/mergedVolume,
        };
        foreach (MeshCollider s in a.sources) merged.sources.Add(s);
        foreach (MeshCollider s in b.sources) merged.sources.Add(s);
        return true;
    }

    static float IntervalGap(float amin,float amax,float bmin,float bmax)
    {
        if (amax < bmin) return bmin-amax;
        if (bmax < amin) return amin-bmax;
        return 0f;
    }

    static bool IsFinite(Vector3 v) =>
        !(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)||
          float.IsInfinity(v.x)||float.IsInfinity(v.y)||float.IsInfinity(v.z));

    // FIXED: разбивает меш на острова по связности треугольников
    // Это решает проблему когда 2 ножки в одном меше дают 1 огромный бокс между ними
    static List<Mesh> SplitMeshIntoIslands(Mesh mesh)
    {
        var result = new List<Mesh>();
        if (mesh == null) return result;
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;
        if (vertices.Length < 4 || triangles.Length < 6) { result.Add(mesh); return result; }

        var vertToTris = new List<int>[vertices.Length];
        for (int i = 0; i < vertToTris.Length; i++) vertToTris[i] = new List<int>();
        for (int ti = 0; ti < triangles.Length; ti += 3)
        {
            int a = triangles[ti], b = triangles[ti+1], c = triangles[ti+2];
            if (a < vertToTris.Length) vertToTris[a].Add(ti/3);
            if (b < vertToTris.Length) vertToTris[b].Add(ti/3);
            if (c < vertToTris.Length) vertToTris[c].Add(ti/3);
        }
        var visited = new bool[triangles.Length/3];
        for (int start = 0; start < visited.Length; start++)
        {
            if (visited[start]) continue;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            var islandTris = new List<int>();
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                islandTris.Add(t);
                int baseIdx = t*3;
                int av = triangles[baseIdx], bv = triangles[baseIdx+1], cv = triangles[baseIdx+2];
                foreach (int v in new int[]{av,bv,cv})
                {
                    if (v >= vertToTris.Length) continue;
                    foreach (int nt in vertToTris[v])
                    {
                        if (!visited[nt]) { visited[nt]=true; queue.Enqueue(nt); }
                    }
                }
            }
            var used = new HashSet<int>();
            foreach (int t in islandTris)
            {
                used.Add(triangles[t*3]); used.Add(triangles[t*3+1]); used.Add(triangles[t*3+2]);
            }
            if (used.Count < 4) continue;
            var usedList = new List<int>(used);
            var remap = new Dictionary<int,int>();
            for (int i=0;i<usedList.Count;i++) remap[usedList[i]]=i;
            var newVerts = new Vector3[usedList.Count];
            for (int i=0;i<usedList.Count;i++) newVerts[i]=vertices[usedList[i]];
            var newTris = new int[islandTris.Count*3];
            for (int i=0;i<islandTris.Count;i++)
            {
                int t=islandTris[i];
                newTris[i*3]=remap[triangles[t*3]];
                newTris[i*3+1]=remap[triangles[t*3+1]];
                newTris[i*3+2]=remap[triangles[t*3+2]];
            }
            var islandMesh = new Mesh();
            islandMesh.name = mesh.name + "_island" + result.Count;
            islandMesh.SetVertices(newVerts);
            islandMesh.SetTriangles(newTris, 0);
            islandMesh.RecalculateBounds();
            result.Add(islandMesh);
        }
        if (result.Count==0) result.Add(mesh);
        return result;
    }

    static List<Mesh> SplitBySpatialGaps(Mesh mesh, float minGap=0.06f, float minGapRatio=0.12f)
    {
        // gap-split как в Python фиксе
        var islands = SplitMeshIntoIslands(mesh);
        var final = new List<Mesh>();
        foreach (var island in islands)
        {
            var verts = island.vertices;
            var tris = island.triangles;
            var centroids = new List<Vector3>();
            for (int i=0;i<tris.Length;i+=3)
            {
                var a=verts[tris[i]]; var b=verts[tris[i+1]]; var c=verts[tris[i+2]];
                centroids.Add((a+b+c)/3f);
            }
            float bestGap=0; int bestAxis=-1; float splitPos=0;
            List<int> leftTris=null, rightTris=null;
            for (int axis=0; axis<=2; axis+=2)
            {
                var sorted = centroids.Select((c, idx) => new {c, idx})
                    .OrderBy(p => axis==0 ? p.c.x : p.c.z).ToList();
                float span = (axis==0 ? sorted.Last().c.x : sorted.Last().c.z) - (axis==0 ? sorted.First().c.x : sorted.First().c.z);
                if (span<0.05f) continue;
                float maxGap=0; int maxIdx=-1;
                for (int i=1;i<sorted.Count;i++)
                {
                    float prev = axis==0 ? sorted[i-1].c.x : sorted[i-1].c.z;
                    float curr = axis==0 ? sorted[i].c.x : sorted[i].c.z;
                    float gap=curr-prev;
                    if (gap>maxGap) { maxGap=gap; maxIdx=i; }
                }
                if (maxGap>minGap && maxGap>span*minGapRatio)
                {
                    if (maxIdx>sorted.Count*0.1f && maxIdx<sorted.Count*0.9f)
                    {
                        if (maxGap>bestGap)
                        {
                            bestGap=maxGap; bestAxis=axis;
                            float prev = axis==0 ? sorted[maxIdx-1].c.x : sorted[maxIdx-1].c.z;
                            float curr = axis==0 ? sorted[maxIdx].c.x : sorted[maxIdx].c.z;
                            splitPos=(prev+curr)*0.5f;
                            leftTris=new List<int>(); rightTris=new List<int>();
                            for (int i=0;i<maxIdx;i++) leftTris.Add(sorted[i].idx);
                            for (int i=maxIdx;i<sorted.Count;i++) rightTris.Add(sorted[i].idx);
                        }
                    }
                }
            }
            if (bestAxis!=-1 && leftTris!=null)
            {
                // режем
                var leftMesh = BuildMeshFromTriSubset(island, leftTris);
                var rightMesh = BuildMeshFromTriSubset(island, rightTris);
                final.AddRange(SplitBySpatialGaps(leftMesh, minGap, minGapRatio));
                final.AddRange(SplitBySpatialGaps(rightMesh, minGap, minGapRatio));
            }
            else
            {
                final.Add(island);
            }
        }
        return final;
    }

    static Mesh BuildMeshFromTriSubset(Mesh src, List<int> triIndices)
    {
        var verts=src.vertices; var tris=src.triangles;
        var used=new HashSet<int>();
        foreach (int t in triIndices) { used.Add(tris[t*3]); used.Add(tris[t*3+1]); used.Add(tris[t*3+2]); }
        var usedList=new List<int>(used);
        var remap=new Dictionary<int,int>();
        for (int i=0;i<usedList.Count;i++) remap[usedList[i]]=i;
        var newVerts=new Vector3[usedList.Count];
        for (int i=0;i<usedList.Count;i++) newVerts[i]=verts[usedList[i]];
        var newTris=new int[triIndices.Count*3];
        for (int i=0;i<triIndices.Count;i++)
        {
            int t=triIndices[i];
            newTris[i*3]=remap[tris[t*3]];
            newTris[i*3+1]=remap[tris[t*3+1]];
            newTris[i*3+2]=remap[tris[t*3+2]];
        }
        var m=new Mesh();
        m.SetVertices(newVerts);
        m.SetTriangles(newTris,0);
        m.RecalculateBounds();
        return m;
    }

    // =====================================================
    // ВТОРОЙ ПРОХОД ВАЛИДАЦИИ КОЛЛАЙДЕРОВ
    // Код из S4ColliderOptimizer_ValidationPass.cs
    // =====================================================

    /// <summary>
    /// Результат валидации одного коллайдера против вершин модели
    /// </summary>
    public sealed class ColliderValidationResult
    {
        public Collider collider;
        public MeshCollider sourceMeshCollider; // оригинальный MeshCollider, из которого создан
        public Mesh visualMesh; // визуальная сетка, против которой проверяем
        public int totalVertices;
        public int verticesInside;
        public int verticesOutside;
        public float coverageRatio; // verticesInside / totalVertices
        public float emptyVolumeRatio; // 1 - (объём вершин внутри / объём коллайдера) примерно
        public bool shouldRemove;
        public bool shouldShrink;
        public Vector3 suggestedSize;
        public Vector3 suggestedCenter;
        public Quaternion suggestedRotation;
        public string debugInfo;
    }

    /// <summary>
    /// Настройки второго прохода валидации
    /// </summary>
    [System.Serializable]
    public class ColliderValidationSettings
    {
        [Range(0f, 1f)] public float minVertexCoverage = 0.15f; // мин. доля вершин внутри коллайдера
        [Range(0f, 1f)] public float maxEmptyVolumeRatio = 0.85f; // макс. доля пустого объёма
        [Range(0f, 0.5f)] public float shrinkMargin = 0.02f; // запас при сужении (в единицах Unity)
        public bool enableShrinking = true;
        public bool enableRemoval = true;
        public bool logDetails = true;
    }

    /// <summary>
    /// Основной метод второго прохода: проверяет все созданные коллайдеры против вершин визуальных мешей
    /// </summary>
    public static int ValidateAndTightenColliders(GameObject root, ColliderValidationSettings settings = null)
    {
        if (root == null) return 0;
        if (settings == null) settings = new ColliderValidationSettings();

        // Находим корень сгенерированных коллайдеров
        Transform generatedRoot = root.transform.Find(GeneratedRootName);
        if (generatedRoot == null)
        {
            if (settings.logDetails) Debug.Log("[S4 Validation] No generated colliders root found.");
            return 0;
        }

        // Собираем все визуальные MeshFilter'ы в префабе (кроме тех, что под GeneratedRoot)
        var visualMeshes = new List<MeshFilter>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null) continue;
            if (IsUnderGeneratedRoot(mf.transform, root.transform)) continue;
            visualMeshes.Add(mf);
        }

        if (visualMeshes.Count == 0)
        {
            if (settings.logDetails) Debug.Log("[S4 Validation] No visual meshes found to validate against.");
            return 0;
        }

        // Собираем все созданные коллайдеры
        var allColliders = new List<Collider>();
        allColliders.AddRange(generatedRoot.GetComponentsInChildren<BoxCollider>(true));
        allColliders.AddRange(generatedRoot.GetComponentsInChildren<CapsuleCollider>(true));
        allColliders.AddRange(generatedRoot.GetComponentsInChildren<SphereCollider>(true));
        // Parametric primitives — это MeshCollider'ы под GeneratedRoot
        allColliders.AddRange(generatedRoot.GetComponentsInChildren<MeshCollider>(true)
            .Where(mc => mc != null && mc.sharedMesh != null && mc.convex));

        if (allColliders.Count == 0)
        {
            if (settings.logDetails) Debug.Log("[S4 Validation] No colliders to validate.");
            return 0;
        }

        int removed = 0;
        int shrunk = 0;
        int kept = 0;

        foreach (var col in allColliders)
        {
            if (col == null) continue;

            // Находим соответствующий визуальный меш
            // Эвристика: коллайдер создан из MeshCollider'а с похожим именем или в той же иерархии
            Mesh visualMesh = ValidationFindBestVisualMesh(col, visualMeshes, root.transform);
            if (visualMesh == null)
            {
                if (settings.logDetails)
                    Debug.Log($"[S4 Validation] {col.name}: no matching visual mesh found, keeping as-is.");
                kept++;
                continue;
            }

            var result = ValidationValidateColliderAgainstMesh(col, visualMesh, root.transform, settings);

            if (settings.logDetails)
            {
                Debug.Log($"[S4 Validation] {col.name} vs {visualMesh.name}: " +
                          $"coverage={result.coverageRatio:P1}, emptyVol={result.emptyVolumeRatio:P1}, " +
                          $"remove={result.shouldRemove}, shrink={result.shouldShrink} - {result.debugInfo}");
            }

            if (result.shouldRemove && settings.enableRemoval)
            {
                // Удаляем коллайдер целиком
                Undo.DestroyObjectImmediate(col.gameObject);
                removed++;
            }
            else if (result.shouldShrink && settings.enableShrinking)
            {
                // Сужаем коллайдер
                ValidationApplyShrink(col, result, settings.shrinkMargin);
                shrunk++;
            }
            else
            {
                kept++;
            }
        }

        if (settings.logDetails)
        {
            Debug.Log($"[S4 Validation] Done: {kept} kept, {shrunk} shrunk, {removed} removed. Total was {allColliders.Count}.");
        }

        return removed + shrunk;
    }

    /// <summary>
    /// Находит лучший визуальный меш для данного коллайдера
    /// </summary>
    static Mesh ValidationFindBestVisualMesh(Collider col, List<MeshFilter> visualMeshes, Transform rootTransform)
    {
        // Получаем мировые границы коллайдера
        Bounds colBounds = ValidationGetColliderWorldBounds(col);

        Mesh bestMesh = null;
        float bestScore = float.MaxValue;

        foreach (var mf in visualMeshes)
        {
            if (mf == null || mf.sharedMesh == null) continue;

            Bounds meshBounds = mf.sharedMesh.bounds;
            // Переводим в мировое пространство
            Matrix4x4 localToWorld = mf.transform.localToWorldMatrix;
            Vector3 center = localToWorld.MultiplyPoint(meshBounds.center);
            Vector3 size = Vector3.Scale(meshBounds.size, mf.transform.lossyScale);
            Bounds worldMeshBounds = new Bounds(center, size);

            // Считаем перекрытие
            float overlap = ValidationBoundsOverlapRatio(colBounds, worldMeshBounds);
            float distance = Vector3.Distance(colBounds.center, worldMeshBounds.center);

            // Скор: меньше = лучше (больше перекрытие, меньше расстояние)
            float score = (1f - overlap) * 10f + distance;

            if (score < bestScore)
            {
                bestScore = score;
                bestMesh = mf.sharedMesh;
            }
        }

        return bestMesh;
    }

    /// <summary>
    /// Получает мировые границы коллайдера любого типа
    /// </summary>
    static Bounds ValidationGetColliderWorldBounds(Collider col)
    {
        if (col is BoxCollider bc)
        {
            return new Bounds(
                bc.transform.TransformPoint(bc.center),
                Vector3.Scale(bc.size, bc.transform.lossyScale)
            );
        }
        else if (col is CapsuleCollider cc)
        {
            // Капсула: вычисляем AABB
            Vector3 center = cc.transform.TransformPoint(cc.center);
            float radius = cc.radius * Mathf.Max(cc.transform.lossyScale.x, cc.transform.lossyScale.z);
            float halfHeight = cc.height * 0.5f * cc.transform.lossyScale.y;
            Vector3 size = new Vector3(radius * 2f, halfHeight * 2f + radius * 2f, radius * 2f);
            return new Bounds(center, size);
        }
        else if (col is SphereCollider sc)
        {
            Vector3 center = sc.transform.TransformPoint(sc.center);
            float radius = sc.radius * Mathf.Max(sc.transform.lossyScale.x, sc.transform.lossyScale.y, sc.transform.lossyScale.z);
            return new Bounds(center, Vector3.one * radius * 2f);
        }
        else if (col is MeshCollider mc && mc.sharedMesh != null)
        {
            return mc.bounds;
        }
        return new Bounds(col.transform.position, Vector3.one * 0.01f);
    }

    /// <summary>
    /// Коэффициент перекрытия двух AABB (0..1)
    /// </summary>
    static float ValidationBoundsOverlapRatio(Bounds a, Bounds b)
    {
        Vector3 min = Vector3.Max(a.min, b.min);
        Vector3 max = Vector3.Min(a.max, b.max);
        Vector3 inter = Vector3.Max(Vector3.zero, max - min);
        float interVol = inter.x * inter.y * inter.z;
        float aVol = a.size.x * a.size.y * a.size.z;
        float bVol = b.size.x * b.size.y * b.size.z;
        float union = aVol + bVol - interVol;
        return union > 0f ? interVol / union : 0f;
    }

    /// <summary>
    /// Валидирует один коллайдер против вершин меша
    /// </summary>
    static ColliderValidationResult ValidationValidateColliderAgainstMesh(
        Collider col, Mesh visualMesh, Transform rootTransform, ColliderValidationSettings settings)
    {
        var result = new ColliderValidationResult
        {
            collider = col,
            visualMesh = visualMesh
        };

        // Получаем вершины визуального меша в мировом пространстве
        Vector3[] vertices = visualMesh.vertices;
        if (vertices == null || vertices.Length == 0)
        {
            result.debugInfo = "empty mesh";
            result.shouldRemove = true;
            return result;
        }

        // Находим MeshFilter для этого меша, чтобы получить правильный transform
        MeshFilter mf = ValidationFindMeshFilterForMesh(visualMesh, rootTransform);
        Matrix4x4 localToWorld = mf != null ? mf.transform.localToWorldMatrix : rootTransform.localToWorldMatrix;

        result.totalVertices = vertices.Length;
        int inside = 0;

        // Проверяем каждую вершину
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldVertex = localToWorld.MultiplyPoint(vertices[i]);
            if (ValidationIsPointInCollider(worldVertex, col))
                inside++;
        }

        result.verticesInside = inside;
        result.verticesOutside = result.totalVertices - inside;
        result.coverageRatio = result.totalVertices > 0 ? (float)inside / result.totalVertices : 0f;

        // Оценка пустого объёма: объём коллайдера vs объём выпуклой оболочки вершин внутри
        float colVolume = ValidationGetColliderVolume(col);
        float verticesVolume = ValidationEstimateVerticesVolume(vertices, localToWorld, col);
        result.emptyVolumeRatio = colVolume > 0f ? 1f - (verticesVolume / colVolume) : 1f;
        result.emptyVolumeRatio = Mathf.Clamp01(result.emptyVolumeRatio);

        // Решение: удалять или сужать
        result.shouldRemove = result.coverageRatio < settings.minVertexCoverage
                           || result.emptyVolumeRatio > settings.maxEmptyVolumeRatio;

        result.shouldShrink = !result.shouldRemove
                           && result.coverageRatio < 0.5f // если покрытие не полное
                           && result.emptyVolumeRatio > 0.3f; // и есть заметный пустой объём

        // Предлагаем новый размер/центр на основе bounding box вершин внутри
        if (result.shouldShrink)
        {
            ValidationComputeTightBounds(vertices, localToWorld, col, out result.suggestedCenter, out result.suggestedSize, out result.suggestedRotation);
        }

        result.debugInfo = $"vertsIn={inside}/{result.totalVertices}, colVol={colVolume:F4}, vertVol≈{verticesVolume:F4}";
        return result;
    }

    /// <summary>
    /// Находит MeshFilter для данного меша в иерархии
    /// </summary>
    static MeshFilter ValidationFindMeshFilterForMesh(Mesh mesh, Transform root)
    {
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == mesh) return mf;
        }
        return null;
    }

    /// <summary>
    /// Проверяет, находится ли точка внутри коллайдера
    /// </summary>
    static bool ValidationIsPointInCollider(Vector3 worldPoint, Collider col)
    {
        if (col is BoxCollider bc)
        {
            Vector3 local = bc.transform.InverseTransformPoint(worldPoint) - bc.center;
            Vector3 halfSize = bc.size * 0.5f;
            return Mathf.Abs(local.x) <= halfSize.x &&
                   Mathf.Abs(local.y) <= halfSize.y &&
                   Mathf.Abs(local.z) <= halfSize.z;
        }
        else if (col is CapsuleCollider cc)
        {
            Vector3 local = cc.transform.InverseTransformPoint(worldPoint) - cc.center;
            int dir = cc.direction; // 0=X, 1=Y, 2=Z
            float radius = cc.radius;
            float halfHeight = cc.height * 0.5f;

            // Проекция на ось капсулы
            float axial = (dir == 0) ? local.x : (dir == 1) ? local.y : local.z;
            Vector2 radial = (dir == 0) ? new Vector2(local.y, local.z)
                             : (dir == 1) ? new Vector2(local.x, local.z)
                             : new Vector2(local.x, local.y);

            float radialDist = radial.magnitude;
            if (radialDist > radius) return false;

            // Проверяем расстояние до концов капсулы
            if (axial > halfHeight)
                return (axial - halfHeight) * (axial - halfHeight) + radialDist * radialDist <= radius * radius;
            if (axial < -halfHeight)
                return (-halfHeight - axial) * (-halfHeight - axial) + radialDist * radialDist <= radius * radius;
            return true;
        }
        else if (col is SphereCollider sc)
        {
            Vector3 local = sc.transform.InverseTransformPoint(worldPoint) - sc.center;
            return local.sqrMagnitude <= sc.radius * sc.radius;
        }
        else if (col is MeshCollider mc && mc.sharedMesh != null && mc.convex)
        {
            // Для convex MeshCollider используем bounds как грубую оценку
            return mc.bounds.Contains(worldPoint);
        }
        return false;
    }

    /// <summary>
    /// Приблизительный объём коллайдера
    /// </summary>
    static float ValidationGetColliderVolume(Collider col)
    {
        if (col is BoxCollider bc)
            return bc.size.x * bc.size.y * bc.size.z;
        if (col is CapsuleCollider cc)
        {
            float r = cc.radius;
            float h = cc.height;
            // Объём цилиндра + сфера (два полушария = сфера)
            return Mathf.PI * r * r * Mathf.Max(0f, h - 2f * r) + (4f / 3f) * Mathf.PI * r * r * r;
        }
        if (col is SphereCollider sc)
            return (4f / 3f) * Mathf.PI * sc.radius * sc.radius * sc.radius;
        if (col is MeshCollider mc && mc.sharedMesh != null)
            return mc.bounds.size.x * mc.bounds.size.y * mc.bounds.size.z;
        return 0f;
    }

    /// <summary>
    /// Оценка объёма, занятого вершинами внутри коллайдера
    /// </summary>
    static float ValidationEstimateVerticesVolume(Vector3[] vertices, Matrix4x4 localToWorld, Collider col)
    {
        // Собираем вершины, которые внутри коллайдера
        var insideVerts = new List<Vector3>();
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 w = localToWorld.MultiplyPoint(vertices[i]);
            if (ValidationIsPointInCollider(w, col))
                insideVerts.Add(w);
        }

        if (insideVerts.Count < 4) return 0f;

        // Грубая оценка: AABB вершин внутри
        Vector3 min = insideVerts[0], max = insideVerts[0];
        foreach (var v in insideVerts)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        Vector3 size = max - min;
        return size.x * size.y * size.z;
    }

    /// <summary>
    /// Вычисляет плотные границы (OBB) для вершин внутри коллайдера через PCA
    /// </summary>
    static void ValidationComputeTightBounds(Vector3[] vertices, Matrix4x4 localToWorld, Collider col,
        out Vector3 center, out Vector3 size, out Quaternion rotation)
    {
        var insideVerts = new List<Vector3>();
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 w = localToWorld.MultiplyPoint(vertices[i]);
            if (ValidationIsPointInCollider(w, col))
                insideVerts.Add(w);
        }

        if (insideVerts.Count < 4)
        {
            center = col.bounds.center;
            size = col.bounds.size;
            rotation = Quaternion.identity;
            return;
        }

        // PCA для нахождения главных осей
        Vector3 mean = Vector3.zero;
        foreach (var v in insideVerts) mean += v;
        mean /= insideVerts.Count;

        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var v in insideVerts)
        {
            Vector3 d = v - mean;
            xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
            yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
        }

        // Power iteration для главной оси
        Vector3 major = new Vector3(0.73f, 0.41f, 0.55f).normalized;
        for (int i = 0; i < 20; i++)
        {
            Vector3 w = new Vector3(xx * major.x + xy * major.y + xz * major.z,
                                    xy * major.x + yy * major.y + yz * major.z,
                                    xz * major.x + yz * major.y + zz * major.z);
            if (w.sqrMagnitude < 1e-12f) break;
            major = w.normalized;
        }

        // Вторая ось
        Vector3 second = new Vector3(0.17f, 0.91f, 0.37f).normalized;
        second -= major * Vector3.Dot(second, major);
        if (second.sqrMagnitude < 1e-8f) second = Vector3.Cross(major, Vector3.up).normalized;
        second = second.normalized;

        Vector3 third = Vector3.Cross(major, second).normalized;
        second = Vector3.Cross(third, major).normalized;

        rotation = Quaternion.LookRotation(third, second);
        Quaternion invRot = Quaternion.Inverse(rotation);

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (var v in insideVerts)
        {
            Vector3 q = invRot * (v - mean);
            min = Vector3.Min(min, q);
            max = Vector3.Max(max, q);
        }

        size = max - min;
        center = mean + rotation * ((min + max) * 0.5f);
    }

    /// <summary>
    /// Применяет сужение к коллайдеру
    /// </summary>
    static void ValidationApplyShrink(Collider col, ColliderValidationResult result, float margin)
    {
        Undo.RecordObject(col, "Shrink collider after validation");

        if (col is BoxCollider bc)
        {
            bc.center = bc.transform.InverseTransformPoint(result.suggestedCenter);
            bc.size = Vector3.Max(Vector3.one * 0.001f, result.suggestedSize + Vector3.one * margin * 2f);
            bc.transform.rotation = result.suggestedRotation;
        }
        else if (col is CapsuleCollider cc)
        {
            // Для капсулы сужаем радиус и высоту на основе предложенных границ
            cc.center = cc.transform.InverseTransformPoint(result.suggestedCenter);
            float newHeight = Mathf.Max(2f * cc.radius, result.suggestedSize.y);
            float newRadius = Mathf.Max(0.001f, Mathf.Max(result.suggestedSize.x, result.suggestedSize.z) * 0.5f);
            cc.height = newHeight + margin * 2f;
            cc.radius = newRadius + margin;
            cc.transform.rotation = result.suggestedRotation;
        }
        else if (col is SphereCollider sc)
        {
            sc.center = sc.transform.InverseTransformPoint(result.suggestedCenter);
            sc.radius = Mathf.Max(0.001f, Mathf.Max(result.suggestedSize.x, result.suggestedSize.y, result.suggestedSize.z) * 0.5f + margin);
        }
        else if (col is MeshCollider mc)
        {
            // Для MeshCollider'ов (parametric primitives) лучше не трогать
        }

        EditorUtility.SetDirty(col);
    }

    // =====================================================
    // КОНЕЦ ВТОРОГО ПРОХОДА ВАЛИДАЦИИ
    // =====================================================

#endif
}
