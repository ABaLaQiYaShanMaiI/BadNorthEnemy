# Bad North Black Spearman

> ⚠️ **核心设计理念**：本 Mod 的目标是将玩家方的 **Pikeman（长矛兵）** 兵种的**武器模型（长矛）**和**冲刺技能（Pike Charge）** 移植到维京方，打造一个全新的敌方单位——**黑色长矛手（Black Spearman）**。
>
> **长矛和冲刺技能都是玩家方 EnglishSquad 兵种才有的东西**，维京方没有任何类似的能力。本 Mod 从零开始模仿和复刻这些机制。

---

为《Bad North》游戏添加**黑色长矛手（Black Spearman）**敌人的 BepInEx 插件 Mod。

## 当前版本：v1.3

### 功能概述

| 功能 | 来源 | 说明 |
|------|------|------|
| **长矛武器模型** | 玩家方 **Pikeman**（长矛兵）| 从 `Faction.allSquads` 中找到 Pikeman 小队的 `minionPrefab`，深拷贝其 Spear/Weapon 子 GameObject 到黑矛兵身上，同时禁用原剑盾兵的 Sword/Shield 子对象 |
| **举矛冲刺技能** | 玩家方 **PikeChargeAbility + PikeChargeComponent** | 原版长矛兵通过 `AgentExclusives` + `AgentState` + `navPos` 移动 + `Agent.DealDamage(Attack)` 实现冲刺。本 Mod 模仿其逻辑，使用简化状态机 + `transform.position` + `AttackSettings` 实现同等效果 |
| **黑色外观** | 原创 | 保留 UV 编码（R/G 通道），仅修改 B（蓝色）通道为 0.02，呈现深黑色调 |
| **属性强化** | 原创 | 伤害 ×1.6、击退 ×2.5、护甲 ×1.3、体型 ×1.05 |
| **独立出场控制** | 原创 | 注册独立 `VikingReference: Viking_BlackSpearman`，控制出现条件 |

### 冲刺技能详解（模仿玩家方 Pike Charge）

原版玩家方长矛兵的冲刺通过 `PikeChargeAbility`（点击按钮触发）→ `PikeChargeComponent`（实际执行）实现，核心机制：
- 使用 `AgentExclusives` 互斥锁住 AI 大脑
- 通过 `NavPos` 滑步移动
- 使用 `Agent.LookInDirection()` 面向冲刺方向
- 使用 `Agent.DealDamage(Attack) ` 对路径上的敌人造成伤害

本 Mod 的黑矛兵冲刺：
- **触发方式**：自动检测 5m 范围内玩家单位 → 自动发动（无需玩家操作）
- **移动方式**：`transform.position` + `navPos` 同步，同时 `maxSpeed=0` 短暂冻结 AI
- **伤害方式**：`ApplyChargeDamage()` 先尝试 `Agent.DealDamage(Attack)`（原版方式），回退到直接扣血
- **攻速**：每 0.15s 检测一次，半径 1.2m
- **冷却**：8 秒冷却 + 0.4 秒硬直恢复
- **眩晕免疫**：冲刺期间 `Stun.stunMultiplier=0`

### 当前关键参数

| 参数 | 值 | 说明 |
|------|-----|------|
| `ConversionChance` | 1.0 (100%) | 测试阶段，正式版建议 0.4 |
| `ScaleMultiplier` | 1.05 | 体型增幅 5% |
| `DamageMultiplier` | 1.6 | 伤害倍率 |
| `KnockbackMultiplier` | 2.5 | 击退倍率 |
| `ArmorMultiplier` | 1.3 | 护甲倍率 |
| `ChargeDistance` | 3.5m | 冲刺距离 |
| `ChargeSpeed` | 6.0m/s | 冲刺速度 |
| `HitRadius` | 1.2m | 伤害碰撞半径 |
| `HitInterval` | 0.15s | 伤害检测间隔 |
| `ChargeCooldown` | 8.0s | 技能冷却 |
| `DetectionRadius` | 5.0m | 敌人检测范围 |

---

## 安装要求

- 《Bad North》游戏（Steam 版）
- [BepInEx](https://github.com/BepInEx/BepInEx) 5.x（已安装到游戏目录）
- **MMHOOK-Assembly-CSharp.dll**（BadNorthDatabase-main 中提供，放入 `BepInEx/plugins/`）

## 安装方法

1. 确保已为 Bad North 安装 BepInEx 5.x
2. 将 `MMHOOK-Assembly-CSharp.dll` 放入 `<游戏目录>/BepInEx/plugins/`
3. 将编译生成的 `BadNorthBlackSpearman.dll` 放入 `<游戏目录>/BepInEx/plugins/`
4. 启动游戏，插件将自动加载

## 从源码编译

### 环境要求

- .NET Framework 4.7.2 SDK
- Visual Studio 2019+ 或 `dotnet` CLI

### 编译步骤

1. 修改 `BadNorthBlackSpearman/BadNorthBlackSpearman.csproj` 中的引用路径：

   ```xml
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\Assembly-CSharp.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BepInEx\core\BepInEx.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.PhysicsModule.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BepInEx\plugins\MMHOOK-Assembly-CSharp.dll</HintPath>
   ```

2. 编译：
   ```bash
   dotnet build BadNorthBlackSpearman/BadNorthBlackSpearman.csproj -c Release
   ```

3. 将 `bin/Release/net472/BadNorthBlackSpearman.dll` 复制到 `BepInEx/plugins/`

---

## 技术架构

### 事件驱动（MMHOOK）

- 使用 **MMHOOK-Assembly-CSharp** 提供的 Hook 委托
- `On.Voxels.TowerDefense.GameSetup.Awake` — 注册 VikingReference、设置出场条件、缓存武器模板
- `On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn` — 拦截每个登岛船只，转化其上的剑盾兵

### 转化链路

```
SwordShield Agent 生成
  → ApplyBlackColor()         # 保留R/G UV编码，仅改B通道为深色
  → Scale ×1.05               # 微调体型
  → Swordsman 属性倍率        # damage ×1.6, knockback ×2.5
  → ApplyArmor()              # armor ×1.3
  → ApplyWeaponSwap()         # ⭐ 禁用剑盾 + 深拷贝长矛
  → SpearChargeComponent      # ⭐ 挂载冲刺技能
  → UpdateVikingReference     # 绑定独立 VikingReference
```

### 武器外观替换

`CachePikemanWeaponTemplate()` 在 `GameSetup.Awake` 时从 `Faction.allSquads` 中找到 Pikeman 小队的 `minionPrefab`（玩家方长矛兵模型），保存其 Spear 子 GameObject 作为模板。

`ApplyWeaponSwap()` 对每个转化的黑矛兵：
1. 禁用所有含 "shield"/"sword"/"盾"/"剑" 的子 GameObject
2. 从模板 `Instantiate` 深拷贝长矛子对象到 Agent

### 冲刺伤害系统

状态机：`Idle → Watching → Charging → Cooldown → Watching → ...`

伤害方式（两种回退）：
- 方法 1：反射调用 `Agent.DealDamage(Attack)`（原版方式，完整结算伤害/击退/眩晕）
- 方法 2：直接 `target.health -= 3.3f`（回退方案）

---

## 文件结构

```
├── BadNorthBlackSpearman/
│   ├── BadNorthBlackSpearman.csproj
│   ├── BadNorthBlackSpearman.sln
│   ├── Plugin.cs                   # BepInEx 入口、转化逻辑、武器替换
│   ├── SpearChargeComponent.cs     # 冲刺技能状态机 + 伤害系统
│   ├── global.json
│   └── Properties/
├── tmpfix/                         # 辅助工具：修复 Assembly-CSharp 引用
├── BlackSpearman整改清单.md        # 待修复/观察项
├── README.md
└── .gitignore
```

---

## 后续开发指引

### 🚀 下一步优先事项

1. **验证武器模型替换**：启动游戏，检查日志中的 `Faction Squads Diagnostic` 和 `Pikeman weapon template`。确认长矛模型是否成功从 Pikeman prefab 克隆。

2. **验证冲刺伤害**：进入战斗，观察黑矛兵登岛后是否对附近士兵发动冲刺并造成伤害。检查 `CHARGE!` 和 `HIT` 消息。

3. **调整参数**：测试阶段结束后调整 `ConversionChance`、伤害公式等。

### 📝 待模仿的原版特性

| 原版 Pikeman 特性 | 当前状态 | 说明 |
|------|---------|------|
| **Spear 武器组件** | ⚠️ 仅复制了 GameObject | 原版 Spear 组件包含 `spearLength`、`spearMidPos`、`attackSetting` 等属性，当前仅深拷贝了视觉模型 |
| **PikeChargeComponent** | ✅ 已模仿核心逻辑 | 简化实现：自动检测 + 直线冲刺 + 伤害 + 击退 + 眩晕免疫 |
| **PikeChargeAbility** | ⚠️ 未实现 | 原版是玩家点击按钮触发的技能，有冷却、范围、能量管理等；当前简化为自动触发 |
| **AgentState/Exclusives 体系** | ❌ 未使用 | 原版用层次化状态机管理技能，当前使用简化的 Phase 枚举 + Update 状态机 |

### 🛠 调试技巧

- BepInEx 控制台输出所有关键事件（Faction Squad 诊断、武器克隆、CHARGE、HIT）
- 首次转化时打印完整诊断报告（子对象结构、武器模板状态）
- 日志防刷屏：同类消息间隔 ≥2s

---

## 已知问题

详见 [`BlackSpearman整改清单.md`](BlackSpearman整改清单.md)

---

## 开源许可

MIT License

## 致谢

- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 Mod 框架
- BadNorthDatabase-main — 游戏逆向工程参考数据库
- 《Bad North》 — [Raw Fury](https://rawfury.com/) 出品的极简策略游戏