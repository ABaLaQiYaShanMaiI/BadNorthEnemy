# BadNorthBlackSpearman v1.2 — D+F 外部工具链

> **理念**：不再使用运行时 DLL 反射注入。改为在游戏启动前，一次性 patch `Assembly-CSharp.dll`（添加枚举值）和 Unity 资源文件（克隆 prefab）。

---

## 快速开始

### 第1步：准备工作

**你需要：**
- .NET Framework 4.7.2 SDK（[下载](https://dotnet.microsoft.com/download)）
- Python 3.8+（[下载](https://python.org)）
- Bad North 已安装

**复制 Mono.Cecil.dll：**
```powershell
cd BadNorthBlackSpearman1.2\EnumPatcher
copy D:\Steam\steamapps\common\BadNorth\BepInEx\core\Mono.Cecil.dll .
```
> 如果你的 Bad North 不在 Steam 默认路径，请替换为实际路径。

### 第2步：编译 EnumPatcher

```powershell
cd BadNorthBlackSpearman1.2\EnumPatcher
dotnet build -c Release
```

成功后会在 `EnumPatcher\bin\Release\net472\EnumPatcher.exe` 生成可执行文件。

### 第3步：安装 Python 依赖

```powershell
cd BadNorthBlackSpearman1.2\AssetPatcher
pip install -r requirements.txt
```

### 第4步：应用 Mod 并启动游戏

```powershell
# 双击运行或命令行:
LaunchModded.bat
```

**首次运行会自动：**
1. 备份 `Assembly-CSharp.dll` → `Assembly-CSharp.dll.orig_backup`
2. 备份 `data.unity3d` → `data.unity3d.orig_backup`
3. 向 `Assembly-CSharp.dll` 添加 `VikingAgent.Type.BlackSpearman = 8`
4. 在 `data.unity3d` 中克隆 `Viking_BlackSpearman` prefab
5. 启动 Bad North

**以后每次运行：**
- 检测到备份存在 → 从备份恢复 → 重新应用最新 patch → 启动游戏

### 第5步：卸载 Mod

```powershell
# 双击运行或命令行:
RestoreBackup.bat
```

交互式确认 → 恢复所有被修改的文件 → **游戏恢复如初，存档不受影响**。

---

## 自定义游戏路径

如果 Bad North 不在 `D:\Steam\steamapps\common\BadNorth`：

**方法1：修改 .bat 文件**
编辑 `LaunchModded.bat`，修改第 12 行：
```batch
set "STEAM_PATH=你的\BadNorth\安装\路径"
```

**方法2：设置环境变量**
```powershell
set BADNORTH_DIR=D:\Games\BadNorth
LaunchModded.bat
```

---

## 文件结构

```
BadNorthBlackSpearman1.2/
├── README.md                    ← 本文件
├── LaunchModded.bat             ← 一键 Patch & 启动游戏
├── RestoreBackup.bat            ← 一键恢复原版（卸载）
│
├── EnumPatcher/
│   ├── EnumPatcher.cs           ← Mono.Cecil 补丁源码
│   └── EnumPatcher.csproj       ← .NET 4.7.2 编译配置
│
├── AssetPatcher/
│   ├── asset_patcher.py         ← UnityPy 资源补丁
│   └── requirements.txt         ← pip install UnityPy
│
└── MiniPlugin/                   ← 可选：AI 技能（BepInEx 插件）
    ├── BlackSpearmanAI.cs       ← SpearCharge + SpearStab 注入
    └── BlackSpearmanAI.csproj
```

## 可选：安装 AI 技能插件

如果不安装 MiniPlugin，黑矛兵仍会出现（外观 + 数值差异），但没有 SpearCharge 和 SpearStab 特殊技能。

```powershell
cd BadNorthBlackSpearman1.2\MiniPlugin
dotnet build -c Release
copy bin\Release\net472\BlackSpearmanAI.dll D:\Steam\steamapps\common\BadNorth\BepInEx\plugins\
```

## 安全机制

| 机制 | 说明 |
|------|------|
| `.orig_backup` 备份 | 每个被修改的文件首次 patch 前自动创建 |
| 永不覆盖备份 | 备份文件一旦创建就不会被再次修改 |
| `RestoreBackup.bat` | 交互式确认 → 逐文件恢复 → 报告结果 |
| 存档安全 | **不修改任何存档文件** |
| 幂等 Patch | 每次从备份恢复 → 重新 patch，不会累积错误 |
| 备份可删除 | `.orig_backup` 在卸载后可删除以释放空间 |



## 可行性审查（基于 BadNorthDatabase-main 源码）

以下是对 D+F 方案所有潜在风险点的逐项审查：

### ✅ 已验证安全的系统

| 系统 | 源码证据 | 结论 |
|------|----------|:--:|
| `CampaignSave.HasEverSeen()` | 使用 `List<SerializeFriendlyEnum>` 迭代查找，非数组索引 | ✅ |
| `CampaignSave.Saw()` | 调用 `vikingsSeen.Add(vikingType)` — List.Add | ✅ |
| `CampaignDifficultySettings` | `Dictionary<VikingAgent.Type, float>` — 不存在 key 就无 multiplier | ✅ |
| `EnemyLineupIntro` | `GetReferencedObjects<VikingReference>()` — 遍历所有 VR | ✅ |
| `Raid/ShipLoad/Landing` | 通过 `VikingReference` 引用操作，不直接依赖枚举值 | ✅ |
| `LevelRule` / `LevelGuessable` | 在 prefab 上独立配置的组件 | ✅ |
| 存档文件 | 我们不修改任何存档 | ✅ |

### ⚠️ 需要注意的问题

| 问题 | 影响 | 缓解方案 |
|------|------|----------|
| **sprite2 图标** | UI 显示 SwordShield 图标（克隆自 SwordShield） | 可接受—黑色外观+微放大足以区分 |
| **AgentTextureBaker** | 部分帧覆盖 BatchedSprite 颜色 | 外观靠 scale+技能区分，颜色为辅 |
| **Asset 克隆兼容性** | UnityPy 对 Unity 2018.x 支持待验证 | .bat 有 fallback（运行时克隆） |

### ❌ 唯一已知局限

`VikingReference.seen` 使用 `type=8` 查询 `HasEverSeen`，List 查找功能正常。但部分 UI 的 `VikingAgent.Type` 过滤逻辑可能将 8 视为"未知类型"。这**通常表现为不会被过滤掉（default 行为是显示/包含）**，不影响游戏体验。

> **总结**：核心风险点（枚举越界、存档损坏、UI 崩溃）全部通过源码审查确认安全。
