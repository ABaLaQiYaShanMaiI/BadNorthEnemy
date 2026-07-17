# BadNorthBlackSpearman 整改清单

> 生成日期：2026-07-17  
> 基于 BadNorthDatabase-main/09文档第16章 与 BadNorthBlackSpearman 源码交叉审查

---

## 🔴 P0 — 必须修复（功能Bug / 严重视觉问题）

### 1. ApplyBlackColor 覆盖 R/G 通道导致显示「?」图标

**文件**：`BadNorthBlackSpearman/Plugin.cs`，行458-502

**问题**：
```csharp
// ❌ 当前代码（行472）
prop.SetValue(comp, BlackColor, null);
// BlackColor = new Color(0.08f, 0.02f, 0.02f, 1f)
```
这行代码将 `BatchedSprite.color` 的**全部 RGBA 四个通道**写入 Mesh 顶点色。但 Bad North 的 `SpriteAnimator.SetSprite2()` 将**sprite2 的图集坐标编码在 R（行索引）和 G（列索引）通道中**（见09文档 §16.1）。覆盖 R/G 后，Shader 无法正确采样 `_PartTex` 图集，导致敌人显示为「?」图标或纹理错乱。

**证据链**：
- `SpriteAnimator.SetSprite2()`（SpriteAnimator.cs 行101-118）：`c.r = (byte)(textureRect.min.y / 256)`，`c.g = (byte)(textureRect.min.x / 256)`
- `BatchedSprite.color` setter（BatchedSprite.cs 行182-198）：将全部4通道写入 `mesh.colors32`
- `Agent.UpdateColor()`（Agent.cs 行829-834）：仅修改 B 通道（健康度），保留 R/G

**修复方案**：
```csharp
// ✅ 正确做法：保留 R/G，仅修改 B/A
private static void ApplyBlackColor(Agent agent)
{
    var allComps = agent.GetComponentsInChildren<Component>(true);
    foreach (var comp in allComps)
    {
        if (comp == null) continue;
        var typeName = comp.GetType().FullName;

        if (typeName.EndsWith(".BatchedSprite") || typeName.EndsWith(".SpriteAnimator"))
        {
            var prop = comp.GetType().GetProperty("color",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(prop, null))
            {
                // ⭐ 关键修复：读取原色保留R/G，仅修改B/A
                Color oldColor = (Color)prop.GetValue(comp, null);
                Color newColor = new Color(
                    oldColor.r,   // ← 保留UV行索引
                    oldColor.g,   // ← 保留UV列索引
                    0.02f,        // ← 新蓝色分量（深色）
                    1f            // ← 新透明度
                );
                prop.SetValue(comp, newColor, null);
            }
        }
        // ...其余Render组件同样处理
    }
}
```

**预估影响**：修复后黑矛兵将恢复正常的敌人外观（而非`?`图标的纹理乱码）。

---

## 🟡 P1 — 建议优化（性能 / 健壮性）

### 2. FindObjectsOfType<Agent>() 每3秒全场景扫描的性能开销

**文件**：`BadNorthBlackSpearman/Plugin.cs`，行171

**问题**：在 `onAgentSpawned` 不可用的 fallback 路径中，每3秒调用 `FindObjectsOfType<Agent>()` 遍历场景中所有 Agent。在大规模波次中场景可能包含 50~100+ 个 Agent，频繁全扫描会造成帧率波动。

**建议**：
1. 优先确保 `onAgentSpawned` 可用——这是最优方案，无需轮询
2. 如果必须 fallback，在 `OnAgentSpawnedHandler` 中只处理已通过 `ConvertedAgents.Add()` 确认的新 Agent（已有此机制，但需要更快的新Agent检测）
3. 考虑将轮询间隔从 3s 增加到 5s（初始化后可降低频率）

**严重程度**：中（如果 `onAgentSpawned` 可用则此问题自动消失）

### 3. onAgentSpawned 反射检测可能漏检

**文件**：`BadNorthBlackSpearman/Plugin.cs`，行185-227

**问题**：`EnsureOnAgentSpawnedAvailable()` 使用 `IndexOf("AgentSpawned", StringComparison.OrdinalIgnoreCase)` 模糊匹配字段名。但：
- 只检查第一个找到的 Squad 实例（行213 `break`）
- 如果第一个 Squad 恰好是 English Squad（无该字段），后续 Viking Squad 不会被检查
- 没有 `break` 在正确的时机——`break` 在内层 `foreach(var field...)` 之后立即执行，但实际上对特定 `squad` 它是对的。更关键的是外层只遍历第一个 faction.squad

**当前代码**：
```csharp
foreach (var squad in faction.allSquads)
{
    // ...检查字段
    break; // ← 只检查第一个 Squad！
}
```

**建议**：遍历至少前 3 个 Squad（或找到匹配字段后停止），并确保至少检查了一个 Viking Squad。

### 4. Physics.Raycast 碰撞层需指定 Viking/Enemy Layer

**文件**：`BadNorthBlackSpearman/SpearChargeComponent.cs`，行238

**问题**：
```csharp
if (Physics.Raycast(_agent.transform.position, _chargeDirection, out hit, checkDist,
    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
```
`Physics.DefaultRaycastLayers` 会命中**所有碰撞体**（包括其他 Agent、尸体、房屋等）。冲刺可能被同阵营的其他 Viking 阻挡而提前结束。

**建议**：
- 使用 LayerMask 过滤，只对建筑物/地形/障碍物做射线检测
- 或者从 `_agent.transform.position + _chargeDirection * moveDelta` 新位置使用 `Physics.CheckSphere` 检测是否与建筑物碰撞

---

## 🟢 P2 — 功能增强（长期改进）

### 5. 缺少出场条件控制（LevelRule / LevelGuessable）

**现状**：黑矛兵 Mod 只在 Agent 生成后**运行时转化** SwordShield → 黑矛兵（40%概率），不控制哪些关卡会出现 SwordShield 本身。

**问题**：
- 如果玩家安装了关闭 SwordShield 出场的其他 Mod，黑矛兵也会消失
- 无法控制黑矛兵的出现概率和进度区间
- 不能在 UI 中显示独立的黑矛兵图标

**建议**（参考 BringBackBerserkers 模式）：
1. 创建独立的 VikingReference GameObject
2. 注册到 `LevelStateObjectReferences.dict`（名称如 `"Viking_BlackSpearman"`）
3. 设置 `LevelRule.condition.expression` 和 `LevelGuessable.probability`
4. `type` 字段可以复用 `VikingAgent.Type.SwordShield`（视觉通过颜色区分）

### 6. tmpfix 目录用途未说明

**目录**：`BadNorthEnemy-main/tmpfix/`（FixAsm.csproj + Program.cs）

该目录的内容和用途在黑矛兵主项目中没有被引用或说明。如果它是用于修复 Assembly-CSharp.dll 的工具，应补充 README 说明。如果不再需要，建议删除以减少混淆。

### 7. 冲刺期间 navPos 未更新

**文件**：`BadNorthBlackSpearman/SpearChargeComponent.cs`，行232

**问题**：冲刺中直接修改 `_agent.transform.position`，但不更新 `_agent.navPos`。这可能导致：
- FlowField 态势计算位置偏差
- AI 寻路系统认为 Agent 还在原位置
- 冲刺结束后 Agent 可能"闪现"回 navPos 位置

**建议**：冲刺结束后调用 `_agent.navPos = NavMesh.SamplePosition(_agent.transform.position)` 或其他同步机制。

---

## 📋 汇总

| # | 优先级 | 问题 | 文件 | 行号 | 状态 |
|---|--------|------|------|------|------|
| 1 | 🔴 P0 | ApplyBlackColor 覆盖 R/G → 显示`?` | Plugin.cs | 458-502 | 待修复 |
| 2 | 🟡 P1 | FindObjectsOfType 性能 | Plugin.cs | 171 | 建议优化 |
| 3 | 🟡 P1 | onAgentSpawned 反射检测不完整 | Plugin.cs | 185-227 | 建议加固 |
| 4 | 🟡 P1 | Raycast 碰撞层不够精确 | SpearChargeComponent.cs | 238 | 建议优化 |
| 5 | 🟢 P2 | 缺少 LevelRule/LevelGuessable | — | — | 长期计划 |
| 6 | 🟢 P2 | tmpfix 目录用途不明 | tmpfix/ | — | 需文档 |
| 7 | 🟢 P2 | 冲刺后 navPos 未同步 | SpearChargeComponent.cs | 232 | 潜在问题 |

---

## 附加建议：架构演进路线

```
当前（v1.0）：纯BepInEx + 反射 + 轮询
    ↓ P1 (#2, #3) 修复后
    ↓
建议（v1.1）：引入 MMHOOK-Assembly-CSharp，Hook GameSetup.Awake
    - 在 Awake 后获取所有 Agent 引用（无需轮询）
    - 性能提升显著
    ↓
建议（v2.0）：创建独立 VikingReference + 接入 AC-3 约束系统
    - 支持独立出场条件控制
    - UI 中显示独立图标
    - 可支持多兵种类型参数化配置