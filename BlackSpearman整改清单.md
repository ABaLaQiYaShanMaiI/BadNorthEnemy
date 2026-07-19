# BadNorthBlackSpearman 整改清单

> 更新日期：2026-07-19
> 基于 BadNorthDatabase-main 源码分析

---

## 🔴 P0 — 武器长矛替换（待深入研究）

### 技术障碍

经过多次尝试，`ApplyWeaponSwap()` 始终无法成功将长矛模型复制到黑矛兵身上。

**已尝试方案：**
1. ~~从 `LevelStateObjectReferences.dict` 查找 Viking_Pikeman~~ → dict 中只有维京方引用，没有 Pikeman
2. ~~从 `Faction.allSquads` 查找 Pikeman minionPrefab~~ → 在所有 Hook 时机（GameSetup.Awake、Landing.Spawn）Faction 的 allSquads 都为空或未初始化
3. ~~禁用 Shield 子对象~~ → ✅ 成功移除盾牌，但武器(Weapon)子对象仍是剑盾兵的剑

**根本原因：**

- Bad North 的武器不是独立模型，而是 `_PartTex` 图集 + UV 编码的 Sprite 渲染
- 玩家方 Squads 的初始化时机晚于我们 Hook 到的生命周期
- 直接 `Instantiate` 子 GameObject 无法生效，因为武器由 `SpriteAnimator.sprite/sprite2` 驱动

---

## 📋 研究任务（按优先级排序）

### 任务 1：研究 Pikeman 武器渲染管线

**文件**：BadNorthDatabase-main → `《BadNorth原版》Assembly-CSharp(源文件)/Assembly-CSharp/Voxels/TowerDefense/`

**需要查找的内容：**

1. **`Spear` 类源码**
   - 查找 `class Spear` 的完整实现
   - 重点关注 `spearLength`, `spearMidPos`, `attackSetting`, `spearDown` 等字段
   - 确认 Spear 是否是独立的 MonoBehaviour 组件

2. **武器 Sprite 系统**
   - `BatchedSprite` / `SpriteAnimator` 如何管理武器的外观切换
   - `_PartTex` 图集的采样机制
   - `sprite` vs `sprite2` 哪个控制武器部分

3. **参考 `AxeThrower.cs` 的 `ChangeSprite` 方法**
   - PlentyTraits/AxeThrower.cs 中如何成功的替换了士兵武器模型：
   ```csharp
   private void ChangeSprite(Agent agent) {
       SpriteAnimator pikemanAnimator = (LevelStateObjectReferences.dict["Viking_AxeThrower"] 
           as VikingReference).viking.agent.GetComponentInChildren<SpriteAnimator>();
       agent.GetComponentInChildren<SpriteAnimator>().sprite = pikemanAnimator.sprite;
       agent.GetComponentInChildren<SpriteAnimator>().sprite2 = pikemanAnimator.sprite2;
   }
   ```
   - 关键：从其他单位拿 `sprite` 和 `sprite2`，不需要 Instantiate

### 任务 2：找到玩家方 Pikeman 的 SpriteAnimator 引用来源

**核心问题**：从哪里获得 Pikeman 的 `SpriteAnimator.sprite` / `SpriteAnimator.sprite2`？

**候选来源（按获取难度排序）：**

1. **LevelStateObjectReferences.dict** 
   - 搜索反编译代码中 `LevelStateObjectReferences.AddToDict` 或 `dict.Add` 或 `dict[` 的所有位置
   - 确认 dict 中是否确实没有类似 `English_Pikeman` 或任何玩家方引用
   - 列出 dict 中所有 key 的完整清单

2. **Faction.allSquads 初始化时机**
   - 搜索 `allSquads` 在源码中何时被填充
   - 搜索 `AddSquad`、`CreateSquad` 或 `faction` 的初始化方法
   - 找到比 Landing.Spawn 更晚的 Hook 时机点

3. **ResourceList / PrefabManager / CampaignGeneration**
   - 搜索 `Pikeman` 关键字，看它是在哪里作为 prefab 被实例化的
   - `Squad.minionPrefab` 字段的反编译源码（确认字段名和类型）
   - 是否有全局的 prefab 注册表？

4. **Hook 更晚的生命周期**
   - 尝试 Hook `Campaign.Start()` / `LevelLoader` / `Faction.LateAwake`
   - 或者 Hook `Agent.Setup()` 时获取玩家的 Pikeman Agent 引用：
     ```csharp
     // Hook Agent.Setup 时检查 brain 是否为 Pikeman
     // 如果是，保存其 SpriteAnimator.sprite/sprite2
     ```

### 任务 3：确认长矛的 Sprite 数据

- 在 `_PartTex` 图集中定位长矛的 UV 区域
- 确认 `SpriteAnimator.SetSprite2()` 的完整逻辑（如何编码 UV 坐标到顶点色 R/G 通道）
- 长剑的 UV 坐标和长矛的 UV 坐标分别是什么？

---

## 🟡 P1 — 冲刺逻辑（已有进展，待完善）

### 当前状态

- ✅ 冲刺移动已修复（`maxSpeed` 恢复、`movability` 控制）
- ✅ Shield 子对象已成功禁用
- ⚠️ 伤害检测待验证（`_agent.movability=0` 修复了 AI 冲突）
- ⚠️ Swordsman 下蹲举盾逻辑仍然可能触发（agent 的 shield 属性可能需要通过反射设为 false）

### 建议

如果长矛武器成功替换后，还需禁用 Swordsman 的举盾行为：
```csharp
// 搜索 Agent 中是否有 shield 字段
typeof(Agent).GetField("shield", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
// 如果有，设为 false
```

---

## 🟢 P2 — 长期改进

### 4. 手抄本图标区分

为黑矛兵添加独立的 VikingReference 后，可在 UI 中显示独立图标。需准备 Sprite 资源。

### 5. 难度分级

根据关卡进度调整 `ConversionChance` 和属性倍率。

### 6. tmpfix 目录用途未说明

该目录的内容和用途在黑矛兵主项目中没有被引用或说明。建议补充 README 或清理。

---

## 📋 汇总

| # | 优先级 | 任务 | 状态 |
|---|--------|------|------|
| 1 | 🔴 P0 | 研究 Pikeman 武器渲染管线（Spear类/BatchedSprite） | 📝 待研究 |
| 2 | 🔴 P0 | 找到 Pikeman SpriteAnimator 引用来源（dict/Faction/Hook） | 📝 待研究 |
| 3 | 🔴 P0 | 确认长矛 Sprite UV 数据 | 📝 待研究 |
| 4 | 🟡 P1 | 冲刺伤害与 AI 冲突验证 | ⚠️ 待验证 |
| 5 | 🟡 P1 | 禁用 Swordsman 举盾逻辑 | ⚠️ 待实现 |
| 6 | 🟢 P2 | 手抄本图标 | 长期计划 |
| 7 | 🟢 P2 | 难度分级 | 长期计划 |
| 8 | 🟢 P2 | tmpfix 目录清理 | 待处理 |