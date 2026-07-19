# BadNorthBlackSpearman 整改清单

> 更新日期：2026-07-19

---

## 当前状态：专注外观实现，技能模块暂时暂停

### 🔴 P0 — 武器长矛替换（当前焦点）

| # | 任务 | 状态 |
|---|------|------|
| 1 | 从玩家方 Pikeman (Spear brain) 提取 `spearSprite` (BatchedSprite) | 📝 待完成 |
| 2 | 将长矛 BatchedSprite 挂载到黑矛兵 Agent | 📝 待完成 |
| 3 | 验证 Spear 渲染正确性 | 📝 待验证 |

**技术路线**：参考 09.01 文档 §14：
- Pikeman 使用独立的 `BatchedSprite spearSprite` (spearAnim 子对象)
- Brain 类名为 `"Spear"`
- 使用 `spearAnim.GetComponentInChildren<BatchedSprite>(true)` 获取

**执行顺序**：先替换 sprite2/武器，再 ApplyBlackColor（保留 R/G 通道）

### ⏸️ P1 — 冲刺技能模块（暂时暂停）

`SpearChargeComponent` 完整代码保留在 `SpearChargeComponent.cs` 中，但 `Plugin.cs` 中以下代码已注释：

```csharp
// ⏸️ 暂时注释 — 等待武器外观修复完成后启用
// var c = SpearChargeComponent.AddTo(agent);
// if (!ReferenceEquals(c, null)) c.Setup(agent);
```

**暂停原因**：
- AI 控制权冲突（`movability` + `maxSpeed` + `walkDir` 方式需进一步验证）
- 冲刺伤害检测未稳定命中（需要更准确的距离判定或 Physics.Overlap）
- 与武器外观功能并行使调试困难

**恢复条件**：武器外观功能稳定后，从注释中恢复。

### 🟡 P1 — 禁用 Swordsman 举盾逻辑

- `agent.shield = false` ✅ 已实现
- 禁用 Shield 子 GameObject ✅ 已实现
- ~~Destroy(Shield 组件)~~ ❌ 会导致模型消失，已移除

---

### 📋 汇总

| # | 优先级 | 任务 | 状态 |
|---|--------|------|------|
| 1 | 🔴 P0 | 从 Pikeman 提取 spearSprite + 挂载 | 📝 待完成 |
| 2 | ⏸️ P1 | 冲刺技能（已注释，准备恢复） | ⏸️ 暂停 |
| 3 | 🟡 P1 | 举盾禁用 | ✅ 完成 |
| 4 | 🟢 P2 | 手抄本图标 + 难度分级 + tmpfix 清理 | 长期 |