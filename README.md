# ZebraOCR - 扫码枪图像识别系统

Zebra DS8288 扫码枪图像捕获 + Unlimited-OCR AI 文字识别桌面软件（C# WPF + Python）。

> 当前版本：**v1.0.1** · 完整开发文档见 [开发文档.md](ZebraOCR/开发文档.md)

## 功能
- 自动发现 Zebra 扫码枪（底座 CR8288 / 手持 DS8288），下拉框选择设备
- 点击「扫码并OCR识别」后扣动手持枪扳机拍照，图像经底座传回电脑
- 左侧实时预览捕获图像，右侧显示 Unlimited-OCR 识别出的全部字符/数字
- 内置「切换图像模式」：将扫码枪切换为 USB-SNAPI with Imaging（图像传输必需）
- OCR 模型本地部署（GPU 加速，4GB 显存 NF4 量化），无需联网

## 架构
```text
ZebraOCR.exe (C# WPF 界面)
  ├─ CoreScanner.dll ── Zebra CR8288 底座 ── DS8288 手持枪（拍照）
  └─ HTTP :5100 ── ocr_server.py (Python) ── baidu/Unlimited-OCR 模型
```

## 模型下载（开源，无需上传到本仓库）
OCR 模型来自 HuggingFace 开源仓库，首次使用请下载到本地：

```powershell
# 方式一：使用 huggingface-cli
pip install huggingface-hub
huggingface-cli download baidu/Unlimited-OCR --local-dir D:\AI\OCR-Scane\Unlimited-OCR

# 方式二：直接访问网页下载
# https://huggingface.co/baidu/Unlimited-OCR （下载全部文件，约 6.7GB）
```

模型页：https://huggingface.co/baidu/Unlimited-OCR

## 快速开始
1. 安装依赖（见开发文档第 8 节）：Python 3.14、`torch==2.11.0+cu128`、transformers 等
2. 下载模型到 `D:\AI\OCR-Scane\Unlimited-OCR`
3. 运行程序：
   ```powershell
   dotnet build -c Debug
   .\ZebraOCR\ZebraOCR\bin\Debug\net8.0-windows\ZebraOCR.exe
   ```
4. 下拉框选择 `[底座] CR8288` → 连接 → 扫码并OCR识别 → 扣动扳机拍照

## 使用步骤
1. 下拉框选择扫码枪：建议选择 `[底座] CR8288`（图像通过底座传回）
2. 点击「连接扫码枪」
3. 点击「扫码并OCR识别」，状态栏提示「请拿起手持扫码枪，对准目标扣动扳机」
4. 扣动扳机 → 左侧预览图像 → 右侧自动显示 OCR 结果（约 60~90 秒）
5. 若首次使用未切换成像模式，先点「切换图像模式」

## 目录结构
```text
ZebraOCR/                       # 主程序（源码 + 开发文档）
├── ZebraOCR/                   # C# WPF 工程
│   ├── MainWindow.xaml(.cs)    # 主界面与主逻辑
│   ├── OCRService.cs           # C# 调用 Python OCR 服务
│   ├── ocr_server.py           # Python 推理服务器（HTTP :5100）
│   └── Interop/CoreScanner.dll # Zebra CoreScanner COM 互操作
├── publish/                    # 发布输出（本地构建产物，不入库）
└── 开发文档.md                  # 完整开发文档（12 章）
CaptureTest/                    # 图像捕获测试工程（开发辅助）
Scanner-SDK-for-Windows-master/ # Zebra 官方示例源码（参考）
OCR_Sample.jpeg                 # 测试图片
```

## 环境要求
| 组件 | 版本 |
|---|---|
| 操作系统 | Windows 10/11（x64） |
| .NET SDK | 8.0 或 10.0 |
| Python | 3.14 |
| PyTorch | 2.11.0 + cu128（CUDA 13.x） |
| 显卡 | NVIDIA 4GB 显存以上（推荐） |
| Zebra 扫码枪 | DS8288 手持枪 + CR8288 底座 |

## 开发文档
完整框架、模块、功能、使用方法、遇到的问题与二次开发指南，见：
[ZebraOCR/开发文档.md](ZebraOCR/开发文档.md)

## 许可证
- 本项目代码：MIT（如适用）
- OCR 模型 baidu/Unlimited-OCR：MIT（见模型仓库）
- Zebra CoreScanner SDK：版权归 Zebra Technologies 所有