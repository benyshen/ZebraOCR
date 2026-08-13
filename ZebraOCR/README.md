# Zebra OCR - 扫码枪图像识别系统

Zebra DS8288 扫码枪图像捕获 + Unlimited-OCR AI 文字识别桌面软件（C# WPF）。

## 功能
- 自动发现 Zebra 扫码枪（底座 CR8288 / 手持 DS8288）
- 点击「扫码并OCR识别」后，扣动手持枪扳机拍照，图像经底座传回电脑
- 左侧实时预览捕获图像，右侧显示 Unlimited-OCR 识别出的全部字符/数字
- 内置「切换图像模式」按钮：将扫码枪从 HID 模式切换到 USB-SNAPI with Imaging（图像传输必需）

## 运行环境（本机已装）
- Windows 11，已安装 Zebra CoreScanner Driver（Windows 11 无需额外安装）
- Python 3.14 + torch 2.11.0+cu128（GPU CUDA 13.3，RTX A500 4GB）
- 本地模型 D:\AI\OCR-Scane\Unlimited-OCR（baidu/Unlimited-OCR）

## 启动方式
1. 启动 OCR 服务（可选，程序会自动启动）：
   ```
   python D:\AI\OCR-Scane\ZebraOCR\ZebraOCR\ocr_server.py --port 5100
   ```
   首次加载模型约需 30~60 秒，之后程序可复用该服务。
2. 运行程序：
   ```
   D:\AI\OCR-Scane\ZebraOCR\publish\ZebraOCR.exe
   ```
   或调试版：
   ```
   D:\AI\OCR-Scane\ZebraOCR\ZebraOCR\bin\Debug\net8.0-windows\ZebraOCR.exe
   ```

## 使用步骤
1. 下拉框选择扫码枪：建议选择 `[底座] CR8288`（图像通过底座传回）
2. 点击「连接扫码枪」
3. 点击「扫码并OCR识别」，状态栏提示“请拿起手持扫码枪，对准目标扣动扳机”
4. 扣动手持枪扳机 → 左侧预览图像 → 右侧自动显示 OCR 结果（约 60~90 秒）
5. 「清空」可清空右侧结果；「刷新设备」重新扫描扫码枪

## 注意事项
- 若第一次使用扫码枪未切换到 SNAPI 模式，点击「切换图像模式」把设备切换为 USB-SNAPI with Imaging（切换后设备会重新枚举，约 10 秒）
- OCR 推理在本地 GPU 上运行，每张图约 60~90 秒（4GB 显存 NF4 量化）
- 结果里的条码数据（BarcodeEvent）也会实时追加到右侧文本框

## 关键文件
| 文件 | 说明 |
|---|---|
| ZebraOCR\ZebraOCR\MainWindow.xaml.cs | 主逻辑：设备发现/连接/拍照/OCR |
| ZebraOCR\ZebraOCR\OCRService.cs | C# 调用 Python OCR 服务 |
| ZebraOCR\ZebraOCR\ocr_server.py | Python 推理服务器（HTTP :5100） |
| ZebraOCR\ZebraOCR\Interop\CoreScanner.dll | Zebra CoreScanner COM 互操作 |
| D:\AI\OCR-Scane\Unlimited-OCR | 本地模型（6.2GB） |
