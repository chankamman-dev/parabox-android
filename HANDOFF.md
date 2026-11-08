# Patrick's Parabox 原生 Android 移植：交接

## 目录整理（2026-11-07 晚）
- 源码：`G:\ai\android-recompile\parabox-android`（原 `G:\ai liabary\parabox-android`）。
- 大文件：`E:\android-recompile-work\parabox-android-work`（原 `F:\parabox-android-work`），脚本和本文里的路径都已改成新位置。
- 已删：所有 x86_64 产物（`il2cpp-x86_64`、`apk-x86_64*`）；旧的 arm64 打包目录（`apk-arm64-stub`、`apk-arm64-bundle`）；`apk-arm64-fmod` 里的中间文件 `aligned.apk`、`unaligned.apk`。下文提到这些目录的地方都是历史记录。
- 保留：签名 `parabox-debug.keystore`；Thor 上在用的、也是最新的 arm64 包 `apk-arm64-fmod`；`il2cpp-arm64`；data、data-dl、stage、fmod 等构建输入。
- 要重新做模拟器测试包：`ABI=x86_64 bash tools/rebuild.sh`（会经由 build-il2cpp.sh 重新生成 `il2cpp-x86_64`，约 6 GB，耗时较长）。
- `tools/ptx/obj` 已删，因为里面写死了旧路径，下次 `dotnet build` 会重新生成。
- GitHub 公开仓库：https://github.com/chankamman-dev/parabox-android（main）。不入库的有：cloud/build、tools/**/bin|obj、keystore/apk/obb/resS/bank、`shots/launcher-login.png`（含登录二维码）、`local.env`（本机 ANDROID_HOME）。
  - 2026-11-08 公开前做过清理：删掉了 Steam 账号 ID 和 Thor 设备号；脚本里的 SDK 路径改成读 `ANDROID_HOME` 或 `local.env`；历史压成一次提交，作者邮箱用 GitHub noreply。**以后提交继续用 noreply 邮箱（本仓库的 git config 已设置），也不要把账号 ID、设备号写进文件。**

## 声音（2026-11-07）：FMOD 2.03.15 安卓库 + 2.01 转接层

- 用户下载的是 FMOD Engine **2.03.15** 安卓包（`Downloads\Compressed\fmodstudioapi20315android.tar.gz`，解压在 `E:\android-recompile-work\parabox-android-work\fmod\sdk-20315`），游戏封装层是 2.01.11。比对结果（`ptx pinv <dll> sig`、`ptx structs <dll>`，输出在 `E:\android-recompile-work\parabox-android-work\fmod\`）：
  - 570 个导入里，只有这些不兼容：
    - `System_Create` 新增了版本参数。
    - `System_GetVersion` 新增了 buildnumber 参数。
    - 两个 `GetCPUUsage` 改成用结构体返回。
    - `GetMinimum/MaximumDistance` 合并成了 `GetMinMaxDistance`。
    - `HasCue/TriggerCue` 改名为 `HasSustainPoint/KeyOff`。
  - 结构体里只有两处变化：`FMOD_ADVANCEDSETTINGS` 末尾多了 `maxSpatialObjects`，FMOD 会检查 cbSize；`PARAMETER_DESCRIPTION` 末尾多了 `guid`，FMOD 会写到 2.01 结构体外面。其余 19 个跨边界的结构体字段完全一致。
- 转接库 `tools/fmodshim/pbxfmod.c` 编成 `libpbxfmod.so`，对外提供 `pbx_<原入口>`。`Hooks.cs` 把这 19 个 DllImport 改指向它，其余入口直接调 2.03。真实函数用 `dlopen("libfmodstudio.so")` 加 `dlsym` 查找。
  - `tools/fmodshim/build.sh`：把 2.03 的 libfmod、libfmodstudio、libresonanceaudio 和编好的 libpbxfmod 放进 `E:\android-recompile-work\parabox-android-work\fmod\lib\<abi>`，fmod.jar 放到 `fmod\fmod.jar`。之后 build-apk 默认就用这些真库（不再需要 FMODLIB 空桩）。
  - 诊断：执行 `setprop debug.pbxfmod.meter 1` 后，日志 `PbxFmod` 每约 300 帧打一次主混音电平。
- 模拟器验证：
  - Studio 系统创建成功，AAudio 输出打开，2.01 的 bank 在 2.03 运行时下正常加载。
  - 语言页没有声音（原版就是这样）。进入标题和枢纽关卡后主混音有信号：6 声道，RMS 0.004→0.014，峰值约 0.03。
  - 走动、撤销都没有 FMOD 报错，没有崩溃。
  - **实际听感没测**：模拟器是 `-no-audio`，电平说明有声音，但音质和混音对不对要上真机听。
- 输出：`E:\android-recompile-work\parabox-android-work\apk-arm64-fmod\parabox-android.apk`（内置包 + 声音，135 MB），2026-11-07 13:36 已覆盖安装到 Thor，`apk-x86_64-fmod`。

## 内置包（2026-11-07）：游戏数据打进 APK，首次启动自动解压

- **起因**：Thor 上扫码登录成功（云同步正常，日志 `sync: 一致 3`），但下载报「版本清单里没有找到游戏数据文件」。原因是 JavaSteam 解密清单文件名时，把 `\` 换成了 `File.separatorChar`，在 Android 上就是 `/`；而 port.txt 和 `downloaded()` 都按 `\` 匹配。在 PC 上测时分隔符仍是 `\`，所以当时没暴露。已修：`GameData.depotName()` 把 `/` 还原成 `\`（`files()` 和 `others()` 两处）。**修好后的 Steam 下载还没在真机上测过。**
  - 公主项目（princess-android/cloud 的 GameData，前缀 `MagicalPrincess_Data\StreamingAssets\...`）是同一套代码，在 Android 上应该有同样的问题。
- **内置包**：`BUNDLE=1 bash tools/build-apk.sh`（或通过 rebuild.sh）会把 port.txt 里 6 个 `dl` 文件从游戏目录原样放进 `assets/gamedata/`，压缩存放。APK 大小：arm64 约 134 MB，x86_64 约 135 MB。
  - 启动页检测到 `GameData.bundled()` 且数据还没就绪时，会自动开始「解压游戏数据」（`AssetSource`）。走的是和下载相同的安装流程，包括 SHA-1 校验和状态文件。模拟器上 134 MB 约 2 秒。不需要登录和联网。
  - 卡片说明、数据状态（待解压、解压中）、主按钮和错误标题都按内置包改了文字；「检查游戏数据」不登录也能用，会从 APK 重新校验和修复。
  - 之后 Steam 若有只改了下载文件的更新，仍然从 Steam 增量下载（target ≠ APK 的清单时走 SteamSource）。
  - 代价：数据在 APK 和 files/gamedata 各有一份，约多占 134 MB。
- 输出：`E:\android-recompile-work\parabox-android-work\apk-arm64-bundle\parabox-android.apk`（Thor，2026-11-07 01:37 已覆盖安装到 Thor，待实测）、`apk-x86_64-bundle`（模拟器）。
- 同时修了触屏提示：菜单里的移动提示（`MoveNR`，例如全新安装后的语言选择页）原来显示成「D-Pad/Up…」文字。`DrawControl` 的 MoveNR 分支只有手柄和键鼠两种图片。现在屏幕按键显示时，`TouchPad.PromptAction` 把它当成 `Move` 处理，于是画出游戏自带的十字键图。

## 第二阶段（2026-11-06 晚）：Steam 集成、首次从 Steam 下载、启动页、性能

### 现状
- **模拟器上验证过**：新启动页（未登录状态）；从本地测试目录安装游戏数据（走和 Steam 下载相同的安装、校验、状态记录流程）；从启动页进入游戏，游戏读的是下载到 `files/gamedata` 的贴图和音频库；游戏里存档触发上传钩子（`onSaved`）；帧率钩子和游戏时长上报（`onGameStarted`）。
- **没验证（需要用户扫码登录 Steam）**：真实的 Steam 下载、云同步、成就上报、游戏时长、头像、成就列表、Steam 记录的游戏时长。下载链路和公主项目是同一套（公主在 PC 上用匿名账号验证过），这里改成了单个文件内多块并行下载，这一点**没有实测**。
- 仍然没有声音（FMOD 2.01 安卓库还没有，测试包用的是空桩）。Thor 没装。
- 截图在 `shots/`：`launcher-first-run.png`（首次启动）、`launcher-home.png`、`launcher-cloud.png`、`launcher-settings.png`、`launcher-login.png`（二维码是 Steam 实时返回的）。

### 首次启动从 Steam 下载
- APK 不再带 `*.resS`（贴图、网格、音频流，65 MB）和 FMOD 的 3 个 bank（69 MB），共 6 个文件、134 MB。启动页从 Steam 的 Windows depot 1260521 原样下载，放到 `/data/data/dev.kmparabox.game/files/gamedata/`。
- **怎么让游戏读到那里**：
  - 贴图：`ptx respath` 把转换后数据里 623 个对象（Texture2D、AudioClip 等）的外部流路径改成绝对路径 `/data/data/dev.kmparabox.game/files/gamedata/xxx.resS`。模拟器上验证过，Unity 2020.3 能从绝对路径读取流数据。改过路径的数据在 `E:\android-recompile-work\parabox-android-work\data-dl`（build-apk 默认用它）。
  - 音频：`FMODUnity.PlatformAndroid.StaticGetBankFolder` 改成返回 `Bridge.BankFolder()`，也就是同一个目录。
- 文件是原版字节，不用在设备上转换。make-port.sh 生成的 `port.txt` 记录了 depot 清单号、APK 里带的每个文件（base）和要下载的每个文件（dl）的 SHA-1。我用 Steam 客户端缓存的清单（`F:\game software\depotcache\1260521_3378613542427424471.manifest`）核对过，144 个文件全部一致。
- 下载：4 个文件并行，单个文件内 6 个块并行，每个文件下载完做 SHA-1 校验，状态写在 `gamedata/.parabox-state`。中断后已完成的文件不会重下。
- APK 从 271 MB 降到 **64 MB**（arm64）。除了不再带数据，libil2cpp.so 现在会去掉调试信息；原生库和 dex 改为压缩存放（`extractNativeLibs=true`）。播放器数据仍然不压缩，因为引擎要直接从 APK 里映射。
- 测试包：`F:\parabox-android-work\apk-arm64-stub\parabox-android.apk（2026-11-07 整理时已删）`（arm64，手机和 Thor 都能用，64 MB，含触屏操作；2026-11-06 23:14 已用 `adb install` 装到 Thor（首次安装），待用户实测）和 `apk-x86_64-stub`（模拟器用）。两个包带的都是空 FMOD 桩，所以都没有声音。

### Steam 更新（可行性结论）
- 设备端已实现：启动时向 Steam 查询 depot 的最新清单。
  - 如果新版本只改了要下载的那 6 个文件，就在设备上增量更新，主按钮变成「更新并开始 · xx MB」。
  - 如果改了 APK 里带的任何文件（代码 Assembly-CSharp、关卡和着色器所在的 .assets 等），就提示「需要新版移植包」，可以继续玩当前版本。
- 实际情况：Parabox 的更新基本都会改代码或关卡数据，所以**几乎每次更新都需要在电脑上重新生成 APK**（代码要重新 IL2CPP 编译，设备上做不到；Thor 的处理器也不支持 32 位，用不了 Mono）。换上新 APK 后，设备只增量下载变化的文件。
- 电脑上更新移植包：Steam 更新游戏 → `ptx pdata` → 复制成 data-dl 再 `ptx respath` → `make-port.sh` → `rebuild.sh`。
- 目前本机的 build 8556490 就是 Steam 上的最新版（appmanifest 里 TargetBuildID = buildid），没有第二个版本可以对比。

### 启动页（`cloud/.../LauncherActivity.java`，dev.kmparabox.cloud）
- 风格跟游戏标题画面一致：深色背景、缓慢漂移的灰色方块、蓝色主按钮、Patrick 的粉色。左边是标题、提示、下载进度、主按钮，以及「成就」「云存档」「设置」三个按钮；右边是 Steam 卡片（头像、昵称、Steam 记录的游戏时长、成就进度条、云存档状态、游戏数据状态）。未登录时，卡片里显示扫码登录说明，头像用游戏图标里的 Patrick。
- 页面：
  - 成就：22 个成就，带 Steam 的中文名、图标和解锁日期；未解锁的显示灰色图标，隐藏成就不显示名字。
  - 云存档：本机存档列表和上次同步时间，可以立即同步。
  - 设置：帧率 30/60/90/120、渲染分辨率 100/85/70%、开发者日志、检查游戏数据。
  - 账户：刷新资料、退出登录（要二次确认）。
  - 扫码登录。
- 动效：进场时左边逐行上浮、卡片从右滑入；卡片页缩放淡入，退场反向；进游戏前整页淡出。全部可以用手柄操作：十字键移动，A 确认，B 返回。
- 资料（`SteamProfile.java`）用一个独立的 Steam 会话获取：昵称和头像来自 PersonaState；成就用 `getExpandedAchievements("schinese")`；游戏时长来自 Player.GetOwnedGames 的 playtime_forever。结果缓存在 `files/profile/`。
- 应用图标换成了游戏 exe 里的图标（`art/icon`，用公主项目的 pctest/Icon.java 生成）。

### 游戏侧钩子（Hooks.cs、paraboxandroid/Bridge.cs）
- `SaveFile.Save` 和 `Prefs.Save` 结尾 → `Bridge.Saved` → 4 秒后上传云存档。
- `Achievements.Achieve(name)` 开头 → `Bridge.Achieve`。每个名字每次运行只发一次，因为 `World.UpdateInner` 每帧都会检查。名字就是 Steam 的 API 名（`ACH_*`），交给 SteamPlay（成就位号 bug 已修好的那版）。
- `World.Awake` 结尾的 Boot 负责几件事：读取启动页设置；按设置调整渲染分辨率；开发者日志关闭时关掉游戏日志（游戏每条 FMOD 警告都带堆栈）；上报游戏时长（SteamPlay）。
- `Application.targetFrameRate` 的 3 处赋值 → 改用启动页的帧率上限。
- PC 显示选项（prefs.txt 从电脑同步过来，里面有窗口模式、垂直同步）：`Screen.fullScreen`（3 处）、`QualitySettings.vSyncCount`（4 处）和 `Screen.SetResolution`（1 处）都改成 Bridge 里的空实现，手机上始终全屏、按 targetFrameRate 控帧。
- 开发者日志打开时，`Diag` 每 5 秒打一次帧率、最慢一帧和托管堆大小（logcat 标签 `Parabox`）。

### 性能
- 画质设置本来就很轻：只有一个画质等级，没有抗锯齿和阴影。
- 游戏的画法：所有方块都用 `Graphics.DrawTexture` 一个一个画，不做合批，所以每帧的绘制调用很多。模拟器上渲染线程（UnityGfxDeviceWorker）占满一个核，主线程几乎空闲。
- 模拟器实测标题画面：渲染分辨率 100% 时约 23 帧，70% 时约 39 帧。模拟器是把 GLES 转发给宿主机执行的，绘制调用和像素都比真机贵得多，所以不能代表 Thor。这个游戏也出了 Switch 版，Thor 比 Switch 强得多，预计能跑满 60 帧。如果 Thor 上不够，就先降渲染分辨率；再不够，就要把 DrawTexture 合批（改动大）。
- 默认 60 帧（游戏在移动平台上本来就设 60），Thor 可以选 120。
- 关闭日志、libil2cpp 去掉调试信息、增量 GC（gc-max-time-slice=3）。
- **Thor 上的实际帧率还没测**：打开开发者日志，看 logcat 里的 `fps:`。

### 纯触屏操作（普通手机，`tools/paraboxandroid/TouchPad.cs`）
- 原版其实写过一套手机按键（`TouchControls`：撤销、重做、重来、暂停、确认，外加滑动移动），但没启用：`LoadLevel.Load` 在移动平台上加载完按键后，紧接着又 `buttons.Clear()` 清掉了；而 `Controls.UpdateButtons` 在没有按键时直接返回，所以滑动也失效。结果是只有触屏时什么都操作不了。菜单也完全不读鼠标，只读 `Controls` 的方向键、确认、返回这些字段。
- 做法：在 `Controls.Update` 的结尾注入代码，先调用 `TouchPad.Poll()`，再把触屏状态按位或进游戏的 20 个输入字段（`up/down/left/right` 及对应的 `*Pressed`，以及 `undo/redo/restart/escape/confirm/cancel`）。这和手柄、键盘写的是同一批字段，所以关卡、菜单、暂停菜单都能用，也不用配对输入设备。
- 布局（横屏）：
  - 左下：方向盘。按住后手指滑动可以换方向。
  - 右下：「撤销」（大）、「确认」、「返回」。
  - 右上：小按键「重做」「重来」「菜单」。
  - 屏幕空白处滑动：每划过屏幕短边的 1/15 走一格，按住不放会继续走，和原版的滑动代码一致。
  - 支持多指同时操作。
- 绘制：在 `World.OnGUI` 结尾调用 `TouchPad.DrawGUI`。游戏的画面全在 `World.OnGUI` 里画，用独立的 OnGUI 会被游戏的画面盖住，所以挂在它后面。贴图（圆、环、胶囊、箭头）在运行时生成，不依赖游戏资源。
- 按键提示：在 `Draw.DrawControl` 开头把输入方案改成 "Touch"。之后 `ControlIcon` 返回 null（不画键盘或手柄的图），`BindingDisplay` 返回按键名，提示就变成「确认键 开始游戏」「菜单键 菜单」。移动提示返回 `D-Pad/Up` 这类文字，正好让游戏画出它自带的十字键图。暂停菜单里的按键设置页不受影响。
- 显示规则（启动页设置「屏幕按键」）：
  - 自动：没接手柄时显示；用了手柄就隐藏，摸一下屏幕又出现。判断只看 `Gamepad.all`，因为 Android 会把键盘和模拟器的虚拟设备也报成 Joystick。
  - 始终显示、隐藏。
  - 另有「按键大小」：小、中、大。
- 按在屏幕按键上的手指不会交给虚拟鼠标，这样不会触发游戏检视模式里的点按。
- 读取的是所有触屏设备，不只 `Touchscreen.current`：模拟器有 11 个，有的手机也不止一个。
- **测试注意**：`adb shell input tap` 发出的事件来自虚拟设备 -1，Input System 不把它当成触屏，所以测不了。要用 `tools/pad/mktouch.js` 生成 uinput 脚本，注册一块真正的多点触控屏，再模拟点按、按住、拖动和双指。
- 模拟器上已验证（用 mktouch.js 的虚拟触屏，从截图里找玩家的位置确认）：
  - 点「确认」能从标题进入游戏。
  - 方向盘按住能移动；上下左右四个方向的滑动都正确，向上被墙挡住时不动；按住不放会连走多格。
  - 「撤销」能撤回一步，「重做」能把它重做回来。
  - 「菜单」能打开暂停菜单，「返回」能关闭。
  - 标题上的提示变成了「确认键」「菜单键」。
  - 接上虚拟 Xbox 手柄按一下，屏幕按键就自动隐藏。
- 截图：`shots/touch-title.png`、`touch-hub.png`、`touch-pause-menu.png`。
- 没验证：真手机上的手感、按键大小和位置（模拟器是 1920×1080）；竖屏（游戏固定横屏）；渲染分辨率低于 100% 时触点坐标对不对得上（按理 Input System 的坐标会跟着缩放）。
- 屏幕按键的点击范围只在按键外扩 4% 屏幕高度。之前外扩 12%，从「重做」附近开始的滑动被当成按了重做。

### 测试方法（模拟器）
- `tools/push-src.sh`：把原版的 6 个文件推到应用外部目录。然后用 `am start -n dev.kmparabox.game/dev.kmparabox.cloud.LauncherActivity --es localSource /sdcard/Android/data/dev.kmparabox.game/files/pbx-src` 启动，点「从测试目录安装」，走的是和 Steam 下载相同的安装流程。
- `tools/run-emu.sh` 现在通过启动页启动，带 `--ez play true`：数据已就绪时直接进游戏。
- cloud 模块不用 Gradle：`cloud/build.sh` 直接用 javac 编译，依赖 jar 在 `cloud/build/deps`（从公主项目复制）。rebuild.sh 会自动调用它。

## 第一阶段现状（2026-11-06 14:10）

- **第一阶段完成：模拟器上跑到了标题画面，还进了第一个枢纽关卡。** 用的是 x86_64 测试包。语言选择、简体中文标题、手柄按键提示（Xbox 的 A 和菜单图标）、枢纽关卡都正常渲染。截图在 `shots/`：`emu-x64-language.png`、`emu-x64-title-zh.png`、`emu-x64-hub.png`。
- **目前没有声音。** 游戏用的是 FMOD 2.01.11，本机只有 Hades 2 目录里的 FMOD 2.02.28 安卓库，两者不兼容（见下文）。测试包里放的是我自己编的「空 FMOD」桩库：所有入口都返回 OK，只用于验证画面，**不能交付**。
- arm64 包（Thor 用）也编过了：`E:\android-recompile-work\parabox-android-work\apk-arm64-stub\parabox-android.apk`，271 MB，带空 FMOD 桩，**没有装机，也没在设备上运行过**。
- 还没做：真正的 FMOD 声音、Steam 集成、Thor 装机。
- 包名 `dev.kmparabox.game`，入口 `dev.kmparabox.ParaboxActivity`（继承 UnityPlayerActivity，在 Unity 启动前先初始化 FMOD 的 Java 层）。

## 结论

**能做，而且比活侠传简单得多。** 只剩 FMOD 安卓库这一个外部依赖。

| 项 | 情况 |
| --- | --- |
| 体量 | 194 MB，1 个场景，单个 APK 就能装下（不用 OBB），约 270 MB |
| 引擎 | Unity **2020.3.30f1**，Mono。本机的 2020.3.49 播放器能直接跑（见下文「版本兼容」） |
| 着色器 | 内置管线，只有 1 个自定义着色器（`Unlit/AllBlocksShader`），加上 42 个内置着色器，共 192 个变体，DXBC→GLES3 **全部转换成功** |
| 纹理 | DXT5 157 个、DXT1 456 个、RGB24/RGBA32 少量。Thor 支持 S3TC |
| 输入 | Input System 1.2.0。改用活侠传已为 Android 编好的 1.5.1，数据做两处转换 |
| 触屏 | 游戏**自带触屏操作**（`TouchControls`，`World.OnMobilePlatform()` 在 Android 上返回 true） |
| 音频 | FMOD 2.01.11（`fmodstudio.dll` 版本信息 `2.1.11 (build 118066)`）+ Resonance Audio 插件 DLL（bank 里没用到 Resonance） |
| Steam DRM | 没有加壳。`SteamManager.Awake` 里 `RestartAppIfNecessary` 失败不会退出 |
| 预计剩余工作量 | 拿到 FMOD 2.01 安卓库后，接声音半天；Steam 集成 1 天（cloud 模块现成）；Thor 实测和手柄、触屏细节半天到 1 天。**合计约 2–3 天** |

## 事实（都从文件里确认过）

- 安装位置：`H:\SteamLibrary\steamapps\common\Patrick's Parabox`，AppID 1260520，depot 1260521，build 8556490。appmanifest 的 LastOwner 是第二个账号，但**云存档在主账号名下**（Steam 目录下 `userdata\<主账号 ID>\1260520\remotecache.vdf`）。
- 原生插件：`Plugins/x86_64/fmodstudio.dll`（2.01.11）、`resonanceaudio.dll`、`steam_api64.dll`（Steamworks.NET）。
- 托管程序集：Assembly-CSharp（149 KB IL）、Assembly-CSharp-firstpass（Steamworks.NET，902 个 P/Invoke）、FMODUnity、FMODUnityResonance、Unity.InputSystem 1.2.0。全部 IL 共 533 KB。
- 存档：`Application.persistentDataPath` 下的纯文本。`save0.txt`…（`SaveFile.GetSaveFilePath`，第一行 `version 6`）、`prefs.txt`（`version 11`，含语言、音量、垂直同步、全屏等），以及 `custom_levels/` 目录。安卓上是 `/sdcard/Android/data/dev.kmparabox.game/files/`。PC 本地存档在 `%USERPROFILE%\AppData\LocalLow\Patrick Traynor\Patrick's Parabox`（save0、save1、prefs）。
- Steam 云（appinfo 的 ufs）：根目录 `WinAppDataLocalLow`，路径 `Patrick Traynor/Patrick's Parabox`，同步 `save*.txt`、`prefs.txt`、`custom_levels/*/*`。Mac 和 Linux 有路径改写（MacAppSupport、LinuxHome `.config/unity3d/...`），说明有 Linux/Mac 版本，但用不上。
- 成就：22 个，名字在 `Achievements.achNames`（`ACH_RECURSE` … `ACH_100_PERCENT`）。游戏调用 `Achievements.Achieve(name)` 只置标志（共 24 处调用：`World.UpdateInner` 11 处、`Movement.Impulse` 6 处等），再由 `SteamSquare.Update` 每帧推给 Steam（`SetAchievement` + `StoreStats`）。**存档里不记录成就**：有些成就（通关、200 关、100%）在 `World.UpdateInner` 里按存档状态判断，所以会在游戏里自动重新触发；但纯操作类成就（比如 `ACH_HELLO_THERE`）只在当次触发。补报方案见「后续工作」。
- 没有统计（stat）接口，只有成就。

## 版本兼容：2020.3.49 播放器跑 2020.3.30 数据

本机只有 2020.3.49 的 Android 播放器（`E:\lom-android-work\unity`）。比对结果：

- 数据里的 33 个引擎类，有 30 个的类型哈希和活侠传（2020.3.49）的数据完全相同。哈希相同就说明序列化布局相同。
- 剩下 3 个只差一两个字段（classdata.tpk 里两个版本的字段树对比）：
  - GraphicsSettings：2020.3.49 多了 `m_CameraRelativeLightCulling`、`m_CameraRelativeShadowCulling`。
  - PlayerSettings：多了 `resetResolutionOnWindowResize`、`windowsGamepadBackendHint`。
  - BuildSettings：少了 `m_AuthToken`。
- `ptx pdata` 把这三个类用 2020.3.30 的字段树读出来，按名字拷到 2020.3.49 的字段树里再写回；新增字段用默认值，类型哈希换成活侠传数据里的值，所有文件的版本号改成 2020.3.49f1。
- 结果：播放器加载正常，没有报数据损坏。**不需要下载 2020.3.30 的 Android 模块。**

## Input System：用 1.5.1 替代 1.2.0

PC 版的 Input System 是按 Windows 编的，Android 的手柄布局被编译开关去掉了。活侠传已经把 1.5.1 按 Android 重编过（`E:\lom-android-work\inputsystem\Unity.InputSystem.dll`），直接拿来用，不用下载 1.2.0 源码。差异逐项处理：

- 序列化布局（`ptx sercmp` 比对）：InputActionAsset、InputActionMap、InputAction、InputBinding、InputControlScheme 完全一致。只有两个对象要改：
  - InputSettings：去掉 1.2.0 的 `m_FilterNoiseOnCurrent`，末尾补上 1.5.1 的 `m_ShortcutKeysConsumeInputs`（false）。
  - PlayerInput：在 `m_NotificationBehavior` 之后插入 1.5.1 的 `m_UIInputModule`（空引用，12 字节）。
  - 这两处直接改字节，因为脚本数据没有类型树；而用 MonoCecil 生成模板要额外下载一个 NuGet 包。注意：脚本里每个 bool 都单独补齐到 4 字节。
- API：只有 `Mouse.scroll` 的返回类型从 Vector2Control 变成了 DeltaControl（它的子类）。`ptx pstage` 把解析不到的调用改指向同名、同参数个数的方法。
- 1.5.1 带 UI 支持，引用了 UnityEngine.UI.dll，而 Parabox 没有这个 DLL。这里用活侠传 depot 里的那份（放在 `extra/`）。

## 织入（ptx pstage + Hooks.cs）

- Steamworks.NET 的 902 个 P/Invoke 全部换成空桩：`SteamAPI.Init` 返回 false，`RestartAppIfNecessary` 返回 false。
- `Steamworks.InteropHelp.TestIfAvailable*` 改成空方法。原因是 `SteamSquare.Init` 没判断 Steam 是否可用就调用 `SteamUtils.GetAppID`，抛出的异常会打断 `World.Awake`，结果是黑屏。改完之后所有 Steam 调用都落到空桩上，返回 0 或 false；商店页按钮会因为 overlay 关闭而走 `Application.OpenURL`。
- `World.Awake` 结尾调 `ParaboxAndroid.Bridge.Boot()`。
- `Controls.ControlIcon` 里的 `PlayerInput.devices[0].ToString()` 换成 `Safe.DeviceName`：没有配对设备时返回 "none"，不再每帧抛越界异常。

## ParaboxAndroid.dll（tools/paraboxandroid）

- **虚拟鼠标 TouchMouse**：PC 代码每帧读 `Mouse.current`（`Controls.Update`、`World.ComputeMousePosition`），游戏自带的触屏按钮也是拿鼠标位置做点击判定。Android 上没有鼠标，`Mouse.current` 为空，导致每帧空引用、画面卡死。现在启动时如果没有鼠标，就加一个虚拟鼠标，用主触点驱动：位置跟随手指，按下时左键按下。**触屏点击的实际效果还没验证**（语言菜单不吃点击，要用方向键选）。
- `Diag`：每 2 秒检查一次，输入设备或 PlayerInput 状态变化时打 `[ParaboxDiag]` 日志。发布前可以去掉。

## FMOD（卡点，需要用户决定）

- 游戏的 FMODUnity.dll 是 2.01 的 C# 封装，按 Windows 编译，所有 DllImport 都指向 `fmodstudio`。Android 上需要：`libfmod.so`、`libfmodstudio.so`（arm64-v8a；模拟器测试还要 x86_64），以及 `fmod.jar`（`org.fmod.FMOD`）。`ParaboxActivity` 已经写好了 `System.loadLibrary("fmod")` 和 `org.fmod.FMOD.init(this)`，因为 Windows 版的 FMODUnity 里没有这一步。
- 读取 bank：`PlatformAndroid.StaticGetBankFolder` 返回 `file:///android_asset`，所以 build-apk.sh 把 `StreamingAssets` 里的三个 bank 放在 APK 的 `assets/` 根目录。
- `libfmodstudio.so` 依赖 `libfmod.so`。core 函数（`FMOD5_*`）通过 `dlopen("libfmodstudio.so")` 的句柄用 dlsym 查找，bionic 会搜索依赖库，理论上能找到。如果找不到，就在 pstage 里把 `FMOD5_*` 的 DllImport 改成 `fmod`。
- **本机只有 2.02.28**（`E:\hades2-android-work\ref-unpacked\lib\arm64-v8a`，反汇编 `SystemI::getVersion` 得到 0x00020228）。2.02 的 `System_Create` 多了 headerversion 参数，几个结构体的布局也变了，2.01 的封装直接调用会出错，所以不能用。
- 需要的是 **FMOD 2.01.x 的安卓库**。同一个小版本内二进制兼容，不一定非要 2.01.11。来源：
  1. fmod.com 下载 FMOD for Unity 2.01.11（`fmodstudio20111.unitypackage`），或 FMOD Engine 2.01.xx 的 Android 包。**要 FMOD 账号登录**，我不能代为注册或登录，需要用户自己下载，然后放到 `E:\android-recompile-work\parabox-android-work\fmod\dl\`。
  2. 不用 FMOD：用 vgmstream 解码 bank（FSB5），再用 Unity 音频重写事件逻辑。工作量 2–3 天，还原度更低（音乐按 `InPuzzle`、`PlayerOutside`、`Paused` 等参数分层，混响），**不推荐**。
- 拿到库之后：把 so 放进 `E:\android-recompile-work\parabox-android-work\fmod\lib\<abi>\`，把 `fmod.jar` 放进 `E:\android-recompile-work\parabox-android-work\fmod\`，重跑 `rebuild.sh` 即可（不再带 `FMODLIB` 参数，就会用真库）。

## 构建

```
cd "G:/ai/android-recompile/parabox-android/tools"
# 数据（游戏更新后才需要重跑）：
dotnet /e/android-recompile-work/parabox-android-work/ptx-bin/ptx.dll pdata "H:\SteamLibrary\steamapps\common\Patrick's Parabox\Patrick's Parabox_Data" \
  'E:\android-recompile-work\parabox-android-work\data' 'E:\android-recompile-work\parabox-android-work\shtmp' 'E:\lom-android-work\hlslcc-build\hlslcc-cli.exe' ptx/classdata.tpk \
  'E:\lom-android-work\unity\Editor\Data\PlaybackEngines\AndroidPlayer\Data\unity default resources' \
  'H:\SteamLibrary\steamapps\common\LegendOfMortal\Mortal_Data\globalgamemanagers' x x
# 下载版数据：贴图流路径指向设备上的 files/gamedata，去掉 *.resS（第二阶段起 build-apk 默认用它）
rm -rf /e/android-recompile-work/parabox-android-work/data-dl && cp -r /e/android-recompile-work/parabox-android-work/data /e/android-recompile-work/parabox-android-work/data-dl && rm /e/android-recompile-work/parabox-android-work/data-dl/*.resS
MSYS_NO_PATHCONV=1 dotnet 'E:\android-recompile-work\parabox-android-work\ptx-bin\ptx.dll' respath 'E:\android-recompile-work\parabox-android-work\data-dl' ptx/classdata.tpk /data/data/dev.kmparabox.game/files/gamedata/
bash make-port.sh                                         # assets/port/port.txt（游戏版本变了才需要）
# 代码 + 打包（il2cpp 约 2 分钟）：
ABI=x86_64 INSTALL=emulator-5620 bash rebuild.sh          # 模拟器测试包 → E:\android-recompile-work\parabox-android-work\apk-x86_64
bash rebuild.sh                                           # Thor 用 arm64 → E:\android-recompile-work\parabox-android-work\apk-arm64
# 没有真 FMOD 时，用空桩看画面：
OUT=/e/android-recompile-work/parabox-android-work/apk-x86_64-stub FMODLIB=/e/android-recompile-work/parabox-android-work/fmodstub/lib/x86_64 ABI=x86_64 INSTALL=emulator-5620 bash rebuild.sh
```

| 文件 | 作用 |
| --- | --- |
| `tools/ptx/`（从 lomtool 复制后改写，`dotnet build -c Release -o E:\android-recompile-work\parabox-android-work\ptx-bin`） | `pdata` 数据转换；`pstage` 组装并织入；`typecmp` / `treediff` / `sercmp` 版本比对；`scripts` / `rawmb` 查看 MonoBehaviour；`refcheck` 检查引用；`pinv` 列出 P/Invoke |
| `tools/paraboxandroid/Bridge.cs` → `build-paraboxandroid.sh` | 游戏侧的 ParaboxAndroid.dll |
| `tools/java/dev/kmparabox/ParaboxActivity.java` | 入口 Activity（初始化 FMOD 的 Java 层） |
| `tools/build-il2cpp.sh`、`build-apk.sh`、`rebuild.sh` | 构建脚本（`ABI=arm64|x86_64`） |
| `tools/run-emu.sh <名字> <秒>` | 在 parabox-sim 上启动并抓日志和截图，存到 `E:\android-recompile-work\parabox-android-work\steps` |
| `tools/pad/mkpad.sh "<步骤>" <毫秒>` | 生成 uinput 脚本：注册虚拟 Xbox 手柄并按键（D/U/L/R/A/B/S，wNNN 表示等待） |

- NDK r19c、il2cpp、hlslcc 用的都是活侠传的（`E:\lom-android-work`）。在 il2cpp 的 baselib 目录里补了 `release_android_x86_64/baselib.a`，是从 AndroidPlayer 复制的，对活侠传没有影响。
- 大文件放在 `E:\android-recompile-work\parabox-android-work`：data、stage、il2cpp-<abi>、apk-*、fmodstub、steps、il（IL 导出）。

## 模拟器

- lime-sim（5600）当时被 brotato-android 会话占着（前台是 Brotato 启动页），所以我没碰它。用 G:\AndroidLab 现成的镜像新建了 AVD **parabox-sim**（`H:\android-emu\avd`，数据分区 6 GB，`-gpu host`，端口 5620），没有下载任何东西。
- 启动：`cd /g/AndroidLab/sdk/emulator; ANDROID_SDK_ROOT='G:\AndroidLab\sdk' ANDROID_AVD_HOME='H:\android-emu\avd' ./emulator.exe -avd parabox-sim -port 5620 -no-window -no-audio -no-boot-anim -no-snapshot -gpu host`
- 用完关掉：`adb -s emulator-5620 emu kill`。
- 模拟器里没有手柄时，PlayerInput 配不上任何设备（游戏的控制方案只有手柄和键鼠两种），菜单可以显示，但不能操作。所以要先跑一次 `uinput` 虚拟手柄：先 `adb push pad/xxx.json /data/local/tmp/`，再 `adb shell uinput /data/local/tmp/xxx.json`。Thor 自带手柄，不会有这个问题。

## 后续工作（第二阶段之后）

1. 用户扫码登录后（在 Thor 或模拟器上）逐项验证：从 Steam 下载 134 MB 数据（单文件多块并行是新改的）；云同步（PC 上的 save0、save1、prefs）；成就（玩出一个成就，或在 PC 上已有的会显示在成就页）；游戏时长；头像。
2. FMOD 2.01 安卓库到位后接声音（读取路径已经改到 files/gamedata）。
3. 装 Thor（**先问用户**）：看帧率（打开开发者日志，logcat 标签 Parabox 里的 `fps:`）、手柄、触屏。

## 第一阶段时的后续工作（部分已在第二阶段完成）

1. **FMOD 2.01 安卓库**（等用户下载）→ 接上声音，在模拟器上确认音乐、音效和参数切换。
2. **Steam 集成**：复用 `princess-android/cloud`（成就位号 bug 已经修好的那版）。AppID 1260520，云端前缀 `%WinAppDataLocalLow%Patrick Traynor/Patrick's Parabox/`，同步 `save*.txt`、`prefs.txt`、`custom_levels/*/*`。
   - `SaveFile.Save` 和 `Prefs.Save` 结尾调用上传。
   - `Achievements.Achieve(name)` 开头上报。
   - 启动补报：读 `Achievements.achs` 里 `achieved=true` 的项。存档里没有成就记录，所以只能补报这一次运行里触发过的成就，加上 `World.UpdateInner` 按存档重新判定的那些。
   - prefs.txt 会从 PC 同步过来（里面有全屏、垂直同步等设置），要确认它在 Android 上不会把分辨率设错（`PauseMenu.Update` 里调用了 `Screen.SetResolution`）。
3. Thor：装 arm64 包（**先问用户**），实测手柄映射（Thor 的手柄会被识别成哪个布局、按键图标显示哪套）、触屏按钮、帧率。
4. 纯触屏兜底：现在没有手柄时 PlayerInput 不配对。如果要支持纯触屏，就让 `TouchMouse` 在 PlayerInput 初始化之前加入设备，或者手动 `PerformPairingWithDevice`。
