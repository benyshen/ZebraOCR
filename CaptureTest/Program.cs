using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreScanner;

class Program
{
    static CCoreScannerClass _cs;
    static int _scannerId;
    static byte[] _imageBytes;
    static ManualResetEventSlim _imageEvent = new ManualResetEventSlim(false);
    static int _eventCount = 0;

    const int REGISTER_FOR_EVENTS = 1001;
    const int CLAIM_DEVICE = 1500;
    const int RELEASE_DEVICE = 1501;
    const int DEVICE_CAPTURE_IMAGE = 3000;
    const int DEVICE_CAPTURE_BARCODE = 3500;
    const int STATUS_SUCCESS = 0;
    const short IMAGE_COMPLETE = 1;

    static void Main(string[] args)
    {
        int targetId = args.Length > 0 ? int.Parse(args[0]) : 1;
        Console.WriteLine("Target scanner ID: " + targetId);

        _cs = new CCoreScannerClass();
        _cs.ImageEvent += OnImageEvent;
        _cs.BarcodeEvent += OnBarcodeEvent;
        _cs.VideoEvent += OnVideoEvent;

        short[] scannerTypes = { 1 };
        int status = STATUS_SUCCESS;
        int appHandle = 0;
        _cs.Open(appHandle, scannerTypes, (short)scannerTypes.Length, out status);
        Console.WriteLine("Open status: " + status);

        // Register events (Image=2, Video=4, Barcode=1)
        string regXml = "<inArgs><cmdArgs><arg-int>5</arg-int><arg-int>1,2,4,16,32</arg-int></cmdArgs></inArgs>";
        _cs.ExecCommand(REGISTER_FOR_EVENTS, ref regXml, out string regOut, out status);
        Console.WriteLine("RegisterEvents status: " + status);

        // Claim device
        string claimXml = "<inArgs><scannerID>" + targetId + "</scannerID></inArgs>";
        _cs.ExecCommand(CLAIM_DEVICE, ref claimXml, out string claimOut, out status);
        Console.WriteLine("Claim status: " + status);

        // Set image mode
        string capXml = "<inArgs><scannerID>" + targetId + "</scannerID></inArgs>";
        _cs.ExecCommand(DEVICE_CAPTURE_IMAGE, ref capXml, out string capOut, out status);
        Console.WriteLine("CaptureImage(3000) status: " + status);

        Console.WriteLine("=== 请对准目标扣动扳机拍照（最多等待 30 秒） ===");
        _imageEvent.Wait(TimeSpan.FromSeconds(30));

        if (_imageBytes != null)
        {
            string outFile = Path.Combine(AppContext.BaseDirectory, "captured_" + DateTime.Now.ToString("HHmmss") + ".jpg");
            File.WriteAllBytes(outFile, _imageBytes);
            Console.WriteLine("SAVED: " + outFile + "  (" + _imageBytes.Length + " bytes)");
        }
        else
        {
            Console.WriteLine("NO IMAGE RECEIVED (timeout)");
        }

        string relXml = "<inArgs><scannerID>" + targetId + "</scannerID></inArgs>";
        _cs.ExecCommand(RELEASE_DEVICE, ref relXml, out string relOut, out status);
        int h2 = 0;
        _cs.Close(h2, out status);
        Console.WriteLine("Events received: " + _eventCount + "  Done");
    }

    static void OnImageEvent(short eventType, int size, short imageFormat, ref object sfImageData, ref string pScannerData)
    {
        _eventCount++;
        Console.WriteLine($"[ImageEvent] type={eventType} size={size} fmt={imageFormat}");

        if (eventType == IMAGE_COMPLETE && sfImageData is Array arr)
        {
            _imageBytes = new byte[arr.Length];
            arr.CopyTo(_imageBytes, 0);
            Console.WriteLine("Image data received: " + _imageBytes.Length + " bytes, format=" + imageFormat);
            _imageEvent.Set();
        }
        else
        {
            Console.WriteLine("  (not complete or wrong data type: " + (sfImageData?.GetType().Name ?? "null") + ")");
        }
    }

    static void OnBarcodeEvent(short eventType, ref string pScanData)
    {
        Console.WriteLine("[BarcodeEvent] " + pScanData);
    }

    static void OnVideoEvent(short eventType, int size, ref object sfVideoData, ref string pScannerData)
    {
        Console.WriteLine("[VideoEvent] type=" + eventType + " size=" + size);
    }
}
