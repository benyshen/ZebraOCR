#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OCR Server - PaddleOCR-VL-0.9B 专用推理服务
本地 HTTP 服务，供 ZebraOCR (C# WPF) 调用。

用法:
    python ocr_server_paddleocr_vl.py [--port 5101] [--model <路径>] [--force-cpu]

加载策略:
    - 优先 CUDA + BF16 (device_map=auto)
    - 显存不足自动回退 CPU (float32)

HTTP API:
    GET  /health   健康检查
    POST /recognize  {"image_path": "...", "max_length": 1024}
    (PaddleOCR-VL 固定使用 query="OCR:"，prompt/image_mode 参数会被忽略)

注意事项:
    - 4GB 显存下 max_length 建议 <= 1024，过大可能导致推理极慢或超时
"""

import sys
if hasattr(sys.stdout, 'reconfigure'):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

import os
os.environ.setdefault('PYTORCH_CUDA_ALLOC_CONF', 'expandable_segments:True')

import argparse
import json
import os
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_NAME = r'D:\AI\OCR-Scane\PaddleOCR-VL-0.9B'   # 默认模型路径
MODEL_TYPE = 'paddleocr-vl'
model = None
tokenizer = None
processor = None
load_error = None
load_progress = ""
device = "cpu"


class OCRHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/health':
            self.send_response(200)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            resp = {
                'status': 'ok',
                'model_loaded': model is not None,
                'model_type': MODEL_TYPE,
                'model_name': MODEL_NAME,
                'device': device,
                'load_progress': load_progress,
                'error': load_error,
            }
            self.wfile.write(json.dumps(resp, ensure_ascii=False).encode('utf-8'))
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if self.path != '/recognize':
            self.send_response(404)
            self.end_headers()
            return

        content_length = int(self.headers.get('Content-Length', 0))
        post_data = self.rfile.read(content_length)
        try:
            data = json.loads(post_data.decode('utf-8'))
        except Exception as e:
            self._send_json(400, {'success': False, 'error': 'Invalid JSON: ' + str(e)})
            return

        image_path = data.get('image_path', '')
        max_length = int(data.get('max_length', 1024))
        if max_length > 4096:
            print('[%s] 警告: max_length=%d 过大，已限制为 4096' % (time.strftime("%H:%M:%S"), max_length), flush=True)
            max_length = 4096

        if not os.path.exists(image_path):
            self._send_json(400, {'success': False, 'error': 'Image file not found'})
            return

        if model is None:
            self._send_json(503, {'success': False, 'error': 'Model not loaded yet: ' + str(load_error or load_progress)})
            return

        try:
            t0 = time.time()
            result_text = run_inference(image_path, max_length)
            elapsed = time.time() - t0
            self._send_json(200, {'success': True, 'result': result_text, 'elapsed_sec': round(elapsed, 2)})
        except Exception as e:
            import traceback
            traceback.print_exc()
            print('[ERROR] recognize failed: ' + str(e), flush=True)
            self._send_json(500, {'success': False, 'error': str(e)})
        finally:
            # 无论成功失败都释放显存缓存，避免 4GB 显存 OOM
            try:
                import torch
                if device == 'cuda':
                    torch.cuda.empty_cache()
            except Exception:
                pass

    def _send_json(self, status, obj):
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass  # 关闭请求日志，保持控制台干净

# ==================== 推理 ====================

def run_inference(image_path, max_length):
    """PaddleOCR-VL-0.9B 推理（官方示例：query='OCR:' + generate）"""
    from transformers.image_utils import load_image
    image = load_image(image_path)

    query = 'OCR:'
    messages = [{"role": "user", "content": query}]
    text = tokenizer.apply_chat_template(messages, tokenize=False)
    inputs = processor(image, text=text, return_tensors='pt', format=True).to(device)

    generate_ids = model.generate(**inputs, do_sample=False, num_beams=1, max_new_tokens=max_length, repetition_penalty=1.5)
    decoded = processor.decode(generate_ids[0], skip_special_tokens=True)
    return decoded.strip()


# ==================== 加载 ====================

def load_model(force_cpu=False):
    global model, tokenizer, processor, load_error, load_progress, device
    try:
        import torch
        from transformers import AutoModel, AutoTokenizer, AutoProcessor

        print('[%s] 正在加载 PaddleOCR-VL 模型 %s ...' % (time.strftime("%H:%M:%S"), MODEL_NAME), flush=True)
        load_progress = "loading PaddleOCR-VL model..."

        tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, trust_remote_code=True)
        processor = AutoProcessor.from_pretrained(MODEL_NAME, trust_remote_code=True)

        use_cuda = (not force_cpu) and torch.cuda.is_available()
        device = "cuda" if use_cuda else "cpu"
        try:
            if use_cuda:
                print('[%s] 使用 CUDA + BF16 加载' % time.strftime("%H:%M:%S"), flush=True)
                model = AutoModel.from_pretrained(
                    MODEL_NAME,
                    trust_remote_code=True,
                    use_safetensors=True,
                    torch_dtype=torch.bfloat16,
                    device_map='auto',
                )
            else:
                print('[%s] 使用 CPU 加载（较慢）' % time.strftime("%H:%M:%S"), flush=True)
                model = AutoModel.from_pretrained(
                    MODEL_NAME,
                    trust_remote_code=True,
                    use_safetensors=True,
                    torch_dtype=torch.float32,
                ).to('cpu')
        except torch.cuda.OutOfMemoryError:
            print('[%s] CUDA 显存不足，回退 CPU 加载' % time.strftime("%H:%M:%S"), flush=True)
            device = "cpu"
            model = AutoModel.from_pretrained(
                MODEL_NAME,
                trust_remote_code=True,
                use_safetensors=True,
                torch_dtype=torch.float32,
            ).to('cpu')

        model = model.eval()
        if device == 'cuda':
            import torch
            torch.cuda.empty_cache()
        load_progress = "loaded"
        print('[%s] 模型加载完成，设备: %s' % (time.strftime("%H:%M:%S"), device), flush=True)
    except Exception as e:
        load_error = str(e)
        load_progress = "failed"
        print('[%s] 模型加载失败: %s' % (time.strftime("%H:%M:%S"), e), flush=True)


def main():
    global MODEL_NAME
    parser = argparse.ArgumentParser(description='ZebraOCR PaddleOCR-VL-0.9B 本地推理服务')
    parser.add_argument('--port', type=int, default=5101)
    parser.add_argument('--model', type=str, default=None, help='模型目录路径')
    parser.add_argument('--force-cpu', action='store_true', help='强制使用 CPU 推理')
    args = parser.parse_args()

    if args.model:
        MODEL_NAME = args.model
    print('[%s] 模型类型: %s | 模型: %s' % (time.strftime("%H:%M:%S"), MODEL_TYPE, MODEL_NAME), flush=True)

    # 后台线程加载模型
    t = threading.Thread(target=load_model, kwargs={'force_cpu': args.force_cpu}, daemon=True)
    t.start()

    server = HTTPServer(('0.0.0.0', args.port), OCRHandler)
    print('[%s] OCR 服务已监听端口 %s' % (time.strftime("%H:%M:%S"), args.port), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
