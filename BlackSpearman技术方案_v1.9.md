# BlackSpearman v1.9 · 完整技术方案与缺漏诊断

> **撰写日期**：2026-07-19  
> **诊断对象**：BadNorthBlackSpearman v1.8（Plugin.cs + SpearChargeComponent.cs）  
> **目标**：从 SwordShield 维京人转化出「持长矛 + 黑色外观 + Pikeman 式刺击行为」的黑矛兵，出厂概率 100%

---

## 1. v1.8 现状与问题全景图

### 1.1 架构概览

```
v1.8 架构：
  GameSetup.Awake Hook
      ├── EnsureSwordShieldAlwaysAvailable()  ← 确保 SwordShield 出场
      └── RegisterBlackSpearmanReference()    ← 注册新的 VikingReference
  
  Landing.Spawn Hook
      └── 遍历 longship.agents
          └── OnAgentSpawnedHandler(agent)
              ├── 筛选: isViking + type==SwordShield + 未转化 + 100%概率
              └── ApplyBlackSpearman(agent)
                  ├── ApplyWeaponSwap (⭐ 长矛BatchedSprite)
                  ├── DisableShield
                  ├── ApplyBlackColor (R/G保护 + B=0.02)
                  ├── scale ×1.05
                  ├── damageLevels ×1.6 / knockbackLevels ×2.5
                  ├── ApplyArmor (×1.3)
                  ├── SpearChargeComponent (⏸️ 暂注释)
                  └── UpdateVikingReference
```

### 1.2 问题分类总览

| 优先级 | 编号 | 类别 | 问题 | 状态 |
|--------|------|------|------|------|
| 🔴 P0 | #1 | 武器 | BatchedSprite 动态创建未初始化 Mesh/Material | 致命 |
| 🔴 P0 | #2 | 武器 | spearSprite 的 sprite 属性反射获取可能为 null | 致命 |
| 🔴 P0 | #3 | 冲刺 | HitRadius=1.5 / ChargeDistance=3.5 / ChargeDuration=0.58 太小 | 功能无效 |
| 🟡 P1 | #4 | 渲染 | AgentTextureBaker 可能在后续帧覆盖颜色修改 | 颜色可能丢失 |
| 🟡 P1 | #5 | AI | 缺少 IBrainAction 长矛刺击行为（目前只有冲刺） | 功能缺漏 |
| 🟡 P1 | #6 | 数值 | 未增大 Swordsman 的 attackRange/idealRange 模拟长矛距离 | 体验不足 |
| 🟡 P1 | #7 | 护甲 | Armor 浮点数组是共享引用，修改影响所有同类 Agent | 副作用 |
| 🟢 P2 | #8 | 冲刺 | FindObjectsOfType<Agent> 每0.1s全场景遍历性能差 | 性能 |
| 🟢 P2 | #9 | 注册 | RegisterBlackSpearmanReference 创建的 VikingReference 缺少 Start() 调用 | UI图标 |
| 🟢 P2 | #10 | 架构 | 未使用 MMHOOK 的 Squad.onAgentSpawned（比 Landing Hook 更精确） | 架构优化 |

---

## 2. 🔴 P0 问题详细分析

### P0-1：BatchedSprite 动态创建问题

**位置**：`Plugin.cs` Line 239-259 `ApplyWeaponSwap()`

**当前代码**：
```csharp
var spearObj = new GameObject("Spear");
spearObj.transform.SetParent(agent.transform);
var bs = spearObj.AddComponent<BatchedSprite>();
// 反射设置 sprite + color...
```

**问题根源**：`BatchedSprite.Awake()` 中执行了大量初始化：
```csharp
// BatchedSprite.cs:217-247
public void Awake()
{
    this.mesh = BatchedSprite.GetMesh();           // ← 从对象池获取四边形 Mesh
    this.SetMeshToSprite(this.mesh, this.bSprite); // ← 根据 sprite.textureRect 设置 UV
    this.block.SetTexture(ShaderId.mainTexId, this.bSprite.texture); // ← 设置主纹理
    // 创建 Rend 子对象（MeshRenderer + MeshFilter）
    this.rends = new BatchedSprite.Rend[] { ... };
    // 禁用原生 SpriteRenderer
    this._spriteRenderer.enabled = false;
}
```

**致命问题**：
1. `AddComponent<BatchedSprite>()` 触发 `Awake()` 时，`sprite` 还未通过反射设置 → `this.bSprite` 为 null → UV/Mesh 初始化失败
2. 即使后面通过反射设置了 sprite，`Awake()` 已经执行完毕，**不会再次初始化**
3. `BatchedSprite` 依赖 `_spriteRenderer`（SerializeField）——动态创建的 GameObject 上**没有这个组件**，`Awake()` 会 NPE

**解决方案**：
```csharp
// ✅ 正确做法：先拷贝 Pikeman 的完整 spearAnim 子对象树
private static void ApplyWeaponSwap(Agent agent)
{
    if (ReferenceEquals(CachedSpearAnim, null)) return;
    
    // 深拷贝整个 spearAnim 子树（包含已初始化的 BatchedSprite 及其 Rend 子对象）
    var spearClone = UnityEngine.Object.Instantiate(CachedSpearAnim.gameObject);
    spearClone.name = "Spear";
    spearClone.transform.SetParent(agent.transform);
    spearClone.transform.localPosition = SpearLocalPos;
    spearClone.transform.localRotation = SpearLocalRot;
    spearClone.transform.localScale = SpearLocalScale;
    
    // Instantiate 会保持 BatchedSprite 的已初始化状态（Mesh/UV/Material 全部就绪）
    // 只需修改颜色
    var bs = spearClone.GetComponentInChildren<BatchedSprite>(true);
    if (!ReferenceEquals(bs, null))
    {
        var cp = bs.GetType().GetProperty("color", ...);
        if (!ReferenceEquals(cp, null))
            cp.SetValue(bs, new Color(0.02f, 0.25f, 0.02f, 1f), null);
    }
}
```

**需要的额外缓存**：
```csharp
internal static GameObject CachedSpearAnim;  // ← 缓存 spearAnim GameObject（非 Transform）
```

---

### P0-2：spearSprite 的 sprite 属性获取

**位置**：`Plugin.cs` Line 149-161 `ExtractWeapon()`

**当前代码**：
```csharp
var bs = spearAnim.GetComponentInChildren<BatchedSprite>(true);
var sp = bst.GetProperty("sprite", ...);
SpearSprite = sp.GetValue(bs, null) as Sprite;
```

**问题**：`BatchedSprite` 的 `sprite` 属性定义在哪里？
- `BatchedSprite` 本身没有 `sprite` 属性
- `SpriteAnimator`（BatchedSprite 子类）有 `sprite` 和 `sprite2` 两个属性
- Pikeman 的 `spearSprite` 是一个 `BatchedSprite`（不是 `SpriteAnimator`）
- 所以 `bSprite` 可能是 `BatchedSprite` 的内部属性名

**证据**（Spear.cs Line 141,220）：
```csharp
public BatchedSprite spearSprite;  // ← 字段类型是 BatchedSprite，不是 SpriteAnimator
```

因此应该用 `bSprite` 属性（BatchedSprite.cs 的内部属性）而不是 `sprite`。

**修复方案**：缓存整个 spearAnim GameObject 而非单独提取 sprite
```csharp
// ✅ 直接 Instantiate 时用原始颜色 + 运行时改色
private static bool ExtractWeapon(Brain brain)
{
    FieldInfo saf = brain.GetType().GetField("spearAnim", ...);
    if (saf == null) return false;
    
    Transform spearAnim = saf.GetValue(brain) as Transform;
    if (spearAnim == null) return false;
    
    // 直接缓存 spearAnim 的 GameObject
    CachedSpearAnim = spearAnim.gameObject;
    SpearLocalPos = spearAnim.localPosition;
    SpearLocalRot = spearAnim.localRotation;
    SpearLocalScale = spearAnim.localScale;
    WeaponCached = true;
    
    return true;
}
```

---

### P0-3：冲刺参数不足

**位置**：`SpearChargeComponent.cs` Line 11-18

| 参数 | 当前值 | 问题 | 建议值 |
|------|--------|------|--------|
| `ChargeDistance` | 3.5m | 冲刺距离太短，敌人稍有位移就错过 | 5.0m |
| `ChargeDuration` | 0.58s | 3.5m/6mps=0.58s，时间太短无法覆盖足够范围 | 0.83s |
| `HitRadius` | 1.5m | 命中判定半径太小，敌人侧移0.5m就错过 | 2.5m |
| `DetectionRadius` | 5.0m | ✅ 合理 | 保持 |
| `ChargeCooldown` | 8.0s | ✅ 合理 | 保持 |

**修复**：
```csharp
private const float ChargeDistance = 5.0f;   // 3.5 → 5.0
private const float ChargeDuration = 0.83f;  // 0.58 → 0.83 (5.0/6.0)
private const float HitRadius = 2.5f;        // 1.5 → 2.5
```

---

## 3. 🟡 P1 问题详细分析

### P1-4：AgentTextureBaker 可能覆盖颜色

**背景**：`AgentTextureBaker` 将同类 Agent 烘焙到共享 RenderTexture。如果它在转化后执行，会用原始纹理覆盖颜色修改。

**时机链**：
```
Landing.Spawn() → OnAgentSpawnedHandler → ApplyBlackColor 
    → 后续帧 AgentTextureBaker.Draw() → 可能覆盖颜色
```

**问题**：`BatchedSprite.color` 修改的是 Mesh 顶点色，而 `AgentTextureBaker` 用的是 `MaterialPropertyBlock`。两者是不同层级：
- `BatchedSprite.color` → `mesh.colors32`（顶点色层）
- `AgentTextureBaker` → CommandBuffer 绘制 → RenderTexture（纹理层）

**实际情况**：`AgentTextureBaker` 用于特定 UI/预览场景，游戏内战斗渲染走 BatchedSprite 管线。当前颜色修改**大概率不会被覆盖**。但需要验证。

**防御策略**：在 `LateUpdate` 中做一次校验：
```csharp
// 添加到 SpearChargeComponent 中
private int _colorCheckFrame;
private void LateUpdate()
{
    if (_colorCheckFrame < 10)  // 前10帧校验
    {
        _colorCheckFrame++;
        ReapplyColorIfNeeded();
    }
}
```

---

### P1-5：缺少 IBrainAction 长矛刺击

**目标**：模拟 Pikeman 的刺击攻击——长距离、直线穿刺、高击退。

**方案**：新增 `SpearStabAction : MonoBehaviour, IBrainAction`

```csharp
public class SpearStabAction : MonoBehaviour, IBrainAction
{
    private const float StabRange = 2.5f;        // 刺击范围（长矛距离）
    private const float StabCooldown = 1.2f;     // 冷却
    private const float StabDamage = 2.0f;       // 伤害
    private const float StabKnockback = 3.0f;    // 击退
    private const float StabAngle = 30f;         // 刺击锥角（度）
    
    private Agent _agent;
    private float _lastStabTime = -999f;
    
    void IBrainAction.MaybeAct(Brain brain)
    {
        if (Time.time - _lastStabTime < StabCooldown) return;
        if (!_agent.enemyData.dangerous) return;  // 只在有敌人在附近时触发
        
        // 直线穿刺判定：前方锥形 + 长距离
        Agent target = _agent.enemyAgent;
        if (ReferenceEquals(target, null)) return;
        
        float dist = Vector3.Distance(_agent.transform.position, target.transform.position);
        if (dist > StabRange) return;
        
        // 锥形方向判定
        Vector3 toTarget = (target.transform.position - _agent.transform.position).normalized;
        float angle = Vector3.Angle(_agent.transform.forward, toTarget);
        if (angle > StabAngle / 2f) return;
        
        // 执行刺击
        _lastStabTime = Time.time;
        // ... 伤害 / 击退 ...
    }
}
```

**注册方式**：在 `ApplyBlackSpearman` 末尾添加：
```csharp
agent.gameObject.AddComponent<SpearStabAction>();
```

**注意**：Brain.Setup() 中通过 `GetComponentsInChildren<IBrainAction>()` 自动收集——只要组件在 Agent 上，就会被自动调用。

---

### P1-6：未增大 Swordsman 攻击距离

**目标**：Swordsman 默认近战范围约 1.2~1.5m，长矛兵应该有更大的攻击距离。

**Swordsman 中相关字段**（反射修改）：
| 字段 | 类型 | 推测用途 | 建议值 |
|------|------|----------|--------|
| `attackRange` | float | 可攻击的最远距离 | 2.5（原~1.5） |
| `idealRange` | float | 理想攻击距离 | 2.0（原~1.0） |
| `agent.radius` | float | 碰撞半径 | 原×1.1 |

**实现**：
```csharp
private static void ApplySpearCombatStats(Agent agent)
{
    var s = agent.brain as Swordsman;
    if (ReferenceEquals(s, null)) return;
    
    // 反射修改 Swordsman 的私有字段
    SetFloatField(s, "attackRange", 2.5f);
    SetFloatField(s, "idealRange", 2.0f);
    
    // 略微增大碰撞半径
    agent.radius *= 1.1f;
}

private static void SetFloatField(object obj, string name, float value)
{
    var f = obj.GetType().GetField(name, 
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (!ReferenceEquals(f, null))
        f.SetValue(obj, value);
}
```

---

### P1-7：Armor 数组共享引用

**位置**：`Plugin.cs` Line 292-298 `ApplyArmor()`

**问题代码**：
```csharp
ScaleFloatArray(_armorField.GetValue(a) as float[], ArmorMultiplier);
// ↑ 直接修改了 Armor 组件内部的 float[] 引用
// 如果多个 Agent 共享同一个 Armor 模板，所有都会被影响
```

**修复**：
```csharp
private static void ApplyArmor(Agent agent)
{
    var a = agent.GetComponent<Armor>();
    if (ReferenceEquals(a, null)) return;
    
    if (!_armorFieldAttempted) { ... }
    
    float[] original = _armorField.GetValue(a) as float[];
    if (ReferenceEquals(original, null)) return;
    
    // ✅ 创建独立副本再修改，避免影响其他 Agent
    float[] copy = new float[original.Length];
    Array.Copy(original, copy, original.Length);
    for (int i = 0; i < copy.Length; i++) copy[i] *= ArmorMultiplier;
    _armorField.SetValue(a, copy);
}
```

---

## 4. 🟢 P2 问题

### P2-8：冲刺伤害检测性能

`FindObjectsOfType<Agent>()` 每 0.1 秒全场景遍历——Bad North 场景中 Agent 数量通常 <100，实际影响不大。长期建议改为 `Physics.OverlapSphere`。

### P2-9：VikingReference 缺少 Start() 调用

`RegisterBlackSpearmanReference()` 中用 `new GameObject` + `AddComponent<VikingReference>` 创建的对象，其 `Start()` 不会被调用（因为不在场景层级中激活）。`Start()` 负责：
- 实例化 `vikingClone`
- 提取 `sprite2` 供 UI 使用

**影响**：UI 中黑矛兵的图标可能为空。

**修复**：手动调用 Start()（通过反射）或在 CopyVikingReferenceFields 中复制 `sprite2`。

---

## 5. 完整修复计划

### Phase 1：P0 致命修复（必须）

| 步骤 | 文件 | 改动 |
|------|------|------|
| 1-a | Plugin.cs | 修改 `ExtractWeapon()` — 缓存 `spearAnim.gameObject` 而非单独 sprite |
| 1-b | Plugin.cs | 修改 `ApplyWeaponSwap()` — 用 `Instantiate(CachedSpearAnim)` 替代 `AddComponent<BatchedSprite>` |
| 1-c | SpearChargeComponent.cs | 修改常量：`ChargeDistance=5.0` / `ChargeDuration=0.83` / `HitRadius=2.5` |
| 1-d | Plugin.cs | 在 `ApplyBlackSpearman` 中取消 SpearChargeComponent 的注释并恢复启用 |

### Phase 2：P1 质量提升（推荐）

| 步骤 | 文件 | 改动 |
|------|------|------|
| 2-a | 新建 SpearStabAction.cs | 实现 `IBrainAction` 刺击行为 |
| 2-b | Plugin.cs | `ApplyBlackSpearman` 中 `AddComponent<SpearStabAction>()` |
| 2-c | Plugin.cs | 新增 `ApplySpearCombatStats()` — 增大 attackRange/idealRange |
| 2-d | Plugin.cs | 修复 `ApplyArmor()` — 创建数组副本再修改 |
| 2-e | Plugin.cs | 修复 `RegisterBlackSpearmanReference()` — 手动初始化 sprite2 |

### Phase 3：P2 长期改进（可选）

| 步骤 | 文件 | 改动 |
|------|------|------|
| 3-a | SpearChargeComponent.cs | 用 `Physics.OverlapSphere` 替代 `FindObjectsOfType<Agent>` |
| 3-b | Plugin.cs | 研究 Squad.onAgentSpawned Hook 替代 Landing.Spawn Hook |

---

## 6. 文件变更清单

```
BadNorthEnemy-main/BadNorthBlackSpearman/
├── Plugin.cs                    ← 🔧 修改（P0-1/2 + P1-4/6/7）
├── SpearChargeComponent.cs      ← 🔧 修改（P0-3 + 恢复启用）
├── SpearStabAction.cs           ← ⭐ 新建（P1-5）
└── BlackSpearman技术方案_v1.9.md ← ⭐ 本文档
```

---

## 7. 关键代码片段汇总

### 7.1 ExtractWeapon() — 修复版

```csharp
internal static GameObject CachedSpearAnim;  // ⭐ 新增缓存

private static bool ExtractWeapon(Brain brain)
{
    Type st = brain.GetType();
    FieldInfo saf = st.GetField("spearAnim", 
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    
    if (ReferenceEquals(saf, null)) return false;
    
    Transform spearAnim = saf.GetValue(brain) as Transform;
    if (ReferenceEquals(spearAnim, null)) return false;
    
    // ⭐ 缓存整个 GameObject 用于后续 Instantiate
    CachedSpearAnim = spearAnim.gameObject;
    SpearLocalPos = spearAnim.localPosition;
    SpearLocalRot = spearAnim.localRotation;
    SpearLocalScale = spearAnim.localScale;
    WeaponCached = true;
    
    LogInfo("Pikeman weapon cached: spearAnim at localPos=" + SpearLocalPos);
    return true;
}
```

### 7.2 ApplyWeaponSwap() — 修复版

```csharp
private static void ApplyWeaponSwap(Agent agent)
{
    if (ReferenceEquals(CachedSpearAnim, null)) return;
    
    // ⭐ 深拷贝 Pikeman 的完整 spearAnim 子树
    var spearClone = UnityEngine.Object.Instantiate(CachedSpearAnim);
    spearClone.name = "Spear";
    spearClone.transform.SetParent(agent.transform);
    spearClone.transform.localPosition = SpearLocalPos;
    spearClone.transform.localRotation = SpearLocalRot;
    spearClone.transform.localScale = SpearLocalScale;
    
    // ⭐ Instantiate 保留了 BatchedSprite 的完整初始化状态
    // 只需修改颜色为暗色
    var bs = spearClone.GetComponentInChildren<BatchedSprite>(true);
    if (!ReferenceEquals(bs, null))
    {
        var cp = bs.GetType().GetProperty("color", 
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (!ReferenceEquals(cp, null))
        {
            Color old = (Color)cp.GetValue(bs, null);
            // 暗绿色调（保留R/G的UV编码）
            cp.SetValue(bs, new Color(old.r, old.g, 0.02f, 1f), null);
        }
    }
    
    LogInfo("Spear weapon instantiated from cached Pikeman spearAnim");
}
```

### 7.3 ApplySpearCombatStats() — 新增

```csharp
private static void ApplySpearCombatStats(Agent agent)
{
    var s = agent.brain as Swordsman;
    if (ReferenceEquals(s, null)) return;
    
    // 增大攻击距离（反射修改私有字段）
    var arField = typeof(Swordsman).GetField("attackRange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (!ReferenceEquals(arField, null))
    {
        float current = (float)arField.GetValue(s);
        arField.SetValue(s, Mathf.Max(current, 2.5f));
    }
    
    var irField = typeof(Swordsman).GetField("idealRange",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (!ReferenceEquals(irField, null))
    {
        float current = (float)irField.GetValue(s);
        irField.SetValue(s, Mathf.Max(current, 2.0f));
    }
    
    // 略微增大碰撞半径
    agent.radius *= 1.1f;
}
```

### 7.4 SpearStabAction — 新增完整文件

```csharp
using System;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 模拟 Pikeman 的长矛刺击行为。
    /// 作为 IBrainAction 挂载在 Swordsman Brain 上。
    /// </summary>
    public class SpearStabAction : MonoBehaviour, IBrainAction
    {
        private const float StabRange = 2.5f;
        private const float StabCooldown = 1.4f;
        private const float StabDamage = 2.0f;
        private const float StabKnockback = 3.0f;
        private const float StabAngle = 35f;  // 锥形半角

        private Agent _agent;
        private Swordsman _swordsman;
        private float _lastStabTime = -999f;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            _swordsman = GetComponent<Swordsman>();
        }

        // IBrainAction 接口
        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (Time.time - _lastStabTime < StabCooldown) return false;
            if (ReferenceEquals(_agent, null)) return false;
            if (!_agent.aliveState.active) return false;
            
            // 只在有敌人在近战范围内时触发刺击
            var enemy = _agent.enemyAgent;
            if (ReferenceEquals(enemy, null)) return false;
            if (!enemy.aliveState.active) return false;

            float dist = Vector3.Distance(_agent.transform.position, enemy.transform.position);
            if (dist > StabRange) return false;

            // 锥形方向判定（模拟长矛只能向前刺）
            Vector3 toTarget = (enemy.chestPos - _agent.transform.position).normalized;
            float angle = Vector3.Angle(_agent.transform.forward, toTarget);
            if (angle > StabAngle * 0.5f) return false;

            // 执行刺击
            _lastStabTime = Time.time;
            PerformStab(enemy);
            return true;  // 消耗这一帧的 action 机会
        }

        private void PerformStab(Agent target)
        {
            // 直接伤害
            float prevHealth = target.health;
            target.health = Mathf.Max(0f, target.health - StabDamage);

            // 击退
            Vector3 kbDir = (target.transform.position - _agent.transform.position).normalized;
            kbDir.y = 0f;
            target.transform.position += kbDir * 0.8f;

            // 施加眩晕
            var stun = target.GetComponent<Stun>();
            if (!ReferenceEquals(stun, null))
            {
                var smf = typeof(Stun).GetField("stunMultiplier",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(smf, null))
                    smf.SetValue(stun, 8f);
            }

            Plugin.LogInfo("[Stab] Hit " + target.name + " | dmg=" + StabDamage +
                " | prevHP=" + prevHealth.ToString("F1") + "→" + target.health.ToString("F1") +
                " | dist=" + Vector3.Distance(_agent.transform.position, target.transform.position).ToString("F2"));
        }
    }
}
```

---

## 8. 验证清单

完成所有修复后，逐项验证：

- [ ] 游戏中黑矛兵手上是否有长矛（可见的 BatchedSprite）
- [ ] 长矛颜色是否为暗色调，且不影响 UV 渲染
- [ ] 盾牌是否已隐藏
- [ ] 身体颜色是否为黑色（B通道≈0）
- [ ] 冲刺是否实际命中敌人（日志显示 HIT! 而非 NO hits）
- [ ] 冲刺距离是否足够（约 5m）
- [ ] 普通刺击（IBrainAction）是否触发（日志显示 [Stab]）
- [ ] 伤害是否 ×1.6、击退是否 ×2.5
- [ ] 护甲是否 ×1.3 且不影响其他 Agent
- [ ] 出场概率是否 100%（每关都有黑矛兵）
- [ ] UI 面板中黑矛兵图标是否正常显示

---

## 9. 参考文件

| 文件 | 作用 |
|------|------|
| `BringBackBerserkers/BBB/Plugin.cs` | GameSetup.Awake Hook + LevelRule/LevelGuessable 修改范例 |
| `BadNorth原版架构分析/09.01-新兵种Mod实战：黑矛兵完整解剖.md` | §11 武器替换分析、§14 BatchedSprite vs sprite2 渲染差异、§15 运行日志诊断 |
| `BadNorth原版架构分析/09.03-Agent建模_渲染_战斗数值.md` | §2 BatchedSprite/SpriteAnimator/AgentTextureBaker 渲染管线 |
| `BadNorth原版架构分析/09.04-Mod实战_美术复用_系统全景.md` | §1 美术复用可行性、MaterialPropertyBlock 颜色修改 |
| `《BadNorth原版》Assembly-CSharp/Voxels/TowerDefense/Spear.cs` | Pikeman Spear Brain 完整状态机 + BatchedSprite 武器渲染器 |