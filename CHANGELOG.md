# Changelog

## 1.5.0 (2026-08-03)

- 修复自定义关卡无法触发自动保存的问题。
- 版本号改用语义化版本，不再累加 `mobile.N` 后缀。

## 1.0.0 (2026-08-01)

ADOFAI Replay 手机版正式发布，基于 PC 版 [ADOFAI-Replay](https://github.com/ADOFAI-gg/ADOFAI-Replay) 完整移植至 Android 平台。

### 核心功能

- **自动录制** — 进入任意关卡（官方 / 编辑器 / 自定义谱面）自动开始录制每一次击打的角度偏差与判定
- **完整回放** — 选择已保存的回放，在相同谱面上逐帧复现原始操作
- **编辑器支持** — 编辑器预览页左上角提供回放管理器入口，支持编辑器内录制与回放
- **旧版兼容** — 可读取 PC 版 `.rpl` 文件（BinaryFormatter 格式），实现跨平台回放共享
- **设置持久化** — 自动 / 条件保存策略、HUD 显示、保存上限等均可配置

### 录制

- 记录每次击打的 `seqID`、`hitAngle`、`hitMargin`（判定）和 `midSpin`（中旋）
- 录制持续到关卡结束（通关 / 失败 / 传送）自动终结
- 自动过滤自动游玩（Autoplay）模式的无效记录

### 回放

- 加载回放后对应谱面自动跳转，无需手动选择
- 逐帧比较行星角度，精确复现原操作时间点
- 支持暂停 / 停止 / 循环复用
- 中旋砖块判定正确注入

### 设置

- 自动保存：通关 / 每次通关 / 每次失败 / 90% 进度失败
- HUD 开关与位置 / 字号调整
- 最大保存回放数量限制
- 自定义回放存储目录

### 技术架构

- 基于 `StArray.ModManager 1.0.4+`，通过 `IModPlugin` 接口加载
- 使用 Dobby IL2CPP Hook 替代 PC 版 Harmony，无需 Unity 程序集依赖
- 编辑器 Hook 按场景动态安装 / 卸载，确保官方关卡回放不受影响
- 使用源生成 JSON 序列化，设置与回放文件独立于 ModManager 自带序列化器
- UI 层使用 ImGui（ModManager 内置），HUD 通过前景层绘制

### 已知限制

- Android 14+ 默认不允许直接写入外部存储，建议回放目录使用 Mod 内部存储路径
- 旧版 PC `.rpl` 文件（BinaryFormatter 格式）仅支持反序列化，手机版保存格式为 JSON
- 编辑器回放依赖 `scnEditor.Play` / `scnEditor.ResetScene` Dobby Hook，可能与未来游戏版本不兼容

---

*本项目基于 [ADOFAI-Replay](https://github.com/ADOFAI-gg/ADOFAI-Replay) 由 Flower / ADOFAI.gg 开发。手机移植版由 iidamie 维护。*
