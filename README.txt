# FlashBar 闪烁提醒

一个单文件、零依赖的 Windows 托盘小工具：当程序请求关注（任务栏按钮闪烁）时，在屏幕底部弹出橙色胶囊提醒。

## 有什么用？

FlashBar 监听系统的 shell 通知（`HSHELL_FLASH`），有程序请求关注时，在**鼠标所在显示器**底部中间显示一个橙色胶囊（鼠标换到另一块屏幕后约 1 秒内跟过去）。

| 状态     | 行为                                                         |
| -------- | ------------------------------------------------------------ |
| 新提醒   | 展开 8 秒：左侧程序图标，文字为「程序名 · 窗口标题」，多条待处理时右侧显示 `+N` |
| 8 秒后   | 收成底部中间一条细橙线；鼠标移上去重新展开，移开 2 秒后收起  |
| 左键点击 | 切换到最近那条提醒对应的窗口并移出列表；切换失败时提醒保留（见日志）。仅展开态可点击，细线态点击穿透 |
| 右键点击 | 忽略当前这条                                                 |
| 全屏时   | 视频/游戏/演示时胶囊自动隐藏（提醒保留，托盘计数照常），退出全屏恢复 |
| 自动消失 | 你切到该窗口、窗口关闭或隐藏后提醒自动消失；全部处理完胶囊完全隐藏 |

闪烁停止并不会让提醒消失（与原生任务栏一致：闪完后仍保持橙色，直到打开该程序）。

## 适合谁用？

原生任务栏被隐藏（例如用 Seelen UI 等第三方 Dock 替代）后，就看不到「任务栏按钮闪橙色」的请求关注提醒了——这就是 FlashBar 存在的原因。

- **Seelen UI 等 Dock 用户**：让 Seelen 接管任务栏即可，无需额外设置，胶囊会自动避开贴底的 Seelen Dock
- **保留原生任务栏但开了自动隐藏的用户**：配合[配套 Windhawk 模组](#配套-windhawk-模组)使用，闪烁不再把任务栏弹出来
- **多显示器用户**：胶囊跟随鼠标所在屏，无需设置

环境要求：Windows 10/11 x64，.NET Framework 4.x（系统自带，无需安装）。

## 如何使用？

### 运行

双击 `FlashBar.exe` 即可。单实例（重复启动直接退出），运行后只在托盘有一个橙色圆点图标。

命令行：`FlashBar.exe --demo` — 启动后自动演示一次「测试提醒」。

### 托盘菜单（右键托盘图标）

- **测试提醒**：弹出一个最小化的测试窗口并让它闪烁（打开 5 秒后、或 60 秒无人理会时自动关闭）
- **开机自启**：写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `FlashBar` 值，默认不开启。在任务管理器「启动」页禁用后菜单显示为未勾选，再勾选会同时清除禁用标记
- **退出**

左键单击托盘图标：如有待处理提醒，重新展开胶囊 8 秒。

### 配套 Windhawk 模组

[`windhawk/taskbar-ignore-flash.wh.cpp`](windhawk/taskbar-ignore-flash.wh.cpp)：可选装。它 hook 任务栏（`taskbar.dll` 的 `CTaskBand::v_WndProc`），丢弃 `HSHELL_FLASH` 通知，让原生任务栏对闪烁完全无反应（不变橙、自动隐藏时不弹出），提醒完全交给 FlashBar。禁用模组即恢复原生行为。

### 编译

运行 `build.bat`（使用 .NET Framework 4.x 自带的 `csc.exe`，无需 Visual Studio）：

```bat
build.bat
```

编译前需先从托盘退出正在运行的 FlashBar（脚本会自动检查并拒绝）。

### 自测

先运行 `FlashBar.exe`，再在 PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File test_flash.ps1 -Seconds 3
```

脚本会创建标题为「FlashBar测试」的最小化窗口并让它闪烁，检查胶囊窗口（标题 `FlashBarPill`）是否出现，再激活测试窗口检查胶囊是否消失，最后输出 `RESULT: PASS / FAIL`。如果测试前胶囊已可见（有其他待处理提醒），改为检查日志并跳过消失检查。

### 项目结构

```
FlashBar/
├── FlashBar.exe            # 编译产物
├── build.bat               # csc.exe 一键编译（x64）
├── app.manifest            # win32 清单
├── test_flash.ps1          # 自动化自测脚本
├── src/
│   ├── Program.cs          # 入口，单实例互斥锁 + 全局异常日志
│   ├── AppController.cs    # 中枢：连接 shell 通知、列表、胶囊与托盘
│   ├── ShellHookWindow.cs  # 隐藏窗口，注册 RegisterShellHookWindow 接收 SHELLHOOK
│   ├── AttentionList.cs    # 待处理提醒列表（15 秒内的重复闪烁视为同一次）
│   ├── PillForm.cs         # 胶囊窗口（展开态/细线态状态机）
│   ├── PillLayout.cs       # 胶囊位置计算（显示器跟踪、避开 Seelen Dock）
│   ├── PillRender.cs       # 胶囊绘制
│   ├── Layered.cs          # UpdateLayeredWindow 逐像素透明
│   ├── Tray.cs             # 托盘图标与菜单（含开机自启读写）
│   ├── WinActivate.cs      # 显示器信息与窗口激活
│   ├── WinUtil.cs          # 窗口/进程工具函数
│   ├── TestWindow.cs       # 「测试提醒」演示窗口
│   ├── NativeMethods.cs    # Win32 P/Invoke 声明
│   └── Log.cs              # 日志
└── windhawk/
    └── taskbar-ignore-flash.wh.cpp   # 可选：让原生任务栏忽略闪烁
```

### 日志

`%LOCALAPPDATA%\FlashBar\flashbar.log`，超过 512KB 轮转为 `flashbar.log.1`（只保留一份）。

### 使用该app时的建议

#### 日常使用习惯

- **把托盘橙点当「待处理收件箱」**：橙点右下角的数字就是没处理的提醒数；下班前看一眼归零即可。左键单击托盘图标可随时重新展开胶囊。
- **演示/会议前先清列表**：全屏演示时胶囊会自动隐藏，但提醒还在积累；如果用窗口化（非全屏）演示，胶囊会一直显示，建议先左键逐个处理掉或右键忽略。
- **勿扰模式管不住它**：FlashBar 不走 Windows 通知中心，系统的勿扰/焦点助手对胶囊无效——想清净只能先把提醒处理完。

#### Windows 设置建议

1. **开机自启**：托盘菜单勾选「开机自启」后，可在任务管理器「启动」页确认 FlashBar 为「已启用」。注意：勾选时记录的是**当时 exe 的完整路径**，以后把文件夹挪了位置，要重新勾选一次。

2. **托盘橙点收进溢出区**（原生任务栏还保留时）：

   - Windows 11：设置 → 个性化 → 任务栏 → 其他系统托盘图标 → 关闭 FlashBar
   - Windows 10：任务栏设置 → 选择哪些图标显示在任务栏上 → 关闭 FlashBar

   图标会收进溢出菜单，功能完全不受影响。

3. **常用管理员程序、且希望左键能切过去**：普通权限的 FlashBar 切不动管理员窗口（权限隔离，见「卸载与限制」）。解决方法是用任务计划程序让它开机以最高权限运行，在**管理员**终端执行（`/tr` 换成你的实际路径）：

   ```bat
   schtasks /create /tn "FlashBar" /tr "D:\A_TOOLS\FlashBar\FlashBar.exe" /sc onlogon /rl highest /f
   ```

   取消则执行 `schtasks /delete /tn "FlashBar" /f`。用了计划任务就别再勾托盘的「开机自启」，两处留一处即可（单实例保证不会跑两个，只是冗余）。注意这会让 FlashBar 以管理员身份运行，介意的话保持默认普通权限。

#### 与隐藏任务栏方案搭配

- **Seelen UI 用户**：让 Seelen 接管任务栏即可，无需额外设置——FlashBar 检测到 Seelen 贴底窗口时会自动把胶囊让到它上方。
- **保留原生任务栏但设为自动隐藏**：强烈建议装 [Windhawk 模组](windhawk/taskbar-ignore-flash.wh.cpp)。否则每次有程序闪烁，自动隐藏的原生任务栏会被弹出来并停着不走——装了模组后任务栏对闪烁完全无反应，提醒全部交给 FlashBar。
- **多显示器**：胶囊跟随鼠标所在屏，无需设置；只要「设置 → 系统 → 显示」里的显示器排列与实际摆放一致即可。

## 卸载与限制

### 卸载

1. 如果勾过「开机自启」，先在托盘菜单取消勾选（或删除注册表 `HKCU\...\Run` 下的 `FlashBar` 值）
2. 托盘菜单 → 退出
3. 删除 FlashBar 所在的整个文件夹和 `%LOCALAPPDATA%\FlashBar`（日志）

程序不写其他注册表项、不装服务，删完即干净。

### 已知限制

- 只处理**可见窗口**（含最小化）的闪烁；隐藏窗口（如 WSLg 的隐藏 RemoteApp 容器）的闪烁会被忽略，与原生任务栏一致
- 同一窗口 15 秒内重复发来的闪烁视为同一次提醒，不会重复展开；被右键忽略的窗口在本轮闪烁结束前不会再出现
- 胶囊放在工作区底部；检测到 Seelen UI（`seelen*.exe`）贴底的可见窗口时放到它上方，每秒刷新。Dock 自动隐藏时胶囊回到底部，弹出后约 1 秒才让开
- 全屏判断：前台窗口正好铺满胶囊所在屏，或系统报告独占全屏/演示模式；无边框「最大化」不算全屏
- 以管理员身份运行的程序窗口，普通权限的 FlashBar 可能无法把它切到前台（Windows 权限隔离；解决方法见上文「Windows 设置建议」）

## License

以 [GPL-3.0](LICENSE) 发布：可以自由使用、修改、再分发，但衍生项目必须同样以 GPL-3.0 开源。