# ADOFAI Replay 手机版

这是 `ADOFAI-gg/ADOFAI-Replay` 针对 `StArray.ModManager` Android、CoreCLR 和 Unity IL2CPP 环境的移植。

## 功能

- 自动记录成功判定、提前按、过早/过晚空拍等每次输入的瓦片序号、行星角度偏差、HitMargin、自动判定与 No Fail 状态
- 起始砖以游戏的 `GCS.checkpointNum` 为准，因此从检查点续关的录制会正确记录真实起点，而不是被记成第 0 砖
- 提供 PC 版同类的细分保存策略：完整通关、任意起点通关、每次失败、90% 后失败，也可手动保存
- 「从第一砖通关后自动保存」只在真正从第 0 砖打通时触发；从检查点开始的通关需要另外开启「任意起点通关」或手动保存
- 从原始起点或检查点重载关卡，并屏蔽真实触摸后注入记录的判定
- 回放会在 `scrController.Awake` 清空 `GCS.checkpointNum` 后把起始砖写回，因此从检查点录制的回放会从中间开始播放，而不是从头
- 跨自定义谱面回放先通过 `PortalTravelAction(CustomLevelsScene)` 进入游戏原生自定义关卡界面，等待 `scnCLS` 扫描完成后调用其 `EnterLevel`；由游戏按正常游玩记录执行 `LoadCustomWorld`，Mod 不再从主界面直接调用 `LoadCustomLevel` 或 `scnGame.LoadAndPlayLevel`
- ImGui 入口只提交命令，暂停、恢复和谱面加载都在 `scrController.Update` 所在的游戏主线程执行
- 通过控制器状态检测谱面转场，不占用 `scrController.StartLoadingScene` Hook，可与 ShowBPM 同时启用
- 使用新版 StArray.ModManager 的 `[UnmanagedHook]` Source Generator 安装和卸载游戏 Hook，不再手工维护 Dobby trampoline
- 开始岛和自定义关卡选择页右下角显示 ImGui 回放入口，可打开独立全屏回放管理器；游戏、设置和其他场景中自动隐藏
- 回放管理器使用适合横屏触控的双行滚动列表和等宽大按钮；点击条目进入独立详情页，可编辑回放标题、查看进度与来源、播放或删除，返回后继续浏览列表
- 录制中和回放中的 HUD 与回放管理器使用同一套文本清洗规则，谱面名里的 Unity 富文本标签（如 `</color>`）和换行都会被去掉，只显示纯文本歌曲名
- 回放时在左下角提供暂停/继续和中止按钮；失败或通关后恢复原版结算并自动释放输入锁
- 通关结算页或失败进度页左下角保留本局录制的手动保存按钮，未达到自动保存条件的记录也可以保存
- 左下角操作窗口会跟随新版管理器的字体与样式缩放，避免高 DPI 手机上按钮被裁剪
- 保存成功后会在游戏画面顶部显示短暂的全局浮窗提示
- 可配置回放目录、最大保存数量、自动保存与 HUD
- 设置独立保存在 `mods/Replay/replay_settings.json`，不依赖 ModManager 的多态设置序列化
- 可读取手机版 JSON `.rpl`，也可安全导入 PC 版 BinaryFormatter `.rpl`

PC 自定义关卡回放通常保存的是电脑路径。把对应关卡放到手机后，先在游戏中打开同一关卡，再从 Replay 设置中播放该文件即可按歌曲名匹配当前关卡。

`1.4.2-mobile.25` 及更早版本录制的检查点记录，其 `StartTile` 被错误写成 0。播放这类旧文件时会按第一条判定的砖号自动还原起点，日志中会出现 `Repaired legacy replay start tile`。需要完全正确的元数据请用新版本重新录制。

## 构建

需要 .NET 10 SDK：

1. 从新版 StArray.ModManager 获取 `StArray.ModManager.dll`、`StArray.ModManager.Analyzer.dll` 和 `ImGui.NET.dll`。
2. 从 .NET 10 SDK 获取 `System.Formats.Nrbf.dll`。
3. 将三个引用文件放入 `References` 目录。

```bash
dotnet build MobilePlugin/Replay.csproj -c Release
python3 package_mod.py
```

最终 Mod 只携带 `Replay.dll` 和用于安全读取旧 PC 回放的 `System.Formats.Nrbf.dll`。公共的 `StArray.ModManager.dll` 与 `ImGui.NET.dll` 由加载器提供。

## 自动构建

推送 `MobilePlugin`、`VERSION.txt`、打包脚本或 Release Workflow 的变更到 `main` 后，GitHub Actions 会自动：

1. 构建固定版本的 StArray.ModManager 与 Hook Analyzer。
2. 编译 Replay 并运行 `package_mod.py`。
3. 校验 Zip、保存 Actions Artifact，并发布到 `v{VERSION.txt}` Release。

同一版本标签不会被移动到其他提交。修改代码并发布新包前必须先提升 `VERSION.txt`，以保证 Release 标签、源码和二进制一致。

## 安装结构

将 Zip 解压到手机 ModManager 的 `mods` 目录：

```text
mods/
└── Replay/
    ├── Replay.dll
    └── System.Formats.Nrbf.dll
```

回放默认保存在 `mods/Replay/Replays`。此版本不使用 UnityModManager、Harmony 或 UnityEngine 托管程序集，也不需要 `Info.json`。

## 下载

预编译安装包发布在 [GitHub Releases](https://github.com/iidamie/ADOFAI_Replay_Mobile/releases)。

## 致谢

- PC 原版：[ADOFAI-gg/ADOFAI-Replay](https://github.com/ADOFAI-gg/ADOFAI-Replay)
- 手机端加载器与模板：
[StArraySharp/StArray.ModManager](https://github.com/StArraySharp/StArray.ModManager)
[StArraySharp/UnityModTemplate](https://github.com/StArraySharp/UnityModTemplate)

仓库不包含《冰与火之舞》游戏程序集或 StArray.ModManager 的本地构建引用。
