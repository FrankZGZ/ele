# Quest Pro 肘部电触觉原型

实现：Meta 身体追踪 → 肩/肘/腕三维坐标 → 肘部弯曲角度 → 现有 eTactileKit Wi-Fi TCP 接口。
使用项目已经声明的 Meta XR Core 201.0.0 中的 OVRBody（Movement 身体追踪底层入口），不需要角色重定向或额外的 Movement 示例包。不会修改原来的碰撞场景和固件。

## 运行

1. 用 Unity **6000.0.46f1** 打开 `5_APIs/2_Unity_API`，等待 Package Manager 完成依赖解析。安装 Android Build Support、SDK/NDK、OpenJDK。
2. 点击菜单 **eTactileKit → Quest Pro → Create Elbow Prototype Scene**。
   它创建 `Assets/1_Scenes/QuestPro_Elbow_Prototype.unity`，包含 OVRCameraRig、OVRBody、ETactileKitManager 和原型控制器，并配置 Android OpenXR Loader、Meta XR Feature、身体权限、ARM64、IL2CPP、Internet 和 Vulkan。再次点击会打开现有原型场景，不覆盖参数。
3. 选择 `Elbow WiFi Prototype`。已启用 Use Discovery，并将 Target Device Id 设置为 **604FE0**。头显与板子需要处于同一局域网，校准 JSON 可在 Manager 修改。
4. 在 QuestProElbowPrototype 中选择左右臂、`Electrode Ids`（从 0 开始的逻辑电极编号，沿用校准映射）。默认左臂、0 号电极。保留原项目的刺激脉冲设置，原型不会提高它们。
5. Quest Pro 和板子连接同一可互通的局域网，或者 Quest 加入板子的 AP。关闭占用板子的电脑串口或其他 Wi-Fi 客户端。板子只能被一个主机控制。
6. 保存场景。点击 **Build Elbow Prototype APK**。若提示切换 Android，等待导入完成后再点一次。构建菜单只构建原型场景，不替换原项目 Build Settings 的场景列表。
7. 用 Meta Quest Developer Hub 或 `adb install -r <APK路径>` 安装到已开启开发者模式的 Quest Pro；允许身体追踪权限。运行不需要电脑或 USB 线。

## 头显内操作

第一版用 Touch Pro 控制器操作；身体骨架使用 SDK 推算，手部输入由 Meta 运行时处理，不需要另装 XR Hands 示例。

| 按钮 | 操作 |
|---|---|
| A（右手） | 有效追踪且硬件已连接时，启用输出 |
| B（右手） | 停止输出，回到预览 |
| X（左手） | 停止输出并尝试连接硬件 |
| Y（左手） | 停止输出并切换左右臂 |

头显显示躯干与双臂线段、当前选择的手臂角度、输出增益、Wi-Fi 状态和操作提示。该版本没有裸手 UI；没有控制器时可以看骨架，但不能通过上述按钮启用输出。

启动默认预览、输出增益 0。弯曲超过 30° 开始映射，回到 20° 以下关闭；100° 达到设定的最大增益（默认校准值的 0.25 倍，可在 Inspector 修改）。这里都是原型控制参数，不代表人体刺激处方或疼痛标定。

追踪无效、数据时间戳超过 200 ms 未更新、应用暂停/失焦、连接丢失时取消启用；恢复后需再按 A。发送上限默认 50 次/秒，使用已有 TCP 通信代码。B 为软件停止，不代替硬件断电。

## 接口和坐标

- OVRBody.BodyState.JointLocations 提供关节位置和 PositionValid 标志。
- 用 Body_Left/RightArmUpper 作为肩关节中心、ArmLower 作为肘关节、HandWrist 作为腕关节；不是用锁骨点计算大臂。
- OVR 原始位置翻转 Z 后，通过 rig.trackingSpace.TransformPoint 转成 Unity 世界坐标。
- 弯曲角度 = 180° - Vector3.Angle(肩-肘, 腕-肘)。不需要输入臂长。
- 输出走 manager.AmplitudeGain 和 SendFrame(electrodeIds)，关闭走 SendOff()；未另写设备协议。

## 已完成的验证（2026-11-13）

- 项目适配到本机 Unity 6000.0.46f1，URP 使用 17.0.4。移除该版本不存在的 adaptiveperformance/vectorgraphics 内置模块声明。
- 使用真实 Meta SDK 和 Unity 完成脚本编译、IL2CPP ARM64 原生编译和 Gradle APK 打包。
- 已生成并保存原型场景、Android XR Loader/Meta XR Feature 配置、本机版本的专用 URP 渲染资产。原来的 URP 资产保留，当前图形和质量设置改用新资产。
- APK 已通过 USB 安装并启动于 Quest Pro。包名 com.etactilekit.questpro.elbow，应用名 Quest Pro Elbow Prototype。
- 身体追踪权限已授权，32 电极模板校准已加载；用户确认在头显中能看到骨架。
- 设备 604FE0 未响应发现，板子联网和实际电触觉输出留待后续联调。没有测量端到端延迟，也没有验证人体刺激效果。

## 本机构建说明

从 Unity 菜单可以打开现有场景并构建。菜单第一次切到 Android 后，再点一次构建。
本次 Codex 环境下，Unity 内置 Gradle 启动出现 Java AF_UNIX loopback 错误；直接用项目内临时目录运行 Gradle 后成功。配套 Build-QuestPro.ps1 提供相同重试步骤，先运行 Unity 构建，只有日志出现该特定错误时才使用生成的 Gradle 工程继续打包。正常从 Unity Hub 启动时未验证是否存在相同问题。
Meta Interaction OVR 201.0.0 因 UPM 下载挂起改为项目 Packages 内嵌包；下载文件 SHA1 与官方元数据一致，SDK 源码没有修改。

## 已知限制

沿用现有同步 TCP 写入，网络异常可能阻塞 Unity；没有修改固件增加控制帧过期保护。主机卡死或网络中断不能保证软件 OFF 立即送达。此版本目标是 SDK/设备功能原型。
默认预览不会自动开启刺激。当前只有控制器按钮可启用输出，未做裸手 UI。模板的强度参数不是肘部专用标定。
若显示 Waiting for valid body joints：检查身体权限、是否戴上头显、手/控制器是否可追踪。若 Wi-Fi offline：先让板子与头显加入同一网络，并关闭占用板子串口的电脑程序，然后按 X 重连。

参考：
- https://developers.meta.com/vr/documentation/native/android/move-body-tracking/
- https://developers.meta.com/vr/documentation/unity/move-body-tracking/
- https://docs.oracle.com/en/java/javase/17/core/java-networking.html
