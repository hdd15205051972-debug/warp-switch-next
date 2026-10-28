# WARP 新开关 · WARP Switch Next

一个轻量的 macOS 图形界面客户端，通过 Cloudflare WARP / MASQUE over HTTP/2 连接网络。一键开启或关闭，可切换入口、修改 SNI，并可尝试 TLS 握手分片。

**适用于 Apple Silicon 和 Intel Mac，要求 macOS 13 或更新版本。** 此项目为非官方客户端，与 Cloudflare 无隶属关系。

## 下载和安装

从 **[Releases 下载最新版](https://github.com/hdd15205051972-debug/warp-switch-next/releases/latest)** 中下载 `WARP-Switch-Next-1.1.1-macOS-universal.zip`。

1. 解压，把「WARP 新开关.app」拖到「应用程序」。
2. 打开应用，首次点击「同意条款并注册」，应用会向 Cloudflare 注册属于你自己的免费 WARP 账号，然后连接。
3. 看到「已连接」后，浏览器和遵循系统 HTTP/HTTPS 代理的应用即可使用。无需安装原版 WARP 开关、Homebrew 或其他代理软件。

应用目前为本地签名，未经过 Apple 公证。首次运行如被 macOS 拦截，请在系统设置 → 隐私与安全性中检查应用来源后选择「仍要打开」。

## 日常使用

- **开启 / 关闭**：验证实际 WARP 流量成功后才启用系统代理；关闭时恢复原代理和内网绕过规则。
- **换入口重试**：跳过上次成功的入口，重新尝试其他候选入口。
- **修改伪装域名**：点击域名旁的铅笔；默认 `www.bing.com`。SNI 改变不会把 Cloudflare 的入口 IP 变成另一家网站的 IP。
- **自动尝试**：先尝试 TLS ClientHello 记录分片，再尝试不分片的兼容模式。也可手动选择其中一种。
- **菜单栏**：关闭窗口后仍可从菜单栏操作；「退出」会先关闭连接并恢复代理。
- **旧版兼容**：检测到原「WARP 开关」的已知后台服务时，连接前会停止旧连接，避免代理冲突。新版不会读取或上传旧账号。

本版不自动开机连接，也不接管不遵循系统代理的应用流量。遇到断线会先尝试隧道恢复，仍未恢复时会自动重建连接；合盖睡眠唤醒后会等待网络恢复再重新连接。重建失败时会恢复网络代理，并提示重试。

## 隐私和服务器验证

安装包不含共享账号、私钥、令牌或个人配置。每位使用者单独注册自己的账号。

账号和日志仅保存在本机 `~/Library/Application Support/WarpSwitchNext/`；目录权限为 `0700`，账号权限为 `0600`。向 Cloudflare 注册和传输流量受其[服务条款](https://www.cloudflare.com/terms/)约束。

连接内核为 **usque 4.2.1**，保持服务器公钥校验开启，不传入 `--insecure`。握手分片只改变 TLS 记录边界，不改变握手内容，也不解密隧道。支持常用内网地址直连，并保留原有绕过规则。

更换入口、SNI 或分片方式不能保证所有网络均可连接，也不能保证网络设备无法识别 WARP。请遵守适用的网络使用规则。

## 从源码构建

需要 macOS 和 Apple Command Line Tools（Swift 编译器）。

```sh
git clone https://github.com/hdd15205051972-debug/warp-switch-next.git
cd warp-switch-next
bash build.sh
```

构建脚本从 usque 官方 GitHub Release 下载固定版本的 Apple Silicon 和 Intel 二进制，核验固定的 SHA-256，再生成通用应用。输出为 `build/WARP 新开关.app`。

如已有可信的通用 usque 二进制，可通过 `USQUE_BINARY=/path/to/usque bash build.sh` 指定。依赖二进制不存放在源码仓库。

## 验证情况

已在 Apple Silicon Mac 上验证：TLS 分片数据重组、部分读取与尾随记录、真实 HTTPS 流量返回 `warp=on`、系统代理启用与恢复、内网绕过规则恢复，以及旧版连接切换。Intel 版本完成编译与架构检查，尚未在 Intel 实机测试。

运行本地测试：

```sh
'build/WARP 新开关.app/Contents/MacOS/WarpSwitchNext' --self-test
```

## 卸载

先在应用中关闭连接并退出，再将应用移到废纸篓。如果需要删除本地账号和日志，可删除 `~/Library/Application Support/WarpSwitchNext/`。不要在仍连接时删除数据目录。

## 致谢与许可

- [Diniboy1123/usque](https://github.com/Diniboy1123/usque)：WARP / MASQUE 连接内核，MIT 许可证，见 `USQUE-LICENSE.txt`。
- [EdmundMad0309/warp-masque-bypass](https://github.com/EdmundMad0309/warp-masque-bypass)：原始使用流程与 Mac 开关项目，为本项目提供启发。

本项目的 Swift 界面、握手转发与构建代码采用 MIT 许可证，见 `LICENSE`。WARP 和 Cloudflare 名称属于其各自权利人。
