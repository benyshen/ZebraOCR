#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OCR Server - 多模型支持（Unlimited-OCR / PaddleOCR-VL-0.9B）
通过本地 HTTP 服务对外提供图像识别接口。

用法:
    python ocr_server.py --port 5100 [--model <路径>] [--model-type auto|unlimited|paddleocr-vl] [--force-cpu]

模型加载策略:
    - Unlimited-OCR : 显存 >= 8GB 用 BF16；显存 < 8GB 用 NF4 4-bit 量化；无 GPU 用 CPU
    - PaddleOCR-VL  : 优先 BF16 + CUDA（device_map=auto）；失败回退 CPU

HTTP API:
    GET  /health            健康检查
    POST /recognize         图像识别  {"image_path": "...", "prompt": "...", "max_length": 8192}
"""

import sys
if hasattr(sys.stdout, 'reconfigure'):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

import argparse
import json
import os
import shutil
import tempfile
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_NAME = r'D:\AI\OCR-Scane\Unlimited-OCR'   # 默认模型路径
MODEL_TYPE = 'auto'                              # auto | unlimited | paddleocr-vl
model = None
tokenizer = None
processor = None
load_error = None
load_progress = ""
device = "cpu"


def detect_model_type(model_dir):
    """根据模型目录内容自动判断模型类型"""
    if os.path.isfile(os.path.join(model_dir, 'modeling_paddleocr_vl.py')):
        return 'paddleocr-vl'
    if os.path.isfile(os.path.join(model_dir, 'modeling_unlimitedocr.py')):
        return 'unlimited'
    return 'unlimited'


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
            self._send_json(400, {'success': False, 'error': f'Invalid JSON: {e}'})
            return

        image_path = data.get('image_path', '')
        prompt = data.get('prompt', '')
        image_mode = data.get('image_mode', 'gundam')
        max_length = int(data.get('max_length', 32768))

        if not os.path.exists(image_path):
            self._send_json(400, {'success': False, 'error': 'Image file not found'})
            return

        if model is None:
            self._send_json(503, {'success': False, 'error': f'Model not loaded yet: {load_error or load_progress}'})
            return

        try:
            t0 = time.time()
            result_text = run_inference(image_path, prompt, image_mode, max_length)
            elapsed = time.time() - t0
            self._send_json(200, {'success': True, 'result': result_text, 'elapsed_sec': round(elapsed, 2)})
        except Exception as e:
            import traceback
            traceback.print_exc()
            print(f'[ERROR] recognize failed: {e}', flush=True)
            self._send_json(500, {'success': False, 'error': str(e)})

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

def run_inference(image_path, prompt, image_mode, max_length):
    """按模型类型分发推理"""
    if MODEL_TYPE == 'paddleocr-vl':
        return run_inference_paddleocr_vl(image_path, max_length)
    return run_inference_unlimited(image_path, prompt, image_mode, max_length)


def run_inference_unlimited(image_path, prompt, image_mode, max_length):
    """Unlimited-OCR 推理（gundam/base 两种模式）"""
    if not prompt:
        prompt = '<image>document parsing.'
    output_path = tempfile.mkdtemp(prefix='uocr_out_')
    try:
        if image_mode == 'gundam':
            model.infer(
                tokenizer,
                prompt=prompt,
                image_file=image_path,
                output_path=output_path,
                base_size=1024,
                image_size=640,
                crop_mode=True,
                max_length=max_length,
                no_repeat_ngram_size=35,
                ngram_window=128,
                save_results=True,
            )
        else:
            model.infer(
                tokenizer,
                prompt=prompt,
                image_file=image_path,
                output_path=output_path,
                image_size=1024,
                max_length=max_length,
                no_repeat_ngram_size=35,
                ngram_window=1024,
                save_results=True,
            )

        result_text = ''
        for fname in sorted(os.listdir(output_path)):
            fpath = os.path.join(output_path, fname)
            if os.path.isfile(fpath) and fname.lower().endswith(('.txt', '.md')):
                with open(fpath, 'r', encoding='utf-8') as f:
                    result_text += f.read()
        return result_text.strip()
    finally:
        shutil.rmtree(output_path, ignore_errors=True)


def run_inference_paddleocr_vl(image_path, max_length):
    """PaddleOCR-VL-0.9B 推理（官方示例：query='OCR:' + generate）"""
    from transformers.image_utils import load_image
    image = load_image(image_path)

    query = 'OCR:'
    messages = [{"role": "user", "content": query}]
    text = tokenizer.apply_chat_template(messages, tokenize=False)
    inputs = processor(image, text=text, return_tensors='pt', format=True).to(device)

    generate_ids = model.generate(**inputs, do_sample=False, num_beams=1, max_new_tokens=max_length)
    decoded = processor.decode(generate_ids[0], skip_special_tokens=True)
    return decoded.strip()


# ==================== 加载 ====================

def pick_load_strategy(force_cpu=False):
    """根据显存选择加载策略（Unlimited-OCR 使用）"""
    global device
    try:
        import torch
        if force_cpu or not torch.cuda.is_available():
            device = "cpu"
            return {"dtype": None, "quant": None, "device": "cpu", "reason": "no GPU / force-cpu"}
        free_mem_gb = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
        device = "cuda"
        if free_mem_gb >= 8:
            return {"dtype": "bf16", "quant": None, "device": "cuda",
                    "reason": f"GPU {free_mem_gb:.1f}GB >= 8GB, BF16 full load"}
        return {"dtype": "bf16", "quant": "nf4", "device": "cuda",
                "reason": f"GPU {free_mem_gb:.1f}GB < 8GB, use 4-bit (NF4) quantized load"}
    except Exception as e:
        device = "cpu"
        return {"dtype": None, "quant": None, "device": "cpu", "reason": f"detect failed: {e}"}


def load_model(force_cpu=False):
    global model, tokenizer, processor, load_error, load_progress
    try:
        import torch
        from transformers import AutoModel, AutoTokenizer

        if MODEL_TYPE == 'paddleocr-vl':
            load_model_paddleocr_vl(torch, AutoModel, AutoTokenizer, force_cpu)
        else:
            load_model_unlimited(torch, AutoModel, AutoTokenizer, force_cpu)

        load_progress = "loaded"
        print(f'[{time.strftime("%H:%M:%S")}] 模型加载完成，设备: {device}', flush=True)
    except Exception as e:
        load_error = str(e)
        load_progress = "failed"
        print(f'[{time.strftime("%H:%M:%S")}] 模型加载失败: {e}', flush=True)


def load_model_unlimited(torch, AutoModel, AutoTokenizer, force_cpu):
    """Unlimited-OCR 加载（BF16 / NF4 / CPU 自动策略）"""
    global model, tokenizer, device
    strategy = pick_load_strategy(force_cpu)
    load_progress = f"loading strategy: {strategy['reason']}"
    print(f'[{time.strftime("%H:%M:%S")}] 加载策略: {strategy["reason"]}', flush=True)

    print(f'[{time.strftime("%H:%M:%S")}] 正在加载模型 {MODEL_NAME} ...', flush=True)
    load_progress = "downloading/loading model weights..."

    tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, trust_remote_code=True)

    load_kwargs = dict(trust_remote_code=True, use_safetensors=True)

    if strategy["quant"] == "nf4":
        from transformers import BitsAndBytesConfig
        load_progress = "4-bit quantized load (NF4)..."
        bnb_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_use_double_quant=True,
            bnb_4bit_compute_dtype=torch.bfloat16,
        )
        load_kwargs["quantization_config"] = bnb_config
        print(f'[{time.strftime("%H:%M:%S")}] 使用 4-bit (NF4) 量化以适配 4GB 显存', flush=True)
    else:
        load_kwargs["torch_dtype"] = torch.bfloat16
        print(f'[{time.strftime("%H:%M:%S")}] 使用 BF16 加载', flush=True)

    model = AutoModel.from_pretrained(MODEL_NAME, **load_kwargs)
    model = model.eval()

    if strategy["device"] == "cuda" and strategy["quant"] is None:
        model = model.cuda()

    device = strategy["device"]


def load_model_paddleocr_vl(torch, AutoModel, AutoTokenizer, force_cpu):
    """PaddleOCR-VL-0.9B 加载（BF16 + CUDA，失败回退 CPU）"""
    global model, tokenizer, processor, device
    from transformers import AutoProcessor

    print(f'[{time.strftime("%H:%M:%S")}] 正在加载 PaddleOCR-VL 模型 {MODEL_NAME} ...', flush=True)
    load_progress = "loading PaddleOCR-VL model..."

    tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, trust_remote_code=True)
    processor = AutoProcessor.from_pretrained(MODEL_NAME, trust_remote_code=True)

    use_cuda = (not force_cpu) and torch.cuda.is_available()
    device = "cuda" if use_cuda else "cpu"
    try:
        if use_cuda:
            print(f'[{time.strftime("%H:%M:%S")}] 使用 CUDA + BF16 加载', flush=True)
            model = AutoModel.from_pretrained(
                MODEL_NAME,
                trust_remote_code=True,
                use_safetensors=True,
                torch_dtype=torch.bfloat16,
                device_map='auto',
            )
        else:
            print(f'[{time.strftime("%H:%M:%S")}] 使用 CPU 加载（较慢）', flush=True)
            model = AutoModel.from_pretrained(
                MODEL_NAME,
                trust_remote_code=True,
                use_safetensors=True,
                torch_dtype=torch.float32,
            ).to('cpu')
    except torch.cuda.OutOfMemoryError:
        print(f'[{time.strftime("%H:%M:%S")}] CUDA 显存不足，回退 CPU 加载', flush=True)
        device = "cpu"
        model = AutoModel.from_pretrained(
            MODEL_NAME,
            trust_remote_code=True,
            use_safetensors=True,
            torch_dtype=torch.float32,
        ).to('cpu')

    model = model.eval()


def main():
    global MODEL_NAME, MODEL_TYPE
    parser = argparse.ArgumentParser(description='ZebraOCR 本地 OCR 服务（多模型支持）')
    parser.add_argument('--port', type=int, default=5100)
    parser.add_argument('--model', type=str, default=None, help='模型目录路径')
    parser.add_argument('--model-type', type=str, default='auto',
                        choices=['auto', 'unlimited', 'paddleocr-vl'],
                        help='模型类型：auto 自动识别 / unlimited / paddleocr-vl')
    parser.add_argument('--force-cpu', action='store_true', help='强制使用 CPU 推理')
    args = parser.parse_args()

    if args.model:
        MODEL_NAME = args.model

    if args.model_type == 'auto':
        MODEL_TYPE = detect_model_type(MODEL_NAME)
    else:
        MODEL_TYPE = args.model_type
    print(f'[{time.strftime("%H:%M:%S")}] 模型类型: {MODEL_TYPE} | 模型: {MODEL_NAME}', flush=True)

    # 后台线程加载模型
    t = threading.Thread(target=load_model, kwargs={'force_cpu': args.force_cpu}, daemon=True)
    t.start()

    server = HTTPServer(('0.0.0.0', args.port), OCRHandler)
    print(f'[{time.strftime("%H:%M:%S")}] OCR 服务已监听端口 {args.port}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()