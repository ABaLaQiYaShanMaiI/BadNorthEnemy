# BadNorthBlackSpearman 整改清单

> 更新日期：2026-07-28

---

## 当前状态：v1.14 — 攻击链路已整合

### ✅ 已完成 — 攻击链路整合（v1.14）

| # | 任务 | 状态 |
|---|------|------|
| 1 | 从玩家方 Pikeman (Spear brain) 提取 `spearAnim` → BatchedSprite | ✅ 完成 |
| 2 | 将长矛 BatchedSprite 挂载到黑矛兵 Agent | ✅ 完成 |
| 3 | SpearChargeComponent：Attack 结构体 + `DealDamage()` 完整链路 | ✅ v1.14 |
| 4 | SpearStabAction：Attack 结构体 + `DealDamage()` 完整链路 | ✅ v1.14 |
| 5 | 冲刺命中检测：Physics.OverlapSphere 替代 FindObjectsOfType | ✅ v1.14 |
| 6 | movability = 0.5f（Spear 风格半限制，替代完全禁用 AI） | ✅ v1.14 |
| 7 | 刺击与 Swordsman pursuing/hunting 状态协调 | ✅ v1.14 |
| 8 | 版本号升级到 v1.14，更新诊断日志 | ✅ v1.14 |

**攻击链路改进效果**：

| 旧方式（v1.13） | 新方式（v1.14） |
|----------------|----------------|
| `target.health -= dmg` 直接扣血 | `target.DealDamage(attack)` 走完整链路 |
| `target.transform.position += kb` 直接位移 | `AttackSettings.knockback` 通过 DealDamage 击退 |
| `stunMultiplier = 10f` 手动设置 | `AttackSettings.stun` 通过 `Stun.PostAttack` 自然累加 |
| ❌ 护甲（Armor）不生效 | ✅ `IAttackResponder.ModifyAttack` 减伤 |
| ❌ 无命中音效/特效 | ✅ `Attack.sound` / `Attack.effect` |
| ❌ 无死亡血迹 | ✅ `Death.StartDie → Die` 完整流程 |

### 🔴 P0 — 武器长矛替换

| # | 任务 | 状态 |
|---|------|------|
| 1 | 从玩家方 Pikeman (Spear brain) 提取 `spearAnim` (BatchedSprite) | ✅ 完成 |
| 2 | 将长矛 BatchedSprite 挂载到黑矛兵 Agent | ✅ 完成 |
| 3 | 验证 Spear 渲染正确性 | 📝 待验证 |

**技术路线**：参考 09.01 文档 §14：
- Pikeman 使用独立的 `BatchedSprite spearSprite` (spearAnim 子对象)
- Brain 类名为 `"Spear"`
- 使用 `spearAnim.GetComponentInChildren<BatchedSprite>(true)` 获取

### ✅ P1 — 冲刺技能模块（v1.14 已修复并启用）

`SpearChargeComponent` 已重新启用，改进如下：
- ✅ `movability = 0.5f`（参考 Spear.cs 官方实现，避免 AI 冲突）
- ✅ `Physics.OverlapSphere` 碰撞检测（性能更好）
- ✅ `Attack` 结构体 + `DealDamage()` 完整攻击链路

### ✅ P1 — 刺击技能模块（v1.14 新增）

`SpearStabAction` 已新增，特性：
- ✅ `Attack` 结构体 + `DealDamage()` 完整攻击链路
- ✅ 与 Swordsman pursuing/hunting 状态协调，避免与 JumpAttack 冲突
- ✅ 眩晕通过 `AttackSettings.stun` 传递

### 🟡 P1 — 禁用 Swordsman 举盾逻辑

- `agent.shield = false` ✅ 已实现
- 禁用 Shield 子 GameObject ✅ 已实现
- ~~Destroy(Shield 组件)~~ ❌ 会导致模型消失，已移除

---

### 📋 汇总

| # | 优先级 | 任务 | 状态 |
|---|--------|------|------|
| 1 | 🔴 P0 | 从 Pikeman 提取 spearAnim + 挂载 | ✅ 完成 |
| 2 | 🔴 P0 | 攻击链路整合（Attack + DealDamage） | ✅ v1.14 |
| 3 | 🔴 P0 | Physics.OverlapSphere 碰撞检测 | ✅ v1.14 |
| 4 | 🟡 P1 | movability = 0.5f AI 半限制 | ✅ v1.14 |
| 5 | 🟡 P1 | 刺击与 pursuing/hunting 协调 | ✅ v1.14 |
| 6 | 🟡 P1 | 举盾禁用 | ✅ 完成 |
| 7 | 🟢 P2 | Brain 级别替换为自定义 BlackSpearmanBrain | 📝 中长期 |
| 8 | 🟢 P2 | IBrainAction 注入冲刺/刺击 | 📝 中长期 |
| 9 | 🟢 P2 | 手抄本图标 + 难度分级 + tmpfix 清理 | 长期 |