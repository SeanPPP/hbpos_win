# Linkly VPP Sandbox 安装与配对

本文说明如何用 `scripts/Install-LinklyVpp.ps1` 在 Windows 开发机安装
Linkly Virtual Pin Pad（VPP），并把它作为 Linkly Cloud Sandbox 测试刷卡机
配对到 HBPOS。脚本只处理本机安装、依赖检查、VPP 启动和操作提示；Cloud
凭据仍由现有后端 Linkly Cloud credential 流程提供。

## 准备材料

开始前，先准备 Linkly 提供的 VPP 或 Offline Development 安装包。脚本不内置
下载地址，因为 Linkly 官网安装包链接可能变化。

- 本地 VPP / Linkly Offline Development 安装包，例如
  `D:\Installers\LinklyOfflineDevelopment.exe`。
- 可选：Microsoft Visual C++ Redistributable x86 安装包，例如
  `D:\Installers\vc_redist.x86.exe`。
- 管理员 PowerShell。
- Linkly Cloud Sandbox 凭据已经配置到 HBPOS 后端，且当前门店能通过现有
  Linkly Cloud credential API 读取这些凭据。

> **Note:** Linkly 官方说明 VPP 需要 Microsoft Visual C++ Redistributable
> x86。即使 Windows 是 64 位，也要安装 x86 运行库。

## 运行安装脚本

用管理员 PowerShell 在仓库根目录运行脚本。默认情况下，脚本会安装依赖和 VPP，
查找 `VirtualPinpad.exe`，然后启动 VPP。

```powershell
.\scripts\Install-LinklyVpp.ps1 `
  -InstallerPath "D:\Installers\LinklyOfflineDevelopment.exe" `
  -VcRedistX86Path "D:\Installers\vc_redist.x86.exe"
```

如果 VC++ x86 已经安装，可以省略 `-VcRedistX86Path`。

```powershell
.\scripts\Install-LinklyVpp.ps1 `
  -InstallerPath "D:\Installers\LinklyOfflineDevelopment.exe"
```

如果只想安装和检查，不想自动启动 VPP，加入 `-NoStart`。

```powershell
.\scripts\Install-LinklyVpp.ps1 `
  -InstallerPath "D:\Installers\LinklyOfflineDevelopment.exe" `
  -NoStart
```

如果 VPP 安装到非默认路径，使用 `-InstallRoot` 指定搜索根目录。

```powershell
.\scripts\Install-LinklyVpp.ps1 `
  -InstallerPath "D:\Installers\LinklyOfflineDevelopment.exe" `
  -InstallRoot "D:\Linkly"
```

## 配置 VPP Cloud Mode

脚本启动 VPP 后，在 VPP 界面完成 Cloud Mode 设置。这个步骤由人工按键完成，
脚本不会模拟窗口点击。

1. 在 VPP 上按 **FUNC**。
2. 输入 `7410`。
3. 按 **OK**。
4. 在 **TERMINAL CONFIGURATION** 画面按 `0`。
5. 在 **CLOUD MODE** 画面按 `1`，开启 Cloud Mode。
6. 按 **OK**。
7. 按 **CANCEL** 返回主画面。

VPP 第一次使用通常已经是 Cloud Mode。如果它曾经切到本地 TCP 或 On-Premises
模式，按上面的步骤切回 Cloud Mode。

## 生成 Pair Code

VPP 切到 Cloud Mode 后，生成 6 位 Pair Code。Pair Code 有时效，过期后重新
生成即可。

1. 在 VPP 上按 **FUNC**。
2. 输入 `8880`。
3. 按 **OK**。
4. 再按一次 **OK**。
5. 记录 VPP 显示的 6 位 Pair Code。

> **Warning:** `FUNC 8880` 会解除旧配对并生成新 Pair Code。测试环境可以按需
> 使用；生产交易环境不要在营业中随意执行。

## 在 HBPOS 配对 Sandbox VPP

在 HBPOS 设置页使用现有 Linkly Cloud 配对流程。脚本不会保存或显示 Cloud
username/password。

1. 打开 HBPOS 设置页。
2. 进入 **Payment Terminal**。
3. 选择 **ANZ Linkly**。
4. 勾选 **Use Linkly Cloud**。
5. 切换到 **Sandbox**。
6. 在 **Pair Code** 输入 VPP 显示的 6 位 Pair Code。
7. 点击 **配对 Cloud**。
8. 配对成功后点击测试连接。
9. 测试成功后保存 Linkly Cloud 配置。

## 测试行为

Linkly VPP 支持通过交易金额尾数模拟不同结果。测试认证场景时，建议关闭
Auto Approve，避免所有交易都自动通过。

使用 VPP 设置测试行为：

1. 在 VPP 上按 **FUNC**。
2. 输入 `7410`。
3. 按 **OK**。
4. 在 **TERMINAL CONFIGURATION** 画面按 `1`。
5. 按提示选择是否启用 Test Dialogs。
6. 在 **Always Approve Sales?** 提示中按 **CLEAR** 关闭 Auto Approve。
7. 按 **CANCEL** 返回主画面。

## 官方依据

Linkly 文档说明以下关键点：

- VPP 用于没有实体刷卡机时的 Linkly Cloud 开发和测试。
- VPP 需要 Microsoft Visual C++ Redistributable x86。
- VPP 可通过 `FUNC -> 7410` 切换 Cloud Mode。
- VPP 可通过 `FUNC -> 8880` 获取或重置 Cloud Pair Code。
- Pair Code、Cloud username 和 password 用于 POS 配对；配对成功后 POS 保存
  secret，并用 secret 获取交易 token。

## Next steps

完成配对后，用 HBPOS 发起 Sandbox 卡支付测试。若测试连接失败，先确认后端已
配置当前门店的 Linkly Cloud Sandbox username/password，再重新生成 Pair Code
并配对。
