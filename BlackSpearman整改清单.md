# BadNorthBlackSpearman 整改清单

> 更新日期：2026-07-28

---

## 当前状态：v1.15 — BlackSpearmanBrain + IBrainAction 集成

### ✅ 已完成 — Brain 层攻击替换（v1.15 🥇 最高优先级）

| # | 任务 | 状态 |
|---|------|------|
| 1 | 创建 BlackSpearmanBrain：Harmony Prefix 拦截 `Swordsman.GetAttack(Agent)` | ✅ v1.15 |
| 2 | 黑矛兵攻击向量替换为长矛风格四维参数（damage/kb/launch/stun） | ✅ v1.15 |
| 3 | 拦截 `Swordsman.range` 属性 getter，扩大判定范围（×3.5） | ✅ v1.15 |
| 4 | 在 `Plugin.Start()` 中注册 Brain Harmony Patch | ✅ v1.15 |

**设计原理**：
- Swordsman 拥有完整状态机（pursuing/hunting/attack/clash/adjecent网络），完全替换 Brain 会破坏战斗系统
- 通过 Harmony Prefix 仅拦截「攻击向量生成」，保留全部 AI 行为，仅修改命中参数
- 使用 `Plugin.ConvertedAgents` 判断是否对当前 Agent 生效

### ✅ 已完成 — IBrainAction 注入（v1.15 🥈 第二优先）

| # | 任务 | 状态 |
|---|------|------|
| 5 | SpearStabAction 实现 `IBrainAction` 接口，替代独立 Update | ✅ v1.15 |
| 6 | SpearChargeComponent 实现 `IBrainAction` 接口（启动由 Brain 调度） | ✅ v1.15 |
| 7 | SpearChargeComponent 执行阶段保留独立 Update（movability/物理检测每帧） | ✅ v1.15 |
| 8 | 参考 AxeThrowing 模式：MaybeAct 触发 → 执行阶段独立更新 | ✅ v1.15 |

**调度机制**：
- `Brain.Setup()` 通过 `GetComponentsInChildren<IBrainAction>()` 自动收集
- `Brain.IdleUpdate()` 每 hz8 轮询 `actions[i].MaybeAct(this)`
- MaybeAct 返回 `true` 消耗 action 机会，返回 `false` 按顺序轮询下一个

### ✅ 已完成 — 运行时验证修复（v1.15 🥉 第三优先）

| # | 任务 | 状态 |
|---|------|------|
| 9 | Physics.OverlapSphere layer mask：从 `GetMask("English")` 改为 `~0` + faction 过滤 | ✅ v1.15 |
| 10 | 验证 Attack 构造签名与 Attack.cs 完全一致（7参数版本） | ✅ v1.15 |
| 11 | 验证 BatchedSprite 武器挂载流程（Instantiate 深拷贝 + BatchedSprite 验证） | ✅ v1.14 |
| 12 | Spear 渲染正确性验证 | 📝 运行时 |

**Layer Mask 修复说明**：
- 原代码 `LayerMask.GetMask("English")` 在 Bad North 中可能返回 0（不存在名为 "English" 的 layer）
- Bad North 的 English 单位实际在 Default layer (0)
- 改用 `~0`（所有层），通过 `other.isViking` 进行 faction 过滤

### ✅ P1 — 冲刺技能模块（v1.15 改进）

`SpearChargeComponent` 改进：
- ✅ `IBrainAction` 接口 — 启动由 Brain 调度系统管理
- ✅ `movability = 0.5f`（参考 Spear.cs 官方实现）
- ✅ `Physics.OverlapSphere(~0)` 碰撞检测 + faction 过滤
- ✅ `Attack` 结构体 + `DealDamage()` 完整攻击链路

### ✅ P1 — 刺击技能模块（v1.15 改进）

`SpearStabAction` 改进：
- ✅ `IBrainAction` 接口 — 由 Brain 在 idle 状态 hz8 调度
- ✅ `Attack` 结构体 + `DealDamage()` 完整攻击链路
- ✅ 与 Swordsman pursuing/hunting 状态协调
- ✅ 眩晕通过 `AttackSettings.stun` 传递

---

### 📋 汇总

| # | 优先级 | 任务 | 状态 |
|---|--------|------|------|
| 1 | 🔴 P0 | 从 Pikeman 提取 spearAnim + 挂载 | ✅ v1.14 |
| 2 | 🔴 P0 | 攻击链路整合（Attack + DealDamage） | ✅ v1.14 |
| 3 | 🔴 P0 | Physics.OverlapSphere 碰撞检测 | ✅ v1.14 |
| 4 | 🥇 P0 | BlackSpearmanBrain：GetAttack() 拦截为长矛四维向量 | ✅ v1.15 |
| 5 | 🥇 P0 | BlackSpearmanBrain：range 属性扩大（×3.5） | ✅ v1.15 |
| 6 | 🥈 P1 | SpearStabAction → IBrainAction 注入 | ✅ v1.15 |
| 7 | 🥈 P1 | SpearChargeComponent → IBrainAction 注入 | ✅ v1.15 |
| 8 | 🥉 P1 | Layer mask 修复（~0 + faction 过滤） | ✅ v1.15 |
| 9 | 🥉 P1 | Attack 构造签名验证 | ✅ v1.15 |
| 10 | 🟡 P1 | movability = 0.5f AI 半限制 | ✅ v1.14 |
| 11 | 🟡 P1 | 举盾禁用 | ✅ v1.14 |
| 12 | 🟢 P2 | 手抄本图标 + 难度分级 | 📝 长期 |
| 13 | 🟢 P2 | tmpfix 清理 | 📝 长期 |

### 🔑 v1.15 关键架构决策

```
BlackSpearmanBrain（静态 Harmony Patch）
    ├── GetAttackPrefix  → 拦截 Swordsman.GetAttack(Agent)
    │   替换为 Spear 风格四维 Attack（damage/kb/launch/stun）
    │   仅对 ConvertedAgents 中包含的 Agent 生效
    └── RangeGetterPrefix → 拦截 Swordsman.range 属性
        扩大判定范围 ×3.5（模拟 spearLength 优势）

SpearStabAction : MonoBehaviour, IBrainAction
    └── MaybeAct(Brain) → Brain.idle.hz8 调度
        冷却/距离/锥角检查 → PerformStab → DealDamage

SpearChargeComponent : MonoBehaviour, IBrainAction
    ├── MaybeAct(Brain) → 检测敌人 → StartCharge
    └── Update() → DoCharging / UpdateCooldown（每帧）
```

### 文件清单

```
BadNorthBlackSpearman/
├── Plugin.cs                    ← v1.15（注册 Brain Patch + IBrainAction 说明）
├── BlackSpearmanBrain.cs        ← ⭐ v1.15 新建（GetAttack + range 拦截）
├── SpearChargeComponent.cs      ← v1.15（IBrainAction + layer mask 修复）
├── SpearStabAction.cs           ← v1.15（IBrainAction 改造）
├── BadNorthBlackSpearman.csproj
├── global.json
└── Properties/
```

### 🟡 已知问题（v1.15 遗留）

| # | 问题 | 严重度 | 说明 |
|---|------|--------|------|
| 1 | IBrainAction 优先级不确定 | 🟡 中 | `GetComponentsInChildren<IBrainAction>()` 返回顺序不保证，冲刺和刺击谁先被轮询取决于 Unity 内部顺序。可能永远只触发其中一个。需运行时验证，若出现则通过 `AxeThrowing` 模式的 `actions.Remove(this)` 管理竞争 |
| 2 | Layer mask `~0` 可能过度扫描 | 🟢 低 | `Physics.OverlapSphere` 使用 `~0` 会扫描地形/建筑 Collider，虽然后续 `GetComponentInParent<Agent>()` 过滤，但 `_hitBuffer[32]` 可能被非 Agent 占满。更优方案：反射获取 Agent 实际 layer 值并硬编码 `1 << layer` |
| 3 | `replace_in_file` 中文编码匹配失败 | 📝 工具 | 对包含中文注释的文件优先使用 `write_to_file` 完整重写，避免 SEARCH 块匹配失败反复回退 |

### 运行时验证清单

- [ ] 黑矛兵 `GetAttack()` 使用长矛四维向量（日志：`[Brain] Spear-style attack`）
- [ ] 黑矛兵攻击距离明显大于普通 SwordShield（range ×3.5）
- [ ] 冲刺 `IBrainAction.MaybeAct` 被 Brain 正确调度（日志出现 `[Charge] CHARGE!`）
- [ ] 刺击 `IBrainAction.MaybeAct` 被 Brain 正确调度（日志出现 `[Stab] Hit`）
- [ ] `Physics.OverlapSphere(~0)` 能检测到 English 单位（不再返回空）
- [ ] `Attack` 构造不抛出 MissingMethodException
- [ ] BatchedSprite 正确渲染长矛
- [ ] 冲刺和刺击交替触发，不存在某技能被另一技能完全抢占

### 推荐提交标题

```
v1.15: BlackSpearmanBrain + IBrainAction — 从数值剑盾兵到真正长矛兵

- 新建 BlackSpearmanBrain: Harmony Prefix 拦截 GetAttack() 替换为 Spear 四维攻击向量
- 拦截 Swordsman.range 属性扩大攻击判定范围 ×3.5
- SpearStabAction/SpearChargeComponent 实现 IBrainAction 注入 Swordsman.actions 列表
- 修复 Physics.OverlapSphere layer mask（"English"→~0 + faction 过滤）
- 验证 Attack 构造签名与 Attack.cs 完全一致
- 更新诊断日志和整改清单至 v1.15
```
