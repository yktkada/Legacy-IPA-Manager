# Legacy IPA Manager for Windows

**适用于旧版 iOS 设备的 IPA 资料库、兼容性分析与多设备安装管理器**

[English](README.md) | 简体中文

**文档：** [使用指南](使用指南.md) · [User Guide](USER-GUIDE.md) · [IdeviceIndex 组件说明](IdeviceGui/prototype/IdeviceIndex/README.md) · [更新日志](RELEASE_NOTES.md)

> 使用 C# / WPF (.NET 10) 开发的 Windows 桌面工具，用于管理大规模本地 `.ipa` 资料库，并通过 USB 向已越狱的旧版 iOS 设备（需安装 AppSync Unified）安装应用。
>
> 非官方第三方工具，与 Apple Inc. 无关联。

---

## 核心能力

### 1. 多设备独立并发队列
- **每设备独立串行泵，零全局锁**：已实测 4 台真机同时并发安装互不阻塞。
- **瞬间入队与智能排序**：双击即入队，自动按 **卸载优先 -> `PeakBytes`（压缩 + 解压估算）由大到小 -> 入队先后** 排序。
- **传输前空间预检与清理**：通过独立子进程 `AfcHelper.exe` 清理 `/PublicStaging` 并探测实时可用空间，空间不足自动安全挂起。
- **三段式严格成功判定**：结合 CLI 退出码、`stderr` 扫描与最多 3 次 `ideviceinstaller list --xml` 回读，杜绝 iOS 6/7 假成功。
- **断连自动恢复与安全取消**：USB 拔线自动暂停，重连稳定后恢复；上传阶段取消立即杀进程树，安装阶段取消等待完成并自动清理卸载。

### 2. 超大规模资料库（50,000+ IPA）与离线深解析
- **增量对账与深度解析**：SQLite (`WAL`) + NTFS `FileId`/体积/时间追踪；解析 `Info.plist`、解压体积、Mach-O 架构（`armv6`–`arm64`）、CgBI PNG 图标还原与来源分类（官方原版 / 重签名 / 砸壳）。
- **快速定位与管理**：同 BundleId 折叠分组、**0–9 / A–Z / #** 字母拼音跳转条、`Shift + 滚轮` 横向滚动、自定义用户分组（`data\groups.json`）、分设备滚动记忆、缺失文件自动识别置灰。

### 3. 硬件级兼容性分析与多维筛选
- **纯离线屏幕适配识别**：从启动图、Storyboard 与图标尺寸提取最高支持设备族（`OG 3.5"` / `3.5"` / `4"` / `4.7"` / `iPad Retina`）。
- **三态兼容性 & 组合高级筛选**：支持 **关闭 / 可装当前 / 已适配该设备** 快速切换，并支持按版本、体积、架构、解析状态与来源进行 AND/OR 组合筛选。
- **已装应用匹配高亮**：同版本精确匹配（绿字 + `✓`）与同 BundleId 不同版本（浅蓝）直观高亮。

### 4. 双语资源与便携零污染
- **统一双语界面（`Strings.resx`）**：单一工程内置英文与简体中文实时切换（设置 -> 布局），支持快捷键徽标提示与关窗进度显示。
- **零用户目录写入**：数据库、图标缓存与日志严格写入 `<程序目录>\data` 与 `<程序目录>\logs`，不污染 `%APPDATA%` 或 `%TEMP%`。
- **配套索引工具 `IdeviceIndex.exe`**：独立离线 IPA 索引浏览与 `aria2c` 下载器，下载完成后可通过本地命名管道直接导入主程序资料库（详见 [IdeviceIndex 说明](IdeviceGui/prototype/IdeviceIndex/README.md)）。

---

## 运行与构建要求

| 项目 | 说明 |
|---|---|
| **系统** | Windows 10 / 11 (x64) |
| **运行环境** | .NET 10 Desktop Runtime（运行）/ .NET 10 SDK（编译）+ Apple iTunes（提供 Apple Mobile Device Service，无需登录 Apple ID） |
| **已实测设备** | iPhone 4 (iOS 4.1)、iPad 2 (iOS 6.1.3)、iPhone 4S (iOS 7.1.1)、iPhone 5 (iOS 8.1.2)、iPod touch 5 (iOS 8.4.1) |
| **系统支持口径** | **iOS 4 / 6 / 7 / 8 + iPad** 真机验证；**iOS 9 / 10 目标支持但尚未实测** |

```powershell
# Release 构建（自动打包 AfcHelper、IdeviceIconHelper、IdeviceIndex 与 toolkit/ 工具链）
dotnet build .\IdeviceGui\prototype\IdeviceGui\IdeviceGui.csproj -c Release

# 运行快速单元测试（排除依赖真实子进程/管道的集成用例）
dotnet test .\tests\IdeviceGui.Tests\IdeviceGui.Tests.csproj -c Release -- --filter-not-trait Category=Integration
```

构建系统会通过 `Directory.Build.props` 自动使用仓库内 `toolkit/`，无需额外配置 MSYS2；工具链源码快照见 `third_party/`。

---

## 许可证

- 主程序源码：**GNU General Public License v3.0**（GPL-3.0-only），见 [LICENSE](LICENSE) 与 [COPYRIGHT](COPYRIGHT)。
- 内置 `aria2c.exe` (1.37.0)：**GPL-2.0**（`toolkit/aria2-LICENSE.txt`、`toolkit/aria2-SOURCE.txt`）。
- 第三方组件与许可证汇总见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 `toolkit/LICENSES/`。
