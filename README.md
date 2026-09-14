# SoftTrace

SoftTrace 是一个轻量级 Windows 软件使用时间追踪工具。它在本地记录当前前台软件，并按日期汇总使用时长。

## v0.2 功能

- 自动识别当前前台软件（不记录网页、窗口标题或键盘内容）
- 从可执行文件提取软件图标，以紧凑排行显示
- 原创彩色秒表应用图标，并内置多种 Windows 显示尺寸
- 1 秒轮询，软件切换时生成活动区间
- 可配置 Idle 阈值；Idle、锁屏和睡眠只会切断区间，不出现在统计中
- SQLite 本地持久化，使用 UTC、WAL 和 15 秒检查点
- 今天、近 7 天、近 30 天或自定义日期范围的软件使用排行
- 系统托盘常驻、暂停/继续记录、单实例运行
- 异常退出后按最后检查点自动封闭未完成区间
- 使用系统浏览器完成 Google 登录，采用 OAuth 2.0 PKCE
- Firebase Firestore 每分钟自动双向同步，启动与登录后立即同步
- 每台电脑使用稳定设备 ID，可查看全部设备或单台设备统计
- 同步区间使用稳定 `sync_id` 去重，重复拉取不会重复累计
- Firebase 刷新令牌使用当前 Windows 用户的 DPAPI 加密保存

## 直接运行

发布包位于：

```text
dist/SoftTrace-v0.2.0-win-x64/SoftTrace.exe
```

该版本为 Windows x64 自包含程序，不要求另行安装 .NET。关闭主窗口只会隐藏到系统托盘；需要彻底退出时，请右键托盘图标并选择“退出”。

数据库与运行日志保存在：

```text
%LOCALAPPDATA%\SoftTrace\softtrace.db
%LOCALAPPDATA%\SoftTrace\softtrace.log
%LOCALAPPDATA%\SoftTrace\firebase-sync.json
```

SQLite 始终是本机采集的权威副本；断网期间继续记录，联网后自动补传。`firebase-sync.json` 只保存账号信息和经 DPAPI 加密的 Firebase 刷新令牌，不保存 Google 密码。

## 开发与验证

```powershell
.\run.ps1
.\build.ps1
```

`run.ps1` 使用项目内的 .NET 8 SDK（若存在）启动开发版；`build.ps1` 会运行 Release 构建、测试并生成自包含发布包。

## 当前边界

v0.2 专注于可靠采集、软件排行和 Firebase 多设备同步，暂不包含每日时间轴、开机自启、CSV 导出或 ManicTime 历史导入。这些能力会在后续版本中逐步加入。
