# BadNorthBlackSpearman v1.3 —— 特质 Mod 式注入（新思路）

> 为《Bad North》添加**黑色长矛手（Black Spearman）**敌方单位。
> **本版本彻底改变思路**：不再克隆（`Instantiate`）现有预制件，而是像特质 Mod 一样，
> 用 **MMHOOK + Harmony 注入**，**新建**一个 `VikingReference`，配好美术资源，加入**敌人生成池**。

---

## 与历史版本的差异

| 版本 | 策略 | 问题 | 状态 |
|------|------|------|:---:|
| v1.0 | 运行时新建 VikingReference + 反射 | 时序问题（dict 在 Awake 时为空） | 🔒 封存 |
| v1.1 | BBB 风格劫持 `Viking_SwordShield` | SwordShield 消失 | 🔒 封存 |
| v1.2 | `Instantiate(源VR)` 克隆 + Enum/Asset 外部工具链 | 克隆脏状态、外部工具链复杂 | 🔒 封存 |
| **v1.3** | **新建 VikingReference（非克隆）+ 注入敌人生成池 + 特质式美术资源** | ✅ | 🚀 当前 |

## 核心思路（源码验证）

敌人完整生成链路：

```
GameSetup.Awake()                          ← MMHOOK ① 在这里新建并注册
  └─ VikingReference.OnGameAwake()
       └─ LevelStateObjectReferences.dict[name] = this    ← 敌人生成池注册表

战役生成：
  LevelNode.Setup(levelState)              ← Harmony ② 在这里加入每关 enemies
       └─ levelState.GetReferencedObjects(this.enemies)    ← 真正的“敌人生成池”

实际生成：
  Raid → possibleAgents = levelNode.enemies → ShipLoad.vikingRef
  Landing.Spawn()                          ← MMHOOK ③ 在这里施加外观/数值/技能
       └─ squad.CreateAgent(vikingRef.agent)
```

**本版本做三件事：**

1. **新建 + 注册**（`GameSetup.Awake`，MMHOOK）：
   `new GameObject` + `AddComponent<VikingReference>`（**不 `Instantiate` 克隆**），
   反射配置私有字段（`viking` 仅借用 `Viking_SwordShield` 的 `VikingAgent` 预制体引用），
   注册进 `LevelStateObjectReferences.dict`。

2. **加入敌人生成池**（`LevelNode.Setup`，Harmony postfix）：
   把新单位 append 到每关的 `enemies` 列表，使其成为 `Raid.possibleAgents` 的候选。

3. **生成时表现**（`Landing.Spawn`，MMHOOK）：
   对新单位施加黑色外观（`BatchedSprite` B 通道）、数值强化（伤害/击退/眩晕/体型）、
   冲刺（`SpearChargeComponent`）与刺击（`SpearStabAction`）技能。

4. **美术资源**（特质 Mod 风格）：
   从插件目录 `Resources/black_spearman_icon.png` 加载图标（无 PNG 时回退程序化图标），
   并注册 I2 本地化名称/描述。

> ⚠️ **关于枚举**：运行时无法新增 `VikingAgent.Type` 枚举值。本版本**复用 `SwordShield` 枚举值**
> （获得近战大脑行为），用 **VikingReference 的名字 + 引用**区分新单位，因此不需要 v1.2 的
> Mono.Cecil 枚举补丁，也不需要 UnityPy 资源补丁——更简单、更稳定。

---

## 安装要求

- 《Bad North》（Steam 版）
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
- `MMHOOK-Assembly-CSharp.dll`（放入 `BepInEx/plugins/`，来自 BadNorthDatabase-main）

## 编译

1. 修改 `BadNorthBlackSpearman1.3.csproj` 中游戏 DLL 的 `HintPath`（默认 `D:\Steam\steamapps\common\BadNorth\...`）。
2. 编译：

```bash
dotnet build BadNorthBlackSpearman1.3.csproj -c Release
```

3. 将 `bin/Release/net472/BadNorthBlackSpearman1.3.dll` 复制到 `BepInEx/plugins/`。
4. 将 `Resources/black_spearman_icon.png` 复制到插件 DLL 同目录（可选，缺失时会自动生成占位图标）。

## 配置（BepInEx 配置）

| 分组 | 键 | 默认 | 说明 |
|------|----|------|------|
| General | `SourceVikingName` | `Viking_SwordShield` | 借用 VikingAgent 预制体引用的源单位 |
| General | `NewVikingName` | `Viking_BlackSpearman` | 新单位在生成池中的名字 |
| General | `Bounty` | 8 | 赏金 |
| Spawn | `SpawnChance` | 0.7 | 每关加入生成池概率 |
| Spawn | `ForceFirstWave` | false | 强制第一波出现（便于测试） |
| Combat | `DamageMult` / `KnockbackMult` / `StunMult` / `ScaleMult` | 1.6 / 2.5 / 1.2 / 1.05 | 数值倍率 |
| Visual | `EnableRecolor` | true | 黑色外观 |
| Skills | `EnableCharge` / `EnableStab` | true / true | 冲刺 / 刺击 |

---

## 文件结构

```
BadNorthBlackSpearman1.3/
├── BadNorthBlackSpearman1.3.csproj
├── Plugin.cs                   # 入口：注册 + 生成池注入 + 生成时表现
├── BlackSpearmanArt.cs         # 美术资源（PNG 图标）+ I2 本地化
├── BlackSpearmanVisual.cs      # 黑色外观（对抗纹理重烘焙）
├── SpearChargeComponent.cs     # 冲刺技能（IBrainAction）
├── SpearStabAction.cs          # 刺击技能（IBrainAction）
├── Resources/
│   └── black_spearman_icon.png # 美术图标（可选）
└── README.md
```

## 许可

MIT License
