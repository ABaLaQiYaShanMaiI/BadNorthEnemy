# Bad North Black Spearman

为《Bad North》游戏添加**黑色长矛手（Black Spearman）**敌人的 BepInEx 插件 Mod。

## 当前版本：v1.1

### 功能概述

- **自动转化**：维京剑盾兵（SwordShield）100% 被转化为黑矛兵（测试阶段，可调回 40%）
- **属性强化**：
  - 伤害 ×1.6
  - 击退 ×2.5
  - 体型 ×1.05（微调以示区分）
  - 护甲 ×1.3
- **黑色外观**：保留 UV 编码（R/G 通道）仅修改蓝色通道，正常显示敌人贴图
- **⭐ 冲刺技能（核心）**：登岛后自动检测 5m 范围内玩家单位，发动直线冲刺
  - 冲刺距离 3.5m，速度 6m/s
  - 对路径上的玩家单位造成**伤害**、**击退**和**眩晕**
  - 冲刺期间免疫眩晕
  - 8 秒冷却，0.4 秒硬直恢复
- **武器外观替换**：自动从游戏中查找长矛兵（Pikeman）模型并替换武器 Sprite
- **独立出场控制**：注册了独立 VikingReference `Viking_BlackSpearman`，控制出现条件

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

1. 修改 `BadNorthBlackSpearman/BadNorthBlackSpearman.csproj` 中的引用路径，指向你的 Bad North 安装位置：

   ```xml
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\Assembly-CSharp.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BepInEx\core\BepInEx.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
   <HintPath>D:\Steam\steamapps\common\BadNorth\BadNorth_Data\Managed\UnityEngine.PhysicsModule.dll</HintPath>
   <!-- 还需要 MMHOOK-Assembly-CSharp 引用 -->
   <HintPath>D:\Steam\steamapps\common\BadNorth\BepInEx\plugins\MMHOOK-Assembly-CSharp.dll</HintPath>
   ```

2. 编译：
   ```bash
   dotnet build BadNorthBlackSpearman/BadNorthBlackSpearman.sln -c Release
   ```

3. 将 `bin/Release/net472/BadNorthBlackSpearman.dll` 复制到 `BepInEx/plugins/`

---

## 技术架构

### 事件驱动（MMHOOK）

- 使用 **MMHOOK-Assembly-CSharp** 提供的 Hook 委托
- `On.Voxels.TowerDefense.GameSetup.Awake` — 注册 VikingReference、设置出场条件
- `On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn` — 拦截每个登岛船只，转化其上的剑盾兵
- **不再使用轮询**，性能优于 v1.0

### 转化链路（ApplyBlackSpearman）

```
SwordShield Agent 生成
  → ApplyBlackColor()      # 保留R/G UV编码，仅改B通道为深色
  → Scale ×1.05            # 微调体型
  → Swordsman 属性倍率     # damage ×1.6, knockback ×2.5
  → ApplyArmor()           # armor ×1.3
  → ApplyPikemanSprite()   # 替换武器Sprite为长矛
  → SpearChargeComponent   # 挂载冲刺技能
  → UpdateVikingReference  # 绑定独立 VikingReference
```

### 武器外观替换（ApplyPikemanSprite）

采用**三级回退策略**查找长矛兵模型：
1. 从 `LevelStateObjectReferences.dict` 按键名查找（`Viking_Pikeman`, `English_Pikeman` 等）
2. 搜索 `Faction.allSquads` 中的 Pikeman 小队
3. 遍历 dict 中全部 VikingReference，匹配 brain 类型含 `Pikeman`/`Spearman`/`Pike`

找到后通过反射复制其 `SpriteAnimator.sprite` / `SpriteAnimator.sprite2`。

### 冲刺伤害系统（SpearChargeComponent）

状态机：`Idle → Watching → Charging → Cooldown → Watching → ...`

**伤害检测**：冲刺中每 0.15s 使用 `Physics.OverlapSphere`（半径 1.2m）检测玩家 Agent，施加：
- **伤害**：`Swordsman.damageLevels[0]`（约 2.0 × 1.6 = 3.2）
- **击退**：沿冲刺方向 0.3m 位移
- **眩晕**：反射调用 `Stun.Begin(duration)` 或设置 `stunMultiplier=10` 使下一击必晕

**NavPos 同步**：冲刺结束后通过反射同步 `Agent.navPos.position` 防止 AI 寻路偏差。

---

## 文件结构

```
├── BadNorthBlackSpearman/               # 主插件项目
│   ├── BadNorthBlackSpearman.csproj
│   ├── BadNorthBlackSpearman.sln
│   ├── Plugin.cs                         # BepInEx 入口、转化逻辑、外观替换
│   ├── SpearChargeComponent.cs           # 冲刺技能状态机 + 伤害系统
│   ├── global.json
│   └── Properties/
├── tmpfix/                               # 辅助工具：修复 Assembly-CSharp 引用
│   ├── FixAsm.csproj
│   └── Program.cs
├── BlackSpearman整改清单.md              # 待修复/观察项
├── README.md
└── .gitignore
```

---

## 后续开发指引

### 🚀 下一步优先事项

1. **验证武器模型替换**：启动游戏，检查 BepInEx 日志是否有 `Pikeman sprite found` 或 `Could not find any Pikeman reference`。如找不到长矛兵引用，需根据实际游戏版本调整查找策略。

2. **验证冲刺伤害**：进入战斗，观察黑矛兵登岛后是否对附近玩家士兵发动直线冲刺并造成伤害。检查日志中的 `CHARGE!` 和 `HIT` 消息。

3. **调整参数**：
   - 如果 100% 转化率导致难度过高，将 `ConversionChance` 改回 `0.4f`
   - 如果冲刺伤害过高/过低，调整 `HitRadius`（1.2m）、伤害公式等

### 📝 可能需要的补充

| 方向 | 说明 |
|------|------|
| **手抄本图标** | 为黑矛兵添加独立的 VikingReference 后，可在 UI 中显示独立图标。需准备 Sprite 资源 |
| **难度分级** | 根据关卡进度调整 `ConversionChance` 和属性倍率（如后期关卡更高概率/更强属性）|
| **兵种多样化** | 目前只转化 SwordShield → BlackSpearman。可扩展支持 Archer → BlackArcher 等 |
| **冲刺视觉特效** | 当前冲刺只有位移，可添加粒子特效、音效（`swordSound`、`swingSound` 等）|

### 🛠 调试技巧

- BepInEx 控制台会输出所有关键事件（转化计数、CHARGE、HIT、Cooldown 等）
- 日志防刷屏：同类消息间隔 ≥2s
- 诊断输出在首次转化时打印完整状态（Agent 属性、颜色、VikingRef 绑定等）

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