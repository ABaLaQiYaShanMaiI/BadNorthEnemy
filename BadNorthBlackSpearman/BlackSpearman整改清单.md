# BadNorthBlackSpearman 整改清单

> 更新日期：2026-08-08

---

## 当前状态：v1.18 — 武器系统重写 + CLR 兼容 + 主动诊断

### ✅ 已完成 — v1.18 武器系统重写

| # | 任务 | 状态 |
|---|------|------|
| 1 | `FindObjectsOfTypeAll(Spear)` 主动提取预制件武器（spearAnim + sprite2） | ✅ v1.18 |
| 2 | sprite2 替换：`SetSprite2(pikemanSprite2)` 三层 fallback（方法/字段/属性） | ✅ v1.18 |
| 3 | `ReapplyWeaponIfNeeded`：Instantiate(CachedSpearAnim) 克隆长矛 BatchedSprite | ✅ v1.18 |
| 4 | 长矛定位修正：基于 `agent.radius` 推算手持高度 | ✅ v1.18 |
| 5 | `ApplyWeaponToAllConverted`：武器缓存后追溯应用长矛 + sprite2 | ✅ v1.18 |
| 6 | BounceAnim 禁用（盾牌视觉移除） | ✅ v1.18 |
| 7 | sprite2 **不设为 null**（避免死亡时 `SpriteAnimator.GetMaterialKey()` NPE） | ✅ v1.18 |

### ✅ 已完成 — v1.18 CLR 兼容性修复

| # | 任务 | 状态 |
|---|------|------|
| 8 | `FieldInfo != null` → `!ReferenceEquals(field, null)` 避免 Mono CLR 2.0 缺失运算符 | ✅ v1.18 |
| 9 | `string.Join(IEnumerable)` → StringBuilder 手动拼接 | ✅ v1.18 |
| 10 | `Dictionary<string>` 枚举诊断：dict 为空时延迟重试 | ✅ v1.18 |

### ✅ 已完成 — v1.18 冲刺技能重写（复刻 Spear.cs）

| # | 任务 | 状态 |
|---|------|------|
| 11 | 状态机：Idle → WindUp(0.25s停步瞄准) → Charging(speed=1.0) → Stab(刺击) → Cooldown(1.5s) | ✅ v1.18 |
| 12 | 刺击：AttackSettings + DealDamage 完整链路 | ✅ v1.18 |
| 13 | 探测：Physics.OverlapSphereNonAlloc(~0, 5m) + faction 过滤 | ✅ v1.18 |

### ✅ 已完成 — v1.18 主动诊断系统

| # | 任务 | 状态 |
|---|------|------|
| 14 | `DumpSpearTypes()`：AppDomain 扫描 + FindObjectsOfTypeAll + 预制件武器提取 | ✅ v1.18 |
| 15 | `PatchBrainSetup()`：Hook Spear.Setup 最早捕获 | ✅ v1.18 |
| 16 | `LevelStateObjectReferences.dict` 完整键名导出 | ✅ v1.18 |
| 17 | 非 Viking Agent brain 类型周期性诊断 | ✅ v1.18 |
| 18 | Agent 层级结构 dump | ✅ v1.18 |

### ✅ 已完成 — v1.15 遗留（Brain 层 + IBrainAction）

| # | 任务 | 状态 |
|---|------|------|
| 19 | BlackSpearmanBrain：GetAttack() Harmony Prefix → Spear 四维向量 | ✅ v1.15 |
| 20 | BlackSpearmanBrain：range 属性 ×3.5 | ✅ v1.15 |
| 21 | SpearStabAction：IBrainAction 注入 | ✅ v1.15 |
| 22 | SpearChargeComponent：IBrainAction 注入 | ✅ v1.15 |

---

## 📊 实测状态总览

| 项目 | 状态 | 说明 |
|------|------|------|
| 🛡️ 盾牌视觉移除 | ✅ 通过 | BounceAnim 禁用生效 |
| ⚔️ 剑视觉移除 | ⚠️ 待验证 | sprite2 替换需 Pikeman 出现后生效 |
| 🔫 长矛生成 | ✅ 通过 | spearAnim → BatchedSprite 克隆 |
| 📍 长矛位置 | ⚠️ 待验证 | 基于 agent.radius 计算 |
| 🎨 颜色修改 | ⚠️ 待验证 | R/G 通道保护 |
| ⚡ 冲刺技能 | ⚠️ 部分通过 | WIND-UP + HIT 日志出现，但用户反馈"未复刻" |
| 🗡️ 刺击技能 | ⚠️ 待验证 | SpearStabAction IBrainAction |
| 💥 Spear-style 攻击 | ✅ 通过 | GetAttack 四维向量日志确认 |
| 📏 攻击范围 ×3.5 | ✅ 通过 | range Prefix 日志确认 |
| 💀 死亡崩溃 | ✅ 修复 | sprite2 不再设为 null |

---

## 🔴 待解决问题

### 🔴 P0-1：武器提取时机不稳定

**现象**：`FindObjectsOfTypeAll(Spear)` 在启动时找到 1 个 English_Spear 预制件（activeInHierarchy=False），但 `ExtractWeapon` 从预制件提取时必须确保 `spearAnim` 字段已序列化且 `BatchedSprite` 子对象完整。

**当前状态**：`DumpSpearTypes()` 中主动调用 `ExtractWeapon(spearBrain)`，待验证是否在启动时成功提取。

**预期日志**：
```
[DIAG] ✅ Weapon extracted proactively!
[WEAPON] Weapon cached via spearAnim->BatchedSprite: SpearAnim
```

### 🔴 P0-2：sprite2 替换仍需 Pikeman 实例

**现象**：`CachedSpearSprite2` 在 `ExtractWeapon` 中从 `brain.GetComponent<Agent>().GetComponentInChildren<SpriteAnimator>()` 获取。预制件上的 Agent 可能未完成 Setup，`SpriteAnimator` 可能尚未初始化。

**影响**：没有 Pikeman sprite2 → 无法替换 SwordShield 的剑盾 sprite2 → 剑视觉残留。

**探索方向**：从预制件直接获取 serialized `sprite2`（而非通过运行时 SpriteAnimator）。

### 🟡 P1-3：冲刺技能行为与预期不符

**现象**：用户反复反馈"和我方长矛兵的冲刺技能完全不一样"、"目前的技能完全就是加速"、"没有技能前摇、没有固定路径、更没有发生位移"。

**当前实现**：复刻 Spear.cs 源码的 charging(speed=1.0, 缓慢逼近) → stabbing(刺击) 模式。

**可能原因**：
- 用户期望的是**视觉上的快速冲刺/突进**（类似 dash），而非 Spear.cs 的慢速接近
- 移速 `ChargeSpeed=1.0` 可能太慢，看起来像普通行走
- `walkDir + maxSpeed` 可能被 Agent 内部系统限制
- 缺少视觉反馈（粒子、轨迹、音效等）

**探索方向**：
- 研究 Bad North 中是否有其他兵种的 dash/rush 机制
- 考虑用 `agent.transform.position +=` 配合 Rigidbody/MovePosition
- 增加 Chargespeed 到 3-5 并缩短持续时间

### 🟡 P1-4：武器必须在 Pikeman 实例化后才能获取

**现象**：之前的多轮测试中，English_Spear 实例在 frame 900+ 才出现（玩家部署 pikemen 后），而 Viking 在 frame 700 就已转化。v1.18 通过 `FindObjectsOfTypeAll` 从预制件主动提取试图解决此问题。

**备用方案**：如果预制件提取失败，保留 `SearchForPikemanWeapon` 的多源搜索链路（VR → Live Agent → Resources）作为 fallback。

### 🟢 P2-5：代理转化时机

**现象**：`GameSetup.Awake` 时 `LevelStateObjectReferences.dict` 为空，导致 `EnsureSwordShieldAlwaysAvailable()` 和 `RegisterBlackSpearmanReference()` 静默失败。

**影响**：SwordShield 可能在某些关卡不出现，BlackSpearman 的 VR 键未正确注册。

**方案**：将这两个方法移到 `Landing.Spawn` 后或使用协程延迟。

---

## 📁 文件清单

```
BadNorthBlackSpearman/
├── Plugin.cs                    ← v1.18（武器系统重写 + CLR兼容 + 主动诊断）
├── BlackSpearmanBrain.cs        ← v1.15（GetAttack + range 拦截）
├── SpearChargeComponent.cs      ← v1.18（复刻 Spear.cs charging→stabbing）
├── SpearStabAction.cs           ← v1.15（IBrainAction 刺击）
├── BadNorthBlackSpearman.csproj
├── global.json                  ← 修复 SDK 版本 8.0.422→8.0.421
└── Properties/
```

---

## 推荐提交标题

```
v1.18 - 武器系统重写 + CLR兼容 + 主动诊断

- 武器: FindObjectsOfTypeAll 主动提取 Spear 预制件 spearAnim+sprite2
- 武器: sprite2 替换三层fallback(SetSprite2/字段/属性) + 死亡NPE修复
- 武器: 长矛定位基于 agent.radius 重新计算 + BounceAnim 盾禁用
- 冲刺: 复刻 Spear.cs 状态机 Idle→WindUp→Charging→Stab→Cooldown
- CLR: FieldInfo.op_Inequality/string.Join 兼容 Unity 2018 Mono 2.0
- 诊断: DumpSpearTypes + Brain.Setup Hook + Dict键名导出 + 层级dump
- 修复: SDK 8.0.422→8.0.421 + global.json rollForward
```
