# ZebraOCR - 扫码枪图像识别系统（Unlimited-OCR）

Zebra DS8288 扫码枪图像捕获 + baidu/Unlimited-OCR 多模态大模型 OCR 文字识别桌面软件（C# WPF + Python）。

> 当前版本：**v1.1.1**（仅支持 Unlimited-OCR）· 完整开发文档见 [开发文档.md](ZebraOCR/开发文档.md)

## 功能
- 自动发现 Zebra 扫码枪（底座 CR8288 / 手持 DS8288），下拉框选择设备
- 点击「扫码并OCR识别」后扣动手持枪扳机拍照，图像经底座传回电脑
- 左侧实时预览捕获图像，右侧显示 OCR 识别出的全部字符/数字
- 内置「切换图像模式」：将扫码枪切换为 USB-SNAPI with Imaging（图像传输必需）
- OCR 模型：**baidu/Unlimited-OCR**（DeepSeek-OCR 架构，中文/英文文档、标签、票据识别）
- 模型本地部署（CUDA + 4-bit NF4 量化，4GB 显存可跑），无需联网
- 内置**循环检测停止条件**：模型陷入重复输出时自动截断，避免“一直无结果”
- 推理后自动释放显存缓存（`torch.cuda.empty_cache()`），防止连续识别 OOM

## 架构
```text
ZebraOCR.exe (C# WPF 界面)
  ├─ CoreScanner.dll ── Zebra CR8288 底座 ── DS8288 手持枪（拍照）
  └─ HTTP :5100 ── ocr_server_unlimited.py ── Unlimited-OCR（CUDA / 4-bit NF4）
```

## 模型下载（开源，无需上传到本仓库）
首次使用请下载模型到本地（约 7~8GB，4-bit 量化后显存占用约 3.5GB）：

```powershell
pip install huggingface-hub
huggingface-cli download baidu/Unlimited-OCR --local-dir D:\AI\OCR-Scane\Unlimited-OCR
```

模型页：https://huggingface.co/baidu/Unlimited-OCR
也可直接访问网页下载全部文件到 `D:\AI\OCR-Scane\Unlimited-OCR`。

## 快速开始
1. 安装依赖（见开发文档第 5、8 节）：Python 3.14、`torch==2.11.0+cu128`、transformers、bitsandbytes 等
2. 下载模型到 `D:\AI\OCR-Scane\Unlimited-OCR`
3. 方式 A：先用快速启动脚本启动 OCR 服务并自测（推荐）
   ```powershell
   .\start_ocr_service.ps1 -Model unlimited -Test    # 启动并自测识别样例图
   ```
4. 方式 B：直接运行程序（程序会自动拉起 OCR 服务）
   ```powershell
   dotnet build -c Debug
   .\ZebraOCR\ZebraOCR\bin\Debug\net8.0-windows\ZebraOCR.exe
   ```
5. 方式 C：用启停脚本手动管理 OCR 服务（不运行程序时独立使用）
   ```bat
   ocr_service_toggle.bat         :: 双击打开交互菜单（1 启动 / 2 停止 / 3 状态 / 4 重启）
   ocr_service_toggle.bat start   :: 命令行直接启动（独立窗口，端口 5100）
   ocr_service_toggle.bat stop    :: 停止服务并释放显存
   ocr_service_toggle.bat status  :: 查看端口监听与 GPU 显存/利用率
   ocr_service_toggle.bat restart :: 重启服务
   ```
6. 下拉框选择 `[底座] CR8288` → 连接 → 点击「扫码并OCR识别」→ 扣动扳机拍照 → 右侧显示识别结果

## 已知说明
- 识别速度：4GB 显存（NF4 量化）单张约 2~4 分钟，属模型特性；识别期间服务不响应其它请求
- 若图片含大量重复文字（如贴纸墙），循环检测会自动截断尾部重复块，结果更干净
- 显存不足时可用 `-ForceCPU` 纯 CPU 运行（更慢）

## 更新历史
| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-08-14 | 1.1.1 | 新增 `ocr_service_toggle.bat` 启停脚本：交互菜单 + 命令行参数（start/stop/status/restart），独立窗口启动、按进程名+端口停止并释放显存、实时查看 GPU 状态 |
| 2026-08-14 | **1.1.1** | 修复 OCR 不稳定：新增块内重复停止检测（短语重复≥5次立即截断）、纯文本行内重复压缩与连续相同行去重、客户端断开静默处理；新增 GPU 利用率/显存实时状态栏；C# 编译 0 警告 |
| 2026-08-14 | 1.1 | 仅支持 Unlimited-OCR：移除多模型切换、max_length=2048、内置重复循环检测、推理后释放显存、修复“一直无 OCR 结果” |
| 2026-08-14 | 1.2 | （已废弃）曾改为仅支持 PaddleOCR-VL-0.9B；因 4GB 显存过慢，恢复 Unlimited-OCR |
