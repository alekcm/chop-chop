# Интеграция второго прохода валидации в S4ColliderOptimizer.cs

## 1. Добавь класс `ColliderValidationSettings` и метод `ValidateAndTightenColliders` в класс `S4ColliderOptimizer`

Скопируй код из `S4ColliderOptimizer_ValidationPass.cs` внутрь класса `S4ColliderOptimizer` (например, перед закрывающей скобкой класса).

## 2. Измени метод `OptimizeForBatch` — добавь вызов валидации в конце

Найди в `OptimizeForBatch` место перед `return` (примерно строка 150-160 в исходном файле):

```csharp
// Strip disabled MeshColliders + their marker objects...
int removedDisabled = 0;
foreach (MeshCollider mc in root.GetComponentsInChildren(true))
{
    // ... existing code ...
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
    logFitDetails                   // Используй существующее поле логирования
};

int validationChanges = ValidateAndTightenColliders(root, validationSettings);
if (validationChanges > 0 && logFitDetails)
{
    Debug.Log($"[S4 collider optimizer] Validation pass: {validationChanges} colliders adjusted/removed.");
}

// === КОНЕЦ ДОБАВЛЕННОГО БЛОКА ===

// Count survivors for a clearer log line...
int remainingMeshes = root.GetComponentsInChildren(true).Length;
// ... остальной существующий код ...
```

## 3. Настройка порогов

| Параметр | Значение по умолчанию | Назначение |
|----------|----------------------|------------|
| `minVertexCoverage` | 0.15 (15%) | Мин. доля вершин модели, которые должны быть внутри коллайдера. Если меньше — коллайдер удаляется. |
| `maxEmptyVolumeRatio` | 0.85 (85%) | Макс. доля пустого объёма коллайдера. Если больше — коллайдер удаляется. |
| `shrinkMargin` | 0.02 | Запас в метрах (Unity units) при сужении, чтобы коллайдер не прижимался к вершинам вплотную. |
| `enableShrinking` | true | Включить сужение коллайдеров. |
| `enableRemoval` | true | Включить удаление «пустых» коллайдеров. |

## Как это работает

1. **После создания коллайдеров** (боксы, капсулы, сферы, примитивы) метод проходит по каждому.
2. **Находит соответствующий визуальный меш** по перекрытию bounds и близости центров.
3. **Считает вершины** визуального меша, попадающие внутрь коллайдера.
4. **Принимает решение**:
   - `coverage < 15%` ИЛИ `emptyVolume > 85%` → **удалить** коллайдер (он «висит в воздухе» или слишком широк)
   - `coverage < 50%` И `emptyVolume > 30%` → **сузить** коллайдер по вершинам внутри (PCA → OBB)
   - Иначе → оставить как есть
5. **Логирует** детали для каждого коллайдера (если `logFitDetails = true`).

## Пример лога

```
[S4 Validation] Box_003 vs chair_leg_L: coverage=12.5%, emptyVol=92.3%, remove=True, shrink=False - vertsIn=48/384, colVol=0.0452, vertVol≈0.0035
[S4 Validation] Box_007 vs chair_seat: coverage=67.2%, emptyVol=41.1%, remove=False, shrink=True - vertsIn=1204/1792, colVol=0.1234, vertVol≈0.0727
[S4 Validation] Done: 42 kept, 3 shrunk, 2 removed. Total was 47.
```

## Что это решает

- ❌ **Коллайдеры «из ниоткуда»** (созданные из шумных/раздробленных конвекс-халлов) — удаляются, так как покрытие вершин ≈ 0%
- ❌ **Слишком широкие боксы** (например, один бокс на две ножки стула) — сужаются до одной ножки или удаляются, если вершины внутри слишком мало
- ✅ **Хорошие коллайдеры** — остаются без изменений
- ✅ **Чуть недопокрывающие** — аккуратно сужаются с запасом `shrinkMargin`