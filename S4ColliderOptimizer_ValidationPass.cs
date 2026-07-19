// Добавь этот код в класс S4ColliderOptimizer (например, в конец класса перед закрывающей скобкой)
// Это второй проход валидации коллайдеров после основной оптимизации

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
        Mesh visualMesh = FindBestVisualMesh(col, visualMeshes, root.transform);
        if (visualMesh == null)
        {
            if (settings.logDetails)
                Debug.Log($"[S4 Validation] {col.name}: no matching visual mesh found, keeping as-is.");
            kept++;
            continue;
        }

        var result = ValidateColliderAgainstMesh(col, visualMesh, root.transform, settings);

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
            ApplyShrink(col, result, settings.shrinkMargin);
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
static Mesh FindBestVisualMesh(Collider col, List<MeshFilter> visualMeshes, Transform rootTransform)
{
    // Стратегия 1: Поиск по имени (коллайдеры именуются Box_000, Capsule_001 и т.д.)
    // но оригинальные MeshCollider'ы имеют имена исходных мешей
    // Попробуем найти MeshFilter с похожим именем или в той же локальной позиции

    // Получаем мировые границы коллайдера
    Bounds colBounds = GetColliderWorldBounds(col);

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
        float overlap = BoundsOverlapRatio(colBounds, worldMeshBounds);
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
static Bounds GetColliderWorldBounds(Collider col)
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
static float BoundsOverlapRatio(Bounds a, Bounds b)
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
static ColliderValidationResult ValidateColliderAgainstMesh(
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

    Matrix4x4 meshLocalToWorld = visualMesh.bounds.size == Vector3.zero
        ? Matrix4x4.identity
        : Matrix4x4.TRS(visualMesh.bounds.center, Quaternion.identity, visualMesh.bounds.size); // placeholder

    // Находим MeshFilter для этого меша, чтобы получить правильный transform
    MeshFilter mf = FindMeshFilterForMesh(visualMesh, rootTransform);
    Matrix4x4 localToWorld = mf != null ? mf.transform.localToWorldMatrix : rootTransform.localToWorldMatrix;

    result.totalVertices = vertices.Length;
    int inside = 0;

    // Проверяем каждую вершину
    for (int i = 0; i < vertices.Length; i++)
    {
        Vector3 worldVertex = localToWorld.MultiplyPoint(vertices[i]);
        if (IsPointInCollider(worldVertex, col))
            inside++;
    }

    result.verticesInside = inside;
    result.verticesOutside = result.totalVertices - inside;
    result.coverageRatio = result.totalVertices > 0 ? (float)inside / result.totalVertices : 0f;

    // Оценка пустого объёма: объём коллайдера vs объём выпуклой оболочки вершин внутри
    float colVolume = GetColliderVolume(col);
    float verticesVolume = EstimateVerticesVolume(vertices, localToWorld, col);
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
        ComputeTightBounds(vertices, localToWorld, col, out result.suggestedCenter, out result.suggestedSize, out result.suggestedRotation);
    }

    result.debugInfo = $"vertsIn={inside}/{result.totalVertices}, colVol={colVolume:F4}, vertVol≈{verticesVolume:F4}";
    return result;
}

/// <summary>
/// Находит MeshFilter для данного меша в иерархии
/// </summary>
static MeshFilter FindMeshFilterForMesh(Mesh mesh, Transform root)
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
static bool IsPointInCollider(Vector3 worldPoint, Collider col)
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
        // (точную проверку делает физический движок, здесь approximation)
        return mc.bounds.Contains(worldPoint);
    }
    return false;
}

/// <summary>
/// Приблизительный объём коллайдера
/// </summary>
static float GetColliderVolume(Collider col)
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
static float EstimateVerticesVolume(Vector3[] vertices, Matrix4x4 localToWorld, Collider col)
{
    // Собираем вершины, которые внутри коллайдера
    var insideVerts = new List<Vector3>();
    for (int i = 0; i < vertices.Length; i++)
    {
        Vector3 w = localToWorld.MultiplyPoint(vertices[i]);
        if (IsPointInCollider(w, col))
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
static void ComputeTightBounds(Vector3[] vertices, Matrix4x4 localToWorld, Collider col,
    out Vector3 center, out Vector3 size, out Quaternion rotation)
{
    var insideVerts = new List<Vector3>();
    for (int i = 0; i < vertices.Length; i++)
    {
        Vector3 w = localToWorld.MultiplyPoint(vertices[i]);
        if (IsPointInCollider(w, col))
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
static void ApplyShrink(Collider col, ColliderValidationResult result, float margin)
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
        // Примерно: новая высота = size.y, новый радиус = max(size.x, size.z) / 2
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
        // или можно заменить на бокс, но это сложнее
    }

    EditorUtility.SetDirty(col);
}