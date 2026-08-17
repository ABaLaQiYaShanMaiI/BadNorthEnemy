敌方单位a# Bad North Black Spearman

> ⚠️ **核心设计理念**：本 Mod 的目标是将玩家方的 **Pikeman（长矛兵）** 兵种的**武器模型（长矛）**和**冲刺技能（Pike Charge）** 移植到维京方，打造一个全新的敌方单位——**黑色长矛手（Black Spearman）**。
>
> **长矛和冲刺技能都是玩家方 EnglishSquad 兵种才有的东西**，维京方没有任何类似的能力。本 Mod 从零开始模仿和复刻这些机制。

---

为《Bad North》游戏添加**黑色长矛手（Black Spearman）**敌人的 BepInEx 插件 Mod。

## 当前版本：v1.3（黑矛兵完整版）🏆

> 📍 **本文档已由 v1.8 停更状态转正到 1.3**。v1.3（`BadNorthBlackSpearman1.3/`）是当前维护版本：
> **新建 VikingReference（非克隆）+ 注入敌人生成池 + 特质式美术资源**，技能/外观/格挡/数值全部定稿，
> 最新的开发记录见 [`BadNorthBlackSpearman1.3/README.md`](BadNorthBlackSpearman1.3/README.md) 与
> [`BadNorthBlackSpearman1.3/困惑清单与调试记录.md`](BadNorthBlackSpearman1.3/困惑清单与调试记录.md)。

### ⚠️ 当前状态（2026-08-17：v1.3 定稿 + 地形/建筑感知冲锋）

**技能与外观已定稿**：登岛触发 + 长矛突击 + 可躲高收益、盾牌格挡、技能期可被击杀、10s 冷却。
**闪白已根治**（根因 = 身体顶点 alpha 恒 0 致身体透明露背景；修复 = 强制 alpha=1）。冲锋/近战橡皮筋、死亡白尸、重影均已修复。

**✅ 最新新需求（地形/建筑感知冲锋，2026-08-17）**：
- 冲刺**受地形与建筑约束**：直线被水面/悬崖/房屋（含烧毁残骸）遮挡时不释放技能；
- 目标背靠海面时终点夹回岸上（不再冲出海岸）；途中遇到不可通过地形被阻拦停住；
- 可走性判定使用游戏权威 `NavPos.MoveTo`（IL 确认返回 `bestDist==0`），配二分细化 + 内收余量。

> 📍 最新完整记录见 [`BadNorthBlackSpearman1.3/README.md`](BadNorthBlackSpearman1.3/README.md) 与
> [`BadNorthBlackSpearman1.3/困惑清单与调试记录.md`](BadNorthBlackSpearman1.3/困惑清单与调试记录.md)。

> ⚠️ 本文档下方"功能概述/冲刺技能详解/技术架构"等章节为历史版本（v1.8 及更早）遗留内容，仅作参考，实际以 1.3 目录内文档为准。

### 功能概述

| 功能 | 状态 | 说明 |
|------|------|------|
| **长矛武器模型** | ⏸️ 暂停 | 从玩家方 Pikeman (Spear brain) 提取 `BatchedSprite spearSprite` 挂载到黑矛兵 — 研究中 |
| **举矛冲刺技能** | ⏸️ 暂停 | `SpearChargeComponent.cs` 代码保留，`Plugin.cs` 调用已注释 |
| **黑色外观** | ✅ 可用 | 保留 UV 编码（R/G 通道），仅修改 B（蓝色）通道为 0.02 |
| **盾牌移除** | ✅ 可用 | `agent.shield = false` + 禁用 Shield 子对象 |
| **属性强化** | ✅ 可用 | 伤害 ×1.6、击退 ×2.5、护甲 ×1.3、体型 ×1.05 |
| **独立出场控制** | ✅ 可用 | 注册独立 `VikingReference: Viking_BlackSpearman` |

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

1. 修改 `BadNorthBlackSpearman1.3/BadNorthBlackSpearman1.3.csproj` 中的引用路径为**本机游戏路径**：

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
   dotnet build BadNorthBlackSpearman1.3/BadNorthBlackSpearman1.3.csproj -c Release
   ```

3. 将 `bin/Release/net472/BadNorthBlackSpearman1.3.dll` 复制到 `BepInEx/plugins/`

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
├── README.md                                  # 本总览文档（历史章节仅作参考）
├── .gitignore
└── BadNorthBlackSpearman1.3/                  # ★ 当前维护版本（v1.3）
    ├── BadNorthBlackSpearman1.3.csproj        # 项目文件（游戏 DLL 引用路径按本机调整）
    ├── Plugin.cs                              # BepInEx 入口：注册 VikingReference + 生成池注入 + cfg 配置
    ├── BSLog.cs                               # 统一日志系统（控制台 + 文件 + 全局异常捕获）
    ├── Diagnostics.cs                         # 运行时诊断探针（心跳 + F8 完整转储）
    ├── BlackSpearmanArt.cs                    # 美术资源（PNG 图标）+ I2 本地化
    ├── BlackSpearmanVisual.cs                 # 黑色外观（对抗纹理重烘焙 / 闪白）
    ├── BlackSpearmanWeapon.cs                 # 武器处理（去剑视觉 + 挂我方长矛）
    ├── BlackSpearmanShield.cs                 # 盾牌格挡效果
    ├── SpearChargeComponent.cs                # 冲锋技能（IBrainAction + 近战刺击 + 地形/建筑感知）
    ├── SpearVisual.cs                         # 长矛朝向统一工具
    ├── SwordRemover.cs                        # 去剑组件（运行时擦除动画帧剑像素）
    ├── BlackSpearmanDiagProbe.cs              # 死亡/影分身专项诊断探针
    ├── Resources/
    │   └── black_spearman_icon.png            # 美术图标
    ├── README.md                              # v1.3 开发记录（当前状态）
    ├── 困惑清单与调试记录.md                  # 问题排查记录
    └── 技术方案_技能复现与残留清理.md          # 技术方案
```

> ℹ️ 历史版本（v1.0 / v1.1 / v1.2）、临时分析工具（`tmpfix/`）、一次性 Python 分析脚本与调试截图已随 v1.3 定稿清理删除，git 历史仍可完整追溯。

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

详见 [`BadNorthBlackSpearman1.3/README.md`](BadNorthBlackSpearman1.3/README.md) 与
[`BadNorthBlackSpearman1.3/困惑清单与调试记录.md`](BadNorthBlackSpearman1.3/困惑清单与调试记录.md)。

---

## 开源许可

MIT License

## 致谢

- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 Mod 框架
- BadNorthDatabase-main — 游戏逆向工程参考数据库
- 《Bad North》 — [Raw Fury](https://rawfury.com/) 出品的极简策略游戏