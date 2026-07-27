# ADOFAI Replay 手机版

这是 `ADOFAI-gg/ADOFAI-Replay` 针对 `StArray.ModManager` Android、CoreCLR 和 Unity IL2CPP 环境的移植。

## 功能

- 自动记录成功判定、提前按、过早/过晚空拍等每次输入的瓦片序号、行星角度偏差、HitMargin、自动判定与 No Fail 状态
- 提供 PC 版同类的细分保存策略：完整通关、任意起点通关、每次失败、90% 后失败，也可手动保存
- 从原始起点或检查点重载关卡，并屏蔽真实触摸后注入记录的判定
- 第三方谱面之间使用 `scnGame.LoadAndPlayLevel` 原地热重载；从主岛进入时调用游戏原生 `scrController.LoadCustomLevel`，由游戏安全建立 IL2CPP 自定义谱面路径数组
- 通过控制器状态检测谱面转场，不占用 `scrController.StartLoadingScene` Hook，可与 ShowBPM 同时启用
- 开始岛显示 ImGui 回放入口，可打开独立全屏回放管理器；游戏、设置和其他场景中自动隐藏
- 回放管理器支持搜索、分页、进度与来源详情、播放和删除确认
- 回放时在左下角提供暂停/继续和中止按钮；失败或通关后恢复原版结算并自动释放输入锁
- 通关结算页或失败进度页左下角保留本局录制的手动保存按钮，未达到自动保存条件的记录也可以保存
- 保存成功后会在游戏画面顶部显示短暂的全局浮窗提示
- 可配置回放目录、最大保存数量、自动保存与 HUD
- 设置独立保存在 `mods/Replay/replay_settings.json`，不依赖 ModManager 的多态设置序列化
- 可读取手机版 JSON `.rpl`，也可安全导入 PC 版 BinaryFormatter `.rpl`

PC 自定义关卡回放通常保存的是电脑路径。把对应关卡放到手机后，先在游戏中打开同一关卡，再从 Replay 设置中播放该文件即可按歌曲名匹配当前关卡。

## 构建

需要 .NET 10 SDK：

1. 从 `StArray.ModManager 1.0.4+` 获取 `StArray.ModManager.dll` 和 `ImGui.NET.dll`。
2. 从 .NET 10 SDK 获取 `System.Formats.Nrbf.dll`。
3. 将三个引用文件放入 `References` 目录。

```bash
dotnet build MobilePlugin/Replay.csproj -c Release
python3 package_mod.py
```

最终 Mod 只携带 `Replay.dll` 和用于安全读取旧 PC 回放的 `System.Formats.Nrbf.dll`。公共的 `StArray.ModManager.dll` 与 `ImGui.NET.dll` 由加载器提供。

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
- 手机端加载器与模板：[StArraySharp/UnityModTemplate](https://github.com/StArraySharp/UnityModTemplate)

仓库不包含《冰与火之舞》游戏程序集或 StArray.ModManager 的本地构建引用。
