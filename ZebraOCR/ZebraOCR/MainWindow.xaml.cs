using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using CoreScanner;

namespace ZebraOCR
{
    /// <summary>
    /// 主窗口：Zebra 扫码枪图像捕获 + AI OCR 识别
    /// </summary>
    public partial class MainWindow : Window
    {
        // ============ CoreScanner 常量（与官方示例一致） ============
        private const int REGISTER_FOR_EVENTS = 1001;
        private const int UNREGISTER_FOR_EVENTS = 1002;
        private const int CLAIM_DEVICE = 1500;
        private const int RELEASE_DEVICE = 1501;
        private const int DEVICE_CAPTURE_IMAGE = 3000;
        private const int DEVICE_SWITCH_HOST_MODE = 6200;
        private const int DEVICE_PULL_TRIGGER = 2011;
        private const int STATUS_SUCCESS = 0;
        private const int STATUS_FALSE = 1;
        private const int IMAGE_COMPLETE = 1;

        // 事件订阅位掩码
        private const int SUBSCRIBE_BARCODE = 1;
        private const int SUBSCRIBE_IMAGE = 2;
        private const int SUBSCRIBE_VIDEO = 4;
        private const int SUBSCRIBE_RMD = 8;
        private const int SUBSCRIBE_PNP = 16;
        private const int SUBSCRIBE_OTHER = 32;

        // 扫描器类型
        public const short SCANNER_TYPES_ALL = 1;

        // ============ 成员 ============
        private CCoreScannerClass? m_pCoreScanner;
        private bool m_bSuccessOpen = false;
        private readonly List<ZebraScanner> m_scanners = new();
        private ZebraScanner? m_selectedScanner;
        private readonly OCRService m_ocrService = new();
        private readonly SemaphoreSlim m_ocrLock = new(1, 1);
        private int m_ocrCount = 0;
        private bool m_capturing = false;
        private byte[]? m_lastImageBytes;
        private ZebraScanner? m_cradleScanner;
        private bool m_modelListInitialized = false;
        private DispatcherTimer? m_gpuTimer;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            tbStatus.Text = "正在初始化...";
            tbInfo.Text = "Zebra OCR 启动中";
            PopulateModelCombo();

            // 初始化 CoreScanner
            try
            {
                m_pCoreScanner = new CCoreScannerClass();

                // 订阅事件
                m_pCoreScanner.ImageEvent += OnImageEvent;
                m_pCoreScanner.VideoEvent += OnVideoEvent;
                m_pCoreScanner.BarcodeEvent += OnBarcodeEvent;
                m_pCoreScanner.PNPEvent += OnPNPEvent;
                m_pCoreScanner.ScannerNotificationEvent += OnScannerNotification;

                tbStatus.Text = "CoreScanner 初始化成功，正在发现设备...";
            }
            catch (Exception ex)
            {
                tbStatus.Text = "CoreScanner 初始化失败：请确认已安装 Zebra CoreScanner Driver";
                tbInfo.Text = ex.Message;
                MessageBox.Show(
                    "无法初始化 Zebra CoreScanner。\n\n请确认已安装 Zebra CoreScanner Driver for Windows。\n\n详细信息：" + ex.Message,
                    "初始化失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // 异步初始化 OCR 服务（加载模型需要时间）
            _ = Task.Run(async () =>
            {
                await m_ocrService.InitializeAsync();
                Dispatcher.Invoke(() =>
                {
                    if (m_ocrService.IsInitialized)
                    {
                        tbInfo.Text = "OCR 模型已就绪（" + (cmbModel.SelectedItem?.ToString() ?? "Unlimited-OCR") + "）";
                    }
                    else
                    {
                        tbInfo.Text = "OCR 模型未就绪（后台重试中...）";
                    }
                });
            });

            // 发现设备
            DiscoverScanners();

            // 启动 GPU 状态监控（每 2 秒刷新一次）
            StartGpuMonitor();
        }

        // ============ GPU 状态监控 ============
        private void StartGpuMonitor()
        {
            try
            {
                m_gpuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                m_gpuTimer.Tick += async (_, _) => await RefreshGpuStatusAsync();
                m_gpuTimer.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GPU] 监控启动失败: " + ex.Message);
            }
        }

        private async Task RefreshGpuStatusAsync()
        {
            try
            {
                var info = await Task.Run(QueryGpuStatus);
                if (string.IsNullOrEmpty(info))
                {
                    tbGpuInfo.Text = "GPU: N/A";
                    tbGpuInfo.Foreground = new SolidColorBrush(Colors.Gray);
                    return;
                }
                tbGpuInfo.Text = info;
                tbGpuInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            }
            catch (Exception)
            {
                tbGpuInfo.Text = "GPU: --";
            }
        }

        /// <summary>通过 nvidia-smi 查询 GPU 利用率与显存占用</summary>
        private static string? QueryGpuStatus()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (string.IsNullOrEmpty(output)) return null;
                var parts = output.Split(',');
                if (parts.Length < 3) return null;
                var util = parts[0].Trim();
                var used = parts[1].Trim();
                var total = parts[2].Trim();
                double usedG = double.Parse(used) / 1024.0;
                double totalG = double.Parse(total) / 1024.0;
                return string.Format("GPU: {0}% | 显存 {1:F1}/{2:F1} GB", util, usedG, totalG);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ============ 设备发现 ============
        private void DiscoverScanners()
        {
            if (m_pCoreScanner == null) return;

            try
            {
                // Open CoreScanner（使用全部扫描器类型）
                int appHandle = 0;
                short[] scannerTypes = { SCANNER_TYPES_ALL };
                int status = STATUS_FALSE;
                m_pCoreScanner.Open(appHandle, scannerTypes, (short)scannerTypes.Length, out status);

                if (status != STATUS_SUCCESS)
                {
                    tbStatus.Text = "Open 失败（状态码 " + status + "）";
                    return;
                }
                m_bSuccessOpen = true;

                // 获取设备列表
                short numberOfScanners = 0;
                int[] scannerIdList = new int[255];
                string outXML = "";
                m_pCoreScanner.GetScanners(out numberOfScanners, scannerIdList, out outXML, out status);

                if (status != STATUS_SUCCESS)
                {
                    tbStatus.Text = "GetScanners 失败（状态码 " + status + "）";
                    return;
                }

                m_scanners.Clear();
                ParseScannersXml(outXML, scannerIdList, numberOfScanners);

                // 填充下拉框
                cmbScanner.Items.Clear();
                foreach (var s in m_scanners)
                {
                    cmbScanner.Items.Add(s);
                }

                if (m_scanners.Count > 0)
                {
                    cmbScanner.SelectedIndex = 0;
                    tbStatus.Text = "发现 " + m_scanners.Count + " 台扫码枪";
                    tbInfo.Text = "请选择扫码枪后点击「连接扫码枪」";
                    btnConnect.IsEnabled = true;
                }
                else
                {
                    cmbScanner.Items.Add("（未检测到扫码枪）");
                    tbStatus.Text = "未检测到 Zebra 扫码枪，请检查 USB 连接";
                    btnConnect.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                tbStatus.Text = "发现设备异常：" + ex.Message;
                tbInfo.Text = ex.Message;
            }
        }

        private void ParseScannersXml(string xml, int[] scannerIdList, short count)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var scannerNodes = doc.SelectNodes("//scanner");
                if (scannerNodes == null) return;

                int idx = 0;
                foreach (XmlNode node in scannerNodes)
                {
                    var s = new ZebraScanner();
                    var idAttr = node.Attributes?["scannerID"];
                    s.ScannerID = idAttr != null ? int.Parse(idAttr.Value) : (idx < count ? scannerIdList[idx] : 0);
                    s.SerialNumber = node.SelectSingleNode("serialnumber")?.InnerText ?? "";
                    s.Model = node.SelectSingleNode("modelnumber")?.InnerText ?? "";
                    s.Guid = node.SelectSingleNode("GUID")?.InnerText ?? "";
                    s.Port = node.SelectSingleNode("port")?.InnerText ?? "";
                    s.Firmware = node.SelectSingleNode("firmware")?.InnerText ?? "";
                    s.IsCradle = s.Model.StartsWith("CR", StringComparison.OrdinalIgnoreCase);
                    s.Description = (s.IsCradle ? "[底座] " : "[手持] ") + "[" + s.SerialNumber + "] " + s.Model + " (ID:" + s.ScannerID + ")";
                    m_scanners.Add(s);
                    idx++;
                }
            }
            catch (Exception ex)
            {
                tbStatus.Text = "解析设备列表失败：" + ex.Message;
            }
        }

        // ============ 连接 / 断开 ============
        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCoreScanner == null) return;

            if (m_selectedScanner != null)
            {
                // 已连接 -> 断开
                DisconnectScanner();
                return;
            }

            if (cmbScanner.SelectedItem is ZebraScanner sel)
            {
                m_selectedScanner = sel;
            }

            if (m_selectedScanner == null)
            {
                MessageBox.Show("请先选择一台扫码枪", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            btnConnect.IsEnabled = false;
            try
            {
                // Claim 主设备
                string claimXml = "<inArgs><scannerID>" + m_selectedScanner.ScannerID + "</scannerID></inArgs>";
                ExecCommand(CLAIM_DEVICE, claimXml);

                // 如果主设备是手持枪，自动同时 Claim 底座（图像通过底座传回）
                m_cradleScanner = null;
                if (!m_selectedScanner.IsCradle)
                {
                    var cradle = m_scanners.FirstOrDefault(s => s.IsCradle);
                    if (cradle != null)
                    {
                        m_cradleScanner = cradle;
                        string cradleClaim = "<inArgs><scannerID>" + cradle.ScannerID + "</scannerID></inArgs>";
                        ExecCommand(CLAIM_DEVICE, cradleClaim);
                    }
                }

                // 注册事件（Barcode + Image + Video + PNP）
                string regXml = "<inArgs><cmdArgs><arg-int>5</arg-int><arg-int>" +
                    SUBSCRIBE_BARCODE + "," + SUBSCRIBE_IMAGE + "," + SUBSCRIBE_VIDEO + "," + SUBSCRIBE_PNP + "," + SUBSCRIBE_OTHER +
                    "</arg-int></cmdArgs></inArgs>";
                ExecCommand(REGISTER_FOR_EVENTS, regXml);

                tbStatus.Text = "已连接：" + m_selectedScanner.Description;
                tbScannerInfo.Text = "SN:" + m_selectedScanner.SerialNumber + "  型号:" + m_selectedScanner.Model;
                btnConnect.Content = "断开连接";
                btnConnect.Background = new SolidColorBrush(Colors.DarkRed);
                btnCaptureOCR.IsEnabled = true;
            }
            catch (Exception ex)
            {
                tbStatus.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                btnConnect.IsEnabled = true;
            }
        }

        private void DisconnectScanner()
        {
            if (m_pCoreScanner == null || !m_bSuccessOpen) return;

            try
            {
                if (m_selectedScanner != null)
                {
                    string releaseXml = "<inArgs><scannerID>" + m_selectedScanner.ScannerID + "</scannerID></inArgs>";
                    ExecCommand(RELEASE_DEVICE, releaseXml);
                }
                if (m_cradleScanner != null)
                {
                    string cradleRelease = "<inArgs><scannerID>" + m_cradleScanner.ScannerID + "</scannerID></inArgs>";
                    ExecCommand(RELEASE_DEVICE, cradleRelease);
                    m_cradleScanner = null;
                }

                string unregXml = "<inArgs><cmdArgs><arg-int>5</arg-int><arg-int>" +
                    SUBSCRIBE_BARCODE + "," + SUBSCRIBE_IMAGE + "," + SUBSCRIBE_VIDEO + "," + SUBSCRIBE_PNP + "," + SUBSCRIBE_OTHER +
                    "</arg-int></cmdArgs></inArgs>";
                ExecCommand(UNREGISTER_FOR_EVENTS, unregXml);

                int appHandle = 0;
                int status = STATUS_FALSE;
                m_pCoreScanner.Close(appHandle, out status);
                m_bSuccessOpen = false;

                btnConnect.Content = "连接扫码枪";
                btnConnect.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
                btnCaptureOCR.IsEnabled = false;
                tbStatus.Text = "已断开连接";
                tbScannerInfo.Text = "";
                m_selectedScanner = null;
            }
            catch (Exception ex)
            {
                tbStatus.Text = "断开异常：" + ex.Message;
            }
        }

        private void ExecCommand(int opcode, string inXml)
        {
            if (m_pCoreScanner == null) return;
            string outXml = "";
            int status = STATUS_FALSE;
            m_pCoreScanner.ExecCommand(opcode, ref inXml, out outXml, out status);
            if (status != STATUS_SUCCESS)
            {
                tbStatus.Text = "命令失败（opcode:" + opcode + ", status:" + status + "）";
            }
        }

        // ============ 切换 SNAPI 图像模式 ============
        private async void btnSNAPI_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCoreScanner == null || m_selectedScanner == null)
            {
                MessageBox.Show("请先连接扫码枪", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                "将把扫码枪切换到「USB-SNAPI with Imaging」图像模式（需要约 10 秒，设备会重新枚举）。" + System.Environment.NewLine + System.Environment.NewLine + "确定继续？",
                "切换图像模式",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            btnSNAPI.IsEnabled = false;
            try
            {
                tbStatus.Text = "正在切换扫码枪到图像模式（SNAPI）...";
                string inXml = "<inArgs><scannerID>" + m_selectedScanner.ScannerID + "</scannerID>" +
                               "<cmdArgs><arg-string>XUA-45001-9</arg-string>" +
                               "<arg-bool>FALSE</arg-bool><arg-bool>TRUE</arg-bool></cmdArgs></inArgs>";
                ExecCommand(DEVICE_SWITCH_HOST_MODE, inXml);

                tbStatus.Text = "切换命令已发送，等待设备重新枚举...";
                await Task.Delay(8000);

                DisconnectScanner();
                await Task.Delay(1000);
                DiscoverScanners();
                tbStatus.Text = "切换完成，请重新选择扫码枪并连接";
            }
            catch (Exception ex)
            {
                tbStatus.Text = "切换失败：" + ex.Message;
            }
            finally
            {
                btnSNAPI.IsEnabled = true;
            }
        }


        // ============ 扫码并 OCR ============
        private async void btnCaptureOCR_Click(object sender, RoutedEventArgs e)
        {
            if (m_pCoreScanner == null || m_selectedScanner == null)
            {
                MessageBox.Show("请先连接扫码枪", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (m_capturing)
            {
                tbStatus.Text = "正在捕获图像，请稍候...";
                return;
            }

            m_capturing = true;
            btnCaptureOCR.IsEnabled = false;
            tbStatus.Text = "正在触发扫码枪拍照...";
            tbInfo.Text = "请对准目标按下扳机";

            try
            {
                // 1. 设置图像模式（图像命令必须发往底座，手持枪通过无线连接底座传图）
                int imageScannerId = m_selectedScanner.ScannerID;
                var cradle = m_scanners.FirstOrDefault(s => s.IsCradle);
                if (!m_selectedScanner.IsCradle && cradle != null)
                {
                    imageScannerId = cradle.ScannerID;
                }
                string imageModeXml = "<inArgs><scannerID>" + imageScannerId + "</scannerID></inArgs>";
                ExecCommand(DEVICE_CAPTURE_IMAGE, imageModeXml);

                // 2. 提示用户扣扳机（无线手持枪需实际扣动扳机触发拍照）
                tbInfo.Text = "请拿起手持扫码枪，对准目标扣动扳机";

                // 3. 等待图像（最多 30 秒）
                var deadline = DateTime.Now.AddSeconds(30);
                while (DateTime.Now < deadline && m_lastImageBytes == null)
                {
                    await Task.Delay(100);
                }

                if (m_lastImageBytes == null)
                {
                    tbStatus.Text = "未捕获到图像（超时）";
                    MessageBox.Show("未捕获到图像。\n\n请确保扫码枪支持图像模式，并尝试扣动扳机对准目标。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 4. 显示图像
                var imageBytes = m_lastImageBytes;
                m_lastImageBytes = null;
                ShowImage(imageBytes);

                // 5. 异步执行 OCR
                tbStatus.Text = "图像捕获成功，正在 OCR 识别...";
                await ProcessOCRAsync(imageBytes);
            }
            catch (Exception ex)
            {
                tbStatus.Text = "扫码异常：" + ex.Message;
                tbInfo.Text = ex.Message;
            }
            finally
            {
                m_capturing = false;
                btnCaptureOCR.IsEnabled = true;
            }
        }

        private async Task ProcessOCRAsync(byte[] imageBytes)
        {
            await m_ocrLock.WaitAsync();
            try
            {
                tbInfo.Text = "OCR 识别中（" + (cmbModel.SelectedItem as ModelOption)?.DisplayName + "）...";

                // 保存临时文件
                string tempFile = Path.Combine(Path.GetTempPath(), "zebra_ocr_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");
                File.WriteAllBytes(tempFile, imageBytes);

                // 调用 OCR 服务
                string result = await m_ocrService.RecognizeAsync(tempFile);

                m_ocrCount++;
                Dispatcher.Invoke(() =>
                {
                    AppendOcrResult("\n===== 第 " + m_ocrCount + " 次识别 [" + DateTime.Now.ToString("HH:mm:ss") + "] =====\n");
                    AppendOcrResult(result + "\n");
                    txtOCRResult.ScrollToEnd();
                });

                tbStatus.Text = "OCR 识别完成（第 " + m_ocrCount + " 次）";
                tbInfo.Text = "识别完成";
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendOcrResult("\n===== OCR 错误 [" + DateTime.Now.ToString("HH:mm:ss") + "] =====\n" + ex.Message + "\n");
                });
                tbStatus.Text = "OCR 错误：" + ex.Message;
            }
            finally
            {
                m_ocrLock.Release();
            }
        }

        private void AppendOcrResult(string text)
        {
            txtOCRResult.AppendText(text);
        }

        // ============ 事件回调 ============
        private void OnImageEvent(short eventType, int size, short imageFormat, ref object sfImageData, ref string pScannerData)
        {
            try
            {
                if (eventType != IMAGE_COMPLETE) return;

                if (sfImageData is Array arr)
                {
                    byte[] bytes = new byte[arr.Length];
                    arr.CopyTo(bytes, 0);
                    m_lastImageBytes = bytes;
                    Dispatcher.BeginInvoke(new Action(() => ShowImage(bytes)));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => tbStatus.Text = "图像事件异常：" + ex.Message));
            }
        }

        private void OnVideoEvent(short eventType, int size, ref object sfVideoData, ref string pScannerData)
        {
            try
            {
                if (eventType != IMAGE_COMPLETE) return;
                if (sfVideoData is Array arr)
                {
                    byte[] bytes = new byte[arr.Length];
                    arr.CopyTo(bytes, 0);
                    m_lastImageBytes = bytes;
                    Dispatcher.BeginInvoke(new Action(() => ShowImage(bytes)));
                }
            }
            catch { }
        }

        private void OnBarcodeEvent(short eventType, ref string pScanData)
        {
            try
            {
                string barcode = CleanBarcodeData(pScanData);
                if (string.IsNullOrEmpty(barcode)) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    AppendOcrResult("\n[条码 " + DateTime.Now.ToString("HH:mm:ss") + "] " + barcode + "\n");
                    tbStatus.Text = "条码：" + barcode;
                }));
            }
            catch { }
        }

        private void OnPNPEvent(short eventType, ref string pnpData)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (eventType == 0)
                    {
                        tbStatus.Text = "扫码枪已插入，刷新设备列表";
                        DiscoverScanners();
                    }
                    else if (eventType == 1)
                    {
                        tbStatus.Text = "扫码枪已拔出";
                        btnConnect.Content = "连接扫码枪";
                        btnCaptureOCR.IsEnabled = false;
                    }
                }));
            }
            catch { }
        }

        private void OnScannerNotification(short notificationType, ref string pScannerData)
        {
            // 预留：可处理 IMAGE_MODE / BARCODE_MODE 等通知
        }

        private string CleanBarcodeData(string hexData)
        {
            // CoreScanner 条码数据是十六进制字符串（如 "48 65 6C 6C 6F"）
            try
            {
                string[] hexParts = hexData.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var sb = new System.Text.StringBuilder();
                foreach (var part in hexParts)
                {
                    int val = Convert.ToInt32(part, 16);
                    sb.Append((char)val);
                }
                return sb.ToString();
            }
            catch
            {
                return hexData;
            }
        }

        // ============ 图像显示 ============
        private void ShowImage(byte[] imageBytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(imageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                imgPreview.Source = bitmap;
                tbNoImage.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                tbStatus.Text = "图像显示失败：" + ex.Message;
            }
        }

        // ============ UI 事件 ============
        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (m_bSuccessOpen)
            {
                try
                {
                    int appHandle = 0;
                    int status = STATUS_FALSE;
                    m_pCoreScanner?.Close(appHandle, out status);
                }
                catch { }
                m_bSuccessOpen = false;
            }
            m_selectedScanner = null;
            DiscoverScanners();
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtOCRResult.Clear();
            m_ocrCount = 0;
        }

        private void PopulateModelCombo()
        {
            cmbModel.Items.Add(new ModelOption("Unlimited-OCR", @"D:\AI\OCR-Scane\Unlimited-OCR", "unlimited"));
            cmbModel.SelectedIndex = 0;
            m_modelListInitialized = true;
            tbModelInfo.Text = "仅支持 Unlimited-OCR（端口 5100）";
        }

        private async void cmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbModel.SelectedItem is not ModelOption opt) return;
            m_ocrService.ModelPath = opt.Path;
            if (!m_modelListInitialized) return; // 启动时首次填充，由初始化流程统一处理

            tbStatus.Text = "正在切换 OCR 模型（首次加载约 30~120 秒）...";
            await m_ocrService.EnsureServerForCurrentModelAsync();
            tbStatus.Text = m_ocrService.IsInitialized
                ? "OCR 模型切换完成：" + opt.DisplayName
                : "OCR 模型切换失败，请查看程序日志";
            tbInfo.Text = m_ocrService.IsInitialized
                ? "OCR 模型已就绪（" + opt.DisplayName + "）"
                : "OCR 模型未就绪（后台重试中...）";
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (m_gpuTimer != null)
            {
                m_gpuTimer.Stop();
                m_gpuTimer = null;
            }
            try { DisconnectScanner(); } catch { }
            m_ocrService.Dispose();
        }
    }

    /// <summary>
    /// Zebra 扫码枪信息模型
    /// </summary>
    public class ZebraScanner
    {
        public int ScannerID { get; set; }
        public string SerialNumber { get; set; } = "";
        public string Model { get; set; } = "";
        public string Guid { get; set; } = "";
        public string Port { get; set; } = "";
        public string Firmware { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsCradle { get; set; }

        public override string ToString() => Description;
    }

    /// <summary>
    /// OCR 模型选项（下拉框数据源）
    /// </summary>
    public class ModelOption
    {
        public string DisplayName { get; }
        public string Path { get; }
        public string Type { get; }

        public ModelOption(string displayName, string path, string type)
        {
            DisplayName = displayName;
            Path = path;
            Type = type;
        }

        public override string ToString() => DisplayName;
    }
}
