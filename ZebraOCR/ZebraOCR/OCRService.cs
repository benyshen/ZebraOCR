using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZebraOCR
{
    /// <summary>
    /// OCR 服务：封装本地推理（baidu/Unlimited-OCR）
    /// 通过 ocr_server.py 子进程 + 本地 HTTP 接口通信
    /// </summary>
    public class OCRService : IDisposable
    {
        private readonly HttpClient httpClient = new();
        private Process? pythonProcess;
        private bool isInitialized = false;
        private string? pythonPath;
        private string ocrServerScriptPath = "";
        private readonly SemaphoreSlim initLock = new(1, 1);

        // 本地模型路径（仅 Unlimited-OCR）
        private const string MODEL_PATH = @"D:\AI\OCR-Scane\Unlimited-OCR";

        public string ModelPath { get; set; } = MODEL_PATH;

        // Unlimited-OCR 固定使用 5100 端口
        private int ServerPort => 5100;

        public OCRService()
        {
            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            ocrServerScriptPath = Path.Combine(appPath, ServerScriptName);
            // Unlimited-OCR 4GB 显存 NF4 量化，预留 30 分钟超时
            httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        /// <summary>当前模型对应的 OCR 服务脚本文件名</summary>
        private string ServerScriptName => "ocr_server_unlimited.py";

        public bool IsInitialized => isInitialized;

        public async Task InitializeAsync()
        {
            await initLock.WaitAsync();
            try
            {
                pythonPath = FindPython();
                if (string.IsNullOrEmpty(pythonPath))
                {
                    pythonPath = "python";
                }

                await StartOCRServer();
                isInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[OCRService] 初始化失败: " + ex.Message);
            }
            finally
            {
                initLock.Release();
            }
        }

        /// <summary>
        /// 模型切换后重新初始化（为当前模型启动对应端口服务）
        /// </summary>
        public async Task EnsureServerForCurrentModelAsync()
        {
            isInitialized = false;
            await InitializeAsync();
        }

        private string? FindPython()
        {
            var candidates = new[]
            {
                @"C:\Python314\python.exe",
                @"C:\Python313\python.exe",
                @"C:\Python312\python.exe",
                @"C:\Python311\python.exe",
                @"C:\Python310\python.exe",
                @"C:\Python39\python.exe",
                @"C:\Program Files\Python314\python.exe",
                @"C:\Program Files\Python313\python.exe",
                @"C:\Program Files\Python312\python.exe",
                @"C:\Program Files\Python311\python.exe",
                @"C:\Program Files\Python310\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python314", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python313", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python310", "python.exe"),
            };

            foreach (var p in candidates)
            {
                if (File.Exists(p)) return p;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "python",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    var first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            }
            catch { }

            return null;
        }

        private async Task StartOCRServer()
        {
        // Unlimited-OCR 专用服务脚本
            ocrServerScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ServerScriptName);

            // 如果服务脚本不在程序目录，尝试从开发目录复制
            if (!File.Exists(ocrServerScriptPath))
            {
                var devPath = Path.Combine(@"D:\AI\OCR-Scane\ZebraOCR\ZebraOCR", ServerScriptName);
                if (File.Exists(devPath))
                {
                    File.Copy(devPath, ocrServerScriptPath, true);
                }
            }

            if (!File.Exists(ocrServerScriptPath))
            {
                Console.WriteLine("[OCRService] " + ServerScriptName + " 不存在，跳过启动");
                return;
            }

            // 检查端口是否已有服务在运行
            bool serverRunning = false;
            try
            {
                var checkResp = await httpClient.GetAsync("http://127.0.0.1:" + ServerPort + "/health");
                if (checkResp.IsSuccessStatusCode)
                {
                    var checkBody = await checkResp.Content.ReadAsStringAsync();
                    using var checkDoc = JsonDocument.Parse(checkBody);
                    var checkRoot = checkDoc.RootElement;
                    bool loaded = checkRoot.TryGetProperty("model_loaded", out var loadedEl) && loadedEl.GetBoolean();
                    if (loaded)
                    {
                        Console.WriteLine("[OCRService] 检测到已就绪的 OCR 服务器，直接复用 (port " + ServerPort + ")");
                        return;
                    }
                    serverRunning = true; // 服务进程在跑但模型还在加载，直接等待就绪
                    Console.WriteLine("[OCRService] 检测到 OCR 服务器正在加载模型，等待就绪 (port " + ServerPort + ")");
                }
            }
            catch { }

            if (!serverRunning)
            {
                // 杀掉旧的子进程
                if (pythonProcess != null && !pythonProcess.HasExited)
                {
                    try { pythonProcess.Kill(); } catch { }
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonPath!,
                    Arguments = "\"" + ocrServerScriptPath + "\" --port " + ServerPort +
                                " --model \"" + ModelPath + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                pythonProcess = Process.Start(startInfo);
            }

            // 等待模型加载完成（首次加载需 30-120 秒）
            var deadline = DateTime.Now.AddSeconds(240);
            while (DateTime.Now < deadline)
            {
                try
                {
                    var resp = await httpClient.GetAsync("http://127.0.0.1:" + ServerPort + "/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        bool loaded = root.TryGetProperty("model_loaded", out var loadedEl) && loadedEl.GetBoolean();
                        if (loaded)
                        {
                            Console.WriteLine("[OCRService] OCR 服务器就绪 (port " + ServerPort + ")");
                            return;
                        }
                    }
                }
                catch { }
                await Task.Delay(2000);
            }

            Console.WriteLine("[OCRService] OCR 服务器健康检查超时 (port " + ServerPort + ")");
        }

        /// <summary>
        /// 识别图像中的文字
        /// </summary>
        public async Task<string> RecognizeAsync(string imagePath, string? prompt = null)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                // 通用文本提取 prompt（适用于扫码枪拍摄的文档/标签/铭牌）
                prompt = "<image>Extract and transcribe all text visible in this image. Preserve layout and line breaks.";
            }

            var payload = new
            {
                image_path = imagePath,
                prompt,
                image_mode = "gundam",
                max_length = 2048
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("http://127.0.0.1:" + ServerPort + "/recognize", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("OCR 服务返回错误 (" + response.StatusCode + "): " + responseBody);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
            {
                throw new Exception("OCR 识别失败: " + errEl.GetString());
            }

            if (root.TryGetProperty("result", out var resultEl))
            {
                var raw = resultEl.GetString() ?? "";

                // 清理模型输出中的 Markdown 图片引用（![](images/xx.jpg)）
                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    raw, @"!\[[^\]]*\]\([^)]*\)", "");
                cleaned = cleaned.Trim('\n', '\r', ' ', '\t');
                return string.IsNullOrWhiteSpace(cleaned) ? raw : cleaned;
            }

            throw new Exception("OCR 服务响应格式不正确");
        }

        public void Dispose()
        {
            try
            {
                if (pythonProcess != null && !pythonProcess.HasExited)
                {
                    pythonProcess.Kill();
                    pythonProcess.WaitForExit(3000);
                }
            }
            catch { }
            httpClient.Dispose();
        }
    }
}
