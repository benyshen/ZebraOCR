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
    /// OCR 服务：封装 baidu/Unlimited-OCR 本地推理
    /// 通过 ocr_server.py 子进程 + 本地 HTTP 接口通信
    /// </summary>
    public class OCRService : IDisposable
    {
        private readonly HttpClient httpClient = new();
        private Process? pythonProcess;
        private bool isInitialized = false;
        private string? pythonPath;
        private string ocrServerScriptPath = "";
        private readonly int serverPort = 5100;

        // 本地已下载的模型路径
        private const string DEFAULT_MODEL_PATH = @"D:\AI\OCR-Scane\Unlimited-OCR";

        public OCRService()
        {
            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            ocrServerScriptPath = Path.Combine(appPath, "ocr_server.py");
            httpClient.Timeout = TimeSpan.FromSeconds(600);
        }

        public bool IsInitialized => isInitialized;

        public async Task InitializeAsync()
        {
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
            // 如果 ocr_server.py 不在程序目录，尝试从开发目录复制
            if (!File.Exists(ocrServerScriptPath))
            {
                var devPath = Path.Combine(@"D:\AI\OCR-Scane\ZebraOCR\ZebraOCR", "ocr_server.py");
                if (File.Exists(devPath))
                {
                    File.Copy(devPath, ocrServerScriptPath, true);
                }
            }

            if (!File.Exists(ocrServerScriptPath))
            {
                Console.WriteLine("[OCRService] ocr_server.py 不存在，跳过启动");
                return;
            }

            // 如果端口已被占用，复用已运行的实例
            try
            {
                var checkResp = await httpClient.GetAsync("http://127.0.0.1:" + serverPort + "/health");
                if (checkResp.IsSuccessStatusCode)
                {
                    Console.WriteLine("[OCRService] 检测到已运行的 OCR 服务器，直接复用");
                    return;
                }
            }
            catch { }

            // 杀掉旧的子进程
            if (pythonProcess != null && !pythonProcess.HasExited)
            {
                try { pythonProcess.Kill(); } catch { }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath!,
                Arguments = "\"" + ocrServerScriptPath + "\" --port " + serverPort,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            pythonProcess = Process.Start(startInfo);

            // 等待健康检查通过（首次加载模型需 30-60 秒）
            var deadline = DateTime.Now.AddSeconds(180);
            while (DateTime.Now < deadline)
            {
                try
                {
                    var resp = await httpClient.GetAsync("http://127.0.0.1:" + serverPort + "/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        if (body.Contains("ok"))
                        {
                            Console.WriteLine("[OCRService] OCR 服务器就绪");
                            return;
                        }
                    }
                }
                catch { }
                await Task.Delay(2000);
            }

            Console.WriteLine("[OCRService] OCR 服务器健康检查超时");
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
                max_length = 8192
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("http://127.0.0.1:" + serverPort + "/recognize", content);
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
