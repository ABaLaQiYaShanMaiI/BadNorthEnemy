# BadNorthBlackSpearman v1.2 — 单文件集成安装器

> **玩家只需一个 .exe。不需要 Python、.NET SDK、任何环境配置。**

---

## 玩家使用说明

### 安装

1. 下载 `BlackSpearmanSetup.exe`
2. 双击运行 → 按 `1` 安装 → 按 `3` 启动游戏

### 卸载

双击 `BlackSpearmanSetup.exe` → 按 `2` 卸载 → 所有文件恢复原样

---

## 开发者构建

```powershell
.\Build.ps1   # 需要 .NET 8.0 SDK
# 输出: publish\BlackSpearmanSetup.exe (自包含，约65MB)
```
