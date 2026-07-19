# BadNorthBlackSpearman 整改清单

> 更新日期：2026-07-19
> 基于 BadNorthDatabase-main 资料与 BadNorthBlackSpearman 源码交叉审查

---

## 🔴 P0 — 待修复

### 1. 武器模型替换 — 依赖运行时找到 Pikeman 引用

**文件**：`BadNorthBlackSpearman/Plugin.cs`

**说明**：`ApplyPikemanSprite()` 使用三级回退策略查找长矛兵 Sprite（dict键名 → Faction Pikeman squad → 遍历dict匹配brain类型）。如果游戏版本中没有 Pikeman 类型，会输出 Warning 并跳过。需实际运行验证日志输出。

**验证方式**：查看 BepInEx 日志中是否有 `[BlackSpearman] Pikeman sprite found via...` 或 `Could not find any Pikeman reference`。

---

## 🟡 P1 — 待观察

### 2. HasNearbyEnemy 使用 FindObjectsOfType<Agent>() 全场景扫描

**文件**：`BadNorthBlackSpearman/SpearChargeComponent.cs`，行487

**说明**：每次冲刺检测敌人时遍历所有 Agent。性能影响有限（仅黑矛兵有此组件，且每0.15s触发一次检测而非每帧），大规模波次时可观察帧率。

---

### 3. 冲刺伤害依赖 Physics.OverlapSphere 的层掩码

**文件**：`BadNorthBlackSpearman/SpearChargeComponent.cs`，行243-251

**说明**：使用 `Vikings` + `Agents` 层过滤碰撞体。如果玩家方单位不在这些层上，可能检测不到。回退方案使用 `Physics.DefaultRaycastLayers`。需实际验证。

---

## 🟢 P2 — 长期改进

### 4. tmpfix 目录用途未说明

**目录**：`BadNorthEnemy-main/tmpfix/`

该目录的内容和用途在黑矛兵主项目中没有被引用或说明。建议补充 README 或清理。