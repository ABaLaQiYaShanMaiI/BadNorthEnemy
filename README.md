# Bad North Black Spearman

为《Bad North》游戏添加**黑色长矛手（Black Spearman）**敌人的 BepInEx 插件 Mod。

## 功能概述

- **自动转化**：维京剑盾兵（SwordShield）有 40% 概率被转化为黑色长矛手
- **属性强化**：
  - 伤害 ×1.6
  - 击退 ×2.5
  - 体型 ×1.2
  - 护甲 ×1.3
- **黑色外观**：转化后的敌人呈现深黑色调
- **冲刺技能**：登岛后自动检测附近玩家单位，发动直线冲刺突袭
- **免疫眩晕**：冲刺期间不受眩晕效果影响

## 安装要求

- 《Bad North》游戏（Steam 版）
- [BepInEx](https://github.com/BepInEx/BepInEx) 5.x（已安装到游戏目录）

## 安装方法

1. 确保已为 Bad North 安装 BepInEx
2. 将编译生成的 `BadNorthBlackSpearman.dll` 放入 `<游戏目录>/BepInEx/plugins/` 文件夹
3. 启动游戏，插件将自动加载

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
   ```

2. 使用 Visual Studio 打开 `BadNorthBlackSpearman/BadNorthBlackSpearman.sln` 并编译，或使用命令行：

   ```bash
   dotnet build BadNorthBlackSpearman/BadNorthBlackSpearman.sln -c Release
   ```

3. 将 `bin/Release/net472/BadNorthBlackSpearman.dll` 复制到 BepInEx plugins 目录

## 技术实现

### 架构

- 纯 **BepInEx** 插件，基于反射 + 事件订阅 + Agent 轮询 fallback
- **不依赖** Harmony（避免补丁兼容性问题）
- 运行时反射检测 `onAgentSpawned` 事件可用性，不可用时自动降级为轮询模式

### 转化机制

1. 每 3 秒扫描新生成的 Viking Agent
2. 对 SwordShield 类型的 Viking 以 40% 概率进行转化
3. 转化包括：
   - 颜色变黑（修改 BatchedSprite/SpriteAnimator 的顶点色）
   - 属性倍率调整
   - 添加 `SpearChargeComponent` 冲刺组件

### 冲刺系统

冲刺组件实现了一个状态机：

```
Idle → Watching → Charging → Cooldown → Watching → ...
```

- **Watching**：等待登岛 + 检测 5m 范围内的玩家单位
- **Charging**：锁定方向直线冲刺 3.5m，速度 6m/s，直接控制 `transform.position` 绕过 AI
- **Cooldown**：8 秒冷却 + 0.4 秒硬直恢复

## 文件结构

```
├── BadNorthBlackSpearman/          # 主插件项目
│   ├── BadNorthBlackSpearman.csproj
│   ├── BadNorthBlackSpearman.sln
│   ├── Plugin.cs                   # BepInEx 插件入口，转化逻辑
│   ├── SpearChargeComponent.cs     # 冲刺技能组件
│   ├── global.json
│   └── Properties/
├── tmpfix/                         # 辅助工具（详见下方说明）
│   ├── FixAsm.csproj
│   └── Program.cs
├── BlackSpearman整改清单.md        # 已知问题与改进计划
├── .gitignore
└── README.md
```

### tmpfix/ 说明

`tmpfix/` 目录包含一个辅助工具，用于修复 Assembly-CSharp.dll 的参考程序集问题。使用 `Mono.Cecil` 读取并重写目标 DLL，使其可被 Visual Studio 正确引用。仅在编译环境配置时使用，不影响运行时插件行为。

## 已知问题

详见 [`BlackSpearman整改清单.md`](BlackSpearman整改清单.md)

| 优先级 | 问题 | 状态 |
|--------|------|------|
| 🔴 P0 | ApplyBlackColor 覆盖 R/G 通道可能导致纹理错乱 | 待修复 |
| 🟡 P1 | FindObjectsOfType 全扫描性能开销 | 建议优化 |
| 🟡 P1 | onAgentSpawned 反射检测不完整 | 建议加固 |
| 🟡 P1 | Raycast 碰撞层不够精确 | 建议优化 |
| 🟢 P2 | 缺少 LevelRule/LevelGuessable 出场控制 | 长期计划 |
| 🟢 P2 | tmpfix 目录用途未说明 | 已完成（见上方） |
| 🟢 P2 | 冲刺后 navPos 未同步 | 潜在问题 |

## 开源许可

MIT License

## 致谢

- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 Mod 框架
- 《Bad North》 — [Raw Fury](https://rawfury.com/) 出品的极简策略游戏