# IdeviceIndex

<p><img src="../../../assets/index-icon.png" width="88" alt="IdeviceIndex icon" /></p>

`Legacy IPA Manager` 的配套离线索引浏览与高速下载组件（随主程序目录同级分发，不可缺失）。

## 运行

```powershell
# 默认自动打开 <程序目录>\index\index.db（不存在则显示缺失提示与「索引」导入入口）
.\IdeviceIndex.exe

# 指定外部只读 index.db
.\IdeviceIndex.exe --db D:\Path\To\index.db
```

## 核心能力（设计规格见 [docs/12](../../docs/12-索引工具与私有索引.md)）

- **高规模全虚拟列表（200,000+ 行验收）**：
  - 默认以 `List.DisplayLimit = 0` 加载全量索引并依靠 WPF DataGrid 虚拟化流畅浏览，支持在设置中切回 `OFFSET` 分页（`500` 条/页）以控制单次查询上限；
  - 支持同 `BundleId` 组行按需异步展开、整数 `1/1` DIP 组边线对齐，以及关窗等待 `aria2c` 收尾时的标题栏步骤提示（` - 正在关闭 - <步骤>`）。
- **拼音快速跳转条 & 横向滚动**：
  - 网格上方与主程序同步提供 **`0–9`、`A–Z`、`#` 横向跳转条**（含汉字拼音首字母定位），空间不足时自动「隐灰不可用项 → 压缩间距 → 2–3 px 极窄横向滚动条」；
  - 列表与队列支持 `Shift + 滚轮` 横向滚动。
- **索引导入与轻量增量更新（`IndexImporter` / `IndexImportWindow`）**：
  - 支持本地 `index.db`（复制到 `<程序目录>\index\index.db` 原子替换或直接外链只读路径）、`manifest.json` / `entries.jsonl.gz` / `entries.jsonl` / `.json` 全量重建与单事务增量 `upsert`/`delete` 补丁，以及填入远程 `Index.UpdateUrl` 一键下载并校验更新。
- **高级搜索与筛选**：
  - 搜索框 `▾` 多字段与包含/前缀/精确匹配、独立「高级筛选」窗口、设备族筛选、最低 iOS 比较符（`≤`/`<`/`=`/`≥`/`>`）及内置 93 个历史 iOS 版本二级菜单。
- **全键快捷键与左列字母角标**：
  - 右键菜单将快捷键字母渲染在最左侧图标列（不展示冗余快捷键提示文字）；菜单展开时直接按单字母、未展开时按 `Ctrl+Alt+字母` 触发（复制二级菜单 `C` → `N/B/V/O`，展开/收起组 `G`，单选入队 `Q`，多选入队 `S`，行高 `H` → `S/M/L`，清空多选 `Z`，`Ctrl+L` 显隐右栏）。
- **aria2 下载引擎与 IPC 自动入库**：
  - 启动前对 `<程序目录>\aria2c.exe` 执行四步 SHA-256 校验，通过 `127.0.0.1` RPC 管控任务、断点续传、ZIP 魔数与体积校验；下载完成后通过 `IdeviceGui.Import.v1` 命名管道通知主程序即时对账入库。
- **写盘安全边界与双语界面**：
  - 默认仅允许下载写入程序目录；在设置页开启「允许写入程序目录外」需经无默认勾选框的二次确认弹窗；界面随主程序语言资源（`Strings.resx` / `Strings.zh-CN.resx`）中英双语切换，源码零非注释中文行。

## 构建

```powershell
dotnet build .\IdeviceIndex.csproj -c Release --nologo
```

构建主程序 `IdeviceGui.csproj` 时会自动构建本工程并连同 `e_sqlite3.dll`、`aria2c.exe` 与 `app.ico` 一并复制到输出目录。
