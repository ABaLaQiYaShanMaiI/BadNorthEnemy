# Bad North Black Spearman

为《Bad North》添加**黑色长矛手（Black Spearman）**敌方单位的 BepInEx 插件 Mod。

> ⚠️ **核心设计理念**：把玩家方 **Pikeman（长矛兵）** 的**长矛武器**与**冲刺技能（Pike Charge）** 移植到维京方，
> 打造一个全新的敌方单位。长矛和冲刺是玩家方 EnglishSquad 才有的东西，维京方没有任何类似能力，本 Mod 从零复刻。

---

## 当前版本：v1.3（黑矛兵完整版）🏆

v1.3（`BadNorthBlackSpearman1.3/`）是当前维护版本。**新建 VikingReference（非克隆）+ 注入敌人生成池 + 特质式美术资源**，
技能/外观/格挡/数值全部定稿，玩法可正常游玩。

### ✅ 当前状态（2026-08-17：v1.3 定稿，第四十六轮·闭环）

- **技能与外观已定稿**：登岛触发 + 长矛突击（非追踪、可躲、高收益）+ 盾牌格挡 + 技能期可被击杀 + 10s 冷却。
- **闪白已根治**：根因 = 原版每帧写 `spriteAnimator.color.b = 1 - healthFraction`（受击白闪通道）+ 身体顶点 alpha 恒 0；
  修复 = 每帧强制顶点色 **B=0.02 + alpha=1**。
- **重影已解决（第四十六轮）**：头部抽搐/闪白 + 死亡腾空分裂/受击双影 = **同一根因（多层渲染叠加）**——
  原版 ColoredCharacter 是 `Cull Off`，身体 **2主+2镜像共 4 层始终同绘**；改黑+头盔提亮后显形。
  修复 = `Diag.SingleBodyMode=2`（保留动画主身、禁用静态主身+2镜像），无重影且待机律动完整。
- **地形/建筑感知冲锋（2026-08-17 最新）**：直线被水面/悬崖/房屋（含烧毁残骸）遮挡时不释放技能；
  目标背靠海面时终点夹回岸上；途中被不可通过地形阻拦即停（权威判定 `NavPos.MoveTo`）。

> 📍 逐轮开发/测试记录见 [`开发日志.md`](BadNorthBlackSpearman1.3/开发日志.md)（每个困惑：提出 → 验证 → 是否解决 → 解法）；
> 原版源码对照/渲染管线等逆向结论见 [`逆向工程技术文档.md`](BadNorthBlackSpearman1.3/逆向工程技术文档.md)。

---

## 功能概述（v1.3 定稿）

| 功能 | 状态 | 说明 |
|------|------|------|
| **长矛武器** | ✅ 定稿 | 复用我方 Pikeman 长矛（`Spear.spearAnim` 的 BatchedSprite）挂到黑矛兵持剑锚点（Weapon 骨） |
| **举矛冲刺技能** | ✅ 定稿 | 登岛触发 + 非追踪直线冲锋（穿透锁定格 1.5m）+ 地形/建筑感知 + 10s 冷却 |
| **近战刺击** | ✅ 定稿 | 长矛穿刺（Patch `Swordsman.Attack/AttackUpdate` 替换原版挥剑+跳扑），对齐原版 `Spear.TestHit` |
| **黑色外观** | ✅ 定稿 | 顶点色 B 恒 0.02（抑制受击白闪）+ 部件贴图分区压暗（躯干/手烘黑、暗灰头盔保留） |
| **盾牌** | ✅ 定稿 | 默认**完全移除**（效果+美术）；`EnableShield=true` 保留基底剑盾兵盾牌并具备格挡（近战/箭矢/飞斧/长矛） |
| **数值强化** | ✅ 定稿 | 伤害 ×1.6、击退 ×2.5、眩晕 ×1.2、体型 ×1.05 |
| **独立出场控制** | ✅ 定稿 | 独立 `VikingReference: Viking_BlackSpearman`，注入敌人生成池，头像已内嵌 DLL |

## 技能机制（举矛冲刺）

| 维度 | 实现 |
|---|---|
| 触发逻辑 | 登岛（`navPos.onMain`）后优先跟随 Swordsman 大脑锁定目标；大脑无目标退回 6m 扫描兜底；被建筑/水面/悬崖遮挡不触发 |
| 技能表现 | 0.5s 举矛前摇 → **非追踪**直线冲锋（5m/s、穿透锁定格 1.5m）→ 后退 0.6m 迎击 → **10s 冷却** |
| 攻击效果 | 沿途单矛线宽 0.5m 扫过一排（能量逐击 ×0.8 递减）→ 终点爆发（伤害×0.3、击退+2、撞飞） |
| 可躲高收益 | 非追踪 + 前摇可见 + 线宽 0.5m：横向拉开即可躲；躲过则黑矛兵冲空陷入长后摇 |

## 当前技能参数（与代码一致）

| 参数 | 值 | 说明 |
|------|-----|------|
| `DetectionRadius` | 6.0m | 扫描兜底范围（优先取 Swordsman 大脑目标） |
| `ChargeSpeed` | 5.0m/s | 冲刺速度 |
| `ChargeOvershoot` | 1.5m | 穿透余量（冲过锁定格 1.5m） |
| `RetreatDistance` | 0.6m | 后退距离 |
| `WindUpDuration` | 0.5s | 起手举矛 |
| `CooldownTime` | 10s | 技能冷却 |
| `StabDamage / Knockback / Stun / Launch` | 3 / 9 / 10 / 8 | 近战刺击基础伤害/击退/眩晕/撞飞 |
| `HitRadius` | 0.5m | 单矛线宽（沿途命中） |
| `ArrivalBurstRadius` | 1.2m | 终点爆发半径 |
| `EnergyDecayPerHit` | 0.8 | 每命中能量衰减（多段递减） |

状态机：`Idle → WindUp → Charging(穿透) → Retreat(后退 0.6m) → Cooldown(10s) → Idle`

---

## 配置（BepInEx 配置）

| 分组 | 键 | 默认 | 说明 |
|------|----|------|------|
| General | `SourceVikingName` | `Viking_SwordShield` | 借用的 VikingAgent 预制体引用（剑盾兵基底：保留盾牌美术） |
| General | `NewVikingName` | `Viking_BlackSpearman` | 新单位在生成池中的名字 |
| General | `Bounty` | 8 | 赏金（占用敌舰配额） |
| Spawn | `SpawnChance` | 0.7 | 每关加入敌人生成池的概率 |
| Spawn | `ForceFirstWave` | false | 强制第一波出现（测试用） |
| Combat | `DamageMult` / `KnockbackMult` / `StunMult` / `ScaleMult` | 1.6 / 2.5 / 1.2 / 1.05 | 数值倍率 |
| Visual | `EnableRecolor` | true | 黑色外观 |
| Visual | `EnableWeaponSwap` | true | 移除剑视觉 + 复用我方长矛 |
| Visual | `RemoveSword` | false | 帧级擦除身体动画帧里的剑（需先校准签名，默认关） |
| Visual | `RemoveSwordSprite2Mode` | 2 | 部件贴图处理：2=分区压暗（定稿） |
| Visual | `RemoveSwordFrameUVErase` | true | UV 感知亮采样擦除（白框根治） |
| Visual | `RemoveSwordFrameUVHalo` | 0 | UV 亮像素光晕（0~6，吃持剑的手/护手） |
| Visual | `SpearMountToHand` | true | 长矛挂到持剑锚点（手位） |
| Skills | `EnableCharge` | true | 冲锋技能 |
| Skills | `EnableShield` | false | 完全移除盾牌（效果+美术）；true=保留基底盾牌+格挡 |
| Diag | `VerboseDumps` | false | 巨型转储开关（F8 手动诊断用） |
| Diag | `HeadTrace` | true | 头部采样追踪（P0 回归监控） |
| Diag | `DeathTrace` | true | 死亡腾空追踪（P1 回归监控） |
| Diag | `HitDoubleTrace` | true | 受击双影探针（P1 回归监控） |
| Diag | `SingleBodyMode` | 2 | 去重影模式：2=只保留动画主身（定稿） |

## 日志与诊断指南（测试必备）

本 Mod 内置双通道日志 + 运行时诊断探针，用于收集比报错日志更多的现场信息。

| 通道 | 位置 | 内容 |
|------|------|------|
| BepInEx 控制台 | 游戏控制台 / `BepInEx/LogOutput.log` | 我们自己的 INFO/WARN/ERROR |
| **独立诊断文件** | 插件 DLL 同目录 `BadNorthBlackSpearman1.3.log` | 我们自己的日志 + **全游戏错误/异常**（含堆栈）+ 所有转储 |

- 通过 `Application.logMessageReceived` 捕获**全游戏** Error/Exception/Assert（含堆栈）；即使游戏闪退，诊断文件也会保留最后现场。
- 游戏运行时按 **F8**：立即写入一次完整诊断（生成池注册表 / VikingReference 字段 / 场景 Agent 概况 / 渲染链路体检）。
- 诊断开关在 cfg 的 `[Diag]` 段（见上表）：`VerboseDumps` 控制巨型转储，`HeadTrace/DeathTrace/HitDoubleTrace` 控制三类问题追踪。
- 日志方法语义（文件/控制台双写 vs 只写文件）见 `BSLog.cs` 类头注释。

---

## 安装要求

- 《Bad North》游戏（Steam 版）
- [BepInEx](https://github.com/BepInEx/BepInEx) 5.x（已安装到游戏目录）
- **MMHOOK-Assembly-CSharp.dll**（BadNorthDatabase-main 中提供，放入 `BepInEx/plugins/`）

## 安装方法

1. 确保已为 Bad North 安装 BepInEx 5.x
2. 将 `MMHOOK-Assembly-CSharp.dll` 放入 `<游戏目录>/BepInEx/plugins/`
3. 将编译生成的 **`BadNorthBlackSpearman1.3.dll`** 放入 `<游戏目录>/BepInEx/plugins/`
4. 启动游戏，插件将自动加载（首次运行在 `BepInEx/config/` 生成 `badnorth.blackspearman.v1.3.cfg`）

## 从源码编译

### 环境要求

- .NET Framework 4.7.2 SDK（`dotnet` CLI 或 VS 2019+）
- 游戏本体（提供程序集引用）

### 一键编译 + 部署（推荐）

仓库根目录提供 `build.ps1`，把"编译 → 备份旧 DLL → 复制到 plugins → SHA256 校验"一条龙自动化：

```powershell
.\build.ps1 -BadNorthDir "D:\Steam\steamapps\common\BadNorth" -Configuration Release
```

- 不传 `-BadNorthDir` 时，按 **环境变量 `BadNorthDir` → csproj 默认值（`D:\Steam\steamapps\common\BadNorth`）** 的顺序解析；
- 部署前旧 DLL 自动备份为 `BadNorthBlackSpearman1.3.dll.bak_<时间戳>`（不覆盖既有备份）；
- `-SkipDeploy`：只编译，不复制、不校验。

### 手动编译

游戏目录由 csproj 属性 `BadNorthDir` 指定（优先级：命令行 `-p:BadNorthDir=` > 环境变量 > csproj 默认值），**不再硬编码路径**：

```powershell
$env:BadNorthDir = "D:\Steam\steamapps\common\BadNorth"   # 或直接在 csproj 里改默认值
dotnet build BadNorthBlackSpearman1.3/BadNorthBlackSpearman1.3.csproj -c Release
```

产物 `bin/Release/net472/BadNorthBlackSpearman1.3.dll` 复制到 `<游戏目录>/BepInEx/plugins/` 即可。
（可选）把 `Resources/black_spearman_icon.png` 放到 DLL 同目录可覆盖默认头像（头像已内嵌 DLL）。

> ⚠️ **运行时兼容**：游戏是 Unity 2018 的老 Mono 运行时（CLR 2.0 ≈ .NET 3.5），但必须编译为 net472 才能引用游戏/BepInEx 的 DLL。
> 代码里只能使用 .NET 3.5 就有的 API——`string.Join(IEnumerable<string>)`、`FieldInfo == null`、`lock`、三参数
> `Path.Combine`、LINQ 等 .NET 4.x 专属写法会运行时崩溃。**尤其禁止直接引用 `System.Action`/`System.Func`（含隐式 lambda 转换）**——
> 曾因 `TypeLoadException` 导致四条 Harmony Patch 静默失效（全项目最贵教训，详见[逆向工程技术文档](BadNorthBlackSpearman1.3/逆向工程技术文档.md) §13）。

---

## 文件结构

```
├── README.md                                  # 本总览文档
├── LICENSE                                    # MIT 许可
├── .gitignore
├── .editorconfig                              # 统一缩进/命名风格
├── build.ps1                                  # 一键编译 + 部署 + SHA256 校验
├── .github/workflows/build.yml                # 最小 CI（dotnet build）
└── BadNorthBlackSpearman1.3/                  # ★ 当前维护版本（v1.3）
    ├── BadNorthBlackSpearman1.3.csproj        # 项目文件（游戏 DLL 引用路径按本机调整）
    ├── Plugin.cs                              # BepInEx 入口：注册 VikingReference + 生成池注入 + Harmony 钩子
    ├── ModConfig.cs                           # ★ 全部 cfg 配置（字段 + 分段 Bind + ShieldFullyRemoved）
    ├── BSLog.cs                               # 统一日志系统（控制台 + 文件 + 全局异常捕获）
    ├── Diagnostics.cs                         # 运行时诊断探针（心跳 + F8 完整转储）
    ├── BlackSpearmanArt.cs                    # 美术资源（PNG 图标，内嵌 DLL）+ I2 本地化
    ├── BlackSpearmanVisual.cs                 # 黑色外观（顶点色 B 恒 0.02 + 长矛手部压暗 + 头部采样诊断）
    ├── BlackSpearmanWeapon.cs                 # 武器处理（去剑视觉 + 移除/保留盾牌 + 挂我方长矛）
    ├── BlackSpearmanShield.cs                 # 盾牌格挡效果
    ├── SpearChargeComponent.cs                # 冲锋技能（状态机 + 近战刺击 + 地形/建筑感知）
    ├── SpearVisual.cs                         # 长矛朝向统一工具
    ├── SwordRemover.cs                        # 去剑组件（帧级擦除 + 部件贴图分区压暗 + 安全阀）
    ├── BlackSpearmanDiagProbe.cs              # 死亡/影分身专项诊断探针（SingleBodyMode 去重影）
    ├── Resources/
    │   └── black_spearman_icon.png            # 美术图标（可选覆盖）
    ├── 开发日志.md                            # 逐轮测试记录：困惑 → 是否解决 → 解决方法
    └── 逆向工程技术文档.md                    # 原版源码对照 / 渲染管线 / 反编译结论
```

> ℹ️ 历史版本（v1.0 / v1.1 / v1.2）、临时分析工具（`tmpfix/`）、一次性 Python 分析脚本与调试截图已随 v1.3 定稿清理删除，git 历史仍可完整追溯。

---

## 文档导航（三份文件各司其职）

| 文件 | 定位 |
|------|------|
| **README.md**（本文件） | 对外总览：是什么、怎么装、怎么配、怎么编译 |
| **开发日志.md** | 逐轮开发/测试记录：每个困惑的提出 → 验证 → 是否解决 → 解决方法（第四十六轮闭环） |
| **逆向工程技术文档.md** | 逆向结论：原版源码逐行对照、生成链路、渲染管线、.NET 兼容坑、UV 解码等 |

## 开源许可

MIT License —— 详见 [LICENSE](LICENSE)。

## 致谢

- [BepInEx](https://github.com/BepInEx/BepInEx) — Unity 游戏 Mod 框架
- BadNorthDatabase-main — 游戏逆向工程参考数据库
- 《Bad North》 — [Raw Fury](https://rawfury.com/) 出品的极简策略游戏

